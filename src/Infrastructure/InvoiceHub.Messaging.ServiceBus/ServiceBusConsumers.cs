using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using InvoiceHub.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Messaging.ServiceBus;

/// <summary>
/// One message consumer: peek-lock, explicit settlement, a fresh DI scope per message (so the tenant context of one message can never leak into the next).
/// Failure policy, tuned for "no event is ever lost":
/// poison (can never succeed) is dead-lettered with a reason; a transient failure (database down, broker hiccup) waits with a growing delay and
/// is then abandoned, so an outage of a few minutes does not burn the delivery count and push healthy messages to the DLQ.
/// Settlement calls tolerate <c>MessageLockLost</c> (Spike 0): the message simply comes back and the idempotent handler absorbs it.
/// </summary>
internal sealed class MessageConsumer<TMessage>(
    Func<ServiceBusProcessor> createProcessor,
    Func<IServiceProvider, TMessage, CancellationToken, Task> handle,
    IServiceScopeFactory scopes,
    IOptions<ServiceBusOptions> options,
    ILogger logger,
    string name) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _drain = new();
    private ServiceBusProcessor? _processor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _processor = createProcessor();
        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning(args.Exception, "{Consumer}: Service Bus error in {Source} ({Entity})", name, args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(cancellationToken);
        logger.LogInformation("{Consumer} started", name);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is null) return;
        // stop taking new messages, let running handlers finish for DrainSeconds, then cancel them (their deliveries are handed back)
        _drain.CancelAfter(TimeSpan.FromSeconds(options.Value.DrainSeconds));
        try
        {
            await _processor.StopProcessingAsync(cancellationToken);
        }
        finally
        {
            await _processor.DisposeAsync();
            logger.LogInformation("{Consumer} stopped", name);
        }
    }

    public void Dispose() => _drain.Dispose();

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        TMessage? payload;
        try
        {
            payload = message.Body.ToObjectFromJson<TMessage>(MessagingTelemetry.Json);
            if (payload is null) throw new JsonException("empty body");
        }
        catch (JsonException e)
        {
            await Settlement.DeadLetterAsync(args, "malformed_message", e.Message, logger);
            return;
        }

        ActivityContext.TryParse(message.ApplicationProperties.TryGetValue(MessagingTelemetry.TraceParentProperty, out var tp) ? tp as string : null, null, out var parent);
        using var activity = MessagingTelemetry.Source.StartActivity($"{name} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.message.delivery_count", message.DeliveryCount);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await handle(scope.ServiceProvider, payload, _drain.Token); // not args.CancellationToken: a stop must let running attempts drain
            await Settlement.CompleteAsync(args, logger);
        }
        catch (PoisonMessageException poison)
        {
            logger.LogError("{Consumer}: poison message {MessageId} ({Reason}): {Description}", name, message.MessageId, poison.Reason, poison.Message);
            await Settlement.DeadLetterAsync(args, poison.Reason, poison.Message, logger);
        }
        catch (Exception e) when (_drain.IsCancellationRequested && e is OperationCanceledException)
        {
            await Settlement.AbandonAsync(args, logger); // shutting down: hand it straight back
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            var wait = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(message.DeliveryCount - 1, 6))));
            logger.LogWarning(e, "{Consumer}: handling {MessageId} failed (delivery {Count}); retrying in {Wait}", name, message.MessageId, message.DeliveryCount, wait);
            try { await Task.Delay(wait, _drain.Token); } catch (OperationCanceledException) { /* shutting down */ }
            await Settlement.AbandonAsync(args, logger);
        }
    }
}

/// <summary>Settlement that tolerates a lost lock (the broker redelivers; handlers are idempotent).</summary>
internal static class Settlement
{
    public static Task CompleteAsync(ProcessMessageEventArgs args, ILogger logger) =>
        Tolerant(() => args.CompleteMessageAsync(args.Message, args.CancellationToken), args.Message, "complete", logger);

    public static Task AbandonAsync(ProcessMessageEventArgs args, ILogger logger) =>
        Tolerant(() => args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken), args.Message, "abandon", logger);

    public static Task DeadLetterAsync(ProcessMessageEventArgs args, string reason, string description, ILogger logger) =>
        Tolerant(() => args.DeadLetterMessageAsync(args.Message, reason, description, args.CancellationToken), args.Message, "dead-letter", logger);

    private static async Task Tolerant(Func<Task> settle, ServiceBusReceivedMessage message, string action, ILogger logger)
    {
        try
        {
            await BrokerGate.RunAsync(settle, CancellationToken.None);
        }
        catch (ServiceBusException e) when (e.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.MessageNotFound or ServiceBusFailureReason.SessionLockLost)
        {
            logger.LogWarning("Could not {Action} message {MessageId}: {Reason}. It will be redelivered; handlers are idempotent.", action, message.MessageId, e.Reason);
        }
        catch (OperationCanceledException)
        {
            // the processor is stopping; the lock will expire and the message returns
        }
    }
}
