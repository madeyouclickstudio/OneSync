using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using OneSync.Config;
using Serilog;

namespace OneSync.Auth;

internal sealed class GraphAuthProvider : IAsyncDisposable
{
    public static readonly string[] Scopes =
    {
        "Files.ReadWrite",
        "Files.ReadWrite.All",
        "Sites.ReadWrite.All",
        "User.Read",
    };

    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;
    private readonly TokenCacheHelper _cacheHelper;

    private IPublicClientApplication? _pca;
    private IAccount? _account;
    private GraphServiceClient? _graphClient;
    private AuthenticationResult? _lastResult;

    public GraphAuthProvider(AppConfig config, string cacheDirectory, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
        _cacheHelper = new TokenCacheHelper(cacheDirectory, logger);
    }

    public string TokenCachePath => _cacheHelper.CacheFilePath;

    public IAccount? Account => _account;

    public GraphServiceClient GraphClient =>
        _graphClient ?? throw new InvalidOperationException("Auth not initialised - call InitializeAsync first");

    public async Task<AuthenticationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _pca = BuildClient();
        _cacheHelper.Register(_pca.UserTokenCache);

        var result = await AcquireTokenAsync(cancellationToken);
        _account = result.Account;
        _lastResult = result;

        var authProvider = new MsalDelegatingAuthenticationProvider(this);
        _graphClient = new GraphServiceClient(authProvider);

        _logger.Information(
            "Token acquired for scopes [{Scopes}], user {User}, expires {Expiry:u}",
            string.Join(", ", result.Scopes), result.Account?.Username, result.ExpiresOn);

        return result;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_pca is null)
            throw new InvalidOperationException("Auth not initialised");

        if (_lastResult != null && _lastResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
            return _lastResult.AccessToken;

        var result = await AcquireTokenAsync(cancellationToken);
        _lastResult = result;
        _account = result.Account;
        return result.AccessToken;
    }

    private async Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        if (_pca is null) throw new InvalidOperationException("Auth not initialised");

        // 1) Try silent (cached or refresh)
        var accounts = await _pca.GetAccountsAsync();
        var account = accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
        try
        {
            _logger.Debug("Attempting silent token acquisition (account: {Account})",
                account?.Username ?? "OS default");
            var silent = await _pca.AcquireTokenSilent(Scopes, account)
                .ExecuteAsync(cancellationToken);
            _logger.Information("Silent token acquisition succeeded");
            return silent;
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.Information("Silent failed ({Code}) - falling back to broker/interactive", ex.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Silent token acquisition threw - falling back to interactive");
        }

        // 2) Try Integrated Windows Auth (Kerberos/NTLM) - works on domain-joined PCs
        try
        {
            _logger.Debug("Attempting Integrated Windows Authentication");
            var iwa = await _pca.AcquireTokenByIntegratedWindowsAuth(Scopes)
                .ExecuteAsync(cancellationToken);
            _logger.Information("Integrated Windows Auth succeeded");
            return iwa;
        }
        catch (Exception ex)
        {
            _logger.Information("IWA not available ({Type}: {Msg}) - falling to interactive", ex.GetType().Name, ex.Message);
        }

        // 3) Interactive (uses WAM broker on Windows 10+)
        try
        {
            var hwnd = GetForegroundOrConsoleWindow();
            _logger.Information(
                "Launching interactive sign-in (WAM broker) - parent HWND=0x{Hwnd:X}",
                hwnd.ToInt64());

            // Let WAM show the account picker - do not force OS account, in case
            // the OS-logged-in account differs from the desired Graph account.
            var interactive = await _pca.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);
            _logger.Information("Interactive sign-in succeeded for {User}", interactive.Account?.Username);
            return interactive;
        }
        catch (MsalException ex)
        {
            _logger.Error(ex, "Interactive sign-in failed: {Code}", ex.ErrorCode);
            throw;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private static IntPtr GetForegroundOrConsoleWindow()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) hwnd = GetShellWindow();
        if (hwnd == IntPtr.Zero) hwnd = GetDesktopWindow();
        return hwnd;
    }

    private IPublicClientApplication BuildClient()
    {
        var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "OneSync",
        };

        var builder = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(_config.Authority)
            .WithRedirectUri("http://localhost")
            .WithParentActivityOrWindow(GetForegroundOrConsoleWindow)
            .WithBroker(brokerOptions);

        return builder.Build();
    }

    public async Task SignOutAsync()
    {
        if (_pca is null) return;
        var accounts = await _pca.GetAccountsAsync();
        foreach (var account in accounts)
        {
            await _pca.RemoveAsync(account);
        }
        _cacheHelper.Clear();
    }

    public ValueTask DisposeAsync()
    {
        // Nothing async to dispose presently
        return ValueTask.CompletedTask;
    }

    private sealed class MsalDelegatingAuthenticationProvider : Microsoft.Kiota.Abstractions.Authentication.IAuthenticationProvider
    {
        private readonly GraphAuthProvider _parent;

        public MsalDelegatingAuthenticationProvider(GraphAuthProvider parent) => _parent = parent;

        public async Task AuthenticateRequestAsync(
            Microsoft.Kiota.Abstractions.RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            var token = await _parent.GetAccessTokenAsync(cancellationToken);
            request.Headers["Authorization"] = new List<string> { $"Bearer {token}" };
        }
    }
}
