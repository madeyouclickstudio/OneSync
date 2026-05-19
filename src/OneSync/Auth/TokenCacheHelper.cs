using System;
using System.IO;
using System.Threading;
using Microsoft.Identity.Client;
using Serilog;

namespace OneSync.Auth;

internal sealed class TokenCacheHelper
{
    private readonly string _cacheFilePath;
    private readonly ILogger _logger;
    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public TokenCacheHelper(string cacheDirectory, ILogger logger)
    {
        Directory.CreateDirectory(cacheDirectory);
        _cacheFilePath = Path.Combine(cacheDirectory, "auth_cache.bin");
        _logger = logger;
    }

    public string CacheFilePath => _cacheFilePath;

    public void Register(ITokenCache tokenCache)
    {
        tokenCache.SetBeforeAccess(OnBeforeAccess);
        tokenCache.SetAfterAccess(OnAfterAccess);
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        _fileLock.Wait();
        try
        {
            if (!File.Exists(_cacheFilePath)) return;

            var encrypted = File.ReadAllBytes(_cacheFilePath);
            byte[] decrypted;
            try
            {
                decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, optionalEntropy: null,
                    scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to decrypt MSAL token cache - discarding");
                try { File.Delete(_cacheFilePath); } catch { }
                return;
            }

            args.TokenCache.DeserializeMsalV3(decrypted);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;

        _fileLock.Wait();
        try
        {
            var data = args.TokenCache.SerializeMsalV3();
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                data, optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);

            File.WriteAllBytes(_cacheFilePath, encrypted);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist MSAL token cache");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void Clear()
    {
        _fileLock.Wait();
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
                _logger.Information("Token cache cleared");
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
