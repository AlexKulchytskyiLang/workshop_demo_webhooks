using InvoiceHub.Api;
using InvoiceHub.Audit;
using InvoiceHub.Compliance;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.ServiceDefaults;
using InvoiceHub.Events;
using InvoiceHub.Notifications;
using InvoiceHub.Tenancy;
using InvoiceHub.Tenancy.Http;
using InvoiceHub.Webhooks;

// Composition root of the portal API: it only wires modules together. No business logic lives here.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddDbRole(DbRole.Api);
builder.Services.AddKeycloakAuthentication(builder.Configuration);
builder.Services.AddInvoiceHubRateLimiting();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .WithHeaders("Authorization", "Content-Type", TenantResolutionMiddleware.SelectedTenantHeader, "Idempotency-Key")
    .AllowAnyMethod()
    .WithExposedHeaders("Retry-After")));

builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql")
    .AddCheck<KeycloakHealthCheck>("keycloak");

// The API hosts the modules' HTTP surface only. Background work (relay, fan-out, delivery, sweeper) runs in the Delivery host.
IModule[] modules = [new TenancyModule(), new AuditModule(), new EventsModule(), new WebhooksModule(), new NotificationsModule(), new ComplianceModule()];
foreach (var module in modules) module.ConfigureServices(builder);

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

foreach (var module in modules) module.MapEndpoints(app);
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

app.Run();

// Lets the integration tests reference the host.
public partial class Program;
