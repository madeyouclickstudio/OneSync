// OneSync - shell icon overlay handler
//
// Provides four overlay states for files inside any OneSync sync
// root. State is recorded by the main app as a single byte in an NTFS
// alternate data stream on the *local* NTFS file (under
// %LOCALAPPDATA%\OneSync\Drives\<DriveName>\). Each of the four
// COneSyncOverlay COM classes matches one specific state value.
//
// The handler also accepts paths beneath any active mount point letter
// (H:\ etc) by translating them back to the local NTFS path via a mapping
// it reads from the registry at process-attach time.

#pragma once

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <olectl.h>
#include <new>
#include <unknwn.h>

// State values (must match OneSync.Sync.SyncOverlayState in C# code).
// A handler answers for a *set* of states (see m_stateMask): the green tick
// covers both CloudOnly and Synced - a cloud placeholder is just as safe as a
// downloaded copy, so to the user it reads simply as "this file is fine".
constexpr BYTE STATE_CLOUD_ONLY = 0;
constexpr BYTE STATE_SYNCING    = 1;
constexpr BYTE STATE_SYNCED     = 2;
constexpr BYTE STATE_ERROR      = 3;

// Stable CLSIDs (one per badged state). Generated once and frozen.
// Syncing: {6f1a4ad0-3b8e-49b1-9c8d-1d3e2c5a1102}
// Synced:  {6f1a4ad0-3b8e-49b1-9c8d-1d3e2c5a1103}
// Error:   {6f1a4ad0-3b8e-49b1-9c8d-1d3e2c5a1104}
// (The retired CloudOnly CLSID ...1101 is cleaned up at registration time.)
extern const CLSID CLSID_OneSyncOverlaySyncing;
extern const CLSID CLSID_OneSyncOverlaySynced;
extern const CLSID CLSID_OneSyncOverlayError;
extern const CLSID CLSID_OneSyncThumbnailProvider;

extern HINSTANCE g_hInstance;
extern LONG      g_lockCount;

class COneSyncOverlay : public IShellIconOverlayIdentifier
{
public:
    COneSyncOverlay(BYTE stateMask, int iconIndex, REFCLSID clsid);
    virtual ~COneSyncOverlay();

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IShellIconOverlayIdentifier
    IFACEMETHODIMP GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags) override;
    IFACEMETHODIMP GetPriority(int* pPriority) override;
    IFACEMETHODIMP IsMemberOf(PCWSTR pwszPath, DWORD dwAttrib) override;

private:
    LONG  m_ref;
    BYTE  m_stateMask;
    int   m_iconIndex;
    CLSID m_clsid;
};

// Class factory shared by all four overlay variants. ProvidedCLSID determines
// which COneSyncOverlay configuration to spawn.
class COneSyncOverlayFactory : public IClassFactory
{
public:
    COneSyncOverlayFactory(BYTE stateMask, int iconIndex, REFCLSID clsid);
    virtual ~COneSyncOverlayFactory();

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override;
    IFACEMETHODIMP LockServer(BOOL fLock) override;

private:
    LONG  m_ref;
    BYTE  m_stateMask;
    int   m_iconIndex;
    CLSID m_clsid;
};

// --- Path resolution / mapping cache ----------------------------------------

// On first call, reads HKCU\SOFTWARE\OneSync\DriveMappings
// (semicolon-separated "H=C:\path;I=C:\path;..."). Returns true if the path was
// successfully translated to a local NTFS path under the cache; otherwise
// returns false and the caller should reject the file.
bool TranslateToLocalPath(PCWSTR inputPath, PWSTR outputPath, size_t outputCch);

// Reads the OneSync ADS from a local NTFS file. Returns the state byte
// on success; returns 0xFF if no ADS exists (caller should ignore).
BYTE ReadSyncStateAds(PCWSTR localPath);
