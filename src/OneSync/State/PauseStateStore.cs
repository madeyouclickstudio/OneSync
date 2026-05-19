using System;
using Microsoft.Win32;
using Serilog;

namespace OneSync.State;

/// <summary>
/// Single source of truth for OneSync's paused state. Backed by HKCU so the
/// state survives process restart (a user who paused for an hour then closed
/// OneSync expects the pause to still be in effect when they reopen it).
///
/// Subsystems that should be silenced when paused (DeltaPoller, UploadWorker,
/// the cooperative cache-write step) poll <see cref="IsPaused"/> at tick
/// boundaries — no callbacks, no event subscription. The hot path costs one
/// volatile read of a private timestamp; HKCU is only re-read once per tray
/// refresh tick (3s) by <see cref="ReloadFromRegistry"/>.
/// </summary>
internal sealed class PauseStateStore
{
    private const string SubKey = @"Software\OneSync\PauseState";
    private const string ValueName = "PausedUntilUtc";
    /// <summary>Sentinel for the indefinite-pause case (no auto-resume timestamp).</summary>
    public static readonly DateTime IndefiniteSentinel = DateTime.MaxValue;

    private readonly ILogger _logger;
    private DateTime _pausedUntilUtc;  // DateTime.MinValue == not paused; MaxValue == indefinite

    public event Action? PauseStateChanged;

    public PauseStateStore(ILogger logger)
    {
        _logger = logger.ForContext("Component", "PauseStateStore");
        ReloadFromRegistry();
    }

    /// <summary>True if currently paused. Volatile read — call from hot paths.</summary>
    public bool IsPaused()
    {
        var until = _pausedUntilUtc;
        if (until == DateTime.MinValue) return false;
        if (until == IndefiniteSentinel) return true;
        return DateTime.UtcNow < until;
    }

    /// <summary>Returns the auto-resume timestamp, or null if not paused or paused indefinitely.</summary>
    public DateTime? PausedUntilUtc()
    {
        var until = _pausedUntilUtc;
        if (until == DateTime.MinValue || until == IndefiniteSentinel) return null;
        return until;
    }

    /// <summary>True if paused with no auto-resume.</summary>
    public bool IsIndefinitelyPaused() => _pausedUntilUtc == IndefiniteSentinel;

    public void PauseUntil(DateTime untilUtc)
    {
        _pausedUntilUtc = untilUtc;
        WriteToRegistry(untilUtc);
        _logger.Information("Sync paused until {Until:O} UTC", untilUtc);
        try { PauseStateChanged?.Invoke(); } catch { /* swallow handler errors */ }
    }

    public void PauseIndefinitely()
    {
        _pausedUntilUtc = IndefiniteSentinel;
        WriteToRegistry(IndefiniteSentinel);
        _logger.Information("Sync paused indefinitely");
        try { PauseStateChanged?.Invoke(); } catch { }
    }

    public void Resume()
    {
        _pausedUntilUtc = DateTime.MinValue;
        WriteToRegistry(DateTime.MinValue);
        _logger.Information("Sync resumed");
        try { PauseStateChanged?.Invoke(); } catch { }
    }

    /// <summary>Re-read the registry. Called once per tray refresh tick so an
    /// external write (e.g. from a sister tool) is picked up. Also handles
    /// auto-resume: if the stored timestamp is in the past, clear it.</summary>
    public void ReloadFromRegistry()
    {
        var loaded = ReadFromRegistry();
        if (loaded != DateTime.MinValue
            && loaded != IndefiniteSentinel
            && DateTime.UtcNow >= loaded)
        {
            // Auto-resume — past timestamp means the pause window ended.
            _logger.Information("Auto-resuming sync (pause window ended at {Until:O})", loaded);
            _pausedUntilUtc = DateTime.MinValue;
            WriteToRegistry(DateTime.MinValue);
            try { PauseStateChanged?.Invoke(); } catch { }
            return;
        }

        var prior = _pausedUntilUtc;
        _pausedUntilUtc = loaded;
        if (prior != loaded)
        {
            try { PauseStateChanged?.Invoke(); } catch { }
        }
    }

    private DateTime ReadFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: false);
            if (key is null) return DateTime.MinValue;
            var raw = key.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(raw)) return DateTime.MinValue;
            if (raw == "indefinite") return IndefiniteSentinel;
            if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read pause state from registry — defaulting to not paused");
            return DateTime.MinValue;
        }
    }

    private void WriteToRegistry(DateTime untilUtc)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
            if (untilUtc == DateTime.MinValue)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else if (untilUtc == IndefiniteSentinel)
            {
                key.SetValue(ValueName, "indefinite", RegistryValueKind.String);
            }
            else
            {
                key.SetValue(ValueName, untilUtc.ToString("O"), RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write pause state to registry — pause not persisted across restart");
        }
    }
}
