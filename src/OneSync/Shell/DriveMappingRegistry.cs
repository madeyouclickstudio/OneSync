using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using OneSync.Config;
using Serilog;

namespace OneSync.Shell;

/// <summary>
/// Writes the per-user drive-letter -> local-NTFS-path mapping to HKCU so the
/// shell overlay DLL (loaded inside Explorer) can translate H:\foo into the
/// underlying NTFS path it needs to read the OneSync ADS from.
/// </summary>
internal static class DriveMappingRegistry
{
    private const string KeyPath = @"SOFTWARE\OneSync";
    private const string ValueName = "DriveMappings";

    public static void Publish(IEnumerable<DriveConfig> drives, ILogger logger)
    {
        try
        {
            var entries = drives
                .Where(d => !string.IsNullOrEmpty(d.Letter) && !string.IsNullOrEmpty(d.LocalRootPath))
                .Select(d => $"{d.Letter.ToUpperInvariant()}={d.LocalRootPath}");
            var value = string.Join(";", entries);

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                logger.Warning("Could not open HKCU\\{Path} for write", KeyPath);
                return;
            }
            key.SetValue(ValueName, value, RegistryValueKind.String);
            logger.Information("Published drive mappings to HKCU: {Value}", value);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "DriveMappingRegistry.Publish failed");
        }
    }

    public static void Clear(ILogger logger)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "DriveMappingRegistry.Clear failed");
        }
    }
}
