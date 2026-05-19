using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace OneSync.Util;

internal static class LoggerFactory
{
    public static ILogger Create(string logDirectory, string levelString)
    {
        Directory.CreateDirectory(logDirectory);

        var level = ParseLevel(levelString);
        var logPath = Path.Combine(logDirectory, "onesync-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("App", "OneSync")
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {Pid}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static LogEventLevel ParseLevel(string level) => level?.Trim().ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "info" or "information" => LogEventLevel.Information,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
