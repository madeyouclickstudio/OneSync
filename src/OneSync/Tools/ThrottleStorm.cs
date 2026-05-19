using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Util;

namespace OneSync.Tools;

/// <summary>
/// Throttle-related verification scenarios. Driven by --throttle-storm
/// &lt;scenario&gt; on the OneSync.exe CLI. Scenarios:
///   - "burst" — TokenBucket burst test; verifies acquire timings are within
///     the theoretical envelope.
///   - "cooldown" — placeholder. Full automated cooldown verification needs
///     HttpMessageHandler injection into GraphHttpClient (see scenario body).
/// </summary>
internal static class ThrottleStorm
{
    public static async Task<int> RunAsync(string scenario, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var reportPath = Path.Combine(outputDir,
            $"throttle-storm-{DateTime.UtcNow:yyyy-MM-dd}.md");
        var sb = new StringBuilder();
        if (File.Exists(reportPath))
        {
            // Append rather than overwrite so multiple scenarios accumulate
            sb.Append(File.ReadAllText(reportPath));
        }
        else
        {
            sb.AppendLine("# ThrottleStorm results");
            sb.AppendLine();
            sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
            sb.AppendLine($"Machine: {Environment.MachineName}, .NET {Environment.Version}");
            sb.AppendLine();
        }

        int exitCode;
        switch (scenario.ToLowerInvariant())
        {
            case "burst":
                exitCode = await RunBurstAsync(sb);
                break;
            case "cooldown":
                exitCode = await RunCooldownAsync(sb);
                break;
            default:
                Console.Error.WriteLine($"Unknown scenario '{scenario}'. Valid: burst | cooldown");
                return 2;
        }

        File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine($"Report written to: {reportPath}");
        return exitCode;
    }

    private static Task<int> RunBurstAsync(StringBuilder sb)
    {
        Console.WriteLine("ThrottleStorm scenario: burst (TokenBucket capacity=10, rate=10/s, 50 acquires)");
        sb.AppendLine($"## Scenario: burst — {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Configuration: TokenBucket(capacity=10, refillPerSecond=10), burst 50 acquires.");
        sb.AppendLine();

        var bucket = new TokenBucket(capacity: 10, refillPerSecond: 10);
        var waitSamples = new List<long>(50);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var waited = bucket.AcquireAsync(default).GetAwaiter().GetResult();
            waitSamples.Add((long)waited.TotalMilliseconds);
        }
        sw.Stop();

        var totalMs = sw.ElapsedMilliseconds;
        var instantCount = waitSamples.FindAll(w => w == 0).Count;
        var throttledCount = waitSamples.Count - instantCount;
        var maxWait = waitSamples.Count > 0 ? waitSamples[^1] : 0;
        foreach (var w in waitSamples) if (w > maxWait) maxWait = w;

        // Theoretical: first 10 instant, remaining 40 at 10/s = 4000ms total.
        sb.AppendLine($"- Total elapsed: **{totalMs} ms** (theoretical ≈ 4000 ms)");
        sb.AppendLine($"- Instant acquires (bucket non-empty): {instantCount}");
        sb.AppendLine($"- Throttled acquires (bucket empty, waited): {throttledCount}");
        sb.AppendLine($"- Max single-acquire wait: {maxWait} ms");
        sb.AppendLine($"- Verdict: { (totalMs >= 3500 && totalMs <= 4500 ? "✅ PASS — within ±12.5% of theoretical" : "⚠️ INVESTIGATE — outside ±12.5%") }");
        sb.AppendLine();
        Console.WriteLine($"  total={totalMs}ms instant={instantCount} throttled={throttledCount} max_wait={maxWait}ms");
        return Task.FromResult(0);
    }

    private static async Task<int> RunCooldownAsync(StringBuilder sb)
    {
        Console.WriteLine("ThrottleStorm scenario: cooldown (HttpMessageHandler injection)");
        sb.AppendLine($"## Scenario: cooldown — {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Full automated cooldown verification needs HttpMessageHandler injection into");
        sb.AppendLine("GraphHttpClient so a fake handler can replay 429 responses. The constructor doesn't");
        sb.AppendLine("currently accept one; add an internal overload taking HttpMessageHandler to enable this.");
        sb.AppendLine();
        sb.AppendLine("Verdict: ⏳ NOT YET IMPLEMENTED.");
        await Task.CompletedTask;
        return 0;
    }
}
