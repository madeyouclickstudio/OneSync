// DLL entry points and COM registration for OneSyncShellOverlay.

#include "Overlay.h"
#include "Thumbnail.h"
#include <strsafe.h>

HINSTANCE g_hInstance = nullptr;
LONG      g_lockCount = 0;

extern void InitMappingsLock();
extern void DestroyMappingsLock();

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
        case DLL_PROCESS_ATTACH:
            g_hInstance = hModule;
            DisableThreadLibraryCalls(hModule);
            InitMappingsLock();
            break;
        case DLL_PROCESS_DETACH:
            DestroyMappingsLock();
            break;
    }
    return TRUE;
}

// IsDllUnloadable - return S_OK only when nobody's using us.
STDAPI DllCanUnloadNow()
{
    return (g_lockCount == 0) ? S_OK : S_FALSE;
}

// Class factory entry point - returns the appropriate factory for the requested CLSID.
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;

    BYTE stateMask = 0;
    int iconIndex = 0;
    CLSID clsid = {};

    // The green tick deliberately covers BOTH cloud-only and fully-synced files:
    // a cloud placeholder is just as safe as a downloaded copy, so to the user it
    // reads as "this file is fine". Only syncing and error get a distinct badge.
    if (rclsid == CLSID_OneSyncOverlaySyncing)
    { stateMask = (1 << STATE_SYNCING); iconIndex = 0; clsid = CLSID_OneSyncOverlaySyncing; }
    else if (rclsid == CLSID_OneSyncOverlaySynced)
    { stateMask = (1 << STATE_CLOUD_ONLY) | (1 << STATE_SYNCED); iconIndex = 1; clsid = CLSID_OneSyncOverlaySynced; }
    else if (rclsid == CLSID_OneSyncOverlayError)
    { stateMask = (1 << STATE_ERROR); iconIndex = 2; clsid = CLSID_OneSyncOverlayError; }
    else if (rclsid == CLSID_OneSyncThumbnailProvider)
    {
        auto* tfactory = new (std::nothrow) COneSyncThumbnailProviderFactory();
        if (tfactory == nullptr) return E_OUTOFMEMORY;
        HRESULT hr = tfactory->QueryInterface(riid, ppv);
        tfactory->Release();
        return hr;
    }
    else
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) COneSyncOverlayFactory(stateMask, iconIndex, clsid);
    if (factory == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

// --- Registration helpers ----------------------------------------------------

namespace {
    HRESULT GetDllPath(PWSTR buffer, DWORD cch)
    {
        if (GetModuleFileNameW(g_hInstance, buffer, cch) == 0)
            return HRESULT_FROM_WIN32(GetLastError());
        return S_OK;
    }

    HRESULT WriteRegString(HKEY root, PCWSTR subkey, PCWSTR name, PCWSTR value)
    {
        HKEY hKey = nullptr;
        DWORD disp = 0;
        LONG status = RegCreateKeyExW(root, subkey, 0, nullptr, 0,
            KEY_WRITE | KEY_WOW64_64KEY, nullptr, &hKey, &disp);
        if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
        DWORD bytes = (DWORD)((wcslen(value) + 1) * sizeof(WCHAR));
        status = RegSetValueExW(hKey, name, 0, REG_SZ, (const BYTE*)value, bytes);
        RegCloseKey(hKey);
        return HRESULT_FROM_WIN32(status);
    }

    // Thumbnail-provider COM interface ID
    constexpr WCHAR THUMBNAIL_PROVIDER_KEY[] = L"{e357fccd-a995-4576-b01f-234630154e96}";

    // Image extensions for which we override the system thumbnail provider.
    // For files outside our drives, we fall back to WIC decoding the actual
    // file content, so we preserve normal behaviour for the rest of the system.
    PCWSTR THUMB_EXTENSIONS[] = {
        L".jpg", L".jpeg", L".png", L".gif", L".bmp",
        L".tif", L".tiff", L".webp", L".heic", L".heif",
    };

    HRESULT RegisterThumbnailProvider()
    {
        WCHAR dllPath[MAX_PATH];
        HRESULT hr = GetDllPath(dllPath, ARRAYSIZE(dllPath));
        if (FAILED(hr)) return hr;

        WCHAR clsidStr[64];
        if (StringFromGUID2(CLSID_OneSyncThumbnailProvider, clsidStr, ARRAYSIZE(clsidStr)) == 0)
            return E_FAIL;

        // CLSID -> InProcServer32 -> DLL path
        WCHAR subkey[256];
        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s", clsidStr);
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, nullptr, L"OneSync - Thumbnail Provider");
        if (FAILED(hr)) return hr;

        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s\\InProcServer32", clsidStr);
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, nullptr, dllPath);
        if (FAILED(hr)) return hr;
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, L"ThreadingModel", L"Apartment");
        if (FAILED(hr)) return hr;

        // For each image extension, register us as the IThumbnailProvider handler
        for (size_t i = 0; i < ARRAYSIZE(THUMB_EXTENSIONS); i++)
        {
            // HKLM\Software\Classes\.<ext>\ShellEx\{thumb-provider-iid}
            StringCchPrintfW(subkey, ARRAYSIZE(subkey),
                L"SOFTWARE\\Classes\\%s\\ShellEx\\%s",
                THUMB_EXTENSIONS[i], THUMBNAIL_PROVIDER_KEY);
            WriteRegString(HKEY_LOCAL_MACHINE, subkey, nullptr, clsidStr);
        }
        return S_OK;
    }

    void UnregisterThumbnailProvider()
    {
        WCHAR clsidStr[64];
        if (StringFromGUID2(CLSID_OneSyncThumbnailProvider, clsidStr, ARRAYSIZE(clsidStr)) == 0) return;

        WCHAR subkey[256];
        for (size_t i = 0; i < ARRAYSIZE(THUMB_EXTENSIONS); i++)
        {
            StringCchPrintfW(subkey, ARRAYSIZE(subkey),
                L"SOFTWARE\\Classes\\%s\\ShellEx\\%s",
                THUMB_EXTENSIONS[i], THUMBNAIL_PROVIDER_KEY);
            RegDeleteKeyExW(HKEY_LOCAL_MACHINE, subkey, KEY_WOW64_64KEY, 0);
        }

        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s\\InProcServer32", clsidStr);
        RegDeleteKeyExW(HKEY_CLASSES_ROOT, subkey, KEY_WOW64_64KEY, 0);
        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s", clsidStr);
        RegDeleteKeyExW(HKEY_CLASSES_ROOT, subkey, KEY_WOW64_64KEY, 0);
    }

    // Register one CLSID + its ShellIconOverlayIdentifier entry.
    // Windows scans this list alphabetically at logon and honours only the first
    // ~15 entries (with a separate, smaller cap on distinct overlay image slots).
    // The overlayName passed in is prefixed with 6 leading spaces by callers so
    // we sort ahead of OneDrive (4 spaces) and Office's AccExtIco (3 spaces).
    HRESULT RegisterClsid(REFCLSID clsid, PCWSTR friendlyName, PCWSTR overlayName)
    {
        WCHAR dllPath[MAX_PATH];
        HRESULT hr = GetDllPath(dllPath, ARRAYSIZE(dllPath));
        if (FAILED(hr)) return hr;

        WCHAR clsidStr[64];
        if (StringFromGUID2(clsid, clsidStr, ARRAYSIZE(clsidStr)) == 0)
            return E_FAIL;

        WCHAR subkey[256];
        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s", clsidStr);
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, nullptr, friendlyName);
        if (FAILED(hr)) return hr;

        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s\\InProcServer32", clsidStr);
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, nullptr, dllPath);
        if (FAILED(hr)) return hr;
        hr = WriteRegString(HKEY_CLASSES_ROOT, subkey, L"ThreadingModel", L"Apartment");
        if (FAILED(hr)) return hr;

        // Add to the ShellIconOverlayIdentifiers list - Windows scans this on
        // login. The leading space pushes us up alphabetically.
        StringCchPrintfW(subkey, ARRAYSIZE(subkey),
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\%s",
            overlayName);
        hr = WriteRegString(HKEY_LOCAL_MACHINE, subkey, nullptr, clsidStr);
        return hr;
    }

    HRESULT UnregisterClsid(REFCLSID clsid, PCWSTR overlayName)
    {
        WCHAR clsidStr[64];
        if (StringFromGUID2(clsid, clsidStr, ARRAYSIZE(clsidStr)) == 0)
            return E_FAIL;

        WCHAR subkey[256];
        StringCchPrintfW(subkey, ARRAYSIZE(subkey),
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\%s",
            overlayName);
        RegDeleteKeyExW(HKEY_LOCAL_MACHINE, subkey, KEY_WOW64_64KEY, 0);

        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s\\InProcServer32", clsidStr);
        RegDeleteKeyExW(HKEY_CLASSES_ROOT, subkey, KEY_WOW64_64KEY, 0);
        StringCchPrintfW(subkey, ARRAYSIZE(subkey), L"CLSID\\%s", clsidStr);
        RegDeleteKeyExW(HKEY_CLASSES_ROOT, subkey, KEY_WOW64_64KEY, 0);
        return S_OK;
    }

}

STDAPI DllRegisterServer()
{
    // 6 leading spaces sort these ahead of OneDrive ("    OneDriveN", 4 spaces)
    // and Office ("   AccExtIcoN", 3 spaces). The shell only honours the first
    // ~15 overlay handlers and has a limited set of overlay image slots, so
    // sorting high is what makes our badges reliably appear.
    HRESULT hr;
    hr = RegisterClsid(CLSID_OneSyncOverlaySyncing, L"OneSync - Syncing",
                       L"      OneSync-Syncing");
    if (FAILED(hr)) return hr;
    hr = RegisterClsid(CLSID_OneSyncOverlaySynced,  L"OneSync - Synced",
                       L"      OneSync-Synced");
    if (FAILED(hr)) return hr;
    hr = RegisterClsid(CLSID_OneSyncOverlayError,   L"OneSync - Error",
                       L"      OneSync-Error");
    if (FAILED(hr)) return hr;

    // Register thumbnail provider (separate COM class, registered per image extension)
    RegisterThumbnailProvider();

    // Notify Explorer to reload the overlay + thumbnail handler lists
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    UnregisterClsid(CLSID_OneSyncOverlaySyncing, L"      OneSync-Syncing");
    UnregisterClsid(CLSID_OneSyncOverlaySynced,  L"      OneSync-Synced");
    UnregisterClsid(CLSID_OneSyncOverlayError,   L"      OneSync-Error");
    UnregisterThumbnailProvider();

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
