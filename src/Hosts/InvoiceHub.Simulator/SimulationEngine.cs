using System.Collections.Concurrent;
using System.Diagnostics;
using InvoiceHub.Billing.Contracts;
using InvoiceHub.BuildingBlocks.Tenancy;

namespace InvoiceHub.Simulator;

public sealed record InvoiceScenarioRequest(string? Tenant, int Count = 10, double PerSecond = 5,
                                            double PayRatio = 0.7, double OverdueRatio = 0.1, double FailedPaymentRatio = 0.1,
                                            int MaxInFlight = 32);

public sealed class Scenario(Guid id, string tenantSlug, Guid tenantId, InvoiceScenarioRequest request)
{
    internal CancellationTokenSource Cts { get; } = new();
    public Guid Id { get; } = id;
    public string Tenant { get; } = tenantSlug;
    public Guid TenantId { get; } = tenantId;
    public InvoiceScenarioRequest Request { get; } = request;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; internal set; }
    public bool Stopped => Cts.IsCancellationRequested;
    public int Created => _created;
    public int Paid => _paid;
    public int Overdue => _overdue;
    public int PaymentsFailed => _failed;
    public int Errors => _errors;

    private int _created, _paid, _overdue, _failed, _errors;

    internal static void Count(ref int counter) => Interlocked.Increment(ref counter);

    internal ref int CreatedRef => ref _created;
    internal ref int PaidRef => ref _paid;
    internal ref int OverdueRef => ref _overdue;
    internal ref int FailedRef => ref _failed;
    internal ref int ErrorsRef => ref _errors;
}

/// <summary>
/// Plays business activity against the real Billing domain: invoices are generated, paid, go overdue, payments fail. Each step is a real command that
/// commits the change and its event together (outbox), so what the receivers see is the platform's real output, only the "customers" are simulated.
/// Invoices run concurrently, so events about one invoice are produced a moment apart and the delivery workers may reorder them: exactly what the
/// receivers' sequence guard is for.
/// </summary>
public sealed class SimulationEngine(IServiceScopeFactory scopes, ILogger<SimulationEngine> logger, IHostApplicationLifetime lifetime)
{
    private static readonly (string Name, string Country, string Currency)[] Customers =
    [
        ("Kovalenko Trade", "UA", "UAH"), ("Nordic Parts AB", "SE", "EUR"), ("Lviv Logistics", "UA", "UAH"), ("Alpine Tools GmbH", "DE", "EUR"),
        ("Baltic Foods", "LT", "EUR"), ("Carpathian Wood", "UA", "USD"), ("Danube Shipping", "RO", "EUR"), ("Warsaw Retail", "PL", "PLN"),
    ];

    /// <summary>One trace per simulated invoice: the commit, the outbox relay, fan-out, every delivery attempt and the receiver's inbox all carry this trace id (ASR-13).</summary>
    private static readonly ActivitySource Source = new("InvoiceHub.Simulator");

    private readonly ConcurrentDictionary<Guid, Scenario> _scenarios = new();

    public IReadOnlyCollection<Scenario> Scenarios => _scenarios.Values.OrderByDescending(s => s.StartedAt).ToList();

    public Scenario Start(string slug, Guid tenantId, InvoiceScenarioRequest request)
    {
        var scenario = new Scenario(Guid.CreateVersion7(), slug, tenantId, request);
        _scenarios[scenario.Id] = scenario;
        _ = Task.Run(() => RunAsync(scenario));
        return scenario;
    }

    public bool Stop(Guid id)
    {
        if (!_scenarios.TryGetValue(id, out var s)) return false;
        s.Cts.Cancel();
        return true;
    }

    public void StopAll()
    {
        foreach (var s in _scenarios.Values) s.Cts.Cancel();
    }

    private async Task RunAsync(Scenario s)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var interval = TimeSpan.FromSeconds(1 / Math.Max(0.1, s.Request.PerSecond));
        using var gate = new SemaphoreSlim(Math.Clamp(s.Request.MaxInFlight, 1, 512)); // never more than this many invoices in progress, whatever the rate (32 unless a load run asks for more)
        var running = new List<Task>();

        try
        {
            for (var i = 0; i < s.Request.Count && !ct.IsCancellationRequested; i++)
            {
                await gate.WaitAsync(ct);
                running.Add(Task.Run(async () =>
                {
                    try { await LifecycleAsync(s, ct); }
                    finally { gate.Release(); }
                }, ct));

                running.RemoveAll(t => t.IsCompleted);
                await Task.Delay(interval, ct);
            }
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
            // stopped on request or at shutdown
        }
        finally
        {
            s.FinishedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Scenario {Scenario} for {Tenant} finished: {Created} invoices, {Paid} paid, {Overdue} overdue, {Failed} failed payments, {Errors} errors",
                                    s.Id, s.Tenant, s.Created, s.Paid, s.Overdue, s.PaymentsFailed, s.Errors);
        }
    }

    private async Task LifecycleAsync(Scenario s, CancellationToken ct)
    {
        using var trace = Source.StartActivity("simulated invoice lifecycle", ActivityKind.Internal);
        trace?.SetTag("tenant", s.Tenant);
        try
        {
            var customer = Customers[Random.Shared.Next(Customers.Length)];
            var amount = Math.Round((decimal)(Random.Shared.NextDouble() * 4900 + 100), 2);
            var invoice = await InTenantAsync(s.TenantId, c => c.GenerateAsync(new GenerateInvoice(customer.Name, customer.Country, amount, customer.Currency), ct));
            Scenario.Count(ref s.CreatedRef);

            await Task.Delay(Random.Shared.Next(100, 1200), ct);
            var roll = Random.Shared.NextDouble();
            var r = s.Request;
            if (roll < r.FailedPaymentRatio)
            {
                await InTenantAsync(s.TenantId, c => c.FailPaymentAsync(invoice.InvoiceId, "card_declined", ct));
                Scenario.Count(ref s.FailedRef);
                await Task.Delay(Random.Shared.Next(200, 1500), ct);
                await InTenantAsync(s.TenantId, c => c.MarkPaidAsync(invoice.InvoiceId, ct)); // the customer tries another card
                Scenario.Count(ref s.PaidRef);
            }
            else if (roll < r.FailedPaymentRatio + r.PayRatio)
            {
                await InTenantAsync(s.TenantId, c => c.MarkPaidAsync(invoice.InvoiceId, ct));
                Scenario.Count(ref s.PaidRef);
            }
            else if (roll < r.FailedPaymentRatio + r.PayRatio + r.OverdueRatio)
            {
                await InTenantAsync(s.TenantId, c => c.MarkOverdueAsync(invoice.InvoiceId, ct));
                Scenario.Count(ref s.OverdueRef);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Scenario.Count(ref s.ErrorsRef);
            logger.LogWarning(e, "A simulated invoice failed in scenario {Scenario}", s.Id);
        }
    }

    /// <summary>Each command gets its own scope with the tenant set, exactly like an API request would.</summary>
    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IInvoiceCommands, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetTenant(tenantId);
        return await action(scope.ServiceProvider.GetRequiredService<IInvoiceCommands>());
    }
}
