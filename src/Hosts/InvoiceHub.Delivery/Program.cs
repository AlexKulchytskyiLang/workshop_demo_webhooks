using InvoiceHub.Audit;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.Events;
using InvoiceHub.Jobs;
using InvoiceHub.Storage.AzureBlob;
using InvoiceHub.Notifications;
using InvoiceHub.Tenancy;
using InvoiceHub.Messaging.ServiceBus;
using InvoiceHub.ServiceDefaults;
using InvoiceHub.Webhooks;

// Composition root of the delivery workers. Several replicas run at once: they compete for outbox rows (UPDLOCK/READPAST),
// queue messages (Service Bus competing consumers) and per-endpoint slots (SQL), so adding a replica adds capacity (ASR-05).
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddDbRole(DbRole.Worker);
builder.Services.AddServiceBusMessaging(builder.Configuration);
builder.Services.AddAzureBlobClaimCheck(builder.Configuration); // big results: written by the job runner, linked (per attempt) by the delivery workers

// graceful drain: the consumers stop taking messages, running attempts get up to ServiceBus:DrainSeconds to finish, then everything is released
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

new AuditModule().ConfigureServices(builder); // workers write audit lines too (an endpoint disabled by the platform is attributed to "system")
builder.Services.AddTenancyDirectories(builder.Configuration, builder.Environment.ApplicationName); // who to notify: read-only, no portal API in this host
IWorkerModule[] modules = [new EventsModule(), new WebhooksModule(), new NotificationsModule(), new JobsModule()];
foreach (IWorkerModule module in modules)
{
    module.ConfigureServices(builder);
    module.ConfigureWorkers(builder);
}

builder.Services.AddServiceBusConsumers(); // registered last: handlers (module services) must exist before messages can arrive

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
