using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OneSync.Auth;
using OneSync.Cleanup;
using OneSync.Config;
using OneSync.State;
using OneSync.Diagnostics;
using OneSync.FileSystem;
using OneSync.Shell;
using OneSync.Shutdown;
using OneSync.Sync;
using OneSync.Tray;
using OneSync.Util;
using OneSync.Widget;
using Serilog;
using System.Linq;

namespace OneSync;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        // Subcommand: --launch-office <path>
        // Invoked by file associations for .docx/.xlsx/.pptx in OneSync drives.
        // Looks up the file's SharePoint webUrl in MetadataStore and launches
        // Office via ms-word:ofe|u|<url> so co-auth + AutoSave activate. Falls
        // back to opening the local file if no webUrl is available.
        //
        // Runs WITHOUT the single-instance mutex so it doesn't block on (or
        // get blocked by) the main OneSync service.
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--launch-office", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                return OfficeLauncher.Launch(args[i + 1]);
            }
        }

        // Single-instance gate per user session. Prevents two concurrent
        // OneSync processes from clashing over the LiteDB files.
        var singleInstanceMutex = new Mutex(initiallyOwned: false,
            name: $"Local\\OneSync-{Environment.UserName}");
        bool acquired;
        try { acquired = singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(1), false); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            try { singleInstanceMutex.Dispose(); } catch { }
            return 0; // another instance already owns the session
        }
        try
        {
            return await RunAsync(args);
        }
        finally
        {
            try { singleInstanceMutex.ReleaseMutex(); } catch { }
            try { singleInstanceMutex.Dispose(); } catch { }
        }
    }

    private static void ShowConfigErrorDialog(Exception ex)
    {
        var configPath = ConfigLoader.ExpectedConfigPath();
        var folder = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        var fileExists = File.Exists(configPath);

        var headline = fileExists
            ? "OneSync isn't configured yet."
            : "OneSync can't find its configuration file.";

        var body =
            $"{headline}\n\n" +
            $"{ex.Message}\n\n" +
            $"Edit this file to fix it (you'll need administrator rights):\n" +
            $"    {configPath}\n\n" +
            $"At minimum, set tenantId and clientId to the GUIDs from your Entra ID app registration. " +
            $"See config.template.json in the same folder for documentation of every setting.\n\n" +
            $"Open the folder now?";

        DialogResult result;
        try
        {
            result = MessageBox.Show(body, "OneSync — Configuration needed",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        }
        catch
        {
            // No UI session (running headless / under SYSTEM during install validation, etc.)
            return;
        }

        if (result == DialogResult.Yes)
        {
            try
            {
                if (fileExists)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{configPath}\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
            }
            catch { /* best-effort — user has the path in the dialog text */ }
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        // WinForms config MUST run before any Form / hidden window gets created.
        // GracefulShutdown creates a hidden form for ShutdownBlockReason later, so we
        // initialise WinForms up front.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // SetUnhandledExceptionMode MUST run before any Form is created on this
        // thread, or it throws InvalidOperationException. The first-run setup
        // wizard creates a Form during config load below, so we have to set the
        // mode up front rather than alongside the other process-wide handlers.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Dev-time CLI probes. These bypass normal sync init.
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--cache-size-probe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var counts = args[i + 1].Split(',')
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0)
                    .Where(v => v > 0)
                    .ToArray();
                if (counts.Length == 0)
                {
                    await Console.Error.WriteLineAsync("--cache-size-probe expects a comma-separated list of positive integers");
                    return 2;
                }
                Console.WriteLine($"CacheSizeProbe — N values: {string.Join(", ", counts)}");
                var outputDir = Environment.CurrentDirectory;
                var report = OneSync.Tools.CacheSizeProbe.Run(counts, outputDir);
                Console.WriteLine($"Report written to: {report}");
                return 0;
            }
            if (args[i].Equals("--throttle-storm", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var scenario = args[i + 1];
                var outputDir = Environment.CurrentDirectory;
                return await OneSync.Tools.ThrottleStorm.RunAsync(scenario, outputDir);
            }
        }

        AppConfig config;
        string configPath;
        try
        {
            config = ConfigLoader.LoadFromDefaultLocations(out configPath);
        }
        catch (InvalidDataException placeholderEx) when (ConfigLoader.LooksLikePlaceholderError(placeholderEx))
        {
            // First-run case: config.json exists but tenantId/clientId are still
            // placeholders. Offer the setup wizard instead of an error dialog.
            var templatePath = ConfigLoader.FindAnyConfigPath();
            if (templatePath is null)
            {
                ShowConfigErrorDialog(placeholderEx);
                return 2;
            }
            DialogResult wizardResult;
            try
            {
                var wizard = new Setup.SetupWizardForm(templatePath);
                wizardResult = wizard.ShowDialog();
            }
            catch (Exception wizardEx)
            {
                await Console.Error.WriteLineAsync($"Setup wizard crashed: {wizardEx.Message}");
                ShowConfigErrorDialog(wizardEx);
                return 2;
            }
            if (wizardResult != DialogResult.OK)
                return 1;
            try
            {
                config = ConfigLoader.LoadFromDefaultLocations(out configPath);
            }
            catch (Exception reloadEx)
            {
                await Console.Error.WriteLineAsync($"FATAL: Reload after setup wizard failed - {reloadEx.Message}");
                ShowConfigErrorDialog(reloadEx);
                return 2;
            }
        }
        catch (Exception ex)
        {
            // OneSync is launched from a Start Menu / Desktop / Startup shortcut — no
            // console is attached, so Console.Error goes nowhere a user can see. Surface
            // the real problem in a dialog with an actionable "open the file" path.
            await Console.Error.WriteLineAsync($"FATAL: Could not load config.json - {ex.Message}");
            ShowConfigErrorDialog(ex);
            return 2;
        }

        var logger = LoggerFactory.Create(config.Logging.Path, config.Logging.Level);
        Log.Logger = logger;

        var pauseStore = new PauseStateStore(logger);

        // Process-wide exception handlers. Wired up BEFORE anything else so we
        // catch crashes during start-up too. We log every uncaught exception so
        // post-mortem analysis is possible, but we DON'T write the clean-shutdown
        // marker - an unhandled exception is by definition not a clean shutdown,
        // and the next session should re-reconcile.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            try { logger.Fatal(ex, "AppDomain unhandled exception (IsTerminating={Term})", e.IsTerminating); }
            catch { /* logger may already be disposed */ }
            var tail = CrashWriter.TryReadLogTail(config.Logging.Path);
            CrashWriter.Write(ex, source: $"AppDomain-Term{e.IsTerminating}", logTail: tail);
            try { Log.CloseAndFlush(); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { logger.Error(e.Exception, "TaskScheduler unobserved task exception"); }
            catch { }
            var tail = CrashWriter.TryReadLogTail(config.Logging.Path);
            CrashWriter.Write(e.Exception, source: "TaskScheduler-Unobserved", logTail: tail);
            e.SetObserved();
        };
        Application.ThreadException += (_, e) =>
        {
            try { logger.Error(e.Exception, "WinForms thread exception"); }
            catch { }
            var tail = CrashWriter.TryReadLogTail(config.Logging.Path);
            CrashWriter.Write(e.Exception, source: "WinForms-Thread", logTail: tail);
        };
        // SetUnhandledExceptionMode is set up at the top of RunAsync because the
        // setup wizard's Form would otherwise pin the mode before this line ran.

        bool checkOnly = Array.Exists(args, a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        bool noMount = Array.Exists(args, a => a.Equals("--no-mount", StringComparison.OrdinalIgnoreCase));
        bool noTray = Array.Exists(args, a => a.Equals("--no-tray", StringComparison.OrdinalIgnoreCase));
        bool noFolderRedirect = Array.Exists(args, a => a.Equals("--no-folder-redirect", StringComparison.OrdinalIgnoreCase));
        bool forceFullSync = Array.Exists(args, a => a.Equals("--full-sync", StringComparison.OrdinalIgnoreCase));

        DriveManager? driveMgr = null;
        SyncEngine? syncEngine = null;
        QuotaCache? quotaCache = null;
        GraphAuthProvider? auth = null;
        StorageCleanup? cleanup = null;
        TrayIcon? tray = null;
        GracefulShutdown? shutdown = null;
        FolderRedirector? folderRedir = null;
        SyncStatusWidget? widget = null;
        SyncStatusViewModel? widgetVm = null;
        RecycleBinWidget? recycleBinWidget = null;
        RecycleBinViewModel? recycleBinVm = null;
        List<OneSync.Sync.Cooperative.LeaderElection> leaderElections = new();
        OneSync.Sync.Cooperative.CooperativePollingService? coopService = null;

        try
        {
            logger.Information("=== OneSync starting ===");
            logger.Information("Process PID: {Pid}, .NET: {Net}, OS: {Os}",
                Environment.ProcessId, Environment.Version, Environment.OSVersion);
            logger.Information("Config source: {Path}", configPath);
            logger.Information("Tenant: {Tenant}, Client: {Client}", config.TenantId, config.ClientId);
            logger.Information("Local storage root: {Root}", config.LocalStorageRoot);
            logger.Information("Configured drives: {Count} -> [{Drives}]",
                config.Drives.Count,
                string.Join(", ", config.Drives.ConvertAll(d => $"{d.Letter}:{d.Label}({d.Type})")));
            logger.Information(
                "Cooperative polling: enabled={Enabled}, controlFolder='{Folder}', leaseTtl={Ttl}s, renew={Renew}s, readerPoll={Read}s, selfCheck={Self}min, lazyFallback={Lazy}",
                config.CooperativePolling.Enabled,
                config.CooperativePolling.ControlFolder,
                config.CooperativePolling.LeaseTtlSeconds,
                config.CooperativePolling.RenewIntervalSeconds,
                config.CooperativePolling.ReaderPollIntervalSeconds,
                config.CooperativePolling.SelfCheckIntervalMinutes,
                config.CooperativePolling.LazyFallbackEnabled);

            // Cooperative polling config validation. A LeaseTtl shorter than
            // 2× RenewInterval guarantees the lease expires before the leader
            // gets a chance to renew, causing constant leadership thrash.
            // Reject the misconfiguration up front rather than discover it as
            // leadership-bouncing in production.
            if (config.CooperativePolling.Enabled)
            {
                var ttl = config.CooperativePolling.LeaseTtlSeconds;
                var renew = config.CooperativePolling.RenewIntervalSeconds;
                // Accept ttl >= 2× renew. At exactly 2×, missing a single renewal
                // leaves zero margin — workable but tight. Below 2× the lease
                // can plausibly expire before the leader gets a chance to renew,
                // causing constant leadership thrash.
                if (ttl < renew * 2)
                {
                    logger.Error(
                        "Cooperative polling config invalid: leaseTtlSeconds={Ttl} must be >= 2× renewIntervalSeconds={Renew}. Disabling cooperative polling for this session — fix config.json and restart.",
                        ttl, renew);
                    config.CooperativePolling.Enabled = false;
                }
            }

            // 1) Logon cleanup
            cleanup = new StorageCleanup(config, logger);
            // Capture shutdown cleanliness BEFORE MarkSessionStarted, which deletes
            // the marker as part of starting the new session.
            var previousShutdownWasClean = cleanup.WasPreviousShutdownClean();
            cleanup.RunLogonCleanup();
            cleanup.MarkSessionStarted();

            // 2) Authentication
            var stateDir = PathUtil.Expand(@"%LOCALAPPDATA%\OneSync");
            Directory.CreateDirectory(stateDir);

            auth = new GraphAuthProvider(config, stateDir, logger);
            await auth.InitializeAsync();

            // 3) Permission check
            var permChecker = new DrivePermissionChecker(auth.GraphClient, config.DriveFiltering, logger);
            var permitted = await permChecker.FilterByPermissionAsync(config.Drives);

            // Persist the resolved per-drive library URLs so the short-lived
            // OneSync.exe --launch-office process (which doesn't run the
            // resolver) can construct correct ms-excel:ofe URLs for SharePoint
            // libraries whose URL slug differs from their display name.
            try { OneSync.State.DriveResolutionStore.Write(permitted, logger); }
            catch { /* logged inside Write */ }

            if (permitted.Count == 0)
            {
                logger.Error("No drives are accessible to user '{User}' - exiting", Environment.UserName);
                return 3;
            }

            foreach (var drive in permitted)
            {
                Directory.CreateDirectory(drive.LocalRootPath);
                logger.Information("Prepared local root: {Letter}: -> {Path}", drive.Letter, drive.LocalRootPath);
            }

            // Session-cache wipe: drop stale metadata + placeholders so each session
            // starts fresh (bounded disk usage on persistent profiles). Runs AFTER
            // local roots exist but BEFORE SyncEngine opens metadata.db.
            OneSync.State.SessionCacheCleaner.WipeIfApplicable(
                config, previousShutdownWasClean, stateDir, permitted, logger);

            // Defensive sync_queue.db compaction: graceful shutdown compacts on every
            // exit, but if the user crashes repeatedly we'd never reach that path.
            // Catch it here so the file doesn't grow without bound.
            OneSync.State.SessionCacheCleaner.CompactSyncQueueIfStale(stateDir, logger);

            // Publish the drive-letter -> local-NTFS-path map so the shell overlay
            // DLL (loaded inside Explorer) can resolve H:\... to the underlying
            // NTFS path where the OneSync alternate data stream lives.
            DriveMappingRegistry.Publish(permitted, logger);

            // Initialise the Shell notifier so SyncStateMarker (and any other
            // static callers) can resolve a local NTFS path back to a drive
            // and notify both Explorer views.
            OneSync.Util.ShellNotifier.Initialize(permitted);

            if (!noTray)
            {
                var widgetReady = new ManualResetEventSlim();
                var widgetThread = new Thread(() =>
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += (_, ex) =>
                    {
                        logger.Warning(ex.Exception, "Widget dispatcher exception");
                        ex.Handled = true;
                    };
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    widgetVm = new SyncStatusViewModel(dispatcher);
                    widget = new SyncStatusWidget(widgetVm);
                    widget.Show();
                    recycleBinVm = new RecycleBinViewModel(dispatcher);
                    recycleBinWidget = new RecycleBinWidget(recycleBinVm);
                    widgetReady.Set();
                    System.Windows.Threading.Dispatcher.Run();
                });
                widgetThread.SetApartmentState(ApartmentState.STA);
                widgetThread.IsBackground = true;
                widgetThread.Start();
                widgetReady.Wait();
            }

            if (checkOnly)
            {
                logger.Information("--check mode complete - all Phase 1 validations passed");
                return 0;
            }

            // 4) Quota cache
            quotaCache = new QuotaCache(auth.GraphClient, stateDir,
                config.SyncSettings.QuotaRefreshIntervalSeconds, logger);
            await quotaCache.RefreshAllAsync(permitted);

            using var appCts = new CancellationTokenSource();
            quotaCache.StartBackgroundRefresh(permitted, appCts.Token);

            // 5) Sync engine (Phase 4 + 5)
            syncEngine = new SyncEngine(config, auth, quotaCache, permitted, stateDir, logger);

            // Cooperative polling: claim leadership per drive, then start the renew+cache loop.
            // The per-user delta poller still runs alongside; a later phase routes readers to
            // the leader's cache.
            // Wire the lazy fallback config gate into HydrationService.
            syncEngine.Hydration.LazyFallbackEnabled = config.CooperativePolling.LazyFallbackEnabled;
            logger.Information("Dokan lazy fallback (on-demand folder enumeration): {Enabled}",
                syncEngine.Hydration.LazyFallbackEnabled ? "enabled" : "disabled");

            // Tell the Dokan layer which folder name to treat as the protected
            // control folder (Hidden+System attributes, delete/rename refused).
            OneSync.FileSystem.OneSyncDokanFS.ProtectedFolderName =
                string.IsNullOrWhiteSpace(config.CooperativePolling.ControlFolder)
                    ? ".onesync"
                    : config.CooperativePolling.ControlFolder.Trim('/');
            logger.Information("Protected control folder: '{Folder}' (hidden, undeletable)",
                OneSync.FileSystem.OneSyncDokanFS.ProtectedFolderName);

            if (config.CooperativePolling.Enabled)
            {
                var userId = auth.Account?.HomeAccountId?.ObjectId ?? "unknown-oid";
                var userEmail = auth.Account?.Username ?? "unknown-user";
                coopService = new OneSync.Sync.Cooperative.CooperativePollingService(
                    config.CooperativePolling, userId, userEmail, logger);
                foreach (var drive in permitted)
                {
                    try
                    {
                        var le = new OneSync.Sync.Cooperative.LeaderElection(
                            syncEngine.GraphHttp, drive, config.CooperativePolling,
                            userId, userEmail, logger);
                        await le.ClaimOrJoinAsync(appCts.Token);
                        leaderElections.Add(le);

                        var cache = new OneSync.Sync.Cooperative.DeltaCache(
                            syncEngine.GraphHttp, drive, config.CooperativePolling, logger);
                        coopService.RegisterDrive(le, cache, drive, syncEngine);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Cooperative-polling setup failed for {Letter}: — falling back to per-user polling", drive.Letter);
                    }
                }
                coopService.WireUpDeltaPollerSkip(syncEngine.DeltaPoller);
                coopService.Start();
                coopService.SetPauseStore(pauseStore);
            }

            if (forceFullSync)
            {
                logger.Information("--full-sync flag set; clearing delta tokens and metadata for full re-poll");
                foreach (var d in permitted)
                    syncEngine.Queue.ClearDeltaToken(d.ConfigId);
                syncEngine.ResetMetadata();
            }

            // 6) Drive mounting (Phase 2)
            var mountedDrives = new List<DriveConfig>();
            var mountWarnings = new List<string>();

            if (!noMount)
            {
                driveMgr = new DriveManager(logger);
                foreach (var drive in permitted)
                {
                    if (driveMgr.IsDriveLetterInUse(drive.Letter))
                    {
                        logger.Warning("Drive letter {Letter}: already in use - skipping mount", drive.Letter);
                        mountWarnings.Add($"{drive.Letter}: ({drive.Label}) is already in use and could not be mapped.");
                        widgetVm?.AddDriveStatus(drive.Letter, drive.Label, mounted: false);
                        continue;
                    }
                    try
                    {
                        var d = drive; // capture for closure
                        Action<string, System.IO.WatcherChangeTypes> onLocalChange = (path, change) =>
                        {
                            if (change != System.IO.WatcherChangeTypes.Deleted) return;
                            var rel = path.Substring(d.LocalRootPath.Length)
                                .TrimStart(System.IO.Path.DirectorySeparatorChar)
                                .Replace(System.IO.Path.DirectorySeparatorChar, '/');

                            // Honor excludePatterns on the delete path too. Without this,
                            // deleting a vim swap file (or other excluded type) queues a
                            // remote delete that 404s harmlessly but burns a Graph call.
                            var basename = System.IO.Path.GetFileName(path);
                            foreach (var pat in config.SyncSettings.ExcludePatterns)
                            {
                                if (PathUtil.MatchesGlob(basename, pat))
                                {
                                    logger.Debug("Local delete ignored (excludePatterns match {Pat}): {Path}", pat, rel);
                                    return;
                                }
                            }

                            var meta = syncEngine.Metadata.Get(d.ConfigId, rel);
                            if (meta != null && !string.IsNullOrEmpty(meta.RemoteItemId))
                            {
                                syncEngine.Metadata.AddDeleted(new DeletedItem
                                {
                                    RemoteItemId = meta.RemoteItemId,
                                    DriveConfigId = d.ConfigId,
                                    DriveLetter = d.Letter,
                                    RelativePath = rel,
                                    Name = meta.Name,
                                    IsFolder = meta.IsFolder,
                                    Size = meta.Size,
                                    DeletedAtUtc = DateTime.UtcNow,
                                    LastModifiedDateTime = meta.LastModifiedDateTime,
                                });
                            }

                            syncEngine.Queue.Enqueue(new SyncOperation
                            {
                                Type = SyncOpType.RemoteDelete,
                                DriveConfigId = d.ConfigId,
                                DriveLetter = d.Letter,
                                RelativePath = rel,
                                Priority = d.Priority,
                            });
                            logger.Information("Dokan delete -> queued remote delete: {Drive}:{Path}", d.Letter, rel);
                        };
                        var mounted = driveMgr.Mount(drive, quotaCache,
                            onLocalChange: onLocalChange,
                            hydration: syncEngine.HydrationTrigger);
                        if (mounted.Ready)
                        {
                            mountedDrives.Add(drive);
                            widgetVm?.AddDriveStatus(drive.Letter, drive.Label, mounted: true);
                        }
                        else
                        {
                            logger.Warning("Drive {Letter}: mounted but not Ready within timeout - skipping folder redirection for it", drive.Letter);
                            mountWarnings.Add($"{drive.Letter}: ({drive.Label}) was slow to come online and may not be fully available.");
                        }
                    }
                    catch (DokanNet.DokanException ex)
                    {
                        logger.Error(
                            "Failed to mount {Letter}: - {Message}. The Dokan driver is not available; register it " +
                            "with 'dokanctl.exe /i d' (as administrator) or reinstall the Dokan driver.",
                            drive.Letter, ex.Message);
                        mountWarnings.Add($"{drive.Letter}: ({drive.Label}) could not be mapped - the Dokan driver is not installed. Please contact IT.");
                        widgetVm?.AddDriveStatus(drive.Letter, drive.Label, mounted: false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to mount {Letter}: - continuing without it", drive.Letter);
                        mountWarnings.Add($"{drive.Letter}: ({drive.Label}) could not be mapped. See the log for details.");
                        widgetVm?.AddDriveStatus(drive.Letter, drive.Label, mounted: false);
                    }
                }
            }
            else
            {
                logger.Information("--no-mount flag set; skipping Dokan mount");
            }

            // Wire widget events BEFORE Start() so we don't miss the initial poll
            if (widgetVm != null)
            {
                widgetVm.SetQueue(syncEngine.Queue);

                syncEngine.DeltaPoller.InitialPollStarted += widgetVm.OnInitialPollStarted;
                syncEngine.DeltaPoller.InitialPollProgress += widgetVm.OnInitialPollProgress;
                syncEngine.DeltaPoller.InitialPollCompleted += widgetVm.OnInitialPollCompleted;
                syncEngine.Conflicts.ConflictDetected += widgetVm.OnConflictDetected;
                syncEngine.GraphHttp.SignificantThrottle += widgetVm.OnSignificantThrottle;
                syncEngine.Hydration.HydrationDenied += widgetVm.OnHydrationDenied;
            }

            recycleBinVm?.SetService(syncEngine.RecycleBin);
            syncEngine.RecycleBin.ItemRestored += item =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await syncEngine.DeltaPoller.RunOnceAsync(CancellationToken.None);
                    }
                    catch { }
                });
            };

            // 7) Start sync (watchers + uploader + delta poller)
            syncEngine.Start();

            // Uploader is created inside Start(), so subscribe after
            if (widgetVm != null && syncEngine.Uploader != null)
            {
                syncEngine.Uploader.UploadStarted += widgetVm.OnUploadStarted;
                syncEngine.Uploader.UploadProgress += widgetVm.OnUploadProgress;
                syncEngine.Uploader.UploadCompleted += widgetVm.OnUploadCompleted;
                syncEngine.Uploader.UploadFailed += widgetVm.OnUploadFailed;
                syncEngine.Uploader.SyncOpStarted += widgetVm.OnSyncOpStarted;
                syncEngine.Uploader.SyncOpCompleted += widgetVm.OnSyncOpCompleted;
            }

            syncEngine.DeltaPoller.SetPauseStore(pauseStore);
            if (syncEngine.Uploader is not null) syncEngine.Uploader.SetPauseStore(pauseStore);

            // If delta tokens already exist, no initial poll events fire —
            // tell the widget it's safe to leave the Starting state.
            widgetVm?.NotifyReady();

            // 8) Folder redirection (Phase 7) - only for drives that actually mounted.
            // Redirecting Desktop/Documents/etc. to a drive that failed to mount would
            // throw DirectoryNotFoundException for every folder (and risk pointing the
            // user's shell folders at a drive that isn't there).
            if (!noFolderRedirect)
            {
                if (mountedDrives.Count > 0)
                {
                    folderRedir = new FolderRedirector(logger);
                    folderRedir.Apply(mountedDrives);
                }
                else
                {
                    logger.Warning("No drives mounted - skipping folder redirection");
                }
            }

            // 9) Tray icon + shutdown handlers (Phase 8)
            Action requestExit = () =>
            {
                try { appCts.Cancel(); } catch { }
                try
                {
                    if (Application.MessageLoop)
                        Application.Exit();
                }
                catch { }
            };

            shutdown = new GracefulShutdown(config, syncEngine, logger, requestExit, cleanup);
            shutdown.Register();

            logger.Information("=== OneSync running ===");

            if (!noTray)
            {
                Action? toggleRecycleBin = recycleBinWidget != null
                    ? () => recycleBinWidget.Dispatcher.BeginInvoke(() => recycleBinWidget.Toggle())
                    : null;
                var diagExporter = new DiagnosticExporter(
                    config: config,
                    liveConfigPath: configPath,
                    sync: syncEngine,
                    quotaCache: quotaCache,
                    graph: syncEngine.GraphHttp,
                    logger: logger);
                tray = new TrayIcon(quotaCache, syncEngine, permitted, config.Logging.Path,
                    requestExit, toggleRecycleBin, pauseStore, diagExporter, logger);
                if (mountWarnings.Count > 0)
                {
                    if (widgetVm != null)
                        foreach (var w in mountWarnings)
                            widgetVm.AddMountWarning(w);
                    else
                        tray.ShowBalloon("Drive not available",
                            string.Join("\n", mountWarnings), ToolTipIcon.Warning);
                }
                if (widgetVm != null)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(12));
                            var manifest = await syncEngine.Manifest.ReadAsync();
                            if (manifest == null) return;
                            var thisMachine = Environment.MachineName;
                            var others = manifest.Entries
                                .Where(e => !string.Equals(e.Machine, thisMachine, StringComparison.OrdinalIgnoreCase))
                                .GroupBy(e => e.Machine);
                            foreach (var g in others)
                                widgetVm.OnOtherMachinePending(g.Key, g.Count());
                        }
                        catch { }
                    });
                }
                else
                {
                    tray.CheckOtherMachinePendingAsync();
                }
                logger.Information("Tray icon active - right-click for menu");
                Application.Run();
            }
            else
            {
                logger.Information("--no-tray flag set; running headless (Ctrl+C to exit)");
                try { await Task.Delay(Timeout.Infinite, appCts.Token); }
                catch (OperationCanceledException) { /* expected */ }
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "Unhandled exception in Main");
            return 1;
        }
        finally
        {
            // FIRST thing: record that we made it to the graceful-shutdown path.
            // We're inside the finally block, so by definition this is NOT an
            // abrupt termination (a real crash would have skipped here via the
            // AppDomain.UnhandledException handler). Windows can kill the process
            // any time during the rest of this block during a logoff, so we mark
            // clean before any long-running cleanup step.
            try { cleanup?.MarkCleanShutdown(); }
            catch (Exception ex) { logger.Warning(ex, "Could not write clean_shutdown marker"); }

            // Graceful shutdown
            try
            {
                if (folderRedir != null && config.Cleanup.CleanOnLogoff)
                    folderRedir.Restore();
            }
            catch (Exception ex) { logger.Warning(ex, "Folder redirection restore failed"); }

            // Snapshot pending-upload paths BEFORE disposing the sync engine:
            // GetPendingUploadLocalPaths reads the LiteDB queue that DisposeAsync
            // closes. Reading it after dispose throws ObjectDisposedException and
            // aborts logoff cleanup - which is the only thing that writes the
            // clean-shutdown marker.
            IReadOnlyCollection<string>? preserve = null;
            try
            {
                if (syncEngine != null)
                {
                    logger.Information("Flushing sync queue (timeout {Timeout}s)...",
                        config.SyncSettings.ShutdownTimeoutSeconds);
                    try
                    {
                        await syncEngine.FlushAndStopAsync(
                            TimeSpan.FromSeconds(config.SyncSettings.ShutdownTimeoutSeconds));

                        preserve = syncEngine.GetPendingUploadLocalPaths();
                        if (preserve.Count > 0)
                        {
                            logger.Warning(
                                "{Count} files have pending uploads after flush - PRESERVING them across cleanup (next logon will retry)",
                                preserve.Count);
                        }
                    }
                    finally
                    {
                        // DisposeAsync must always run to release the sync_queue.db
                        // file lock - otherwise the next instance fails to start.
                        await syncEngine.DisposeAsync();
                    }
                }
            }
            catch (Exception ex) { logger.Warning(ex, "Sync engine shutdown failed"); }

            try { tray?.Dispose(); } catch { }
            try
            {
                if (widget != null)
                    widget.Dispatcher.InvokeShutdown();
                else if (recycleBinWidget != null)
                    recycleBinWidget.Dispatcher.InvokeShutdown();
            }
            catch { }
            try { shutdown?.Dispose(); } catch { }

            // Release cooperative-polling leadership (best effort; expiry covers failure).
            if (coopService is not null)
            {
                try { await coopService.RelinquishAllAsync(); }
                catch (Exception ex) { logger.Warning(ex, "Cooperative-polling release failed"); }
                try { await coopService.DisposeAsync(); }
                catch { /* swallow */ }
            }

            try { driveMgr?.Dispose(); }
            catch (Exception ex) { logger.Warning(ex, "Drive unmount failed"); }

            try { quotaCache?.Dispose(); } catch { }
            try { auth?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }

            // Logoff cleanup. Files with pending uploads (snapshotted above, before
            // the sync engine was disposed) are preserved so the next logon resumes them.
            try
            {
                cleanup?.RunLogoffCleanup(preserve);
            }
            catch (Exception ex) { logger.Warning(ex, "Logoff cleanup failed"); }

            await Log.CloseAndFlushAsync();
        }
    }
}
