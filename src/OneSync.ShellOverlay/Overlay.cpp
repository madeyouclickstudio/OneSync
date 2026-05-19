#include "Overlay.h"
#include <string.h>
#include <strsafe.h>

// CLSID constants
const CLSID CLSID_OneSyncOverlaySyncing = { 0x6f1a4ad0, 0x3b8e, 0x49b1, { 0x9c, 0x8d, 0x1d, 0x3e, 0x2c, 0x5a, 0x11, 0x02 } };
const CLSID CLSID_OneSyncOverlaySynced  = { 0x6f1a4ad0, 0x3b8e, 0x49b1, { 0x9c, 0x8d, 0x1d, 0x3e, 0x2c, 0x5a, 0x11, 0x03 } };
const CLSID CLSID_OneSyncOverlayError   = { 0x6f1a4ad0, 0x3b8e, 0x49b1, { 0x9c, 0x8d, 0x1d, 0x3e, 0x2c, 0x5a, 0x11, 0x04 } };

// --- COneSyncOverlay -----------------------------------------------------------

COneSyncOverlay::COneSyncOverlay(BYTE stateMask, int iconIndex, REFCLSID clsid)
    : m_ref(1), m_stateMask(stateMask), m_iconIndex(iconIndex), m_clsid(clsid)
{
    InterlockedIncrement(&g_lockCount);
}

COneSyncOverlay::~COneSyncOverlay()
{
    InterlockedDecrement(&g_lockCount);
}

IFACEMETHODIMP COneSyncOverlay::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IShellIconOverlayIdentifier)
    {
        *ppv = static_cast<IShellIconOverlayIdentifier*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) COneSyncOverlay::AddRef()
{
    return InterlockedIncrement(&m_ref);
}

IFACEMETHODIMP_(ULONG) COneSyncOverlay::Release()
{
    LONG ref = InterlockedDecrement(&m_ref);
    if (ref == 0) delete this;
    return ref;
}

IFACEMETHODIMP COneSyncOverlay::GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags)
{
    // Return the path to our own DLL and the icon resource index
    if (GetModuleFileNameW(g_hInstance, pwszIconFile, cchMax) == 0)
        return HRESULT_FROM_WIN32(GetLastError());
    *pIndex = m_iconIndex;
    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

IFACEMETHODIMP COneSyncOverlay::GetPriority(int* pPriority)
{
    // 0 = highest priority. We're equal among our 4 states; only one matches per file.
    *pPriority = 0;
    return S_OK;
}

IFACEMETHODIMP COneSyncOverlay::IsMemberOf(PCWSTR pwszPath, DWORD /*dwAttrib*/)
{
    if (pwszPath == nullptr) return S_FALSE;

    WCHAR localPath[MAX_PATH * 2];
    if (!TranslateToLocalPath(pwszPath, localPath, ARRAYSIZE(localPath)))
        return S_FALSE;

    BYTE state = ReadSyncStateAds(localPath);
    if (state > 3) return S_FALSE; // no ADS (0xFF) or a corrupt value - leave alone
    // m_stateMask is a bitmask of the states this handler answers for - e.g. the
    // green tick covers both cloud-only and fully-synced files (both are "safe").
    return ((1 << state) & m_stateMask) ? S_OK : S_FALSE;
}

// --- COneSyncOverlayFactory ----------------------------------------------------

COneSyncOverlayFactory::COneSyncOverlayFactory(BYTE stateMask, int iconIndex, REFCLSID clsid)
    : m_ref(1), m_stateMask(stateMask), m_iconIndex(iconIndex), m_clsid(clsid)
{
    InterlockedIncrement(&g_lockCount);
}

COneSyncOverlayFactory::~COneSyncOverlayFactory()
{
    InterlockedDecrement(&g_lockCount);
}

IFACEMETHODIMP COneSyncOverlayFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
    {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) COneSyncOverlayFactory::AddRef()
{
    return InterlockedIncrement(&m_ref);
}

IFACEMETHODIMP_(ULONG) COneSyncOverlayFactory::Release()
{
    LONG ref = InterlockedDecrement(&m_ref);
    if (ref == 0) delete this;
    return ref;
}

IFACEMETHODIMP COneSyncOverlayFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
{
    if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    auto* overlay = new (std::nothrow) COneSyncOverlay(m_stateMask, m_iconIndex, m_clsid);
    if (overlay == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = overlay->QueryInterface(riid, ppv);
    overlay->Release();
    return hr;
}

IFACEMETHODIMP COneSyncOverlayFactory::LockServer(BOOL fLock)
{
    if (fLock) InterlockedIncrement(&g_lockCount);
    else InterlockedDecrement(&g_lockCount);
    return S_OK;
}

// --- Path translation --------------------------------------------------------

namespace {
    struct DriveMapping
    {
        WCHAR letter;
        WCHAR localRoot[MAX_PATH];
    };

    constexpr int kMaxMappings = 12;
    DriveMapping g_mappings[kMaxMappings];
    int          g_mappingCount = 0;
    bool         g_mappingsLoaded = false;
    CRITICAL_SECTION g_mappingsLock;
    bool         g_mappingsLockInit = false;

    // Retry the registry read every few seconds while no mappings exist - the
    // app may not have written them yet (e.g. Explorer started before OneSync.exe).
    constexpr DWORD kMappingsRetryMs = 5000;
    DWORD g_lastLoadAttemptTick = 0;

    void LoadMappingsLocked()
    {
        g_mappingCount = 0;
        g_lastLoadAttemptTick = GetTickCount();

        HKEY hKey = nullptr;
        // HKCU first (per-user, written by the app at runtime), HKLM as fallback.
        LONG status = RegOpenKeyExW(
            HKEY_CURRENT_USER,
            L"SOFTWARE\\OneSync",
            0, KEY_READ, &hKey);
        if (status != ERROR_SUCCESS)
        {
            status = RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                L"SOFTWARE\\OneSync",
                0, KEY_READ | KEY_WOW64_64KEY, &hKey);
            if (status != ERROR_SUCCESS) return;
        }

        WCHAR buffer[2048];
        DWORD bytes = sizeof(buffer);
        DWORD type = 0;
        status = RegQueryValueExW(hKey, L"DriveMappings", nullptr, &type, (LPBYTE)buffer, &bytes);
        RegCloseKey(hKey);
        if (status != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ))
        {
            return;
        }

        // Expand environment variables (e.g. %LOCALAPPDATA%)
        WCHAR expanded[4096];
        DWORD expandedLen = ExpandEnvironmentStringsW(buffer, expanded, ARRAYSIZE(expanded));
        if (expandedLen == 0 || expandedLen > ARRAYSIZE(expanded)) { g_mappingsLoaded = true; return; }

        // Parse semicolon-separated entries: "H=C:\path;I=C:\path"
        WCHAR* context = nullptr;
        WCHAR* token = wcstok_s(expanded, L";", &context);
        while (token != nullptr && g_mappingCount < kMaxMappings)
        {
            WCHAR* eq = wcschr(token, L'=');
            if (eq != nullptr && eq != token)
            {
                WCHAR letter = (WCHAR)towupper(token[0]);
                if (letter >= L'A' && letter <= L'Z')
                {
                    g_mappings[g_mappingCount].letter = letter;
                    // Skip the '=' and copy path
                    StringCchCopyW(g_mappings[g_mappingCount].localRoot,
                                   ARRAYSIZE(g_mappings[g_mappingCount].localRoot),
                                   eq + 1);
                    // Trim trailing backslash for clean concatenation
                    size_t len = wcslen(g_mappings[g_mappingCount].localRoot);
                    if (len > 0 && g_mappings[g_mappingCount].localRoot[len - 1] == L'\\')
                        g_mappings[g_mappingCount].localRoot[len - 1] = L'\0';
                    g_mappingCount++;
                }
            }
            token = wcstok_s(nullptr, L";", &context);
        }

        if (g_mappingCount > 0) g_mappingsLoaded = true;
    }
}

void InitMappingsLock()
{
    if (!g_mappingsLockInit)
    {
        InitializeCriticalSection(&g_mappingsLock);
        g_mappingsLockInit = true;
    }
}

void DestroyMappingsLock()
{
    if (g_mappingsLockInit)
    {
        DeleteCriticalSection(&g_mappingsLock);
        g_mappingsLockInit = false;
    }
}

bool TranslateToLocalPath(PCWSTR inputPath, PWSTR outputPath, size_t outputCch)
{
    if (inputPath == nullptr || outputPath == nullptr || outputCch == 0) return false;

    // Lazy-load + periodic retry until we get mappings (app may not have started yet).
    EnterCriticalSection(&g_mappingsLock);
    if (!g_mappingsLoaded)
    {
        DWORD now = GetTickCount();
        if (g_lastLoadAttemptTick == 0 ||
            (now - g_lastLoadAttemptTick) > kMappingsRetryMs)
        {
            LoadMappingsLocked();
        }
    }
    LeaveCriticalSection(&g_mappingsLock);

    if (g_mappingCount == 0) return false;

    size_t inputLen = wcslen(inputPath);
    if (inputLen < 3) return false;

    // Match by drive letter: "H:\..." or "H:..."
    WCHAR inputLetter = (WCHAR)towupper(inputPath[0]);
    if (inputPath[1] != L':' || (inputPath[2] != L'\\' && inputPath[2] != L'/'))
    {
        // Not a drive-letter path - check if it's already a local path under any
        // of our managed roots.
        for (int i = 0; i < g_mappingCount; i++)
        {
            size_t rootLen = wcslen(g_mappings[i].localRoot);
            if (inputLen >= rootLen &&
                _wcsnicmp(inputPath, g_mappings[i].localRoot, rootLen) == 0)
            {
                StringCchCopyW(outputPath, outputCch, inputPath);
                return true;
            }
        }
        return false;
    }

    // Find matching drive mapping.
    for (int i = 0; i < g_mappingCount; i++)
    {
        if (g_mappings[i].letter == inputLetter)
        {
            // Replace "H:" prefix with the local root
            HRESULT hr = StringCchPrintfW(outputPath, outputCch, L"%s%s",
                g_mappings[i].localRoot, inputPath + 2);
            return SUCCEEDED(hr);
        }
    }

    return false;
}

BYTE ReadSyncStateAds(PCWSTR localPath)
{
    if (localPath == nullptr) return 0xFF;

    // Build the ADS path: "C:\foo\bar.txt:OneSync"
    WCHAR adsPath[MAX_PATH * 2];
    HRESULT hr = StringCchPrintfW(adsPath, ARRAYSIZE(adsPath), L"%s:OneSync", localPath);
    if (FAILED(hr)) return 0xFF;

    HANDLE h = CreateFileW(adsPath, GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return 0xFF;

    BYTE state = 0xFF;
    DWORD read = 0;
    if (ReadFile(h, &state, 1, &read, nullptr) && read == 1)
    {
        // valid
    }
    else
    {
        state = 0xFF;
    }
    CloseHandle(h);
    return state;
}
