using InvoiceHub.Billing;
using InvoiceHub.Billing.Contracts;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Compliance;
using InvoiceHub.Compliance.Contracts;
using InvoiceHub.Jobs;
using InvoiceHub.Jobs.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.Events;
using InvoiceHub.ServiceDefaults;
using InvoiceHub.Simulator;
using InvoiceHub.Tenancy;
using InvoiceHub.Tenancy.Contracts;

// Composition root of the business-event simulator (demo tooling). It plays "the platform's customers" against the real Billing domain.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// A tool that can fire business events for any tenant must never run anywhere but a developer machine.
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The simulator is demo tooling and only runs in Development.");

// Tenant-scoped writes only: the API login is enough (it can never act across tenants).
builder.Services.AddDbRole(DbRole.Api);
IModule[] modules = [new TenancyModule(), new EventsModule(), new BillingModule(), new JobsModule(), new ComplianceModule()];
foreach (var module in modules) module.ConfigureServices(builder);
builder.Services.AddSingleton<SimulationEngine>();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.UseDefaultFiles(); // the console (wwwroot/index.html) is the simulator's front door
app.UseStaticFiles();
app.MapDefaultEndpoints();

var api = app.MapGroup("/simulator");

api.MapGet("/tenants", async (ITenantDirectory directory, CancellationToken ct) => Results.Ok(await directory.ListActiveAsync(ct)));

api.MapGet("/scenarios", (SimulationEngine engine) => Results.Ok(engine.Scenarios.Select(Describe)));

api.MapPost("/invoices", async (InvoiceScenarioRequest request, SimulationEngine engine, ITenantDirectory directory, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Tenant))
        return Results.Problem(statusCode: 400, title: "A tenant slug is required.");

    if (request.Count is < 1 or > 5000)
        return Results.Problem(statusCode: 400, title: "Count must be between 1 and 5000.");

    if (request.PerSecond is < 0.1 or > 500)
        return Results.Problem(statusCode: 400, title: "PerSecond must be between 0.1 and 500.");

    if (request.MaxInFlight is < 1 or > 512)
        return Results.Problem(statusCode: 400, title: "MaxInFlight must be between 1 and 512.");

    if (request.PayRatio < 0 || request.OverdueRatio < 0 || request.FailedPaymentRatio < 0 || request.PayRatio + request.OverdueRatio + request.FailedPaymentRatio > 1.0001)
        return Results.Problem(statusCode: 400, title: "The ratios must not be negative and must add up to at most 1.");

    var tenant = await directory.FindBySlugAsync(request.Tenant, ct);
    if (tenant is null || tenant.Status != TenantStatus.Active)
        return Results.Problem(statusCode: 404, title: $"Unknown tenant '{request.Tenant}'.");

    var scenario = engine.Start(tenant.Slug, tenant.Id, request);
    return Results.Accepted($"/simulator/scenarios/{scenario.Id}", Describe(scenario));
});

api.MapGet("/scenarios/{id:guid}", (Guid id, SimulationEngine engine) =>
    engine.Scenarios.FirstOrDefault(s => s.Id == id) is { } s ? Results.Ok(Describe(s)) : Results.NotFound());

api.MapPost("/scenarios/{id:guid}/stop", (Guid id, SimulationEngine engine) => engine.Stop(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/stop-all", (SimulationEngine engine) =>
{
    engine.StopAll();
    return Results.NoContent();
});

// ---- the other kinds of business activity: big files, identity checks, renewals ----

api.MapPost("/exports", async (ExportRequest request, IServiceScopeFactory scopes, ITenantDirectory directory, CancellationToken ct) =>
{
    if (request.SizeMegabytes is < 1 or > 200) return Results.Problem(statusCode: 400, title: "SizeMegabytes must be between 1 and 200.");
    var tenant = await ActiveTenantAsync(directory, request.Tenant, ct);
    if (tenant is null) return Results.Problem(statusCode: 404, title: $"Unknown tenant '{request.Tenant}'.");
    var job = await InTenantAsync(scopes, tenant.Id, sp => sp.GetRequiredService<IJobCommands>().RequestExportAsync(new RequestExport("invoices", request.SizeMegabytes), ct));
    return Results.Accepted($"/simulator/jobs/{job.JobId}", job);
});

api.MapPost("/imports", async (ImportRequest request, IServiceScopeFactory scopes, ITenantDirectory directory, CancellationToken ct) =>
{
    if (request.Products is < 1 or > 100_000) return Results.Problem(statusCode: 400, title: "Products must be between 1 and 100000.");
    var tenant = await ActiveTenantAsync(directory, request.Tenant, ct);
    if (tenant is null) return Results.Problem(statusCode: 404, title: $"Unknown tenant '{request.Tenant}'.");
    var job = await InTenantAsync(scopes, tenant.Id, sp => sp.GetRequiredService<IJobCommands>().RequestImportAsync(new RequestImport(request.VendorId ?? "ven_042", request.Source ?? "edi:832", request.Products), ct));
    return Results.Accepted($"/simulator/jobs/{job.JobId}", job);
});

api.MapPost("/kyc", async (KycRequest request, IServiceScopeFactory scopes, ITenantDirectory directory, CancellationToken ct) =>
{
    if (request.Count is < 1 or > 200) return Results.Problem(statusCode: 400, title: "Count must be between 1 and 200.");
    var tenant = await ActiveTenantAsync(directory, request.Tenant, ct);
    if (tenant is null) return Results.Problem(statusCode: 404, title: $"Unknown tenant '{request.Tenant}'.");
    var created = new List<KycRef>();
    for (var i = 0; i < request.Count; i++)
    {
        var person = Persons[Random.Shared.Next(Persons.Length)];
        var name = request.CanaryName is { Length: > 0 } && i == 0 ? request.CanaryName : person.Name;
        created.Add(await InTenantAsync(scopes, tenant.Id, sp => sp.GetRequiredService<IKycCommands>().CompleteAsync(
            new CompleteKyc(name, person.DateOfBirth, $"{person.Prefix}{Random.Shared.Next(100000, 999999)}", person.Country, i % 7 == 6 ? "review" : "approved", Random.Shared.Next(3, 40)), ct)));
    }
    return Results.Accepted(value: created.Select(k => new { verificationId = k.PublicId, k.Decision }));
});

api.MapPost("/renewals", async (RenewalRequest request, IServiceScopeFactory scopes, ITenantDirectory directory, CancellationToken ct) =>
{
    if (request.Count is < 1 or > 500) return Results.Problem(statusCode: 400, title: "Count must be between 1 and 500.");
    var tenant = await ActiveTenantAsync(directory, request.Tenant, ct);
    if (tenant is null) return Results.Problem(statusCode: 404, title: $"Unknown tenant '{request.Tenant}'.");
    for (var i = 0; i < request.Count; i++)
        await InTenantAsync(scopes, tenant.Id, sp => sp.GetRequiredService<IInvoiceCommands>().RenewSubscriptionAsync(new RenewSubscription(Plans[Random.Shared.Next(Plans.Length)], Random.Shared.Next(20, 400) * 10, "USD"), ct));
    return Results.Accepted(value: new { renewed = request.Count });
});

app.Run();

static async Task<TenantInfo?> ActiveTenantAsync(ITenantDirectory directory, string? slug, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(slug)) return null;
    var tenant = await directory.FindBySlugAsync(slug, ct);
    return tenant is { Status: TenantStatus.Active } ? tenant : null;
}

/// <summary>Each command gets its own scope with the tenant set, exactly like an API request would.</summary>
static async Task<T> InTenantAsync<T>(IServiceScopeFactory scopes, Guid tenantId, Func<IServiceProvider, Task<T>> action)
{
    await using var scope = scopes.CreateAsyncScope();
    scope.ServiceProvider.GetRequiredService<ITenantScope>().SetTenant(tenantId);
    return await action(scope.ServiceProvider);
}

static object Describe(Scenario s) => new
{
    s.Id,
    s.Tenant,
    request = s.Request,
    state = s.FinishedAt is null ? "running" : s.Stopped ? "stopped" : "finished",
    s.StartedAt,
    s.FinishedAt,
    s.Created,
    s.Paid,
    s.Overdue,
    s.PaymentsFailed,
    s.Errors,
};

/// <summary>The people the demo verifies. Made up; none of it is real data.</summary>
partial class Program
{
    internal static readonly (string Name, DateOnly DateOfBirth, string Country, string Prefix)[] Persons =
    [
        ("Olena Shevchenko", new(1988, 4, 12), "UA", "AB"), ("Lars Nilsson", new(1979, 11, 2), "SE", "SE"), ("Katarzyna Nowak", new(1992, 7, 30), "PL", "PL"),
        ("Jonas Weber", new(1985, 1, 19), "DE", "DE"), ("Marta Kowalska", new(1990, 9, 5), "PL", "PL"), ("Andrii Melnyk", new(1983, 3, 27), "UA", "MK"),
    ];

    internal static readonly string[] Plans = ["starter-monthly", "business-annual", "enterprise-annual", "team-monthly"];
}

internal sealed record ExportRequest(string? Tenant, int SizeMegabytes = 50);

internal sealed record ImportRequest(string? Tenant, string? VendorId, string? Source, int Products = 1000);

internal sealed record KycRequest(string? Tenant, int Count = 1, string? CanaryName = null);

internal sealed record RenewalRequest(string? Tenant, int Count = 1);

// Lets the integration tests reference the host.
public partial class Program;
