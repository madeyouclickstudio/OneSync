// OneSync - IThumbnailProvider shell extension.
//
// Renders previews for image files inside OneSync-managed drives that haven't
// been hydrated yet. The C# ThumbnailPrefetcher fetches small JPEG thumbnails
// from Microsoft Graph and writes them as an NTFS alternate data stream on
// each placeholder (filename:OneSyncThumb). This handler reads that ADS via
// WIC and returns an HBITMAP to Explorer.
//
// Registered for a curated list of image extensions only. For files OUTSIDE
// our drives (regular system files with the same extension), we fall back
// to standard WIC decoding of the actual file content, so we don't break
// thumbnails for the rest of the user's photos.

#pragma once

#include <windows.h>
#include <shlobj.h>
#include <thumbcache.h>
#include <wincodec.h>

class COneSyncThumbnailProvider :
    public IInitializeWithFile,
    public IThumbnailProvider
{
public:
    COneSyncThumbnailProvider();
    virtual ~COneSyncThumbnailProvider();

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IInitializeWithFile - Explorer hands us the file path
    IFACEMETHODIMP Initialize(LPCWSTR pszFilePath, DWORD grfMode) override;

    // IThumbnailProvider - return an HBITMAP at the requested size
    IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha) override;

private:
    LONG  m_ref;
    WCHAR m_filePath[MAX_PATH * 2];

    HRESULT TryReadOneSyncThumbAds(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha);
    HRESULT FallbackFromFileContent(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha);
    HRESULT DecodeJpegStreamToHBITMAP(IStream* stream, UINT cx,
                                      HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha);
};

class COneSyncThumbnailProviderFactory : public IClassFactory
{
public:
    COneSyncThumbnailProviderFactory();
    virtual ~COneSyncThumbnailProviderFactory();

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override;
    IFACEMETHODIMP LockServer(BOOL fLock) override;

private:
    LONG m_ref;
};
