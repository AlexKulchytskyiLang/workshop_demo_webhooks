using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// Boots the real platform once for all tests: SQL Server, Keycloak (with the test realm), migrator and API, exactly as the AppHost
/// wires them. Nothing is mocked: tokens come from Keycloak and SQL enforces row-level security.
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    /// <summary>A fixed master key for the test run, so a test can decrypt a signing secret the way a tenant sees it after the (step-up protected) reveal.</summary>
    public const string MasterKey = "integration-tests-master-key-0123456789-abcdefghijklmnop";

    private DistributedApplication _app = null!;

    public HttpClient Api { get; private set; } = null!;

    /// <summary>The demo simulator's control API (plays business activity against the real billing domain).</summary>
    public HttpClient Simulator { get; private set; } = null!;

    /// <summary>Contoso's receiving app: the other end of the integration.</summary>
    public HttpClient Receiver { get; private set; } = null!;

    public Uri ReceiverUrl { get; private set; } = null!;

    /// <summary>Fabrikam's receiving app: a second, independent tenant system with its own database.</summary>
    public HttpClient Receiver2 { get; private set; } = null!;

    /// <summary>Mailpit's API: where the alert emails land.</summary>
    public HttpClient Mail { get; private set; } = null!;

    public Uri KeycloakUrl { get; private set; } = null!;

    public string AdminConnectionString { get; private set; } = "";

    /// <summary>How many delivery workers to start (0 = the profile's default). The scale test sets this to 1, 2 and 3.</summary>
    public int Replicas { get; init; }

    /// <summary>Keep the production values of the re-send safety nets (a load run must not send again what merely waits in a long line).</summary>
    public bool RealTimers { get; init; }

    public async ValueTask InitializeAsync()
    {
        string[] args = ["--Demo:Profile=test", $"--Parameters:secrets-master-key={MasterKey}", .. Replicas > 0 ? [$"--Demo:DeliveryReplicas={Replicas}"] : Array.Empty<string>(), .. RealTimers ? ["--Demo:RealTimers=true"] : Array.Empty<string>()];
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.InvoiceHub_AppHost>(args, (options, _) => options.DisableDashboard = true);
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        foreach (var resource in new[] { "api", "delivery", "simulator", "receiver-contoso", "receiver-fabrikam" })
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(resource, cts.Token);

        Api = _app.CreateHttpClient("api");
        Simulator = _app.CreateHttpClient("simulator");
        Receiver = _app.CreateHttpClient("receiver-contoso");
        Receiver2 = _app.CreateHttpClient("receiver-fabrikam");
        Mail = _app.CreateHttpClient("mailpit", "http");
        ReceiverUrl = _app.GetEndpoint("receiver-contoso", "http");
        KeycloakUrl = _app.GetEndpoint("keycloak", "http");
        AdminConnectionString = (await _app.GetConnectionStringAsync("invoicehub"))!;
    }

    public async ValueTask DisposeAsync()
    {
        Api?.Dispose();
        Simulator?.Dispose();
        Receiver?.Dispose();
        Receiver2?.Dispose();
        Mail?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>Signs in against Keycloak with the password grant (test realm only) and returns the access token.</summary>
    public async Task<string> TokenAsync(string email, string password)
    {
        using var http = new HttpClient { BaseAddress = KeycloakUrl };
        var response = await http.PostAsync("/realms/invoicehub/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "it-direct",
            ["username"] = email,
            ["password"] = password,
            ["scope"] = "openid",
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Sign-in failed for {email}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// A token from a REAL interactive sign-in (authorization code with PKCE, <c>prompt=login&amp;max_age=0</c>), driven over HTTP the way the portal's step-up pop-up does it.
    /// Unlike the password grant it carries <c>auth_time</c>, so it passes the API's step-up check (secret reveal, API key creation).
    /// </summary>
    public async Task<string> StepUpTokenAsync(string email, string password = DemoPassword)
    {
        var cookies = new System.Net.CookieContainer();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler) { BaseAddress = KeycloakUrl };

        var verifier = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        const string redirect = "http://localhost:5173/auth/popup-callback";
        var authUrl = "/realms/invoicehub/protocol/openid-connect/auth?client_id=portal&response_type=code&scope=openid"
            + $"&redirect_uri={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&code_challenge_method=S256&prompt=login&max_age=0";

        var page = await http.GetAsync(authUrl);
        var html = await page.Content.ReadAsStringAsync();
        var form = System.Text.RegularExpressions.Regex.Match(html, "<form[^>]*id=\"kc-form-login\"[^>]*action=\"([^\"]+)\"");
        Assert.True(form.Success, "The Keycloak login form was not found: " + html[..Math.Min(400, html.Length)]);
        var action = System.Net.WebUtility.HtmlDecode(form.Groups[1].Value);

        var login = await http.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = password, ["credentialId"] = "" }));
        Assert.Equal(System.Net.HttpStatusCode.Found, login.StatusCode);
        var location = login.Headers.Location!;
        var code = System.Web.HttpUtility.ParseQueryString(location.Query)["code"];
        Assert.False(string.IsNullOrEmpty(code), "The sign-in did not return a code: " + location);

        var token = await http.PostAsync("/realms/invoicehub/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "portal", ["code"] = code!, ["code_verifier"] = verifier, ["redirect_uri"] = redirect,
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Token exchange failed: {(int)token.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public const string DemoPassword = "Passw0rd!2026";

    public Task<string> DemoTokenAsync(string email) => TokenAsync(email, DemoPassword);

    public HttpRequestMessage Get(string url, string? token = null, string? selectedTenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (selectedTenant is not null) request.Headers.Add("X-Selected-Tenant", selectedTenant);
        return request;
    }

    /// <summary>Runs a query as a given SQL user (EXECUTE AS), with an optional tenant or system context, so row-level security is observed for that login.</summary>
    public async Task<T> ScalarAsAsync<T>(string sqlUser, string query, Guid? tenant = null, bool system = false)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        var context = tenant is not null
            ? $"EXEC sp_set_session_context @key = N'tenant_id', @value = '{tenant}';"
            : system ? "EXEC sp_set_session_context @key = N'system', @value = 1;" : "";
        await using var command = new SqlCommand($"{context} EXECUTE AS USER = '{sqlUser}'; {query}; REVERT;", connection);
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    /// <summary>Polls until the condition holds (the platform is asynchronous by design: a webhook is delivered "soon", not "now").</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out after {timeout.TotalSeconds:0} s waiting for: {description}");
    }

    /// <summary>The shared secret of an endpoint's current HMAC key (read straight from the database and decrypted with the test master key).</summary>
    public async Task<string> HmacSecretOfAsync(Guid endpointId)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"EXEC sp_set_session_context @key = N'system', @value = 1; EXECUTE AS USER = 'worker_user'; " +
            $"SELECT TOP 1 secret_enc FROM webhooks.endpoint_secret WHERE endpoint_id = '{endpointId}' AND algorithm = 'hmac' AND (valid_until IS NULL OR valid_until > SYSUTCDATETIME()) ORDER BY valid_from DESC; REVERT;", connection);
        var blob = (byte[])(await command.ExecuteScalarAsync())!;
        return System.Text.Encoding.UTF8.GetString(new InvoiceHub.BuildingBlocks.Security.AesGcmSecretProtector(MasterKey).Unprotect(blob, $"endpoint:{endpointId:N}"));
    }

    public async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;
}

[CollectionDefinition(Name)]
public sealed class PlatformCollection : ICollectionFixture<PlatformFixture>
{
    public const string Name = "platform";
}
