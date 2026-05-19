using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OneSync.Diagnostics;

/// <summary>
/// Writes a single self-contained text file when OneSync hits an unhandled
/// exception. Must be fast and resilient: Windows can kill the process within
/// seconds of an unhandled exception, so this method:
///   - is synchronous (no async/await)
///   - pre-builds the path
///   - swallows any internal failure (inner try/catch) — a crash-writer crash
///     would be undignified
///
/// File picked up by DiagnosticExporter, but useful even on its own —
/// post-crash IT can find C:\Users\...\AppData\Local\OneSync\Crashes\*.txt.
/// </summary>
internal static class CrashWriter
{
    public static readonly string CrashesDir = Environment.ExpandEnvironmentVariables(
        @"%LOCALAPPDATA%\OneSync\Crashes");

    public static void Write(Exception? ex, string source, string? logTail = null)
    {
        try
        {
            Directory.CreateDirectory(CrashesDir);
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
            var path = Path.Combine(CrashesDir, $"crash-{ts}-{Sanitize(source)}.txt");

            var sb = new StringBuilder(8192);
            sb.AppendLine($"OneSync crash report");
            sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine();

            sb.AppendLine("=== Process ===");
            try
            {
                using var proc = Process.GetCurrentProcess();
                sb.AppendLine($"PID:           {proc.Id}");
                sb.AppendLine($"Uptime:        {(DateTime.Now - proc.StartTime):c}");
                sb.AppendLine($"WorkingSet:    {proc.WorkingSet64 / 1024 / 1024} MB");
                sb.AppendLine($"GC TotalMem:   {GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024} MB");
                sb.AppendLine($"Threads:       {proc.Threads.Count}");
            }
            catch (Exception pex) { sb.AppendLine($"(process info unavailable: {pex.Message})"); }
            sb.AppendLine();

            sb.AppendLine("=== Exception ===");
            if (ex is null)
            {
                sb.AppendLine("(no exception object provided)");
            }
            else
            {
                AppendException(sb, ex, depth: 0);
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(logTail))
            {
                sb.AppendLine("=== Log tail (last 200 lines) ===");
                sb.AppendLine(logTail);
            }

            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // Last-resort swallow. A crash-writer crash must not propagate.
            try { Debug.WriteLine("CrashWriter.Write failed"); } catch { }
        }
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}Type:    {ex.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {ex.Message}");
        sb.AppendLine($"{indent}Source:  {ex.Source}");
        sb.AppendLine($"{indent}Stack:");
        sb.AppendLine(ex.StackTrace ?? "(no stack trace)");
        if (ex.InnerException is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}--- Inner exception ---");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    /// <summary>Try to read the tail of the current Serilog file. Best-effort.</summary>
    public static string? TryReadLogTail(string logDir, int lines = 200)
    {
        try
        {
            var today = $"onesync-{DateTime.Now:yyyyMMdd}.log";
            var path = Path.Combine(Environment.ExpandEnvironmentVariables(logDir), today);
            if (!File.Exists(path)) return null;

            // Open with FileShare.ReadWrite so Serilog's open handle doesn't block us.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd();
            var split = all.Split('\n');
            if (split.Length <= lines) return all;
            return string.Join('\n', split[^lines..]);
        }
        catch { return null; }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
