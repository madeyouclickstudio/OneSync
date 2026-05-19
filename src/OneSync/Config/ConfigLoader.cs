using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OneSync.Util;

namespace OneSync.Config;

internal static class ConfigLoader
{
    public static AppConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found at: {path}", path);

        var json = File.ReadAllText(path);
        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"config.json is not valid JSON: {ex.Message}", ex);
        }

        if (config is null)
            throw new InvalidDataException("config.json deserialised to null");

        ExpandEnvironmentVariables(config);
        Validate(config);

        return config;
    }

    public static void ExpandEnvironmentVariables(AppConfig config)
    {
        config.LocalStorageRoot = PathUtil.Expand(config.LocalStorageRoot);
        config.Logging.Path = PathUtil.Expand(config.Logging.Path);
        config.Authority = PathUtil.Expand(config.Authority);

        foreach (var drive in config.Drives)
        {
            drive.LocalRootPath = ComputeLocalRootPath(config.LocalStorageRoot, drive);
        }
    }

    private static string ComputeLocalRootPath(string storageRoot, DriveConfig drive)
    {
        var name = drive.Label.Replace(' ', '_');
        if (string.IsNullOrWhiteSpace(name)) name = $"Drive_{drive.Letter}";
        return Path.Combine(storageRoot, name);
    }

    public static void Validate(AppConfig config)
    {
        var errors = new List<string>();

        if (!IsConfiguredGuid(config.TenantId))
            errors.Add("tenantId is not set — replace the placeholder in config.json with your Entra ID tenant GUID (Entra admin centre → Overview → Tenant ID)");

        if (!IsConfiguredGuid(config.ClientId))
            errors.Add("clientId is not set — replace the placeholder in config.json with the Application (client) ID of your Entra app registration");

        if (string.IsNullOrWhiteSpace(config.Authority))
            config.Authority = $"https://login.microsoftonline.com/{config.TenantId}";

        if (config.Drives.Count == 0)
            errors.Add("config.drives is empty - at least one drive required");

        var seenLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in config.Drives)
        {
            if (string.IsNullOrWhiteSpace(d.Letter) || d.Letter.Length != 1 ||
                !char.IsLetter(d.Letter[0]))
            {
                errors.Add($"Invalid drive letter '{d.Letter}' (must be A-Z)");
                continue;
            }
            d.Letter = d.Letter.ToUpperInvariant();
            if (!seenLetters.Add(d.Letter))
                errors.Add($"Duplicate drive letter '{d.Letter}'");

            if (d.IsSharePoint)
            {
                if (string.IsNullOrWhiteSpace(d.SiteUrl))
                    errors.Add($"Drive {d.Letter}: siteUrl required for SharePoint drives");
                if (string.IsNullOrWhiteSpace(d.LibraryName))
                    errors.Add($"Drive {d.Letter}: libraryName required for SharePoint drives");
            }
            else if (!d.IsOneDrive)
            {
                errors.Add($"Drive {d.Letter}: type must be 'onedrive' or 'sharepoint', got '{d.Type}'");
            }
        }

        if (errors.Count > 0)
            throw new InvalidDataException("config.json validation failed:\n  - " + string.Join("\n  - ", errors));
    }

    private static bool IsConfiguredGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Equals("YOUR-AZURE-TENANT-ID", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Equals("YOUR-APP-CLIENT-ID", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Guid.TryParse(value, out var g)) return false;
        return g != Guid.Empty;
    }

    /// <summary>Returns the path the MSI installs config.json to (next to OneSync.exe).
    /// Returned even if the file doesn't exist, so callers can show it in error UI.</summary>
    public static string ExpectedConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig LoadFromDefaultLocations(out string sourcePath)
    {
        foreach (var candidate in DefaultPaths())
        {
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                return LoadFromFile(candidate);
            }
        }
        sourcePath = DefaultPaths().First();
        throw new FileNotFoundException(
            $"config.json not found. Looked in:\n  - {string.Join("\n  - ", DefaultPaths())}");
    }

    /// <summary>
    /// Returns the first existing config.json path from the default search list,
    /// or null if none exist. Unlike LoadFromDefaultLocations, this never throws
    /// and never validates the contents — it's a path lookup only. Used by the
    /// first-run setup wizard to find a template to substitute values into.
    /// </summary>
    public static string? FindAnyConfigPath()
    {
        foreach (var candidate in DefaultPaths())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// Heuristic that detects the "placeholder tenantId/clientId" validation
    /// failure thrown by Validate(). The setup wizard uses this to decide
    /// whether to offer a first-run config experience or surface the raw error.
    /// </summary>
    public static bool LooksLikePlaceholderError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("tenantId is not set", StringComparison.Ordinal)
            || msg.Contains("clientId is not set", StringComparison.Ordinal);
    }

    private static IEnumerable<string> DefaultPaths()
    {
        // Per-user override — writable without admin rights, where the
        // first-run setup wizard saves its output. Users can customise here
        // and their choices win over machine-wide and installer defaults.
        yield return PathUtil.Expand(@"%LOCALAPPDATA%\OneSync\config.json");

        // Machine-wide override — the path IT pushes a tenant config to via
        // GPO / Intune / SCCM. Beats the installer's placeholder so a fresh
        // user on a managed machine never sees the setup wizard.
        yield return PathUtil.Expand(@"%PROGRAMDATA%\OneSync\config.json");

        // Installer-shipped placeholder (Program Files\OneSync\config.json on
        // a real install, OR the dev project root when running from bin/).
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "config.json");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
