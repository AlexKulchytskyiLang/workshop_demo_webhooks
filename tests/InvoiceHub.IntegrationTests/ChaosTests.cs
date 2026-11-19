using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// Things going wrong for real: a worker process is killed in the middle of a run, the message broker is restarted (and forgets everything it held).
/// The promise under test is the platform's central one: every business event reaches the receiver, and a retried or duplicated message has no second effect.
/// </summary>
[Collection(PlatformCollection.Name)]
public class ChaosTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(4);

    public async ValueTask InitializeAsync() => await QuiesceAsync();

    public async ValueTask DisposeAsync() => await QuiesceAsync();

    [Fact]
    public async Task Killing_a_delivery_worker_in_the_middle_of_a_run_loses_nothing()
    {
        var workers = Process.GetProcessesByName("InvoiceHub.Delivery");
        Assert.True(workers.Length >= 2, $"expected two delivery replicas, found {workers.Length}");

        const int invoices = 40; // 40 x (generated + payment + paid) = 120 events
        var started = DateTime.UtcNow.AddSeconds(-1);
        await SimulateAsync(invoices, perSecond: 8);
        await Task.Delay(TimeSpan.FromSeconds(2)); // mid-run: events are being relayed, fanned out and delivered right now

        workers[0].Kill(entireProcessTree: true); // no graceful shutdown: the process just disappears, with whatever it was doing

        await WaitForReceivedAsync(invoices * 3);

        // every event arrived exactly once as far as effects go: 120 distinct inbox rows, every invoice converged on "paid"
        var stats = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
        Assert.Equal(invoices * 3, stats.GetProperty("received").GetInt32());
        Assert.Equal(0, stats.GetProperty("dead").GetInt32());
        var all = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/invoices?limit={invoices + 10}");
        Assert.Equal(invoices, all.GetArrayLength());
        Assert.All(all.EnumerateArray(), i => Assert.Equal("paid", i.GetProperty("status").GetString()));

        // and the platform's books: nothing dead, nothing stuck
        await SettleAsync();
        // (an endpoint a test deleted has deliveries that end dead on purpose, and earlier tests closed their own leftovers: they are not "lost". Only what THIS run created counts)
        Assert.Equal(0, await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery d JOIN webhooks.endpoint e ON e.id = d.endpoint_id WHERE d.status IN ('dead', 'held') AND e.deleted_at IS NULL AND d.created_at >= '{started:yyyy-MM-dd HH:mm:ss}'"));
    }

    [Fact]
    public async Task Restarting_the_message_broker_in_the_middle_of_a_run_loses_nothing()
    {
        // the emulator keeps nothing: everything it held (queued events, scheduled retries) is gone after the restart.
        // The outbox republishes events that fan-out never confirmed and the sweeper re-queues deliveries that have no live message.
        var container = await FindBrokerContainerAsync();

        const int invoices = 30; // 90 events
        await SimulateAsync(invoices, perSecond: 6);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await DockerAsync($"restart --time 1 {container}"); // hard restart

        await WaitForReceivedAsync(invoices * 3);

        var stats = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
        Assert.Equal(invoices * 3, stats.GetProperty("received").GetInt32());
        var all = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/invoices?limit={invoices + 10}");
        Assert.Equal(invoices, all.GetArrayLength());
        Assert.All(all.EnumerateArray(), i => Assert.Equal("paid", i.GetProperty("status").GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private async Task SimulateAsync(int count, double perSecond)
    {
        var response = await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count, perSecond, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task WaitForReceivedAsync(int received) =>
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
            return counts.GetProperty("received").GetInt32() >= received && counts.GetProperty("waiting").GetInt32() == 0;
        }, Patience, $"{received} events to reach the receiver");

    private async Task SettleAsync() =>
        await PlatformFixture.WaitUntilAsync(async () =>
            await SqlAsync<int>("SELECT COUNT(*) FROM webhooks.delivery WHERE status IN ('pending', 'in_flight', 'retry_scheduled')") == 0
            && await SqlAsync<int>("SELECT COUNT(*) FROM events.outbox WHERE published_at IS NULL OR fanned_out_at IS NULL") == 0,
            Patience, "all deliveries to settle");

    private async Task QuiesceAsync()
    {
        (await platform.Simulator.PostAsync("/simulator/stop-all", null)).EnsureSuccessStatusCode();
        await SettleAsync();
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
    }

    private Task<T> SqlAsync<T>(string query) => platform.ScalarAsAsync<T>("worker_user", query, system: true);

    private static async Task<string> FindBrokerContainerAsync()
    {
        var names = (await DockerAsync("ps --filter ancestor=mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0 --format {{.Names}}")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(names);
        return names[0];
    }

    private static async Task<string> DockerAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"docker {arguments} failed: {await process.StandardError.ReadToEndAsync()}");
        return output;
    }
}
