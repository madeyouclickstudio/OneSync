using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneSync.Sync;
using OneSync.Sync.Cooperative;

namespace OneSync.Tools;

/// <summary>
/// Synthetic-payload size + serialisation-time probe for DeltaCachePayload.
/// Driven by --cache-size-probe N1,N2,N3 on the OneSync.exe CLI.
///
/// Quantifies where the cooperative-polling cache approach hits its size
/// ceiling for a given machine and payload shape. Useful before enabling
/// cooperative polling against a very large SharePoint library.
///
/// Folder/file ratio is modelled at ~73% folders / ~27% files for libraries
/// under ~70k items, shifting toward 80/20 above that (large document
/// libraries tend to be folder-heavy in practice).
///
/// Each N is measured twice — full cache (maxAgeDays=0) and sliding window
/// (maxAgeDays=30) — via DeltaCache.BuildPayload, the same code path
/// production uses, so the numbers reflect the real cost the leader and
/// readers pay.
/// </summary>
internal static class CacheSizeProbe
{
    private const int SlidingWindowDays = 30;

    /// <summary>Run the probe for each N in counts, write a markdown report,
    /// return the report path. Exits the process after writing.</summary>
    public static string Run(IEnumerable<int> counts, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var reportPath = Path.Combine(outputDir,
            $"cache-size-{DateTime.UtcNow:yyyy-MM-dd}.md");

        var sb = new StringBuilder();
        sb.AppendLine("# CacheSizeProbe results");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"Machine: {Environment.MachineName}, .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("Synthetic `RemoteItem` list fed through `DeltaCache.BuildPayload` (the same code");
        sb.AppendLine("path production uses) and serialised with `JsonSerializer.Serialize` +");
        sb.AppendLine("`DefaultIgnoreCondition = WhenWritingNull` to mirror `DeltaCache.WriteIfChangedAsync`.");
        sb.AppendLine();
        sb.AppendLine($"Each N is measured TWICE: full cache (maxAgeDays=0) AND sliding-window cache");
        sb.AppendLine($"(maxAgeDays={SlidingWindowDays}). The sliding-window row models actual production behaviour");
        sb.AppendLine($"per the cooperative-polling design (folders always kept, files only if modified");
        sb.AppendLine($"within the window).");
        sb.AppendLine();
        sb.AppendLine("Spec exit criteria for the cooperative-polling cache:");
        sb.AppendLine("- Serialised size at 351k items should be ≤ 50 MB.");
        sb.AppendLine("- Write time at 351k items should be ≤ 250 ms.");
        sb.AppendLine("- If exceeded: ship a streaming/sliding-window variant.");
        sb.AppendLine();
        sb.AppendLine("Note: synthetic file `LastModified` is uniform across last 365 days, so the");
        sb.AppendLine($"sliding-window filter (which drops files older than {SlidingWindowDays} days but keeps every folder)");
        sb.AppendLine($"retains roughly {SlidingWindowDays}/365 ≈ 8% of files. Real-world libraries skew newer, so production");
        sb.AppendLine("sliding-window size is likely between the two columns below — closer to the");
        sb.AppendLine("sliding-window number for cold libraries, closer to the full-cache number for");
        sb.AppendLine("actively-edited libraries.");
        sb.AppendLine();
        sb.AppendLine("| Items | Folders | Files | Full KB | Sliding KB | Sliding items | Full write (ms) | Sliding write (ms) |");
        sb.AppendLine("|------:|--------:|------:|--------:|-----------:|--------------:|----------------:|-------------------:|");

        // Match production's JsonSerializer options exactly (DeltaCache.WriteIfChangedAsync line 95-99)
        var prodJsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        foreach (var n in counts)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var (folderCount, fileCount) = RatioFor(n);
            var remoteItems = BuildSyntheticRemoteItems(folderCount, fileCount, driveConfigId: "probe");

            // FULL CACHE (maxAgeDays=0) — every item included
            var fullPayload = DeltaCache.BuildPayload(
                "synthetic-user", "probe@local", Environment.MachineName, "probe",
                deltaToken: "synthetic-token",
                items: remoteItems,
                maxAgeDays: 0);
            var fullSw = Stopwatch.StartNew();
            var fullBytes = JsonSerializer.SerializeToUtf8Bytes(fullPayload, prodJsonOpts);
            fullSw.Stop();

            // SLIDING WINDOW (maxAgeDays=30) — files filtered, folders kept
            var slidingPayload = DeltaCache.BuildPayload(
                "synthetic-user", "probe@local", Environment.MachineName, "probe",
                deltaToken: "synthetic-token",
                items: remoteItems,
                maxAgeDays: SlidingWindowDays);
            var slidingSw = Stopwatch.StartNew();
            var slidingBytes = JsonSerializer.SerializeToUtf8Bytes(slidingPayload, prodJsonOpts);
            slidingSw.Stop();

            sb.AppendLine($"| {n,6} | {folderCount,7} | {fileCount,5} | {fullBytes.Length / 1024.0,7:F1} | {slidingBytes.Length / 1024.0,10:F1} | {slidingPayload.Items.Count,13} | {fullSw.ElapsedMilliseconds,15} | {slidingSw.ElapsedMilliseconds,18} |");
            Console.WriteLine($"  N={n}: full {fullBytes.Length / 1024.0:F1} KB ({fullSw.ElapsedMilliseconds}ms), sliding {slidingBytes.Length / 1024.0:F1} KB ({slidingSw.ElapsedMilliseconds}ms, {slidingPayload.Items.Count} items)");

            remoteItems = null!;
            fullPayload = null!;
            slidingPayload = null!;
            fullBytes = null!;
            slidingBytes = null!;
        }

        sb.AppendLine();
        sb.AppendLine("## Verdict");
        sb.AppendLine();
        sb.AppendLine("Fill in by hand after inspecting the table above. Spec gate at 351k items:");
        sb.AppendLine("- **Sliding KB ≤ 51200 (50 MB)** AND **Sliding write ≤ 250 ms** ⇒ PASS, production design holds.");
        sb.AppendLine("- Full KB column is informational — production never serialises the full cache when `maxAgeDays > 0`.");
        sb.AppendLine();
        sb.AppendLine("If only the FULL column fails: the spec is satisfied by keeping `cacheItemMaxAgeDays = 30` (current default).");
        sb.AppendLine("If SLIDING also fails: ship a smaller window (e.g. 14 days) or move to delta-encoded cache format.");

        File.WriteAllText(reportPath, sb.ToString());
        return reportPath;
    }

    private static (int folders, int files) RatioFor(int total)
    {
        // Observed at ~67k items: 66023/806 = 98.8% folders. That's extreme.
        // At smaller N a more typical 70/30 looks right. At very large N stay
        // folder-heavy to model HR-style libraries.
        double folderFraction = total switch
        {
            < 10_000 => 0.70,
            < 100_000 => 0.80,
            _ => 0.90,
        };
        int folders = (int)(total * folderFraction);
        int files = total - folders;
        return (folders, files);
    }

    private static List<RemoteItem> BuildSyntheticRemoteItems(int folderCount, int fileCount, string driveConfigId)
    {
        var items = new List<RemoteItem>(folderCount + fileCount);
        var rng = new Random(42);  // deterministic

        // Folders — 1..4 levels deep
        for (int i = 0; i < folderCount; i++)
        {
            var depth = 1 + (i % 4);
            var pathParts = new string[depth];
            for (int d = 0; d < depth; d++)
                pathParts[d] = $"Folder_{rng.Next(100_000):D6}";
            var relPath = "/" + string.Join('/', pathParts);
            var remoteId = Guid.NewGuid().ToString("N");
            items.Add(new RemoteItem
            {
                Key = RemoteItem.MakeKey(driveConfigId, relPath),
                DriveConfigId = driveConfigId,
                RemoteItemId = remoteId,
                ParentRemoteItemId = depth > 1 ? Guid.NewGuid().ToString("N") : string.Empty,
                RelativePath = relPath,
                Name = pathParts[^1],
                IsFolder = true,
                Size = 0,
                ETag = $"\"{Guid.NewGuid():N}\"",
                WebUrl = $"https://contoso.sharepoint.com/sites/test/Shared%20Documents/{string.Join('/', pathParts)}",
                // Folders are kept by the sliding window regardless of LastModified;
                // distribute uniformly anyway since BuildPayload still includes the
                // field in the serialised output.
                LastModifiedDateTime = DateTime.UtcNow.AddDays(-rng.Next(365)),
            });
        }

        // Files — log-distributed sizes, uniform last-modified across 365 days
        for (int i = 0; i < fileCount; i++)
        {
            var sizeKB = (long)Math.Pow(2, rng.NextDouble() * 15);  // ~1 KB..32 MB
            var ext = (rng.Next(4)) switch { 0 => "docx", 1 => "xlsx", 2 => "pdf", _ => "pptx" };
            var name = $"Doc_{rng.Next(1_000_000):D7}.{ext}";
            var relPath = $"/Folder_{rng.Next(100_000):D6}/{name}";
            items.Add(new RemoteItem
            {
                Key = RemoteItem.MakeKey(driveConfigId, relPath),
                DriveConfigId = driveConfigId,
                RemoteItemId = Guid.NewGuid().ToString("N"),
                ParentRemoteItemId = Guid.NewGuid().ToString("N"),
                RelativePath = relPath,
                Name = name,
                IsFolder = false,
                Size = sizeKB * 1024,
                ETag = $"\"{Guid.NewGuid():N}\"",
                WebUrl = $"https://contoso.sharepoint.com/sites/test/Shared%20Documents/Folder/{name}",
                LastModifiedDateTime = DateTime.UtcNow.AddDays(-rng.Next(365)),
            });
        }

        return items;
    }
}
