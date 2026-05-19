#include "Overlay.h"
#include "Thumbnail.h"
#include <new>
#include <strsafe.h>
#include <objbase.h>

// Stable CLSID for the thumbnail provider COM class.
const CLSID CLSID_OneSyncThumbnailProvider =
    { 0x6f1a4ad0, 0x3b8e, 0x49b1, { 0x9c, 0x8d, 0x1d, 0x3e, 0x2c, 0x5a, 0x12, 0x00 } };

COneSyncThumbnailProvider::COneSyncThumbnailProvider()
    : m_ref(1)
{
    m_filePath[0] = L'\0';
    InterlockedIncrement(&g_lockCount);
}

COneSyncThumbnailProvider::~COneSyncThumbnailProvider()
{
    InterlockedDecrement(&g_lockCount);
}

IFACEMETHODIMP COneSyncThumbnailProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    if (riid == IID_IUnknown)
        *ppv = static_cast<IInitializeWithFile*>(this);
    else if (riid == IID_IInitializeWithFile)
        *ppv = static_cast<IInitializeWithFile*>(this);
    else if (riid == IID_IThumbnailProvider)
        *ppv = static_cast<IThumbnailProvider*>(this);
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) COneSyncThumbnailProvider::AddRef()
{
    return InterlockedIncrement(&m_ref);
}

IFACEMETHODIMP_(ULONG) COneSyncThumbnailProvider::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) delete this;
    return r;
}

IFACEMETHODIMP COneSyncThumbnailProvider::Initialize(LPCWSTR pszFilePath, DWORD /*grfMode*/)
{
    if (pszFilePath == nullptr) return E_INVALIDARG;
    StringCchCopyW(m_filePath, ARRAYSIZE(m_filePath), pszFilePath);
    return S_OK;
}

IFACEMETHODIMP COneSyncThumbnailProvider::GetThumbnail(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha)
{
    if (phbmp == nullptr || pdwAlpha == nullptr) return E_POINTER;
    *phbmp = nullptr;
    *pdwAlpha = WTSAT_UNKNOWN;

    if (m_filePath[0] == L'\0') return E_UNEXPECTED;

    // Translate "H:\path" -> "C:\Users\...\Drives\Home\path" using the same
    // drive-mapping cache the overlay handler uses. If translation succeeds
    // AND the local file has a OneSyncThumb ADS, serve our cached thumbnail.
    WCHAR localPath[MAX_PATH * 2];
    bool isInOneSyncDrive = TranslateToLocalPath(m_filePath, localPath, ARRAYSIZE(localPath));

    if (isInOneSyncDrive)
    {
        HRESULT hr = TryReadOneSyncThumbAds(cx, phbmp, pdwAlpha);
        if (SUCCEEDED(hr) && *phbmp != nullptr) return hr;
        // Fall through to file content (useful once the file has been hydrated)
    }

    // Either the file is outside our drives (regular system file with the
    // same extension we registered) OR it's in our drive but has no ADS yet
    // (e.g. hydrated full content). Use WIC to decode the actual file content.
    return FallbackFromFileContent(cx, phbmp, pdwAlpha);
}

HRESULT COneSyncThumbnailProvider::TryReadOneSyncThumbAds(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha)
{
    WCHAR localPath[MAX_PATH * 2];
    if (!TranslateToLocalPath(m_filePath, localPath, ARRAYSIZE(localPath))) return E_FAIL;

    WCHAR adsPath[MAX_PATH * 3];
    if (FAILED(StringCchPrintfW(adsPath, ARRAYSIZE(adsPath), L"%s:OneSyncThumb", localPath)))
        return E_FAIL;

    HANDLE h = CreateFileW(adsPath, GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return E_FAIL;

    LARGE_INTEGER size;
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0 || size.QuadPart > 8 * 1024 * 1024)
    {
        CloseHandle(h);
        return E_FAIL;
    }

    // Load ADS bytes into a memory stream
    HGLOBAL hGlobal = GlobalAlloc(GMEM_MOVEABLE, (SIZE_T)size.QuadPart);
    if (hGlobal == nullptr) { CloseHandle(h); return E_OUTOFMEMORY; }
    void* pBuf = GlobalLock(hGlobal);
    DWORD readBytes = 0;
    BOOL ok = ReadFile(h, pBuf, (DWORD)size.QuadPart, &readBytes, nullptr);
    GlobalUnlock(hGlobal);
    CloseHandle(h);
    if (!ok || readBytes != size.QuadPart) { GlobalFree(hGlobal); return E_FAIL; }

    IStream* stream = nullptr;
    HRESULT hr = CreateStreamOnHGlobal(hGlobal, TRUE, &stream); // takes ownership
    if (FAILED(hr)) { GlobalFree(hGlobal); return hr; }

    hr = DecodeJpegStreamToHBITMAP(stream, cx, phbmp, pdwAlpha);
    stream->Release();
    return hr;
}

HRESULT COneSyncThumbnailProvider::FallbackFromFileContent(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha)
{
    // For files outside our drives OR hydrated images, decode the real file
    // content with WIC. This preserves system thumbnail behaviour for all
    // image files of our registered extensions when our handler is the OS's
    // chosen one (we override the default jpg/png/etc handler when registered).
    IWICImagingFactory* factory = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;

    IWICBitmapDecoder* decoder = nullptr;
    hr = factory->CreateDecoderFromFilename(m_filePath, nullptr, GENERIC_READ,
        WICDecodeMetadataCacheOnLoad, &decoder);
    if (FAILED(hr)) { factory->Release(); return hr; }

    IWICBitmapFrameDecode* frame = nullptr;
    hr = decoder->GetFrame(0, &frame);
    if (SUCCEEDED(hr))
    {
        // Wrap the frame as a stream-like source and use the shared path
        IWICFormatConverter* converter = nullptr;
        hr = factory->CreateFormatConverter(&converter);
        if (SUCCEEDED(hr))
        {
            hr = converter->Initialize(frame, GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
            if (SUCCEEDED(hr))
            {
                UINT srcW = 0, srcH = 0;
                converter->GetSize(&srcW, &srcH);

                UINT dstW = cx, dstH = cx;
                if (srcW > 0 && srcH > 0)
                {
                    double scale = (double)cx / (double)((srcW > srcH) ? srcW : srcH);
                    dstW = (UINT)(srcW * scale);
                    dstH = (UINT)(srcH * scale);
                    if (dstW < 1) dstW = 1;
                    if (dstH < 1) dstH = 1;
                }

                IWICBitmapScaler* scaler = nullptr;
                hr = factory->CreateBitmapScaler(&scaler);
                IWICBitmapSource* src = converter;
                if (SUCCEEDED(hr))
                {
                    hr = scaler->Initialize(converter, dstW, dstH, WICBitmapInterpolationModeFant);
                    if (SUCCEEDED(hr)) src = scaler;
                }

                BITMAPINFO bmi = {};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = (LONG)dstW;
                bmi.bmiHeader.biHeight = -(LONG)dstH; // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                void* pBits = nullptr;
                HBITMAP hbmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &pBits, nullptr, 0);
                if (hbmp != nullptr && pBits != nullptr)
                {
                    UINT stride = dstW * 4;
                    UINT cbBuffer = stride * dstH;
                    hr = src->CopyPixels(nullptr, stride, cbBuffer, (BYTE*)pBits);
                    if (SUCCEEDED(hr))
                    {
                        *phbmp = hbmp;
                        *pdwAlpha = WTSAT_ARGB;
                    }
                    else { DeleteObject(hbmp); }
                }
                else { hr = E_FAIL; }

                if (scaler) scaler->Release();
            }
            converter->Release();
        }
        frame->Release();
    }
    decoder->Release();
    factory->Release();
    return hr;
}

HRESULT COneSyncThumbnailProvider::DecodeJpegStreamToHBITMAP(IStream* stream, UINT cx,
                                                             HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha)
{
    IWICImagingFactory* factory = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;

    IWICBitmapDecoder* decoder = nullptr;
    hr = factory->CreateDecoderFromStream(stream, nullptr,
        WICDecodeMetadataCacheOnLoad, &decoder);
    if (FAILED(hr)) { factory->Release(); return hr; }

    IWICBitmapFrameDecode* frame = nullptr;
    hr = decoder->GetFrame(0, &frame);
    if (FAILED(hr)) { decoder->Release(); factory->Release(); return hr; }

    IWICFormatConverter* converter = nullptr;
    hr = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(hr))
    {
        hr = converter->Initialize(frame, GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
    }

    UINT srcW = 0, srcH = 0;
    if (SUCCEEDED(hr)) converter->GetSize(&srcW, &srcH);

    UINT dstW = cx, dstH = cx;
    if (srcW > 0 && srcH > 0)
    {
        double scale = (double)cx / (double)((srcW > srcH) ? srcW : srcH);
        dstW = (UINT)(srcW * scale);
        dstH = (UINT)(srcH * scale);
        if (dstW < 1) dstW = 1;
        if (dstH < 1) dstH = 1;
    }

    IWICBitmapScaler* scaler = nullptr;
    if (SUCCEEDED(hr))
    {
        hr = factory->CreateBitmapScaler(&scaler);
        if (SUCCEEDED(hr))
            hr = scaler->Initialize(converter, dstW, dstH, WICBitmapInterpolationModeFant);
    }

    IWICBitmapSource* src = SUCCEEDED(hr) && scaler ? (IWICBitmapSource*)scaler : (IWICBitmapSource*)converter;

    if (SUCCEEDED(hr))
    {
        BITMAPINFO bmi = {};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = (LONG)dstW;
        bmi.bmiHeader.biHeight = -(LONG)dstH;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        void* pBits = nullptr;
        HBITMAP hbmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &pBits, nullptr, 0);
        if (hbmp != nullptr && pBits != nullptr)
        {
            UINT stride = dstW * 4;
            UINT cbBuffer = stride * dstH;
            hr = src->CopyPixels(nullptr, stride, cbBuffer, (BYTE*)pBits);
            if (SUCCEEDED(hr))
            {
                *phbmp = hbmp;
                *pdwAlpha = WTSAT_ARGB;
            }
            else { DeleteObject(hbmp); }
        }
        else { hr = E_FAIL; }
    }

    if (scaler) scaler->Release();
    if (converter) converter->Release();
    frame->Release();
    decoder->Release();
    factory->Release();
    return hr;
}

// --- Class factory ----------------------------------------------------------

COneSyncThumbnailProviderFactory::COneSyncThumbnailProviderFactory() : m_ref(1)
{
    InterlockedIncrement(&g_lockCount);
}

COneSyncThumbnailProviderFactory::~COneSyncThumbnailProviderFactory()
{
    InterlockedDecrement(&g_lockCount);
}

IFACEMETHODIMP COneSyncThumbnailProviderFactory::QueryInterface(REFIID riid, void** ppv)
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

IFACEMETHODIMP_(ULONG) COneSyncThumbnailProviderFactory::AddRef()
{
    return InterlockedIncrement(&m_ref);
}

IFACEMETHODIMP_(ULONG) COneSyncThumbnailProviderFactory::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) delete this;
    return r;
}

IFACEMETHODIMP COneSyncThumbnailProviderFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
{
    if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    auto* inst = new (std::nothrow) COneSyncThumbnailProvider();
    if (inst == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = inst->QueryInterface(riid, ppv);
    inst->Release();
    return hr;
}

IFACEMETHODIMP COneSyncThumbnailProviderFactory::LockServer(BOOL fLock)
{
    if (fLock) InterlockedIncrement(&g_lockCount);
    else InterlockedDecrement(&g_lockCount);
    return S_OK;
}
