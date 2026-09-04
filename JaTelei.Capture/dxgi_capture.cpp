// =============================================================================
//  JaTelei.Capture — dxgi_capture.cpp
//  New engine: WGC + DXGI DDup | D3D11 GPU | NVENC/AMF/QSV | H.264/AV1 | WASAPI
// =============================================================================
#define _WIN32_WINNT 0x0A00
#define WINVER       0x0A00
#define COBJMACROS
#define NOMINMAX

#include "capture.h"

// Standard Windows
#include <windows.h>
#include <unknwn.h>
#include <inspectable.h>
#include <winrt/base.h>

// D3D / DXGI
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <dxgi1_6.h>

// Media Foundation
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <wmcodecdsp.h>

// WASAPI / Audio
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <avrt.h>

// WinRT / Windows Graphics Capture
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

// STL
#include <atomic>
#include <mutex>
#include <thread>
#include <vector>
#include <string>
#include <algorithm>
#include <cstring>
#include <cassert>

// COM helpers
#include <comdef.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

// WinRT namespaces
namespace wgc  = winrt::Windows::Graphics::Capture;
namespace wgd  = winrt::Windows::Graphics::DirectX;
namespace wgdd = winrt::Windows::Graphics::DirectX::Direct3D11;
namespace wf   = winrt::Windows::Foundation;

// ---------------------------------------------------------------------------
// Helpers / macros
// ---------------------------------------------------------------------------
#define CHECK_HR(hr, msg) do { if (FAILED(hr)) { LogError(msg, hr); return hr; } } while(0)
#define SAFE_RELEASE(p)   do { if (p) { (p)->Release(); (p) = nullptr; } } while(0)
#define ALIGN(v, a)       (((v) + (a) - 1) & ~((a) - 1))

static const char* g_logPath = nullptr;  // set in JC_Init

static void LogError(const char* msg, HRESULT hr = 0)
{
    if (!g_logPath) return;
    char buf[512];
    if (hr)
        snprintf(buf, sizeof(buf), "[JC] ERROR %s  hr=0x%08X\n", msg, (unsigned)hr);
    else
        snprintf(buf, sizeof(buf), "[JC] %s\n", msg);
    HANDLE f = CreateFileA(g_logPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        SetFilePointer(f, 0, nullptr, FILE_END);
        DWORD w; WriteFile(f, buf, (DWORD)strlen(buf), &w, nullptr);
        CloseHandle(f);
    }
}

static void LogInfo(const char* msg)
{
    LogError(msg, 0);
}

// ---------------------------------------------------------------------------
// Encoder capability detection helpers
// ---------------------------------------------------------------------------
struct MftEncoderCandidate {
    JC_EncoderVendor vendor;
    JC_Codec         codec;
    CLSID            clsid;
    WCHAR            name[128];
};

static std::vector<MftEncoderCandidate> g_availableEncoders;

static JC_EncoderVendor VendorFromName(const WCHAR* name)
{
    if (wcsstr(name, L"NVENC") || wcsstr(name, L"NVIDIA") || wcsstr(name, L"NV H"))
        return JC_ENCODER_NVENC;
    if (wcsstr(name, L"AMD") || wcsstr(name, L"Radeon") || wcsstr(name, L"AMF") || wcsstr(name, L"H264 Encode"))
        return JC_ENCODER_AMF;
    if (wcsstr(name, L"Intel") || wcsstr(name, L"QSV") || wcsstr(name, L"Quick Sync"))
        return JC_ENCODER_QSV;
    return JC_ENCODER_SOFTWARE;
}

static void EnumerateMftEncoders()
{
    g_availableEncoders.clear();

    // H.264
    {
        MFT_REGISTER_TYPE_INFO in  = { MFMediaType_Video, MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO out = { MFMediaType_Video, MFVideoFormat_H264 };
        IMFActivate** ppActivate = nullptr;
        UINT32 count = 0;
        MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                  MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                  &in, &out, &ppActivate, &count);
        for (UINT32 i = 0; i < count; i++) {
            MftEncoderCandidate c = {};
            c.codec = JC_CODEC_H264;
            WCHAR* nameBuf = nullptr;
            UINT32 nameLen = 0;
            ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &nameBuf, &nameLen);
            if (nameBuf) {
                wcsncpy_s(c.name, nameBuf, _TRUNCATE);
                CoTaskMemFree(nameBuf);
            }
            c.vendor = VendorFromName(c.name);
            CLSID clsid = {};
            ppActivate[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &clsid);
            c.clsid = clsid;
            g_availableEncoders.push_back(c);
            ppActivate[i]->Release();
        }
        CoTaskMemFree(ppActivate);
    }

    // AV1
    {
        MFT_REGISTER_TYPE_INFO in  = { MFMediaType_Video, MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO out = { MFMediaType_Video, MFVideoFormat_AV1  };
        IMFActivate** ppActivate = nullptr;
        UINT32 count = 0;
        MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                  MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                  &in, &out, &ppActivate, &count);
        for (UINT32 i = 0; i < count; i++) {
            MftEncoderCandidate c = {};
            c.codec = JC_CODEC_AV1;
            WCHAR* nameBuf = nullptr;
            UINT32 nameLen = 0;
            ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &nameBuf, &nameLen);
            if (nameBuf) {
                wcsncpy_s(c.name, nameBuf, _TRUNCATE);
                CoTaskMemFree(nameBuf);
            }
            c.vendor = VendorFromName(c.name);
            CLSID clsid = {};
            ppActivate[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &clsid);
            c.clsid = clsid;
            g_availableEncoders.push_back(c);
            ppActivate[i]->Release();
        }
        CoTaskMemFree(ppActivate);
    }

    // Software H.264 fallback
    {
        MFT_REGISTER_TYPE_INFO in  = { MFMediaType_Video, MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO out = { MFMediaType_Video, MFVideoFormat_H264 };
        IMFActivate** ppActivate = nullptr;
        UINT32 count = 0;
        MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                  MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                  &in, &out, &ppActivate, &count);
        for (UINT32 i = 0; i < count; i++) {
            MftEncoderCandidate c = {};
            c.codec = JC_CODEC_H264;
            c.vendor = JC_ENCODER_SOFTWARE;
            WCHAR* nameBuf = nullptr;
            UINT32 nameLen = 0;
            ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &nameBuf, &nameLen);
            if (nameBuf) {
                wcsncpy_s(c.name, nameBuf, _TRUNCATE);
                CoTaskMemFree(nameBuf);
            }
            CLSID clsid = {};
            ppActivate[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &clsid);
            c.clsid = clsid;
            g_availableEncoders.push_back(c);
            ppActivate[i]->Release();
        }
        CoTaskMemFree(ppActivate);
    }
}

static bool FindEncoder(JC_Codec codec, JC_EncoderVendor preferred, MftEncoderCandidate& out)
{
    // Priority order for hardware
    const JC_EncoderVendor hwOrder[] = {
        JC_ENCODER_NVENC, JC_ENCODER_AMF, JC_ENCODER_QSV
    };

    if (preferred == JC_ENCODER_AUTO) {
        // Try hardware vendors in order
        for (auto v : hwOrder) {
            for (auto& c : g_availableEncoders)
                if (c.codec == codec && c.vendor == v) { out = c; return true; }
        }
        // Software fallback
        for (auto& c : g_availableEncoders)
            if (c.codec == codec && c.vendor == JC_ENCODER_SOFTWARE) { out = c; return true; }
        // Last resort: any codec match
        for (auto& c : g_availableEncoders)
            if (c.codec == codec) { out = c; return true; }
        return false;
    }

    for (auto& c : g_availableEncoders)
        if (c.codec == codec && c.vendor == preferred) { out = c; return true; }
    // Fallback within same codec
    for (auto& c : g_availableEncoders)
        if (c.codec == codec) { out = c; return true; }
    return false;
}

// ---------------------------------------------------------------------------
// Global engine state
// ---------------------------------------------------------------------------
struct EngineState {
    // D3D11 core
    ComPtr<ID3D11Device>        d3dDevice;
    ComPtr<ID3D11DeviceContext> d3dCtx;
    ComPtr<ID3D11Multithread>   d3dMultithread;
    ComPtr<IDXGIAdapter1>       adapter;

    // DXGI Desktop Duplication
    ComPtr<IDXGIOutputDuplication> dxgiDup;
    ComPtr<IDXGIOutput1>           dxgiOutput;

    // WGC
    bool                           useWGC = false;
    wgc::GraphicsCaptureSession    wgcSession  = nullptr;
    wgc::GraphicsCaptureItem       wgcItem     = nullptr;
    wgc::Direct3D11CaptureFramePool wgcPool    = nullptr;
    ComPtr<ID3D11Texture2D>        wgcLastTex;
    std::mutex                     wgcMtx;
    bool                           wgcNewFrame = false;

    // D3D11 Video Processor (BGRA -> NV12 + resize)
    ComPtr<ID3D11VideoDevice>           videoDevice;
    ComPtr<ID3D11VideoContext>          videoCtx;
    ComPtr<ID3D11VideoProcessorEnum>    vpEnum;
    ComPtr<ID3D11VideoProcessor>        vp;
    ComPtr<ID3D11VideoProcessorInputView>  vpInView;
    ComPtr<ID3D11VideoProcessorOutputView> vpOutView;
    ComPtr<ID3D11Texture2D>             nv12Tex;

    // Staging texture (CPU fallback, used only when DXGI surface binding fails)
    ComPtr<ID3D11Texture2D>             stagingTex;

    // MFT Encoder
    ComPtr<IMFTransform>        encoder;
    ComPtr<IMFDXGIDeviceManager> devMgr;
    UINT                         devMgrToken = 0;
    LONGLONG                     sampleCount = 0;
    std::atomic<bool>            forceKey    = false;

    // Params
    JC_InitParams params;
    int           srcWidth = 0, srcHeight = 0;
    GUID          videoFmt; // MFVideoFormat_H264 or MFVideoFormat_AV1
    bool          isHwEncoder = false;
    MftEncoderCandidate chosenEncoder;

    // WASAPI
    ComPtr<IMMDeviceEnumerator> mmEnum;
    ComPtr<IMMDevice>           mmDevice;
    ComPtr<IAudioClient>        audioClient;
    ComPtr<IAudioCaptureClient> capClient;
    WAVEFORMATEX*               pWaveFmt = nullptr;
    std::thread                 audioThread;
    std::atomic<bool>           audioRunning = false;
    // Audio ring buffer (raw PCM, lock-free with mutex for simplicity)
    std::mutex                  audioBufMtx;
    std::vector<BYTE>           audioBuf;

    // Encoded audio buffer (AAC - we use MFT AAC encoder)
    ComPtr<IMFTransform>        aacEncoder;
    std::mutex                  audioOutMtx;
    std::vector<BYTE>           audioOutBuf;

    bool initialized = false;
};

static EngineState* g_eng = nullptr;
static std::mutex   g_initMtx;

// ---------------------------------------------------------------------------
// D3D11 + DXGI init
// ---------------------------------------------------------------------------
static HRESULT InitD3D(EngineState* e)
{
    // Enumerate adapters
    ComPtr<IDXGIFactory6> factory;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory6), &factory);
    CHECK_HR(hr, "CreateDXGIFactory1");

    ComPtr<IDXGIAdapter1> adapter;
    if (e->params.adapterIndex == 0) {
        // Pick highest performance GPU
        factory->EnumAdapterByGpuPreference(
            0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter));
    } else {
        factory->EnumAdapters1(e->params.adapterIndex, &adapter);
    }
    if (!adapter) factory->EnumAdapters1(0, &adapter);
    e->adapter = adapter;

    // Create D3D11 device
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL featureLevel;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                           D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                           levels, ARRAYSIZE(levels),
                           D3D11_SDK_VERSION,
                           &e->d3dDevice, &featureLevel, &e->d3dCtx);
    CHECK_HR(hr, "D3D11CreateDevice");

    // Enable D3D11 multithread protection
    hr = e->d3dDevice.As(&e->d3dMultithread);
    if (SUCCEEDED(hr)) e->d3dMultithread->SetMultithreadProtected(TRUE);

    return S_OK;
}

// ---------------------------------------------------------------------------
// DXGI Desktop Duplication init
// ---------------------------------------------------------------------------
static HRESULT InitDxgiDup(EngineState* e)
{
    ComPtr<IDXGIDevice> dxgiDev;
    HRESULT hr = e->d3dDevice.As(&dxgiDev);
    CHECK_HR(hr, "As IDXGIDevice");

    ComPtr<IDXGIAdapter> adapterBase;
    dxgiDev->GetAdapter(&adapterBase);
    ComPtr<IDXGIAdapter1> adapter1;
    adapterBase.As(&adapter1);

    int outIdx = e->params.outputIndex;
    ComPtr<IDXGIOutput> out;
    hr = adapter1->EnumOutputs(outIdx, &out);
    if (FAILED(hr)) { hr = adapter1->EnumOutputs(0, &out); }
    CHECK_HR(hr, "EnumOutputs");

    hr = out.As(&e->dxgiOutput);
    CHECK_HR(hr, "As IDXGIOutput1");

    hr = e->dxgiOutput->DuplicateOutput(e->d3dDevice.Get(), &e->dxgiDup);
    CHECK_HR(hr, "DuplicateOutput");

    DXGI_OUTPUT_DESC desc = {};
    out->GetDesc(&desc);
    e->srcWidth  = desc.DesktopCoordinates.right  - desc.DesktopCoordinates.left;
    e->srcHeight = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;

    char info[128];
    snprintf(info, sizeof(info), "[JC] DXGI DDup: %dx%d", e->srcWidth, e->srcHeight);
    LogInfo(info);
    return S_OK;
}

// ---------------------------------------------------------------------------
// Windows Graphics Capture init
// ---------------------------------------------------------------------------
static HRESULT InitWGC(EngineState* e)
{
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        // Get WinRT D3D device wrapper
        ComPtr<IDXGIDevice> dxgiDev;
        e->d3dDevice.As(&dxgiDev);
        ComPtr<IInspectable> inspectable;
        HRESULT hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDev.Get(), &inspectable);
        if (FAILED(hr)) return hr;
        wgdd::IDirect3DDevice rtDevice = inspectable.as<wgdd::IDirect3DDevice>();

        // Get capture item
        if (e->params.targetKind == JC_TARGET_WINDOW && e->params.windowHandle) {
            HWND hwnd = (HWND)e->params.windowHandle;
            auto factory = winrt::get_activation_factory<
                wgc::GraphicsCaptureItem,
                IGraphicsCaptureItemInterop>();
            hr = factory->CreateForWindow(hwnd, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                          winrt::put_abi(e->wgcItem));
            if (FAILED(hr)) return hr;
        } else {
            // Monitor via HMONITOR
            ComPtr<IDXGIOutput> out;
            ComPtr<IDXGIDevice> dev;
            e->d3dDevice.As(&dev);
            ComPtr<IDXGIAdapter> ad;
            dev->GetAdapter(&ad);
            ad->EnumOutputs(e->params.outputIndex, &out);
            DXGI_OUTPUT_DESC od = {};
            out->GetDesc(&od);
            HMONITOR hmon = od.Monitor;
            auto factory = winrt::get_activation_factory<
                wgc::GraphicsCaptureItem,
                IGraphicsCaptureItemInterop>();
            hr = factory->CreateForMonitor(hmon, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                           winrt::put_abi(e->wgcItem));
            if (FAILED(hr)) return hr;
        }

        auto size = e->wgcItem.Size();
        e->srcWidth  = size.Width;
        e->srcHeight = size.Height;

        // Create frame pool
        e->wgcPool = wgc::Direct3D11CaptureFramePool::Create(
            rtDevice,
            wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, size);

        // Subscribe to new frames
        e->wgcPool.FrameArrived([e](auto& pool, auto&) {
            auto frame = pool.TryGetNextFrame();
            if (!frame) return;
            auto surf = frame.Surface();
            ComPtr<IDirect3DDxgiInterfaceAccess> access =
                surf.as<IDirect3DDxgiInterfaceAccess>().detach();
            ComPtr<ID3D11Texture2D> tex;
            if (access) access->GetInterface(IID_PPV_ARGS(&tex));
            std::lock_guard<std::mutex> lk(e->wgcMtx);
            e->wgcLastTex = tex;
            e->wgcNewFrame = true;
        });

        e->wgcSession = e->wgcPool.CreateCaptureSession(e->wgcItem);
        e->wgcSession.IsBorderRequired(false);
        e->wgcSession.IsCursorCaptureEnabled(true);
        e->wgcSession.StartCapture();

        e->useWGC = true;
        char info[128];
        snprintf(info, sizeof(info), "[JC] WGC: %dx%d", e->srcWidth, e->srcHeight);
        LogInfo(info);
        return S_OK;
    }
    catch (winrt::hresult_error const& ex) {
        LogError("WGC init failed", ex.code().value);
        return E_FAIL;
    }
}

// ---------------------------------------------------------------------------
// D3D11 Video Processor: BGRA/BGRX → NV12 + resize
// ---------------------------------------------------------------------------
static HRESULT InitVideoProcessor(EngineState* e)
{
    int dstW = e->params.dstWidth;
    int dstH = e->params.dstHeight;

    // NV12 output texture
    D3D11_TEXTURE2D_DESC td = {};
    td.Width     = ALIGN(dstW, 16);
    td.Height    = ALIGN(dstH, 16);
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format    = DXGI_FORMAT_NV12;
    td.SampleDesc.Count   = 1;
    td.Usage     = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_VIDEO_ENCODER | D3D11_BIND_RENDER_TARGET;
    HRESULT hr = e->d3dDevice->CreateTexture2D(&td, nullptr, &e->nv12Tex);
    CHECK_HR(hr, "CreateTexture2D NV12");

    // VideoDevice
    hr = e->d3dDevice.As(&e->videoDevice);
    CHECK_HR(hr, "As ID3D11VideoDevice");
    hr = e->d3dCtx.As(&e->videoCtx);
    CHECK_HR(hr, "As ID3D11VideoContext");

    // VideoProcessorEnum
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC vpDesc = {};
    vpDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    vpDesc.InputWidth       = e->srcWidth;
    vpDesc.InputHeight      = e->srcHeight;
    vpDesc.OutputWidth      = td.Width;
    vpDesc.OutputHeight     = td.Height;
    vpDesc.Usage            = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    hr = e->videoDevice->CreateVideoProcessorEnumerator(&vpDesc, &e->vpEnum);
    CHECK_HR(hr, "CreateVideoProcessorEnumerator");

    hr = e->videoDevice->CreateVideoProcessor(e->vpEnum.Get(), 0, &e->vp);
    CHECK_HR(hr, "CreateVideoProcessor");

    // Input stream state
    e->videoCtx->VideoProcessorSetStreamSourceRect(e->vp.Get(), 0, FALSE, nullptr);
    e->videoCtx->VideoProcessorSetStreamDestRect (e->vp.Get(), 0, FALSE, nullptr);

    // Output view on NV12 texture
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd = {};
    ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    ovd.Texture2D.MipSlice = 0;
    hr = e->videoDevice->CreateVideoProcessorOutputView(
        e->nv12Tex.Get(), e->vpEnum.Get(), &ovd, &e->vpOutView);
    CHECK_HR(hr, "CreateVideoProcessorOutputView");

    return S_OK;
}

// ---------------------------------------------------------------------------
// Create input view for a specific BGRA/BGRX texture
// ---------------------------------------------------------------------------
static HRESULT CreateInputView(EngineState* e, ID3D11Texture2D* srcTex, DXGI_FORMAT fmt)
{
    e->vpInView.Reset();
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd = {};
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ivd.Texture2D.MipSlice  = 0;
    ivd.Texture2D.ArraySlice= 0;
    ivd.FourCC = 0; // auto
    HRESULT hr = e->videoDevice->CreateVideoProcessorInputView(
        srcTex, e->vpEnum.Get(), &ivd, &e->vpInView);
    return hr;
}

// ---------------------------------------------------------------------------
// MFT Encoder init
// ---------------------------------------------------------------------------
static HRESULT InitEncoder(EngineState* e)
{
    int dstW = ALIGN(e->params.dstWidth,  16);
    int dstH = ALIGN(e->params.dstHeight, 16);

    // Choose encoder
    JC_Codec wantCodec = e->params.codec;
    if (wantCodec == JC_CODEC_AV1) {
        if (!FindEncoder(JC_CODEC_AV1, e->params.encoderVendor, e->chosenEncoder)) {
            LogInfo("[JC] AV1 not available, falling back to H.264");
            wantCodec = JC_CODEC_H264;
        }
    }
    if (wantCodec == JC_CODEC_H264) {
        if (!FindEncoder(JC_CODEC_H264, e->params.encoderVendor, e->chosenEncoder)) {
            LogError("No H264 encoder found");
            return E_FAIL;
        }
    }
    e->videoFmt = (wantCodec == JC_CODEC_AV1) ? MFVideoFormat_AV1 : MFVideoFormat_H264;
    e->isHwEncoder = (e->chosenEncoder.vendor != JC_ENCODER_SOFTWARE);

    char info[256];
    {
        char narrow[128] = {};
        WideCharToMultiByte(CP_UTF8, 0, e->chosenEncoder.name, -1, narrow, 127, nullptr, nullptr);
        snprintf(info, sizeof(info), "[JC] Encoder: %s (hw=%d)", narrow, (int)e->isHwEncoder);
        LogInfo(info);
    }

    // Activate MFT
    MFT_REGISTER_TYPE_INFO inType  = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outType = { MFMediaType_Video, e->videoFmt };
    UINT32 flags = e->isHwEncoder
        ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
        : MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, &inType, &outType,
                            &ppActivate, &count);
    CHECK_HR(hr, "MFTEnumEx encoder");
    if (count == 0) { CoTaskMemFree(ppActivate); return E_FAIL; }

    // Try to find preferred vendor
    IMFActivate* chosen = ppActivate[0];
    if (e->params.encoderVendor != JC_ENCODER_AUTO) {
        for (UINT32 i = 0; i < count; i++) {
            WCHAR* n = nullptr; UINT32 nl = 0;
            ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &n, &nl);
            if (n) {
                JC_EncoderVendor v = VendorFromName(n);
                CoTaskMemFree(n);
                if (v == e->params.encoderVendor) { chosen = ppActivate[i]; break; }
            }
        }
    }

    hr = chosen->ActivateObject(__uuidof(IMFTransform), (void**)&e->encoder);
    for (UINT32 i = 0; i < count; i++) ppActivate[i]->Release();
    CoTaskMemFree(ppActivate);
    CHECK_HR(hr, "ActivateObject encoder");

    // DXGI Device Manager (for hardware encoder GPU sharing)
    if (e->isHwEncoder) {
        hr = MFCreateDXGIDeviceManager(&e->devMgrToken, &e->devMgr);
        CHECK_HR(hr, "MFCreateDXGIDeviceManager");
        hr = e->devMgr->ResetDevice(e->d3dDevice.Get(), e->devMgrToken);
        CHECK_HR(hr, "devMgr->ResetDevice");
        ComPtr<IMFAttributes> attrs;
        hr = e->encoder->GetAttributes(&attrs);
        if (SUCCEEDED(hr)) {
            attrs->SetUINT32(MF_SA_D3D11_AWARE, TRUE);
            hr = e->encoder->ProcessMessage(
                MFT_MESSAGE_SET_D3D_MANAGER,
                reinterpret_cast<ULONG_PTR>(e->devMgr.Get()));
            if (FAILED(hr)) LogError("SetD3DManager failed (non-fatal)", hr);
        }
    }

    // Input type: NV12
    ComPtr<IMFMediaType> inMT;
    MFCreateMediaType(&inMT);
    inMT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inMT->SetGUID(MF_MT_SUBTYPE,    MFVideoFormat_NV12);
    MFSetAttributeSize(inMT.Get(), MF_MT_FRAME_SIZE, dstW, dstH);
    MFSetAttributeRatio(inMT.Get(), MF_MT_FRAME_RATE, e->params.fps, 1);
    MFSetAttributeRatio(inMT.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    inMT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    hr = e->encoder->SetInputType(0, inMT.Get(), 0);
    CHECK_HR(hr, "SetInputType encoder");

    // Output type: H264/AV1
    ComPtr<IMFMediaType> outMT;
    MFCreateMediaType(&outMT);
    outMT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outMT->SetGUID(MF_MT_SUBTYPE,    e->videoFmt);
    outMT->SetUINT32(MF_MT_AVG_BITRATE, e->params.bitrateKbps * 1000);
    MFSetAttributeSize(outMT.Get(), MF_MT_FRAME_SIZE, dstW, dstH);
    MFSetAttributeRatio(outMT.Get(), MF_MT_FRAME_RATE, e->params.fps, 1);
    MFSetAttributeRatio(outMT.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    outMT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    if (e->videoFmt == MFVideoFormat_H264) {
        outMT->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
        outMT->SetUINT32(MF_MT_VIDEO_PROFILE,  eAVEncH264VProfile_Base);
    }

    hr = e->encoder->SetOutputType(0, outMT.Get(), 0);
    CHECK_HR(hr, "SetOutputType encoder");

    // Begin streaming
    hr = e->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    CHECK_HR(hr, "Begin streaming");
    hr = e->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    CHECK_HR(hr, "Start of stream");

    snprintf(info, sizeof(info), "[JC] Encoder ready %dx%d @%dfps %dkbps",
             dstW, dstH, e->params.fps, e->params.bitrateKbps);
    LogInfo(info);
    return S_OK;
}

// ---------------------------------------------------------------------------
// WASAPI init
// ---------------------------------------------------------------------------
static HRESULT InitWasapi(EngineState* e)
{
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  IID_PPV_ARGS(&e->mmEnum));
    CHECK_HR(hr, "CoCreateInstance MMDeviceEnumerator");

    // Default render endpoint (loopback)
    hr = e->mmEnum->GetDefaultAudioEndpoint(eRender, eConsole, &e->mmDevice);
    CHECK_HR(hr, "GetDefaultAudioEndpoint");

    hr = e->mmDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                               (void**)&e->audioClient);
    CHECK_HR(hr, "Activate IAudioClient");

    hr = e->audioClient->GetMixFormat(&e->pWaveFmt);
    CHECK_HR(hr, "GetMixFormat");

    // Initialize in loopback mode
    hr = e->audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_LOOPBACK,
        10000000LL, 0,
        e->pWaveFmt, nullptr);
    CHECK_HR(hr, "AudioClient Initialize loopback");

    hr = e->audioClient->GetService(IID_PPV_ARGS(&e->capClient));
    CHECK_HR(hr, "GetService IAudioCaptureClient");

    // AAC encoder MFT
    {
        MFT_REGISTER_TYPE_INFO inT  = { MFMediaType_Audio, MFAudioFormat_Float };
        MFT_REGISTER_TYPE_INFO outT = { MFMediaType_Audio, MFAudioFormat_AAC   };
        IMFActivate** pp = nullptr; UINT32 cnt = 0;
        MFTEnumEx(MFT_CATEGORY_AUDIO_ENCODER,
                  MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                  &inT, &outT, &pp, &cnt);
        if (cnt > 0) {
            pp[0]->ActivateObject(__uuidof(IMFTransform), (void**)&e->aacEncoder);
            for (UINT32 i = 0; i < cnt; i++) pp[i]->Release();
            CoTaskMemFree(pp);

            // Configure AAC encoder
            int sampleRate = e->pWaveFmt->nSamplesPerSec;
            int channels   = min((int)e->pWaveFmt->nChannels, 2); // max stereo
            int bitrate    = e->params.audioBitrate > 0 ? e->params.audioBitrate * 1000 : 128000;

            ComPtr<IMFMediaType> aacIn;
            MFCreateMediaType(&aacIn);
            aacIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            aacIn->SetGUID(MF_MT_SUBTYPE,    MFAudioFormat_Float);
            aacIn->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
            aacIn->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels);
            aacIn->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
            aacIn->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 4);
            aacIn->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate * channels * 4);
            e->aacEncoder->SetInputType(0, aacIn.Get(), 0);

            ComPtr<IMFMediaType> aacOut;
            MFCreateMediaType(&aacOut);
            aacOut->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            aacOut->SetGUID(MF_MT_SUBTYPE,    MFAudioFormat_AAC);
            aacOut->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
            aacOut->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels);
            aacOut->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bitrate / 8);
            e->aacEncoder->SetOutputType(0, aacOut.Get(), 0);
            e->aacEncoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            e->aacEncoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
            LogInfo("[JC] AAC encoder ready");
        } else {
            LogInfo("[JC] AAC encoder not found — audio disabled");
            e->params.enableAudio = 0;
        }
    }

    hr = e->audioClient->Start();
    CHECK_HR(hr, "AudioClient Start");

    // Audio capture thread
    e->audioRunning = true;
    e->audioThread = std::thread([e]() {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        while (e->audioRunning.load()) {
            UINT32 packetFrames = 0;
            if (FAILED(e->capClient->GetNextPacketSize(&packetFrames)) || packetFrames == 0) {
                Sleep(5);
                continue;
            }
            BYTE* data = nullptr; DWORD flags = 0;
            UINT32 numFrames = 0;
            HRESULT hr2 = e->capClient->GetBuffer(&data, &numFrames, &flags, nullptr, nullptr);
            if (FAILED(hr2) || numFrames == 0) { Sleep(1); continue; }

            if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) && data) {
                // Only copy stereo/mono float
                int ch = min((int)e->pWaveFmt->nChannels, 2);
                size_t bytes = numFrames * ch * sizeof(float);

                if (e->aacEncoder) {
                    // Feed to AAC encoder
                    ComPtr<IMFSample> sample;
                    ComPtr<IMFMediaBuffer> buf;
                    MFCreateMemoryBuffer((DWORD)bytes, &buf);
                    BYTE* dst = nullptr; DWORD maxLen = 0;
                    buf->Lock(&dst, &maxLen, nullptr);
                    if (ch == (int)e->pWaveFmt->nChannels) {
                        memcpy(dst, data, bytes);
                    } else {
                        // Downmix to stereo
                        float* src = (float*)data;
                        float* out = (float*)dst;
                        int srcCh = e->pWaveFmt->nChannels;
                        for (UINT32 f = 0; f < numFrames; f++) {
                            out[f*2+0] = src[f*srcCh+0];
                            out[f*2+1] = srcCh > 1 ? src[f*srcCh+1] : src[f*srcCh];
                        }
                    }
                    buf->Unlock();
                    buf->SetCurrentLength((DWORD)bytes);
                    MFCreateSample(&sample);
                    sample->AddBuffer(buf.Get());
                    e->aacEncoder->ProcessInput(0, sample.Get(), 0);

                    // Drain output
                    MFT_OUTPUT_DATA_BUFFER outBuf = {};
                    ComPtr<IMFSample> outSample;
                    MFCreateSample(&outSample);
                    ComPtr<IMFMediaBuffer> outMFBuf;
                    MFCreateMemoryBuffer(65536, &outMFBuf);
                    outSample->AddBuffer(outMFBuf.Get());
                    outBuf.pSample = outSample.Get();
                    DWORD status = 0;
                    if (SUCCEEDED(e->aacEncoder->ProcessOutput(0, 1, &outBuf, &status))) {
                        BYTE* od = nullptr; DWORD oLen = 0;
                        outMFBuf->Lock(&od, nullptr, &oLen);
                        if (oLen > 0) {
                            std::lock_guard<std::mutex> lk(e->audioOutMtx);
                            size_t prev = e->audioOutBuf.size();
                            e->audioOutBuf.resize(prev + oLen);
                            memcpy(e->audioOutBuf.data() + prev, od, oLen);
                        }
                        outMFBuf->Unlock();
                    }
                }
            }
            e->capClient->ReleaseBuffer(numFrames);
        }
    });

    LogInfo("[JC] WASAPI loopback started");
    return S_OK;
}

// ---------------------------------------------------------------------------
// Capture one frame → convert to NV12 on GPU
// Returns true if a new frame was produced
// ---------------------------------------------------------------------------
static bool CaptureFrameToNV12(EngineState* e)
{
    ComPtr<ID3D11Texture2D> srcTex;
    DXGI_FORMAT srcFmt = DXGI_FORMAT_B8G8R8A8_UNORM;

    if (e->useWGC) {
        std::lock_guard<std::mutex> lk(e->wgcMtx);
        if (!e->wgcNewFrame || !e->wgcLastTex) return false;
        srcTex = e->wgcLastTex;
        e->wgcNewFrame = false;
    } else {
        // DXGI Desktop Duplication
        ComPtr<IDXGIResource> resource;
        DXGI_OUTDUPL_FRAME_INFO fi = {};
        HRESULT hr = e->dxgiDup->AcquireNextFrame(100, &fi, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr == DXGI_ERROR_ACCESS_LOST || FAILED(hr)) {
            LogError("AcquireNextFrame failed, re-init DXGI", hr);
            e->dxgiDup.Reset();
            InitDxgiDup(e);
            return false;
        }
        resource.As(&srcTex);
        srcFmt = DXGI_FORMAT_B8G8R8A8_UNORM;
    }

    if (!srcTex) {
        if (!e->useWGC) e->dxgiDup->ReleaseFrame();
        return false;
    }

    // Create input view (recreate each frame - cheap)
    HRESULT hr = CreateInputView(e, srcTex.Get(), srcFmt);
    if (FAILED(hr)) {
        LogError("CreateInputView failed", hr);
        if (!e->useWGC) e->dxgiDup->ReleaseFrame();
        return false;
    }

    // GPU blit: BGRA → NV12 + resize
    D3D11_VIDEO_PROCESSOR_STREAM vpStream = {};
    vpStream.Enable = TRUE;
    vpStream.pInputSurface = e->vpInView.Get();

    // Destination rect
    int dstW = ALIGN(e->params.dstWidth,  16);
    int dstH = ALIGN(e->params.dstHeight, 16);
    RECT destRect = { 0, 0, dstW, dstH };
    e->videoCtx->VideoProcessorSetOutputTargetRect(e->vp.Get(), TRUE, &destRect);
    e->videoCtx->VideoProcessorSetStreamSourceRect(e->vp.Get(), 0, FALSE, nullptr);

    hr = e->videoCtx->VideoProcessorBlt(e->vp.Get(), e->vpOutView.Get(), 0, 1, &vpStream);
    if (FAILED(hr)) LogError("VideoProcessorBlt", hr);

    if (!e->useWGC) e->dxgiDup->ReleaseFrame();
    return SUCCEEDED(hr);
}

// ---------------------------------------------------------------------------
// Encode NV12 texture → bitstream
// ---------------------------------------------------------------------------
static HRESULT EncodeNV12(EngineState* e,
    uint8_t* outBuf, int bufSize, int* outBytes, int* outIsKey)
{
    *outBytes = 0;
    *outIsKey = 0;

    int dstW = ALIGN(e->params.dstWidth,  16);
    int dstH = ALIGN(e->params.dstHeight, 16);
    LONGLONG ts  = e->sampleCount * (10000000LL / e->params.fps);
    LONGLONG dur = 10000000LL / e->params.fps;
    e->sampleCount++;

    bool forceKey = e->forceKey.exchange(false);

    // Try zero-copy GPU path: bind NV12 texture directly as DXGI surface
    ComPtr<IMFSample> sample;
    HRESULT hr = MFCreateSample(&sample);
    CHECK_HR(hr, "MFCreateSample");
    sample->SetSampleTime(ts);
    sample->SetSampleDuration(dur);
    if (forceKey)
        sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);

    // Attempt DXGI surface buffer (zero-copy)
    ComPtr<IMFMediaBuffer> mfBuf;
    hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), e->nv12Tex.Get(), 0, FALSE, &mfBuf);
    if (FAILED(hr)) {
        // CPU fallback: copy GPU → staging → CPU → IMFMediaBuffer
        if (!e->stagingTex) {
            D3D11_TEXTURE2D_DESC td = {};
            td.Width     = dstW;
            td.Height    = ALIGN(dstH, 16) * 3 / 2; // NV12 includes UV plane
            td.MipLevels = 1; td.ArraySize = 1;
            td.Format    = DXGI_FORMAT_NV12;
            td.SampleDesc.Count = 1;
            td.Usage     = D3D11_USAGE_STAGING;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            e->d3dDevice->CreateTexture2D(&td, nullptr, &e->stagingTex);
        }
        e->d3dCtx->CopyResource(e->stagingTex.Get(), e->nv12Tex.Get());
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        hr = e->d3dCtx->Map(e->stagingTex.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return hr;
        int stride = ALIGN(dstW, 16);
        int totalBytes = stride * dstH + stride * (dstH / 2);
        ComPtr<IMFMediaBuffer> cpuBuf;
        MFCreateMemoryBuffer(totalBytes, &cpuBuf);
        BYTE* dst = nullptr; DWORD maxLen = 0;
        cpuBuf->Lock(&dst, &maxLen, nullptr);
        for (int y = 0; y < dstH + dstH / 2; y++) {
            memcpy(dst + y * stride,
                   (BYTE*)mapped.pData + y * mapped.RowPitch, stride);
        }
        cpuBuf->Unlock();
        cpuBuf->SetCurrentLength(totalBytes);
        e->d3dCtx->Unmap(e->stagingTex.Get(), 0);
        mfBuf = cpuBuf;
    }

    hr = sample->AddBuffer(mfBuf.Get());
    CHECK_HR(hr, "AddBuffer");

    // Submit to encoder
    hr = e->encoder->ProcessInput(0, sample.Get(), 0);
    if (hr == MF_E_NOTACCEPTING) return S_OK; // encoder busy
    CHECK_HR(hr, "ProcessInput");

    // Drain output
    MFT_OUTPUT_DATA_BUFFER outData = {};
    ComPtr<IMFSample> outSample;
    MFCreateSample(&outSample);
    ComPtr<IMFMediaBuffer> outMFBuf;
    MFCreateMemoryBuffer(bufSize, &outMFBuf);
    outSample->AddBuffer(outMFBuf.Get());
    outData.pSample = outSample.Get();

    DWORD status = 0;
    hr = e->encoder->ProcessOutput(0, 1, &outData, &status);
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
    CHECK_HR(hr, "ProcessOutput");

    // Ensure Annex-B start codes (H264)
    BYTE* encData = nullptr; DWORD encLen = 0;
    outMFBuf->Lock(&encData, nullptr, &encLen);
    if (encLen > 0 && encLen <= (DWORD)bufSize) {
        UINT32 isClean = 0;
        outSample->GetUINT32(MFSampleExtension_CleanPoint, &isClean);
        *outIsKey = isClean ? 1 : 0;
        if (e->videoFmt == MFVideoFormat_H264) {
            // Prepend Annex-B start code if missing
            if (encData[0] != 0 || encData[1] != 0 || encData[2] != 0 || encData[3] != 1) {
                if (encLen + 4 <= (DWORD)bufSize) {
                    outBuf[0]=0; outBuf[1]=0; outBuf[2]=0; outBuf[3]=1;
                    memcpy(outBuf + 4, encData, encLen);
                    *outBytes = encLen + 4;
                } else {
                    memcpy(outBuf, encData, encLen);
                    *outBytes = encLen;
                }
            } else {
                memcpy(outBuf, encData, encLen);
                *outBytes = encLen;
            }
        } else {
            memcpy(outBuf, encData, encLen);
            *outBytes = encLen;
        }
    }
    outMFBuf->Unlock();
    return S_OK;
}

// =============================================================================
//  Public API
// =============================================================================

extern "C" {

JCAPI int JC_Init(const JC_InitParams* params)
{
    if (!params) return E_INVALIDARG;

    std::lock_guard<std::mutex> lk(g_initMtx);
    if (g_eng && g_eng->initialized) JC_Release();

    // Setup log path
    static char logBuf[MAX_PATH];
    GetTempPathA(MAX_PATH, logBuf);
    strcat_s(logBuf, "jaclipei_capture.txt");
    g_logPath = logBuf;
    LogInfo("[JC] JC_Init starting");

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) { LogError("CoInitialize", hr); return hr; }
    hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) { LogError("MFStartup", hr); return hr; }

    g_eng = new EngineState();
    g_eng->params = *params;

    // Defaults
    if (g_eng->params.fps <= 0)         g_eng->params.fps = 30;
    if (g_eng->params.bitrateKbps <= 0) g_eng->params.bitrateKbps = 4000;
    if (g_eng->params.dstWidth  <= 0)   g_eng->params.dstWidth  = 1920;
    if (g_eng->params.dstHeight <= 0)   g_eng->params.dstHeight = 1080;

    // D3D11
    hr = InitD3D(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    // Enumerate available encoders (before capture)
    EnumerateMftEncoders();

    // Capture init: WGC first, then DXGI DDup
    bool captureOk = false;
    if (params->captureMode != JC_CAPTURE_DXGI) {
        HRESULT wgcHr = InitWGC(g_eng);
        if (SUCCEEDED(wgcHr)) captureOk = true;
        else LogError("WGC failed, trying DXGI DDup", wgcHr);
    }
    if (!captureOk && params->captureMode != JC_CAPTURE_WGC) {
        hr = InitDxgiDup(g_eng);
        if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }
    }

    // Clamp destination to source if not specified
    if (g_eng->params.dstWidth  > g_eng->srcWidth)  g_eng->params.dstWidth  = g_eng->srcWidth;
    if (g_eng->params.dstHeight > g_eng->srcHeight) g_eng->params.dstHeight = g_eng->srcHeight;

    // Video Processor
    hr = InitVideoProcessor(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    // Encoder
    hr = InitEncoder(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    // Audio
    if (params->enableAudio) {
        HRESULT aHr = InitWasapi(g_eng);
        if (FAILED(aHr)) {
            LogError("WASAPI init failed (audio disabled)", aHr);
            g_eng->params.enableAudio = 0;
        }
    }

    g_eng->initialized = true;
    LogInfo("[JC] JC_Init complete");
    return S_OK;
}

JCAPI void JC_Release(void)
{
    std::lock_guard<std::mutex> lk(g_initMtx);
    if (!g_eng) return;

    // Stop audio
    if (g_eng->audioRunning.load()) {
        g_eng->audioRunning = false;
        if (g_eng->audioThread.joinable()) g_eng->audioThread.join();
    }
    if (g_eng->audioClient) g_eng->audioClient->Stop();
    if (g_eng->pWaveFmt) { CoTaskMemFree(g_eng->pWaveFmt); g_eng->pWaveFmt = nullptr; }

    // Stop WGC
    if (g_eng->useWGC && g_eng->wgcSession) {
        try { g_eng->wgcSession.StopCapture(); } catch (...) {}
        g_eng->wgcSession = nullptr;
        g_eng->wgcPool    = nullptr;
        g_eng->wgcItem    = nullptr;
    }

    // Flush encoder
    if (g_eng->encoder) {
        g_eng->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_eng->encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    }

    delete g_eng;
    g_eng = nullptr;

    MFShutdown();
    LogInfo("[JC] JC_Release done");
}

JCAPI int JC_CaptureAndEncode(
    uint8_t* outVideoBuffer, int videoBufferSize, int* outVideoBytes, int* outIsKeyFrame,
    uint8_t* outAudioBuffer, int audioBufferSize, int* outAudioBytes)
{
    if (!g_eng || !g_eng->initialized) return E_FAIL;
    if (outVideoBytes)  *outVideoBytes = 0;
    if (outIsKeyFrame)  *outIsKeyFrame = 0;
    if (outAudioBytes)  *outAudioBytes = 0;

    // Capture & convert
    bool hasFrame = CaptureFrameToNV12(g_eng);
    if (!hasFrame) return S_OK; // no frame yet

    // Encode video
    int vBytes = 0, isKey = 0;
    HRESULT hr = EncodeNV12(g_eng, outVideoBuffer, videoBufferSize, &vBytes, &isKey);
    if (outVideoBytes)  *outVideoBytes  = vBytes;
    if (outIsKeyFrame)  *outIsKeyFrame  = isKey;

    // Return audio if available
    if (outAudioBuffer && outAudioBytes && audioBufferSize > 0 && g_eng->params.enableAudio) {
        std::lock_guard<std::mutex> lk(g_eng->audioOutMtx);
        int audioLen = (int)min((size_t)audioBufferSize, g_eng->audioOutBuf.size());
        if (audioLen > 0) {
            memcpy(outAudioBuffer, g_eng->audioOutBuf.data(), audioLen);
            g_eng->audioOutBuf.erase(g_eng->audioOutBuf.begin(),
                                     g_eng->audioOutBuf.begin() + audioLen);
            *outAudioBytes = audioLen;
        }
    }

    return SUCCEEDED(hr) ? S_OK : hr;
}

JCAPI void JC_ForceKeyframe(void)
{
    if (g_eng) g_eng->forceKey = true;
}

JCAPI void JC_SetBitrate(int bitrateKbps)
{
    if (!g_eng || !g_eng->encoder || bitrateKbps <= 0) return;
    ComPtr<ICodecAPI> codec;
    if (SUCCEEDED(g_eng->encoder.As(&codec))) {
        VARIANT v = {}; v.vt = VT_UI4;
        v.uintVal = (UINT)bitrateKbps * 1000;
        codec->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);
    }
    g_eng->params.bitrateKbps = bitrateKbps;
}

JCAPI void JC_GetOutputSize(int* width, int* height)
{
    if (!g_eng) { if (width) *width = 0; if (height) *height = 0; return; }
    if (width)  *width  = g_eng->params.dstWidth;
    if (height) *height = g_eng->params.dstHeight;
}

JCAPI int JC_EnumEncoders(JC_EncoderInfo* outInfo, int maxCount)
{
    if (g_availableEncoders.empty()) EnumerateMftEncoders();
    int n = (int)g_availableEncoders.size();
    if (outInfo) {
        int fill = min(n, maxCount);
        for (int i = 0; i < fill; i++) {
            outInfo[i].vendor     = g_availableEncoders[i].vendor;
            outInfo[i].codec      = g_availableEncoders[i].codec;
            outInfo[i].isHardware = (g_availableEncoders[i].vendor != JC_ENCODER_SOFTWARE) ? 1 : 0;
            WideCharToMultiByte(CP_UTF8, 0, g_availableEncoders[i].name, -1,
                                outInfo[i].name, 127, nullptr, nullptr);
        }
    }
    return n;
}

JCAPI int JC_EnumDisplays(JC_DisplayInfo* outInfo, int maxCount)
{
    struct MonitorCtx { JC_DisplayInfo* info; int max; int count; int primary; };
    auto cb = [](HMONITOR hm, HDC, LPRECT, LPARAM lp) -> BOOL {
        auto* ctx = (MonitorCtx*)lp;
        MONITORINFOEXA mi = {}; mi.cbSize = sizeof(mi);
        GetMonitorInfoA(hm, &mi);
        if (ctx->count < ctx->max) {
            JC_DisplayInfo& d = ctx->info[ctx->count];
            d.index    = ctx->count;
            d.width    = mi.rcMonitor.right - mi.rcMonitor.left;
            d.height   = mi.rcMonitor.bottom - mi.rcMonitor.top;
            d.isPrimary= (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0;
            strncpy_s(d.friendlyName, mi.szDevice, _TRUNCATE);
        }
        ctx->count++;
        return TRUE;
    };
    MonitorCtx ctx = { outInfo, maxCount, 0, 0 };
    EnumDisplayMonitors(nullptr, nullptr, cb, (LPARAM)&ctx);
    return ctx.count;
}

JCAPI int JC_EnumWindows(JC_WindowInfo* outInfo, int maxCount)
{
    struct WndCtx { JC_WindowInfo* info; int max; int count; };
    auto cb = [](HWND hwnd, LPARAM lp) -> BOOL {
        auto* ctx = (WndCtx*)lp;
        if (!IsWindowVisible(hwnd)) return TRUE;
        char title[256] = {};
        GetWindowTextA(hwnd, title, 255);
        if (!title[0]) return TRUE;
        // Skip tool windows
        LONG_PTR style = GetWindowLongPtrA(hwnd, GWL_EXSTYLE);
        if (style & WS_EX_TOOLWINDOW) return TRUE;
        if (ctx->count < ctx->max) {
            JC_WindowInfo& w = ctx->info[ctx->count];
            w.hwnd = hwnd;
            strncpy_s(w.title, title, _TRUNCATE);
            DWORD pid = 0; GetWindowThreadProcessId(hwnd, &pid);
            HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (h) {
                char exePath[MAX_PATH] = {};
                DWORD sz = MAX_PATH;
                QueryFullProcessImageNameA(h, 0, exePath, &sz);
                CloseHandle(h);
                const char* slash = strrchr(exePath, '\\');
                strncpy_s(w.processName, slash ? slash + 1 : exePath, _TRUNCATE);
            }
        }
        ctx->count++;
        return TRUE;
    };
    WndCtx ctx = { outInfo, maxCount, 0 };
    ::EnumWindows(cb, (LPARAM)&ctx);
    return ctx.count;
}

} // extern "C"
