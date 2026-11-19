using InvoiceHub.Receiver.Api;
using InvoiceHub.ServiceDefaults;
using Microsoft.Extensions.Options;

// The tenant's receiving application (a stand-in for Contoso's ERP). Independent of the platform: own database, own code.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The demo receiver (it can inject failures on demand) only runs in Development.");

builder.Services.AddOptions<ReceiverOptions>().Bind(builder.Configuration.GetSection(ReceiverOptions.Section))
    .Validate(o => o.Secrets.Count > 0 && o.Secrets.All(s => s.StartsWith("whsec_", StringComparison.Ordinal)), "Receiver:Secrets needs at least one whsec_ secret.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReceiverDb>();
builder.Services.AddSingleton<Chaos>();
builder.Services.AddSingleton<InboxSignal>();
builder.Services.AddSingleton<TrustStore>();
builder.Services.AddSingleton<PullConfig>();
builder.Services.AddSingleton<PlatformClient>();
builder.Services.AddSingleton<FileTransfers>();
builder.Services.AddSingleton<KycPuller>();
builder.Services.AddSingleton<Reconciler>();
builder.Services.AddHttpClient("jwks", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("pull", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("files", c => c.Timeout = TimeSpan.FromMinutes(5)); // a 50 MB file over a local link; a real receiver would stream it to its own storage
builder.Services.AddHostedService<InboxProcessor>();
builder.Services.AddProblemDetails();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await app.Services.GetRequiredService<ReceiverDb>().EnsureSchemaAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<TrustStore>().LoadAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<PullConfig>().LoadAsync(app.Lifetime.ApplicationStopping);

app.UseExceptionHandler();
app.UseCors();
app.MapDefaultEndpoints();
HookEndpoint.Map(app);
FeedApi.Map(app);
app.MapGet("/", (IOptions<ReceiverOptions> o) => Results.Ok(new { app = o.Value.Name, hook = $"/hooks/{o.Value.EndpointKey}", feed = "/api/stats" }));

app.Run();

// Lets the integration tests reference the host.
public partial class Program;
