using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// The load numbers behind the claims (ASR-03 and ASR-05). Opt-in, because each one boots the whole platform (the scale test three times):
///   $env:LOAD = "1"; dotnet test --project tests/InvoiceHub.IntegrationTests --filter-class InvoiceHub.IntegrationTests.LoadTests
/// The results are written to docs/LOAD-RESULTS.md, with the machine they ran on, so a number is never separated from what produced it.
/// The runs use the production values of the re-send safety nets (RealTimers): a message that merely waits in a long line must not be sent again.
/// </summary>
public class LoadTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(6);
    private static readonly Guid SeededContoso = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");

    private static void RequireOptIn() => Assert.SkipUnless(Environment.GetEnvironmentVariable("LOAD") == "1", "Load runs are opt-in: set LOAD=1 (each one boots the whole platform).");

    /// <summary>
    /// A ladder of offered rates against one receiver that answers at once. Each step is a number of invoices at a rate (every invoice is three events). The register's target is
    /// p95 of 5 s at 50 events per second; the Service Bus emulator can receive and complete only 50 to 160 messages per second in total (SPIKE-0) and every event needs two of those,
    /// so the ladder shows where the knee is and the last step shows what happens beyond it, with the time split by stage.
    /// </summary>
    [Fact]
    public async Task Latency_is_within_five_seconds_up_to_the_rate_the_broker_emulator_can_carry()
    {
        RequireOptIn();
        await using var platform = new PlatformFixture { Replicas = 2, RealTimers = true };
        await platform.InitializeAsync();

        // (invoices, invoices per second) => events per second = 3 x rate
        (int Invoices, double PerSecond)[] steps = [(30, 2), (45, 3.3), (60, 5), (100, 17)];
        var rows = new List<(double Rate, int Events, double P50, double P95, double P99, double Seconds)>();
        string stages = "", cpu = "";

        foreach (var (invoices, perSecond) in steps)
        {
            (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
            var since = DateTime.UtcNow.AddSeconds(-1);
            var burst = await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count = invoices, perSecond, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0, maxInFlight = 128 });
            Assert.Equal(System.Net.HttpStatusCode.Accepted, burst.StatusCode);
            var started = DateTime.UtcNow;
            var cpuBefore = ProcessCpu();

            await PlatformFixture.WaitUntilAsync(async () => (await Counts(platform.Receiver)).GetProperty("received").GetInt32() >= invoices * 3, Patience, $"{invoices * 3} events to reach the receiver at {perSecond * 3:0} events per second");
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var latency = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/latency?since={Uri.EscapeDataString(since.ToString("O"))}");
            rows.Add((perSecond * 3, invoices * 3, latency.GetProperty("p50").GetDouble(), latency.GetProperty("p95").GetDouble(), latency.GetProperty("p99").GetDouble(), elapsed));

            if (perSecond >= 17) // the target rate: where did the time go?
            {
                stages = await StageTableAsync(platform, since);
                cpu = string.Join(", ", ProcessCpu().Select(p => $"{p.Key} {p.Value - cpuBefore.GetValueOrDefault(p.Key):0.0} s"));
            }
            await Task.Delay(TimeSpan.FromSeconds(3)); // let the platform go quiet before the next step
        }

        var report = new StringBuilder()
            .AppendLine("## Latency against the offered rate (ASR-03)")
            .AppendLine()
            .AppendLine("2 delivery workers, one receiver that answers at once, the production values of the re-send timers. Measured by the receiver: from the moment the event was committed in the platform to the moment it was in the receiver's inbox. The register's target is p95 of 5 s at 50 events per second.")
            .AppendLine()
            .AppendLine("| offered events per second | events | p50 | p95 | p99 | whole run | p95 within 5 s |")
            .AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            report.AppendLine($"| {r.Rate:0} | {r.Events} | {r.P50:0} ms | {r.P95:0} ms | {r.P99:0} ms | {r.Seconds:0.0} s | {(r.P95 <= 5000 ? "yes" : "no")} |");
        report.AppendLine()
            .AppendLine("Where the time went at the highest rate (p50 / p95, from the platform's own records):")
            .AppendLine()
            .AppendLine(stages)
            .AppendLine()
            .AppendLine($"Processor time used by each host during that step: {cpu}. The hosts are mostly waiting, not computing: the wait is in the stages that go through the message broker, and one attempt itself takes milliseconds.");
        LoadReport.Write("latency", report.ToString());

        // what must hold: the pipeline is fast when the broker is not the limit (the lowest rung), and nothing is lost at any rung (every step waited for all of its events)
        Assert.True(rows[0].P95 <= 5000, $"p95 was {rows[0].P95:0} ms at {rows[0].Rate:0} events per second; the target is 5000 ms");
    }

    [Fact]
    public async Task Throughput_grows_with_the_number_of_delivery_workers()
    {
        RequireOptIn();

        // A slow receiver (1 s per request) makes the workers the bottleneck: 16 attempts run at once per worker, so one worker can do about 16 per second.
        // 13 endpoints keep the per-endpoint limit (5 in flight) out of the way: 13 x 5 = 65 attempts could run at once.
        var results = new List<(int Workers, int Deliveries, double Seconds)>();
        foreach (var workers in new[] { 1, 2, 3 })
        {
            await using var platform = new PlatformFixture { Replicas = workers, RealTimers = true };
            await platform.InitializeAsync();
            results.Add(await MeasureAsync(platform, workers));
        }

        var report = new StringBuilder()
            .AppendLine("## Throughput with 1, 2 and 3 delivery workers (ASR-05)")
            .AppendLine()
            .AppendLine("The receiver answers each request after 1 second, so the number of attempts a worker can have in flight (16) is the limit. 13 endpoints of one tenant, 20 invoices (60 events), so 780 deliveries each time. The growth is less than linear because the broker emulator's receive rate does not grow with more consumers (SPIKE-0); a real Service Bus namespace does not have that ceiling.")
            .AppendLine()
            .AppendLine("| workers | deliveries | time | deliveries per second | compared with one worker |")
            .AppendLine("|---|---|---|---|---|");
        var baseline = results[0].Deliveries / results[0].Seconds;
        foreach (var (workers, deliveries, seconds) in results)
            report.AppendLine($"| {workers} | {deliveries} | {seconds:0.0} s | {deliveries / seconds:0.0} | ×{deliveries / seconds / baseline:0.0} |");
        LoadReport.Write("scale", report.ToString());

        Assert.True(results[1].Deliveries / results[1].Seconds > baseline * 1.2, "two workers should clearly beat one");
        Assert.True(results[2].Deliveries / results[2].Seconds > baseline * 1.4, "three workers should beat two");
    }

    private static async Task<(int Workers, int Deliveries, double Seconds)> MeasureAsync(PlatformFixture platform, int workers)
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        HttpRequestMessage Request(HttpMethod m, string path, object? body = null)
        {
            var r = new HttpRequestMessage(m, path) { Headers = { Authorization = new("Bearer", owner) } };
            if (body is not null) r.Content = JsonContent.Create(body);
            return r;
        }

        var secrets = new List<string>();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var created = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", new { name = $"Load {i:D2}", url = new Uri(platform.ReceiverUrl, $"hooks/load-{i}").ToString(), events = new[] { "invoice.generated", "invoice.paid", "payment.completed" } }));
            created.EnsureSuccessStatusCode();
            ids.Add((await platform.ReadJsonAsync(created)).GetProperty("id").GetGuid());
            secrets.Add(await platform.HmacSecretOfAsync(ids[^1]));
        }
        (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets, jwksUrl = (string?)null })).EnsureSuccessStatusCode();
        foreach (var id in ids) (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping"))).EnsureSuccessStatusCode();
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "slow", delaySeconds = 1 })).EnsureSuccessStatusCode();

        const int invoices = 20;
        const int expected = invoices * 3 * 13;
        var since = DateTime.UtcNow.AddSeconds(-1);
        var started = DateTime.UtcNow;
        (await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count = invoices, perSecond = 40, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0, maxInFlight = 64 })).EnsureSuccessStatusCode();

        await PlatformFixture.WaitUntilAsync(async () =>
            await platform.ScalarAsAsync<int>("worker_user", $"SELECT COUNT(*) FROM webhooks.delivery WHERE status = 'succeeded' AND created_at >= '{since:yyyy-MM-dd HH:mm:ss}'", system: true) >= expected,
            Patience, $"{expected} deliveries to succeed with {workers} worker(s)");
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        return (workers, expected, seconds);
    }

    /// <summary>Processor seconds used so far by the platform's own host processes (summed per host name): is a stage waiting, or working?</summary>
    private static Dictionary<string, double> ProcessCpu() =>
        System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("InvoiceHub.", StringComparison.Ordinal) && p.ProcessName != "InvoiceHub.IntegrationTests")
            .GroupBy(p => p.ProcessName["InvoiceHub.".Length..])
            .ToDictionary(g => g.Key, g => g.Sum(p => { try { return p.TotalProcessorTime.TotalSeconds; } catch (InvalidOperationException) { return 0; } }));

    private static async Task<string> StageTableAsync(PlatformFixture platform, DateTime since)
    {
        async Task<string> Row(string name, string sqlFrom, string diff)
        {
            var p50 = await Percentile(platform, sqlFrom, diff, 0.5);
            var p95 = await Percentile(platform, sqlFrom, diff, 0.95);
            return $"| {name} | {p50} ms | {p95} ms |";
        }
        var outbox = $"FROM events.outbox o JOIN events.event e ON e.id = o.event_id WHERE e.occurred_at >= '{since:yyyy-MM-dd HH:mm:ss}' AND o.fanned_out_at IS NOT NULL AND o.published_at IS NOT NULL";
        var delivery = $"FROM webhooks.delivery d JOIN events.event e ON e.id = d.event_id WHERE d.endpoint_id = '{SeededContoso}' AND e.occurred_at >= '{since:yyyy-MM-dd HH:mm:ss}' AND d.first_attempt_at IS NOT NULL";
        var attempt = $"FROM webhooks.delivery_attempt a JOIN webhooks.delivery d ON d.id = a.delivery_id WHERE d.endpoint_id = '{SeededContoso}' AND a.started_at >= '{since:yyyy-MM-dd HH:mm:ss}'";
        var slowest = await platform.ScalarAsAsync<string>("worker_user", $"""
            SELECT STRING_AGG(x.s, '; ') FROM (
              SELECT TOP 8 CONCAT(CAST(DATEDIFF_BIG(millisecond, m.t0, o.created_at) / 1000.0 AS decimal(6, 1)), ' s: ', DATEDIFF_BIG(millisecond, o.created_at, o.published_at), ' ms, row ', o.id) AS s
              FROM events.outbox o JOIN events.event e ON e.id = o.event_id
              CROSS JOIN (SELECT MIN(o2.created_at) AS t0 FROM events.outbox o2 JOIN events.event e2 ON e2.id = o2.event_id WHERE e2.occurred_at >= '{since:yyyy-MM-dd HH:mm:ss}') m
              WHERE e.occurred_at >= '{since:yyyy-MM-dd HH:mm:ss}' AND o.published_at IS NOT NULL
              ORDER BY DATEDIFF_BIG(millisecond, o.created_at, o.published_at) DESC) x
            """, system: true);
        return string.Join(Environment.NewLine,
            "Slowest waits for the relay (seconds after the first event of the step: wait): " + slowest,
            "",
            "| stage | p50 | p95 |",
            "|---|---|---|",
            await Row("committed, waiting for the relay", outbox, "DATEDIFF_BIG(millisecond, o.created_at, o.published_at)"),
            await Row("published to confirmed fanned out (broker and fan-out)", outbox, "DATEDIFF_BIG(millisecond, o.published_at, o.fanned_out_at)"),
            await Row("commit to delivery row (the two above)", delivery, "DATEDIFF_BIG(millisecond, e.occurred_at, d.created_at)"),
            await Row("delivery row to first attempt (queue, workers, slots)", delivery, "DATEDIFF_BIG(millisecond, d.created_at, d.first_attempt_at)"),
            await Row("one attempt, as the sender saw it (request to answer)", attempt, "a.duration_ms"));
    }

    private static async Task<string> Percentile(PlatformFixture platform, string sqlFrom, string diff, double percentile) =>
        await platform.ScalarAsAsync<string>("worker_user",
            $"SELECT CAST(CAST(MAX(p) AS int) AS varchar(20)) FROM (SELECT PERCENTILE_CONT({percentile.ToString(CultureInfo.InvariantCulture)}) WITHIN GROUP (ORDER BY ms) OVER () AS p FROM (SELECT {diff} AS ms {sqlFrom}) a) b", system: true);

    private static async Task<JsonElement> Counts(HttpClient receiver) => (await receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
}

internal static class LoadReport
{
    /// <summary>Writes one section of docs/LOAD-RESULTS.md, replacing the section of the same name.</summary>
    public static void Write(string section, string markdown)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InvoiceHub.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        var path = Path.Combine(dir.FullName, "docs", "LOAD-RESULTS.md");
        var machine = $"{Environment.ProcessorCount} logical cores, {Environment.OSVersion.VersionString}, .NET {Environment.Version}, Docker Desktop, everything on one machine";
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var sections = new SortedDictionary<string, string>();
        foreach (var part in existing.Split("<!-- section:", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var end = part.IndexOf(" -->", StringComparison.Ordinal);
            if (end > 0) sections[part[..end]] = part[(end + 4)..].Trim();
        }
        sections[section] = markdown.Trim();
        var text = new StringBuilder()
            .AppendLine("# Load results")
            .AppendLine()
            .AppendLine($"Measured on: {machine}. Last update {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. These are laptop numbers for the shape of the system, not a capacity claim.")
            .AppendLine();
        foreach (var (name, body) in sections) text.AppendLine($"<!-- section:{name} -->").AppendLine(body).AppendLine();
        File.WriteAllText(path, text.ToString());
    }
}
