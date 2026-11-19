using Azure.Messaging.ServiceBus;
using InvoiceHub.BuildingBlocks.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Messaging.ServiceBus;

public static class ServiceBusRegistration
{
    /// <summary>
    /// The shared client and the two publishers. ONE client per process: the emulator allows only 10 connections in total (Spike 0),
    /// and the real service prefers few long-lived connections anyway.
    /// </summary>
    public static IServiceCollection AddServiceBusMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceBusOptions>().Bind(configuration.GetSection(ServiceBusOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(ServiceBusOptions.ConnectionName)
                ?? throw new InvalidOperationException($"Connection string '{ServiceBusOptions.ConnectionName}' is missing. Start the app through the AppHost.");
            return new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpTcp,
                RetryOptions = new ServiceBusRetryOptions { Mode = ServiceBusRetryMode.Exponential, MaxRetries = 5, Delay = TimeSpan.FromMilliseconds(500), MaxDelay = TimeSpan.FromSeconds(10) },
            });
        });
        services.AddSingleton<IEventTransport, ServiceBusEventTransport>();
        services.AddSingleton<IDeliveryQueue, ServiceBusDeliveryQueue>();
        return services;
    }

    /// <summary>Starts the fan-out consumer (topic subscription) and the delivery consumer (queue). Only the Delivery host calls this.</summary>
    public static IServiceCollection AddServiceBusConsumers(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ServiceBusOptions>>();
            var client = sp.GetRequiredService<ServiceBusClient>();
            return new MessageConsumer<EventEnvelope>(
                () => client.CreateProcessor(o.Value.EventsTopic, o.Value.FanOutSubscription, Processor(o.Value, o.Value.FanOutConcurrency)),
                async (scope, message, ct) =>
                {
                    // every module that cares about committed events gets it: fan-out first (registered first), then observers such as alerts.
                    // All are idempotent, so the retry of a whole message is harmless.
                    foreach (var handler in scope.GetServices<IEventMessageHandler>()) await handler.HandleAsync(message, ct);
                },
                sp.GetRequiredService<IServiceScopeFactory>(), o, sp.GetRequiredService<ILoggerFactory>().CreateLogger("InvoiceHub.Messaging.FanOut"), "fan-out");
        });
        services.AddSingleton<IHostedService>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ServiceBusOptions>>();
            var client = sp.GetRequiredService<ServiceBusClient>();
            return new MessageConsumer<DeliveryMessage>(
                () => client.CreateProcessor(o.Value.DeliveriesQueue, Processor(o.Value, o.Value.DeliveryConcurrency)),
                (scope, message, ct) => scope.GetRequiredService<IDeliveryMessageHandler>().HandleAsync(message, ct),
                sp.GetRequiredService<IServiceScopeFactory>(), o, sp.GetRequiredService<ILoggerFactory>().CreateLogger("InvoiceHub.Messaging.Delivery"), "delivery");
        });
        return services;
    }

    private static ServiceBusProcessorOptions Processor(ServiceBusOptions o, int concurrency) => new()
    {
        AutoCompleteMessages = false,
        ReceiveMode = ServiceBusReceiveMode.PeekLock,
        MaxConcurrentCalls = concurrency,
        PrefetchCount = o.PrefetchCount,
        MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(3),
    };
}
