using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using OneSync.Config;
using Serilog;

namespace OneSync.FileSystem;

internal sealed class DrivePermissionChecker
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger _logger;
    private readonly DriveFilteringConfig _filteringConfig;

    public DrivePermissionChecker(
        GraphServiceClient graph, DriveFilteringConfig filteringConfig, ILogger logger)
    {
        _graph = graph;
        _filteringConfig = filteringConfig;
        _logger = logger;
    }

    public async Task<List<DriveConfig>> FilterByPermissionAsync(
        IEnumerable<DriveConfig> drives, CancellationToken cancellationToken = default)
    {
        var input = drives.ToList();

        if (!_filteringConfig.Enabled || !_filteringConfig.CheckPermissionsAtStartup)
        {
            _logger.Information("Drive filtering disabled - mounting all {Count} configured drives", input.Count);
            return input;
        }

        var permitted = new List<DriveConfig>(input.Count);

        foreach (var drive in input)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (drive.IsOneDrive)
            {
                if (await CheckOneDriveAsync(drive, cancellationToken))
                    permitted.Add(drive);
            }
            else if (drive.IsSharePoint)
            {
                if (await CheckSharePointAsync(drive, cancellationToken))
                    permitted.Add(drive);
            }
            else
            {
                _logger.Warning("SKIP {Letter}: unknown drive type '{Type}'", drive.Letter, drive.Type);
            }
        }

        _logger.Information("Drives permitted: [{Drives}] (skipped {SkipCount} of {Total})",
            string.Join(", ", permitted.Select(d => $"{d.Letter}:{d.Label}")),
            input.Count - permitted.Count,
            input.Count);

        return permitted;
    }

    private async Task<bool> CheckOneDriveAsync(DriveConfig drive, CancellationToken ct)
    {
        try
        {
            var oneDrive = await _graph.Me.Drive.GetAsync(cancellationToken: ct);
            if (oneDrive is null)
            {
                LogSkip(drive, "Me.Drive returned null");
                return false;
            }

            drive.ResolvedDriveId = oneDrive.Id;
            _logger.Information(
                "OK {Letter}: OneDrive resolved (driveId {Id}, type {Type}, owner {Owner})",
                drive.Letter, oneDrive.Id, oneDrive.DriveType, oneDrive.Owner?.User?.DisplayName);
            return true;
        }
        catch (ODataError ex)
        {
            LogSkip(drive, $"OData error {ex.ResponseStatusCode}: {ex.Error?.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogSkip(drive, $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CheckSharePointAsync(DriveConfig drive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(drive.SiteUrl) || string.IsNullOrWhiteSpace(drive.LibraryName))
        {
            LogSkip(drive, "SharePoint drive missing siteUrl or libraryName");
            return false;
        }

        try
        {
            var siteUri = new Uri(drive.SiteUrl);
            var hostname = siteUri.Host;
            var sitePath = siteUri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(sitePath)) sitePath = "/";

            var siteRef = $"{hostname}:{sitePath}";
            _logger.Information(
                "SharePoint {Letter}: resolving site ref '{SiteRef}' (from url '{Url}')",
                drive.Letter, siteRef, drive.SiteUrl);

            var site = await _graph.Sites[siteRef].GetAsync(cancellationToken: ct);
            if (site?.Id is null)
            {
                LogSkip(drive, $"Site '{siteRef}' returned null");
                return false;
            }

            _logger.Information(
                "SharePoint {Letter}: site resolved — id={SiteId}, name='{Name}', url='{Url}'",
                drive.Letter, site.Id, site.DisplayName, site.WebUrl);

            var drivesResult = await _graph.Sites[site.Id].Drives
                .GetAsync(cancellationToken: ct);

            var availableNames = drivesResult?.Value?.Select(d => d.Name) ?? [];
            _logger.Information(
                "SharePoint {Letter}: available libraries on site: [{Libraries}]",
                drive.Letter, string.Join(", ", availableNames));

            var library = drivesResult?.Value?.FirstOrDefault(d =>
                string.Equals(d.Name, drive.LibraryName, StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                LogSkip(drive, $"Library '{drive.LibraryName}' not found in site '{siteRef}' — available: [{string.Join(", ", availableNames)}]");
                return false;
            }

            drive.ResolvedDriveId = library.Id;
            drive.ResolvedSiteId = site.Id;
            drive.ResolvedLibraryWebUrl = library.WebUrl;
            _logger.Information(
                "OK {Letter}: SharePoint resolved (siteId {SiteId}, driveId {DriveId}, library '{Lib}', libraryWebUrl '{LibUrl}')",
                drive.Letter, site.Id, library.Id, library.Name, library.WebUrl);
            return true;
        }
        catch (ODataError ex)
        {
            var status = ex.ResponseStatusCode;
            if (status == 403 || status == 404)
            {
                LogSkip(drive, $"User has no access ({status}: {ex.Error?.Message})");
                return false;
            }
            LogSkip(drive, $"OData error {status}: {ex.Error?.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogSkip(drive, $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void LogSkip(DriveConfig drive, string reason)
    {
        if (_filteringConfig.LogSkippedDrives)
            _logger.Warning("SKIP {Letter}: {Label} - {Reason}", drive.Letter, drive.Label, reason);
    }
}
