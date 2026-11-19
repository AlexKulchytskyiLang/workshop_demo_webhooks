// One command starts the whole platform:  dotnet run --project demo/src/AppHost/InvoiceHub.AppHost
//
//   SQL Server ── migrator (schemas, RLS, logins, seeds) ──┬── api         portal API + Keycloak-validated tenant context   :5100
//   Keycloak (realm invoicehub, custom login theme)        ├── delivery    outbox relay, fan-out, delivery workers, sweeper (2 replicas)
//   Service Bus emulator (topic events, queue deliveries)  ├── simulator   plays business activity (demo tooling)           :5400
//   portal (Vite, React)                                   └── receiver    Contoso's ERP stand-in, own database            :5201
//
//   Mailpit (alert e-mail, web inbox :8025)                Azurite (claim-check files)

using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

// Keep data between runs (SQL and Keycloak are volumes, so tenants registered earlier still exist and stay consistent).
// Start clean with:  docker volume rm invoicehub-sql invoicehub-keycloak    or set Demo:PersistData=false
var isTest = string.Equals(builder.Configuration["Demo:Profile"], "test", StringComparison.OrdinalIgnoreCase); // integration tests: no portal, test realm, no volumes, fast timers
var persist = !isTest && !string.Equals(builder.Configuration["Demo:PersistData"], "false", StringComparison.OrdinalIgnoreCase);

// Generated once and kept in user secrets, so the same password still opens the persisted SQL volume next time.
var generated = new GenerateParameterDefault { MinLength = 24, Lower = true, Upper = true, Numeric = true, Special = false };
var sqlPassword = builder.AddParameter("sql-password", generated, secret: true, persist: true);
var apiDbPassword = builder.AddParameter("db-api-password", generated, secret: true, persist: true);
var workerDbPassword = builder.AddParameter("db-worker-password", generated, secret: true, persist: true);
// Master secret for the envelope that protects webhook signing secrets at rest (production: Key Vault).
var masterKey = builder.AddParameter("secrets-master-key", new GenerateParameterDefault { MinLength = 48, Lower = true, Upper = true, Numeric = true, Special = false }, secret: true, persist: true);

var sql = builder.AddSqlServer("sql", sqlPassword);
if (persist) sql.WithDataVolume("invoicehub-sql");
var db = sql.AddDatabase("invoicehub");
var receiverDb = sql.AddDatabase("receiver-contoso-db", "receiver-contoso"); // the tenant's own database: nothing of the platform lives in it
var receiverDb2 = sql.AddDatabase("receiver-fabrikam-db", "receiver-fabrikam"); // a second, completely separate tenant system

// Realm is imported on first start from infra/keycloak/realms (generated from infra/seed/seed.json).
var keycloak = builder.AddKeycloak("keycloak", 8085)
    .WithRealmImport(isTest ? "../../../infra/keycloak/realms-test" : "../../../infra/keycloak/realms")
    .WithBindMount("../../../infra/keycloak/themes/invoicehub", "/opt/keycloak/themes/invoicehub", isReadOnly: true);
if (persist) keycloak.WithDataVolume("invoicehub-keycloak");

// The emulator keeps nothing on disk (Spike 0): after a restart the timers of scheduled retries are gone and the sweeper re-queues them from SQL.
var servicebus = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator(e => e.WithConfigurationFile(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../../infra/servicebus/config.json"))));

// Large results (exports, import reports) are stored in Azurite, the local stand-in for Azure Blob Storage. Persisted like the database: events refer to these files.
var storage = builder.AddAzureStorage("storage").RunAsEmulator(e => { if (persist) e.WithDataVolume("invoicehub-azurite"); });
var blobs = storage.AddBlobs("blobs");

// Alert e-mail goes here and nowhere else: open http://localhost:8025 to read it. Nothing leaves the machine.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.27")
    .WithHttpEndpoint(port: isTest ? null : 8025, targetPort: 8025, name: "http")
    .WithEndpoint(port: isTest ? null : 1025, targetPort: 1025, name: "smtp", scheme: "tcp");
var smtp = mailpit.GetEndpoint("smtp");

// Issuer of every token. It must match the token's `iss` claim exactly (Spike 0).
var authority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/invoicehub");

// The two tenants' ERP stand-ins: independent apps with their own databases. Each reads its signing secret (and its demo API key) from the same seed files the platform uses.
var seedDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../../infra/seed"));
var webhookSeed = JsonDocument.Parse(File.ReadAllText(Path.Combine(seedDir, "webhook-seed.json"))).RootElement.GetProperty("endpoints");
var tenantSeed = JsonDocument.Parse(File.ReadAllText(Path.Combine(seedDir, "seed.json"))).RootElement;
string SecretOf(string endpointName) => webhookSeed.EnumerateArray().First(e => e.GetProperty("name").GetString() == endpointName).GetProperty("secret").GetString()!;
string ApiKeyOf(string tenant) => tenantSeed.GetProperty("apiKeys").EnumerateArray().First(k => k.GetProperty("tenant").GetString() == tenant).GetProperty("key").GetString()!;

var receiverOrigins = new[] { "http://localhost:5173", "http://localhost:5174", "http://localhost:5175" }; // the portal and the two receiver UIs

var receiver = builder.AddProject<Projects.InvoiceHub_Receiver_Api>("receiver-contoso")
    .WithReference(receiverDb, "receiverdb")
    .WaitFor(receiverDb)
    .WithEnvironment("Receiver__Name", "Contoso ERP")
    .WithEnvironment("Receiver__EndpointKey", "contoso-erp")
    .WithEnvironment("Receiver__Secrets__0", SecretOf("Contoso ERP"))
    .WithEnvironment("Receiver__PlatformApiKey", ApiKeyOf("contoso"))
    .WithHttpHealthCheck("/health/ready");

// The second receiver is the same program with other settings (the same way a second customer runs their own copy of their own system).
// It has its own launch profile ("fabrikam", port 5202) in the project's launchSettings.json, exactly as the first one has "http" (5201). The port comes from the profile
// the same way for `dotnet run`, for Visual Studio and for the tests (which randomise it), instead of from an environment variable that not every launcher passes on.
var receiver2 = builder.AddProject<Projects.InvoiceHub_Receiver_Api>("receiver-fabrikam", o => o.LaunchProfileName = "fabrikam")
    .WithReference(receiverDb2, "receiverdb")
    .WaitFor(receiverDb2)
    .WithEnvironment("Receiver__Name", "Fabrikam ERP")
    .WithEnvironment("Receiver__EndpointKey", "fabrikam-erp")
    .WithEnvironment("Receiver__Secrets__0", SecretOf("Fabrikam ERP"))
    .WithEnvironment("Receiver__PlatformApiKey", ApiKeyOf("fabrikam"))
    .WithHttpHealthCheck("/health/ready");

foreach (var r in new[] { receiver, receiver2 })
    for (var i = 0; i < receiverOrigins.Length; i++) r.WithEnvironment($"Cors__Origins__{i}", receiverOrigins[i]);

// Where webhooks may point even though they are plain HTTP on this machine: the seeded URLs (fixed ports) and wherever the receivers really listen
// (the integration tests get dynamic ports). Honoured only in Development; the guard stays strict for everything else.
var receiverEndpoint = receiver.GetEndpoint("http");
var receiverAddress = ReferenceExpression.Create($"{receiverEndpoint.Property(EndpointProperty.Host)}:{receiverEndpoint.Property(EndpointProperty.Port)}");
var receiver2Endpoint = receiver2.GetEndpoint("http");
var receiver2Address = ReferenceExpression.Create($"{receiver2Endpoint.Property(EndpointProperty.Host)}:{receiver2Endpoint.Property(EndpointProperty.Port)}");

var migrator = builder.AddProject<Projects.InvoiceHub_Migrator>("migrator")
    .WithReference(db)
    .WaitFor(db)
    .WithEnvironment("Db__Api__Password", apiDbPassword)
    .WithEnvironment("Db__Worker__Password", workerDbPassword)
    .WithEnvironment("Secrets__MasterKey", masterKey)
    .WithEnvironment("Seed__ReceiverBaseUrl", receiverEndpoint) // the seeded demo endpoints point at wherever the receivers really listen
    .WithEnvironment("Seed__Receivers__fabrikam", receiver2Endpoint);

var api = builder.AddProject<Projects.InvoiceHub_Api>("api")
    .WithReference(db)
    .WaitForCompletion(migrator)
    .WaitFor(keycloak)
    .WithEnvironment("Db__Api__User", "api_user")
    .WithEnvironment("Db__Api__Password", apiDbPassword)
    .WithEnvironment("Keycloak__Authority", authority)
    .WithEnvironment("Keycloak__AdminClientSecret", "invoicehub-dev-admin-secret")
    .WithEnvironment("Secrets__MasterKey", masterKey)
    .WithEnvironment("Webhooks__Egress__DevAllow__0", "localhost:5201")
    .WithEnvironment("Webhooks__Egress__DevAllow__1", receiverAddress)
    .WithEnvironment("Webhooks__Egress__DevAllow__2", "localhost:5202")
    .WithEnvironment("Webhooks__Egress__DevAllow__3", receiver2Address)
    .WithEnvironment("Cors__Origins__0", "http://localhost:5173")
    .WithHttpHealthCheck("/health/ready");

// the receivers reach the platform's pull API (events, KYC details) with the demo API keys that are seeded from the same file
foreach (var r in new[] { receiver, receiver2 }) r.WithEnvironment("Receiver__PlatformApiUrl", api.GetEndpoint("http")).WaitFor(api);

IResourceBuilder<ProjectResource> DeliveryHost(string name) => builder.AddProject<Projects.InvoiceHub_Delivery>(name)
    .WithReference(db)
    .WithReference(servicebus)
    .WithReference(blobs)
    .WaitFor(storage)
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS") // each replica gets its own port (health and metrics only)
    .WithHttpHealthCheck("/health/ready")
    .WaitForCompletion(migrator)
    .WaitFor(servicebus)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development") // this AppHost is the developer-machine topology; the dev-only egress allow-list is honoured only here
    .WithEnvironment("Db__Worker__User", "worker_user")
    .WithEnvironment("Db__Worker__Password", workerDbPassword)
    .WithEnvironment("Secrets__MasterKey", masterKey)
    .WithEnvironment("Webhooks__Egress__DevAllow__0", "localhost:5201")
    .WithEnvironment("Webhooks__Egress__DevAllow__1", receiverAddress)
    .WithEnvironment("Webhooks__Egress__DevAllow__2", "localhost:5202")
    .WithEnvironment("Webhooks__Egress__DevAllow__3", receiver2Address)
    .WaitFor(mailpit)
    .WithEnvironment("Notifications__SmtpHost", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Notifications__SmtpPort", smtp.Property(EndpointProperty.Port))
    .WithEnvironment("Notifications__PortalBaseUrl", "http://localhost:5173");

// Replicas compete for outbox rows, queue messages and endpoint slots, so adding one adds capacity and killing one loses nothing.
// The tests run two. The demo starts with ONE and has two more ready to start from the dashboard while a burst is running (demo scene 1: scale 1 -> 3).
// Demo:DeliveryReplicas (1 to 3) overrides the default; the scale test uses it to measure throughput with 1, 2 and 3 workers.
var replicas = int.TryParse(builder.Configuration["Demo:DeliveryReplicas"], out var configured) && configured is >= 1 and <= 3 ? configured : (isTest ? 2 : 1);
var delivery = DeliveryHost("delivery").WithReplicas(replicas);
if (!isTest)
{
    DeliveryHost("delivery-b").WithExplicitStart();
    DeliveryHost("delivery-c").WithExplicitStart();
}

if (isTest)
{
    // Integration tests: seconds instead of minutes (same code paths, compressed clocks).
    delivery
        .WithEnvironment("Webhooks__Delivery__ForceRetryPreset", "test")
        .WithEnvironment("Webhooks__Delivery__ProbeScale", "0.02")
        .WithEnvironment("Webhooks__Delivery__ResumeSpacingMilliseconds", "50")
        .WithEnvironment("Webhooks__Sweeper__PollMilliseconds", "500")
        .WithEnvironment("Webhooks__Delivery__BusyRetryMilliseconds", "300");
    // The two safety nets that re-send what looks lost are compressed as well, so a chaos test sees them act within seconds. Under a LOAD run that would be wrong:
    // a message that merely waits in a long line for more than 10 to 15 seconds would be sent again, adding traffic exactly when there is too much already.
    // Demo:RealTimers=true keeps the production values (60 s and 120 s) for such a run.
    if (builder.Configuration["Demo:RealTimers"] != "true")
    {
        delivery
            .WithEnvironment("Webhooks__Sweeper__DispatchLeaseSeconds", "10")
            .WithEnvironment("Events__Relay__StalledAfterSeconds", "15")
            .WithEnvironment("Events__Relay__StalledCheckSeconds", "2");
    }
    api.WithEnvironment("Webhooks__Delivery__ForceRetryPreset", "test");
}

// Demo tooling: plays the platform's customers against the real billing domain.
var simulator = builder.AddProject<Projects.InvoiceHub_Simulator>("simulator")
    .WithReference(db)
    .WaitForCompletion(migrator)
    .WithEnvironment("Db__Api__User", "api_user")
    .WithEnvironment("Db__Api__Password", apiDbPassword)
    .WithEnvironment("Cors__Origins__0", "http://localhost:5173");

if (!isTest)
{
    // Keycloak only accepts the redirect URI http://localhost:5173/*, so the portal must keep this exact port (no proxy, no random port).
    builder.AddViteApp("portal", "../../../web/portal")
        .WithEndpoint("http", endpoint =>
        {
            endpoint.Port = 5173;
            endpoint.TargetPort = 5173;
            endpoint.IsProxied = false;
        })
        .WithReference(api)
        .WaitFor(api)
        .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
        .WithEnvironment("VITE_OIDC_AUTHORITY", authority)
        .WithEnvironment("VITE_OIDC_CLIENT_ID", "portal");

    // One small React app, run twice: each instance shows one tenant's receiving system (the live inbox, the files it verified, the chaos switch).
    foreach (var (name, port, target, title) in new[] { ("receiver-contoso-web", 5174, receiver, "Contoso ERP"), ("receiver-fabrikam-web", 5175, receiver2, "Fabrikam ERP") })
    {
        builder.AddViteApp(name, "../../../web/receiver")
            .WithEndpoint("http", endpoint =>
            {
                endpoint.Port = port;
                endpoint.TargetPort = port;
                endpoint.IsProxied = false;
            })
            .WaitFor(target)
            .WithEnvironment("VITE_RECEIVER_API", target.GetEndpoint("http"))
            .WithEnvironment("VITE_RECEIVER_NAME", title)
            .WithEnvironment("VITE_PLATFORM_API", api.GetEndpoint("http"));
    }
}

var app = builder.Build();

// Scripted runs (tests/run-e2e.ps1): create the file named by Demo:StopFile to shut everything down gracefully,
// so containers are removed and the next run starts clean. Not used in normal operation.
var stopFile = builder.Configuration["Demo:StopFile"];
if (!string.IsNullOrEmpty(stopFile))
{
    _ = Task.Run(async () =>
    {
        while (!File.Exists(stopFile)) await Task.Delay(1000);
        await app.StopAsync();
    });
}

app.Run();
