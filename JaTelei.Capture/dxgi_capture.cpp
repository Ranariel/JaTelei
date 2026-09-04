// =============================================================================
//  JaTelei.Capture — dxgi_capture.cpp
//  Engine: WGC + DXGI DDup | D3D11 GPU | NVENC/AMF/QSV | H.264/AV1 | WASAPI
// =============================================================================
#define _WIN32_WINNT 0x0A00
#define WINVER       0x0A00
#define COBJMACROS
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "capture.h"

// Standard Windows
#include <windows.h>
#include <unknwn.h>
#include <inspectable.h>

// D3D / DXGI
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
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
#include <avrt.h>

// WinRT base (before WGC headers)
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

// STL
#include <algorithm>
#include <atomic>
#include <mutex>
#include <memory>
#include <thread>
#include <vector>
#include <string>
#include <cassert>
#include <cstring>

// WRL
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

// WinRT namespaces
namespace wgc  = winrt::Windows::Graphics::Capture;
namespace wgd  = winrt::Windows::Graphics::DirectX;
namespace wgdd = winrt::Windows::Graphics::DirectX::Direct3D11;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
#define CHECK_HR(hr, msg) do { if (FAILED(hr)) { LogError(msg, hr); return hr; } } while(0)
#define ALIGN16(v)        (((v) + 15) & ~15)

static const char* g_logPath = nullptr;

static void LogError(const char* msg, HRESULT hr = 0)
{
    if (!g_logPath) return;
    char buf[512];
    if (hr) snprintf(buf, sizeof(buf), "[JC] ERROR %s  hr=0x%08X\n", msg, (unsigned)hr);
    else     snprintf(buf, sizeof(buf), "[JC] %s\n", msg);
    HANDLE f = CreateFileA(g_logPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        SetFilePointer(f, 0, nullptr, FILE_END);
        DWORD w; WriteFile(f, buf, (DWORD)strlen(buf), &w, nullptr);
        CloseHandle(f);
    }
}
static void LogInfo(const char* msg) { LogError(msg, 0); }

// ---------------------------------------------------------------------------
// Encoder enumeration
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
    if (wcsstr(name, L"AMD") || wcsstr(name, L"Radeon") || wcsstr(name, L"AMF"))
        return JC_ENCODER_AMF;
    if (wcsstr(name, L"Intel") || wcsstr(name, L"QSV") || wcsstr(name, L"Quick Sync"))
        return JC_ENCODER_QSV;
    return JC_ENCODER_SOFTWARE;
}

static void EnumMftEncoders(MFT_REGISTER_TYPE_INFO outType, bool hardware)
{
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, MFVideoFormat_NV12 };
    UINT32 flags = hardware
        ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
        : MFT_ENUM_FLAG_SYNCMFT  | MFT_ENUM_FLAG_SORTANDFILTER;
    IMFActivate** pp = nullptr; UINT32 cnt = 0;
    if (FAILED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, &inType, &outType, &pp, &cnt)))
        return;
    JC_Codec codec = (outType.guidSubtype == MFVideoFormat_AV1) ? JC_CODEC_AV1 : JC_CODEC_H264;
    for (UINT32 i = 0; i < cnt; i++) {
        MftEncoderCandidate c = {}; c.codec = codec;
        WCHAR* n = nullptr; UINT32 nl = 0;
        pp[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &n, &nl);
        if (n) { wcsncpy_s(c.name, n, _TRUNCATE); CoTaskMemFree(n); }
        c.vendor = hardware ? VendorFromName(c.name) : JC_ENCODER_SOFTWARE;
        pp[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &c.clsid);
        g_availableEncoders.push_back(c);
        pp[i]->Release();
    }
    CoTaskMemFree(pp);
}

static void EnumerateMftEncoders()
{
    g_availableEncoders.clear();
    EnumMftEncoders({ MFMediaType_Video, MFVideoFormat_H264 }, true);
    EnumMftEncoders({ MFMediaType_Video, MFVideoFormat_AV1  }, true);
    EnumMftEncoders({ MFMediaType_Video, MFVideoFormat_H264 }, false);
}

static bool FindEncoder(JC_Codec codec, JC_EncoderVendor preferred, MftEncoderCandidate& out)
{
    const JC_EncoderVendor hwOrder[] = { JC_ENCODER_NVENC, JC_ENCODER_AMF, JC_ENCODER_QSV };
    if (preferred == JC_ENCODER_AUTO) {
        for (auto v : hwOrder)
            for (auto& c : g_availableEncoders)
                if (c.codec == codec && c.vendor == v) { out = c; return true; }
        for (auto& c : g_availableEncoders)
            if (c.codec == codec) { out = c; return true; }
        return false;
    }
    for (auto& c : g_availableEncoders)
        if (c.codec == codec && c.vendor == preferred) { out = c; return true; }
    for (auto& c : g_availableEncoders)
        if (c.codec == codec) { out = c; return true; }
    return false;
}

// ---------------------------------------------------------------------------
// WGC state (heap-allocated — avoids WinRT default-constructor requirements)
// ---------------------------------------------------------------------------
struct WgcContext {
    wgc::GraphicsCaptureItem        item;
    wgc::Direct3D11CaptureFramePool pool;
    wgc::GraphicsCaptureSession     session;
    ComPtr<ID3D11Texture2D>         lastTex;
    std::mutex                      mtx;
    bool                            newFrame = false;

    WgcContext(wgc::GraphicsCaptureItem        i,
               wgc::Direct3D11CaptureFramePool p,
               wgc::GraphicsCaptureSession     s)
        : item(std::move(i)), pool(std::move(p)), session(std::move(s)) {}
};

// ---------------------------------------------------------------------------
// Engine state (no WinRT types here — only POD / ComPtr / std types)
// ---------------------------------------------------------------------------
struct EngineState {
    // D3D11
    ComPtr<ID3D11Device>           d3dDevice;
    ComPtr<ID3D11DeviceContext>    d3dCtx;
    ComPtr<ID3D11Multithread>      d3dMultithread;

    // DXGI Desktop Duplication
    ComPtr<IDXGIOutputDuplication> dxgiDup;
    ComPtr<IDXGIOutput1>           dxgiOutput;

    // WGC (heap-allocated, only non-null when WGC is active)
    std::unique_ptr<WgcContext>    wgcCtx;
    bool                           useWGC = false;

    // D3D11 Video Processor (BGRA/NV12 + resize)
    ComPtr<ID3D11VideoDevice>               videoDevice;
    ComPtr<ID3D11VideoContext>              videoCtx;
    ComPtr<ID3D11VideoProcessorEnumerator>  vpEnum;
    ComPtr<ID3D11VideoProcessor>            vp;
    ComPtr<ID3D11VideoProcessorInputView>   vpInView;
    ComPtr<ID3D11VideoProcessorOutputView>  vpOutView;
    ComPtr<ID3D11Texture2D>                 nv12Tex;

    // CPU staging fallback
    ComPtr<ID3D11Texture2D>        stagingTex;

    // MFT encoder
    ComPtr<IMFTransform>           encoder;
    ComPtr<IMFDXGIDeviceManager>   devMgr;
    UINT                           devMgrToken = 0;
    LONGLONG                       sampleCount = 0;
    std::atomic<bool>              forceKey    { false };
    GUID                           videoFmt    = MFVideoFormat_H264;
    bool                           isHwEncoder = false;
    MftEncoderCandidate            chosenEncoder {};

    // WASAPI
    ComPtr<IMMDeviceEnumerator>    mmEnum;
    ComPtr<IMMDevice>              mmDevice;
    ComPtr<IAudioClient>           audioClient;
    ComPtr<IAudioCaptureClient>    capClient;
    WAVEFORMATEX*                  pWaveFmt    = nullptr;
    std::thread                    audioThread;
    std::atomic<bool>              audioRunning { false };

    // AAC encoder + output buffer
    ComPtr<IMFTransform>           aacEncoder;
    std::mutex                     audioOutMtx;
    std::vector<BYTE>              audioOutBuf;

    // Params
    JC_InitParams  params {};
    int            srcWidth  = 0;
    int            srcHeight = 0;
    bool           initialized = false;
};

static EngineState* g_eng = nullptr;
static std::mutex   g_initMtx;

// ---------------------------------------------------------------------------
// D3D11 init
// ---------------------------------------------------------------------------
static HRESULT InitD3D(EngineState* e)
{
    ComPtr<IDXGIFactory6> factory;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory6), &factory);
    CHECK_HR(hr, "CreateDXGIFactory1");

    ComPtr<IDXGIAdapter1> adapter;
    if (e->params.adapterIndex == 0)
        factory->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                            IID_PPV_ARGS(&adapter));
    else
        factory->EnumAdapters1(e->params.adapterIndex, &adapter);
    if (!adapter) factory->EnumAdapters1(0, &adapter);

    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL fl;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                           D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                           levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
                           &e->d3dDevice, &fl, &e->d3dCtx);
    CHECK_HR(hr, "D3D11CreateDevice");

    {
        ID3D11Multithread* pMT = nullptr;
        if (SUCCEEDED(e->d3dDevice->QueryInterface(IID_PPV_ARGS(&pMT)))) {
            e->d3dMultithread.Attach(pMT);
            pMT->SetMultithreadProtected(TRUE);
        }
    }

    return S_OK;
}

// ---------------------------------------------------------------------------
// DXGI Desktop Duplication
// ---------------------------------------------------------------------------
static HRESULT InitDxgiDup(EngineState* e)
{
    ComPtr<IDXGIDevice> dxgiDev;
    e->d3dDevice->QueryInterface(IID_PPV_ARGS(&dxgiDev));
    ComPtr<IDXGIAdapter> ad; dxgiDev->GetAdapter(&ad);
    ComPtr<IDXGIAdapter1> ad1;
    ad->QueryInterface(IID_PPV_ARGS(&ad1));

    ComPtr<IDXGIOutput> out;
    if (FAILED(ad1->EnumOutputs(e->params.outputIndex, &out)))
        ad1->EnumOutputs(0, &out);
    CHECK_HR(out ? S_OK : E_FAIL, "EnumOutputs");

    {
        IDXGIOutput1* pO1 = nullptr;
        out->QueryInterface(IID_PPV_ARGS(&pO1));
        e->dxgiOutput.Attach(pO1);
    }
    HRESULT hr = e->dxgiOutput->DuplicateOutput(e->d3dDevice.Get(), &e->dxgiDup);
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
// Windows Graphics Capture
// ---------------------------------------------------------------------------
static HRESULT InitWGC(EngineState* e)
{
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        // Wrap D3D11 device as WinRT IDirect3DDevice
        ComPtr<IDXGIDevice> dxgiDev;
        e->d3dDevice->QueryInterface(IID_PPV_ARGS(&dxgiDev));
        winrt::com_ptr<IInspectable> inspectable;
        HRESULT hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDev.Get(), inspectable.put());
        if (FAILED(hr)) return hr;
        auto rtDevice = inspectable.as<wgdd::IDirect3DDevice>();

        // Create capture item
        wgc::GraphicsCaptureItem item = nullptr;
        auto factory = winrt::get_activation_factory<
            wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();

        if (e->params.targetKind == JC_TARGET_WINDOW && e->params.windowHandle) {
            hr = factory->CreateForWindow(
                (HWND)e->params.windowHandle,
                winrt::guid_of<wgc::GraphicsCaptureItem>(),
                winrt::put_abi(item));
            if (FAILED(hr)) return hr;
        } else {
            // Get HMONITOR from adapter output
            ComPtr<IDXGIDevice> dev; e->d3dDevice->QueryInterface(IID_PPV_ARGS(&dev));
            ComPtr<IDXGIAdapter> ad; dev->GetAdapter(&ad);
            ComPtr<IDXGIOutput> out;
            ad->EnumOutputs(e->params.outputIndex, &out);
            DXGI_OUTPUT_DESC od = {}; out->GetDesc(&od);
            hr = factory->CreateForMonitor(
                od.Monitor,
                winrt::guid_of<wgc::GraphicsCaptureItem>(),
                winrt::put_abi(item));
            if (FAILED(hr)) return hr;
        }

        auto sz = item.Size();
        e->srcWidth  = sz.Width;
        e->srcHeight = sz.Height;

        // Frame pool
        auto pool = wgc::Direct3D11CaptureFramePool::Create(
            rtDevice,
            wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, sz);

        auto session = pool.CreateCaptureSession(item);
        session.IsBorderRequired(false);
        session.IsCursorCaptureEnabled(true);

        // Create WgcContext on heap (avoids WinRT default-constructor issues in EngineState)
        e->wgcCtx = std::make_unique<WgcContext>(std::move(item), std::move(pool), std::move(session));
        WgcContext* ctx = e->wgcCtx.get();

        // Subscribe to frames
        ctx->pool.FrameArrived([ctx](auto& pool, auto&) {
            auto frame = pool.TryGetNextFrame();
            if (!frame) return;
            auto surface = frame.Surface();
            // Get underlying D3D11 texture via QI on IUnknown
            ::IUnknown* unk = winrt::get_unknown(surface);
            ID3D11Texture2D* rawTex = nullptr;
            if (FAILED(unk->QueryInterface(IID_PPV_ARGS(&rawTex)))) return;
            std::lock_guard<std::mutex> lk(ctx->mtx);
            ctx->lastTex.Attach(rawTex); // takes ownership (QI called AddRef)
            ctx->newFrame = true;
        });

        ctx->session.StartCapture();
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
    int dstW = ALIGN16(e->params.dstWidth);
    int dstH = ALIGN16(e->params.dstHeight);

    // NV12 output texture
    D3D11_TEXTURE2D_DESC td = {};
    td.Width            = (UINT)dstW;
    td.Height           = (UINT)dstH;
    td.MipLevels        = 1;
    td.ArraySize        = 1;
    td.Format           = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage            = D3D11_USAGE_DEFAULT;
    td.BindFlags        = D3D11_BIND_VIDEO_ENCODER | D3D11_BIND_RENDER_TARGET;
    HRESULT hr = e->d3dDevice->CreateTexture2D(&td, nullptr, &e->nv12Tex);
    CHECK_HR(hr, "CreateTexture2D NV12");

    {
        ID3D11VideoDevice* pVD = nullptr;
        hr = e->d3dDevice->QueryInterface(IID_PPV_ARGS(&pVD));
        e->videoDevice.Attach(pVD);
    }
    CHECK_HR(hr, "QI ID3D11VideoDevice");
    {
        ID3D11VideoContext* pVC = nullptr;
        hr = e->d3dCtx->QueryInterface(IID_PPV_ARGS(&pVC));
        e->videoCtx.Attach(pVC);
    }
    CHECK_HR(hr, "QI ID3D11VideoContext");

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC vpDesc = {};
    vpDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    vpDesc.InputWidth       = (UINT)e->srcWidth;
    vpDesc.InputHeight      = (UINT)e->srcHeight;
    vpDesc.OutputWidth      = (UINT)dstW;
    vpDesc.OutputHeight     = (UINT)dstH;
    vpDesc.Usage            = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    hr = e->videoDevice->CreateVideoProcessorEnumerator(&vpDesc, &e->vpEnum);
    CHECK_HR(hr, "CreateVideoProcessorEnumerator");

    hr = e->videoDevice->CreateVideoProcessor(e->vpEnum.Get(), 0, &e->vp);
    CHECK_HR(hr, "CreateVideoProcessor");

    // Output view — use raw pointer to avoid ComPtrRef ambiguity
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd = {};
    ovd.ViewDimension          = D3D11_VPOV_DIMENSION_TEXTURE2D;
    ovd.Texture2D.MipSlice     = 0;
    {
        ID3D11VideoProcessorOutputView* raw = nullptr;
        hr = e->videoDevice->CreateVideoProcessorOutputView(
            e->nv12Tex.Get(), e->vpEnum.Get(), &ovd, &raw);
        CHECK_HR(hr, "CreateVideoProcessorOutputView");
        e->vpOutView.Attach(raw);
    }
    return S_OK;
}

// ---------------------------------------------------------------------------
// Per-frame input view (recreated each capture because source texture changes)
// ---------------------------------------------------------------------------
static HRESULT CreateInputView(EngineState* e, ID3D11Texture2D* srcTex)
{
    e->vpInView.Reset();
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd = {};
    ivd.ViewDimension            = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ivd.Texture2D.MipSlice       = 0;
    ivd.Texture2D.ArraySlice     = 0;
    ivd.FourCC                   = 0;

    ID3D11VideoProcessorInputView* raw = nullptr;
    HRESULT hr = e->videoDevice->CreateVideoProcessorInputView(
        srcTex, e->vpEnum.Get(), &ivd, &raw);
    if (SUCCEEDED(hr)) e->vpInView.Attach(raw);
    return hr;
}

// ---------------------------------------------------------------------------
// MFT Encoder init
// ---------------------------------------------------------------------------
static HRESULT InitEncoder(EngineState* e)
{
    int dstW = ALIGN16(e->params.dstWidth);
    int dstH = ALIGN16(e->params.dstHeight);

    // Choose codec + encoder
    JC_Codec codec = e->params.codec;
    if (codec == JC_CODEC_AV1 &&
        !FindEncoder(JC_CODEC_AV1, e->params.encoderVendor, e->chosenEncoder)) {
        LogInfo("[JC] AV1 unavailable, falling back to H.264");
        codec = JC_CODEC_H264;
    }
    if (codec == JC_CODEC_H264 &&
        !FindEncoder(JC_CODEC_H264, e->params.encoderVendor, e->chosenEncoder)) {
        LogError("No H264 encoder found"); return E_FAIL;
    }
    e->videoFmt    = (codec == JC_CODEC_AV1) ? MFVideoFormat_AV1 : MFVideoFormat_H264;
    e->isHwEncoder = (e->chosenEncoder.vendor != JC_ENCODER_SOFTWARE);

    {
        char narrow[128] = {};
        WideCharToMultiByte(CP_UTF8, 0, e->chosenEncoder.name, -1, narrow, 127, nullptr, nullptr);
        char info[256];
        snprintf(info, sizeof(info), "[JC] Encoder: %s (hw=%d)", narrow, (int)e->isHwEncoder);
        LogInfo(info);
    }

    // Activate
    MFT_REGISTER_TYPE_INFO inT  = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outT = { MFMediaType_Video, e->videoFmt };
    UINT32 flags = e->isHwEncoder
        ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
        : MFT_ENUM_FLAG_SYNCMFT  | MFT_ENUM_FLAG_SORTANDFILTER;

    IMFActivate** pp = nullptr; UINT32 cnt = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, &inT, &outT, &pp, &cnt);
    CHECK_HR(hr, "MFTEnumEx encoder");
    if (cnt == 0) { CoTaskMemFree(pp); return E_FAIL; }

    // Pick preferred vendor
    IMFActivate* chosen = pp[0];
    if (e->params.encoderVendor != JC_ENCODER_AUTO) {
        for (UINT32 i = 0; i < cnt; i++) {
            WCHAR* n = nullptr; UINT32 nl = 0;
            pp[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &n, &nl);
            if (n) {
                bool match = (VendorFromName(n) == e->params.encoderVendor);
                CoTaskMemFree(n);
                if (match) { chosen = pp[i]; break; }
            }
        }
    }

    hr = chosen->ActivateObject(__uuidof(IMFTransform), (void**)&e->encoder);
    for (UINT32 i = 0; i < cnt; i++) pp[i]->Release();
    CoTaskMemFree(pp);
    CHECK_HR(hr, "ActivateObject encoder");

    // DXGI device manager (GPU sharing with HW encoder)
    if (e->isHwEncoder) {
        hr = MFCreateDXGIDeviceManager(&e->devMgrToken, &e->devMgr);
        CHECK_HR(hr, "MFCreateDXGIDeviceManager");
        hr = e->devMgr->ResetDevice(e->d3dDevice.Get(), e->devMgrToken);
        CHECK_HR(hr, "devMgr->ResetDevice");
        ComPtr<IMFAttributes> attrs;
        if (SUCCEEDED(e->encoder->GetAttributes(&attrs))) {
            attrs->SetUINT32(MF_SA_D3D11_AWARE, TRUE);
            e->encoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                reinterpret_cast<ULONG_PTR>(e->devMgr.Get()));
        }
    }

    // Input type: NV12
    ComPtr<IMFMediaType> inMT;
    MFCreateMediaType(&inMT);
    inMT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inMT->SetGUID(MF_MT_SUBTYPE,    MFVideoFormat_NV12);
    MFSetAttributeSize(inMT.Get(), MF_MT_FRAME_SIZE, (UINT32)dstW, (UINT32)dstH);
    MFSetAttributeRatio(inMT.Get(), MF_MT_FRAME_RATE, (UINT32)e->params.fps, 1);
    MFSetAttributeRatio(inMT.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    inMT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    hr = e->encoder->SetInputType(0, inMT.Get(), 0);
    CHECK_HR(hr, "SetInputType");

    // Output type: H264/AV1
    ComPtr<IMFMediaType> outMT;
    MFCreateMediaType(&outMT);
    outMT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outMT->SetGUID(MF_MT_SUBTYPE,    e->videoFmt);
    outMT->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)(e->params.bitrateKbps * 1000));
    MFSetAttributeSize(outMT.Get(), MF_MT_FRAME_SIZE, (UINT32)dstW, (UINT32)dstH);
    MFSetAttributeRatio(outMT.Get(), MF_MT_FRAME_RATE, (UINT32)e->params.fps, 1);
    MFSetAttributeRatio(outMT.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    outMT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (e->videoFmt == MFVideoFormat_H264)
        outMT->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);

    hr = e->encoder->SetOutputType(0, outMT.Get(), 0);
    CHECK_HR(hr, "SetOutputType");

    e->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    e->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    char info[128];
    snprintf(info, sizeof(info), "[JC] Encoder ready %dx%d @%dfps %dkbps",
             dstW, dstH, e->params.fps, e->params.bitrateKbps);
    LogInfo(info);
    return S_OK;
}

// ---------------------------------------------------------------------------
// WASAPI loopback + AAC encoder
// ---------------------------------------------------------------------------
static HRESULT InitWasapi(EngineState* e)
{
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  IID_PPV_ARGS(&e->mmEnum));
    CHECK_HR(hr, "CoCreateInstance MMDeviceEnumerator");
    hr = e->mmEnum->GetDefaultAudioEndpoint(eRender, eConsole, &e->mmDevice);
    CHECK_HR(hr, "GetDefaultAudioEndpoint");
    hr = e->mmDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                               (void**)&e->audioClient);
    CHECK_HR(hr, "Activate IAudioClient");
    hr = e->audioClient->GetMixFormat(&e->pWaveFmt);
    CHECK_HR(hr, "GetMixFormat");
    hr = e->audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                    AUDCLNT_STREAMFLAGS_LOOPBACK,
                                    10000000LL, 0, e->pWaveFmt, nullptr);
    CHECK_HR(hr, "AudioClient Initialize");
    hr = e->audioClient->GetService(IID_PPV_ARGS(&e->capClient));
    CHECK_HR(hr, "GetService IAudioCaptureClient");

    // AAC encoder
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

            int sr  = (int)e->pWaveFmt->nSamplesPerSec;
            int ch  = std::min((int)e->pWaveFmt->nChannels, 2);
            int bps = e->params.audioBitrate > 0 ? e->params.audioBitrate * 1000 : 128000;

            ComPtr<IMFMediaType> aIn, aOut;
            MFCreateMediaType(&aIn);
            aIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            aIn->SetGUID(MF_MT_SUBTYPE,    MFAudioFormat_Float);
            aIn->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)sr);
            aIn->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,       (UINT32)ch);
            aIn->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE,    32);
            aIn->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT,    (UINT32)(ch * 4));
            aIn->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)(sr * ch * 4));
            e->aacEncoder->SetInputType(0, aIn.Get(), 0);

            MFCreateMediaType(&aOut);
            aOut->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            aOut->SetGUID(MF_MT_SUBTYPE,    MFAudioFormat_AAC);
            aOut->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)sr);
            aOut->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,       (UINT32)ch);
            aOut->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)(bps / 8));
            e->aacEncoder->SetOutputType(0, aOut.Get(), 0);
            e->aacEncoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            e->aacEncoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
            LogInfo("[JC] AAC encoder ready");
        } else {
            CoTaskMemFree(pp);
            LogInfo("[JC] AAC encoder not found — audio disabled");
            e->params.enableAudio = 0;
            return S_OK;
        }
    }

    hr = e->audioClient->Start();
    CHECK_HR(hr, "AudioClient Start");

    e->audioRunning = true;
    e->audioThread = std::thread([e]() {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        while (e->audioRunning.load()) {
            UINT32 packetFrames = 0;
            if (FAILED(e->capClient->GetNextPacketSize(&packetFrames)) || packetFrames == 0) {
                Sleep(5); continue;
            }
            BYTE* data = nullptr; DWORD flags = 0; UINT32 nf = 0;
            if (FAILED(e->capClient->GetBuffer(&data, &nf, &flags, nullptr, nullptr)) || nf == 0) {
                Sleep(1); continue;
            }
            if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) && data && e->aacEncoder) {
                int srcCh = (int)e->pWaveFmt->nChannels;
                int outCh = std::min(srcCh, 2);
                size_t bytes = (size_t)nf * outCh * sizeof(float);
                ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buf;
                MFCreateMemoryBuffer((DWORD)bytes, &buf);
                BYTE* dst = nullptr;
                buf->Lock(&dst, nullptr, nullptr);
                if (outCh == srcCh) {
                    memcpy(dst, data, bytes);
                } else {
                    float* src = (float*)data;
                    float* out = (float*)dst;
                    for (UINT32 f = 0; f < nf; f++) {
                        out[f*2+0] = src[f*srcCh+0];
                        out[f*2+1] = (srcCh > 1) ? src[f*srcCh+1] : src[f*srcCh];
                    }
                }
                buf->Unlock();
                buf->SetCurrentLength((DWORD)bytes);
                MFCreateSample(&sample);
                sample->AddBuffer(buf.Get());
                e->aacEncoder->ProcessInput(0, sample.Get(), 0);

                MFT_OUTPUT_DATA_BUFFER ob = {};
                ComPtr<IMFSample> os; MFCreateSample(&os);
                ComPtr<IMFMediaBuffer> om; MFCreateMemoryBuffer(65536, &om);
                os->AddBuffer(om.Get()); ob.pSample = os.Get();
                DWORD status = 0;
                if (SUCCEEDED(e->aacEncoder->ProcessOutput(0, 1, &ob, &status))) {
                    BYTE* od = nullptr; DWORD oLen = 0;
                    om->Lock(&od, nullptr, &oLen);
                    if (oLen > 0) {
                        std::lock_guard<std::mutex> lk(e->audioOutMtx);
                        size_t prev = e->audioOutBuf.size();
                        e->audioOutBuf.resize(prev + oLen);
                        memcpy(e->audioOutBuf.data() + prev, od, oLen);
                    }
                    om->Unlock();
                }
            }
            e->capClient->ReleaseBuffer(nf);
        }
    });

    LogInfo("[JC] WASAPI loopback started");
    return S_OK;
}

// ---------------------------------------------------------------------------
// Capture one frame → NV12 on GPU
// ---------------------------------------------------------------------------
static bool CaptureFrameToNV12(EngineState* e)
{
    ComPtr<ID3D11Texture2D> srcTex;

    if (e->useWGC && e->wgcCtx) {
        std::lock_guard<std::mutex> lk(e->wgcCtx->mtx);
        if (!e->wgcCtx->newFrame || !e->wgcCtx->lastTex) return false;
        srcTex = e->wgcCtx->lastTex;
        e->wgcCtx->newFrame = false;
    } else {
        ComPtr<IDXGIResource> res;
        DXGI_OUTDUPL_FRAME_INFO fi = {};
        HRESULT hr = e->dxgiDup->AcquireNextFrame(100, &fi, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr == DXGI_ERROR_ACCESS_LOST || FAILED(hr)) {
            LogError("AcquireNextFrame — reinit DDup", hr);
            e->dxgiDup.Reset();
            InitDxgiDup(e);
            return false;
        }
        {
            ID3D11Texture2D* pTex = nullptr;
            res->QueryInterface(IID_PPV_ARGS(&pTex));
            srcTex.Attach(pTex);
        }
    }
    if (!srcTex) {
        if (!e->useWGC) e->dxgiDup->ReleaseFrame();
        return false;
    }

    // Create input view for this source texture
    HRESULT hr = CreateInputView(e, srcTex.Get());
    if (FAILED(hr)) {
        LogError("CreateInputView", hr);
        if (!e->useWGC) e->dxgiDup->ReleaseFrame();
        return false;
    }

    // GPU blit: BGRA → NV12 + resize
    int dstW = ALIGN16(e->params.dstWidth);
    int dstH = ALIGN16(e->params.dstHeight);
    RECT dr = { 0, 0, dstW, dstH };
    e->videoCtx->VideoProcessorSetOutputTargetRect(e->vp.Get(), TRUE, &dr);
    e->videoCtx->VideoProcessorSetStreamSourceRect(e->vp.Get(), 0, FALSE, nullptr);

    D3D11_VIDEO_PROCESSOR_STREAM vs = {};
    vs.Enable        = TRUE;
    vs.pInputSurface = e->vpInView.Get();
    hr = e->videoCtx->VideoProcessorBlt(e->vp.Get(), e->vpOutView.Get(), 0, 1, &vs);
    if (FAILED(hr)) LogError("VideoProcessorBlt", hr);

    if (!e->useWGC) e->dxgiDup->ReleaseFrame();
    return SUCCEEDED(hr);
}

// ---------------------------------------------------------------------------
// Encode NV12 → bitstream
// ---------------------------------------------------------------------------
static HRESULT EncodeNV12(EngineState* e,
    uint8_t* outBuf, int bufSize, int* outBytes, int* outIsKey)
{
    *outBytes = 0; *outIsKey = 0;
    int dstW = ALIGN16(e->params.dstWidth);
    int dstH = ALIGN16(e->params.dstHeight);

    LONGLONG ts  = e->sampleCount * (10000000LL / e->params.fps);
    LONGLONG dur = 10000000LL / e->params.fps;
    e->sampleCount++;
    bool forceKey = e->forceKey.exchange(false);

    ComPtr<IMFSample> sample;
    MFCreateSample(&sample);
    sample->SetSampleTime(ts);
    sample->SetSampleDuration(dur);
    if (forceKey) sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);

    // Try zero-copy GPU surface buffer
    ComPtr<IMFMediaBuffer> mfBuf;
    HRESULT hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D),
                                           e->nv12Tex.Get(), 0, FALSE, &mfBuf);
    if (FAILED(hr)) {
        // CPU staging fallback
        if (!e->stagingTex) {
            D3D11_TEXTURE2D_DESC td = {};
            td.Width = (UINT)dstW; td.Height = (UINT)dstH;
            td.MipLevels = td.ArraySize = 1;
            td.Format    = DXGI_FORMAT_NV12;
            td.SampleDesc.Count = 1;
            td.Usage          = D3D11_USAGE_STAGING;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            e->d3dDevice->CreateTexture2D(&td, nullptr, &e->stagingTex);
        }
        e->d3dCtx->CopyResource(e->stagingTex.Get(), e->nv12Tex.Get());
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        hr = e->d3dCtx->Map(e->stagingTex.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return hr;
        int stride     = ALIGN16(dstW);
        int totalBytes = stride * (dstH + dstH / 2);
        ComPtr<IMFMediaBuffer> cpuBuf;
        MFCreateMemoryBuffer(totalBytes, &cpuBuf);
        BYTE* dst = nullptr;
        cpuBuf->Lock(&dst, nullptr, nullptr);
        for (int y = 0; y < dstH + dstH / 2; y++)
            memcpy(dst + y * stride, (BYTE*)mapped.pData + y * mapped.RowPitch, stride);
        cpuBuf->Unlock();
        cpuBuf->SetCurrentLength(totalBytes);
        e->d3dCtx->Unmap(e->stagingTex.Get(), 0);
        mfBuf = cpuBuf;
    }

    sample->AddBuffer(mfBuf.Get());

    hr = e->encoder->ProcessInput(0, sample.Get(), 0);
    if (hr == MF_E_NOTACCEPTING) return S_OK;
    CHECK_HR(hr, "ProcessInput");

    MFT_OUTPUT_DATA_BUFFER od = {};
    ComPtr<IMFSample> os; MFCreateSample(&os);
    ComPtr<IMFMediaBuffer> om; MFCreateMemoryBuffer(bufSize, &om);
    os->AddBuffer(om.Get()); od.pSample = os.Get();
    DWORD status = 0;
    hr = e->encoder->ProcessOutput(0, 1, &od, &status);
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
    CHECK_HR(hr, "ProcessOutput");

    BYTE* encData = nullptr; DWORD encLen = 0;
    om->Lock(&encData, nullptr, &encLen);
    if (encLen > 0 && (int)encLen <= bufSize) {
        UINT32 isClean = 0;
        os->GetUINT32(MFSampleExtension_CleanPoint, &isClean);
        *outIsKey = isClean ? 1 : 0;
        if (e->videoFmt == MFVideoFormat_H264 &&
            (encData[0] != 0 || encData[1] != 0 || encData[2] != 0 || encData[3] != 1))
        {
            // Prepend Annex-B start code
            if ((int)encLen + 4 <= bufSize) {
                outBuf[0]=0; outBuf[1]=0; outBuf[2]=0; outBuf[3]=1;
                memcpy(outBuf + 4, encData, encLen);
                *outBytes = (int)encLen + 4;
            } else {
                memcpy(outBuf, encData, encLen);
                *outBytes = (int)encLen;
            }
        } else {
            memcpy(outBuf, encData, encLen);
            *outBytes = (int)encLen;
        }
    }
    om->Unlock();
    return S_OK;
}

// =============================================================================
//  Public C API
// =============================================================================
extern "C" {

JCAPI int JC_Init(const JC_InitParams* params)
{
    if (!params) return E_INVALIDARG;
    std::lock_guard<std::mutex> lk(g_initMtx);
    if (g_eng && g_eng->initialized) JC_Release();

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
    if (g_eng->params.fps         <= 0) g_eng->params.fps         = 30;
    if (g_eng->params.bitrateKbps <= 0) g_eng->params.bitrateKbps = 4000;
    if (g_eng->params.dstWidth    <= 0) g_eng->params.dstWidth    = 1920;
    if (g_eng->params.dstHeight   <= 0) g_eng->params.dstHeight   = 1080;

    hr = InitD3D(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    EnumerateMftEncoders();

    // Capture: try WGC first, then DXGI DDup
    bool captureOk = false;
    if (params->captureMode != JC_CAPTURE_DXGI) {
        if (SUCCEEDED(InitWGC(g_eng))) captureOk = true;
        else LogInfo("[JC] WGC failed, trying DXGI DDup");
    }
    if (!captureOk && params->captureMode != JC_CAPTURE_WGC) {
        hr = InitDxgiDup(g_eng);
        if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }
    }

    // Clamp dst to src
    if (g_eng->params.dstWidth  > g_eng->srcWidth)  g_eng->params.dstWidth  = g_eng->srcWidth;
    if (g_eng->params.dstHeight > g_eng->srcHeight) g_eng->params.dstHeight = g_eng->srcHeight;

    hr = InitVideoProcessor(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    hr = InitEncoder(g_eng);
    if (FAILED(hr)) { delete g_eng; g_eng = nullptr; return hr; }

    if (params->enableAudio) {
        HRESULT ah = InitWasapi(g_eng);
        if (FAILED(ah)) {
            LogError("WASAPI init failed (audio disabled)", ah);
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

    // Stop audio thread
    if (g_eng->audioRunning.load()) {
        g_eng->audioRunning = false;
        if (g_eng->audioThread.joinable()) g_eng->audioThread.join();
    }
    if (g_eng->audioClient) g_eng->audioClient->Stop();
    if (g_eng->pWaveFmt) { CoTaskMemFree(g_eng->pWaveFmt); g_eng->pWaveFmt = nullptr; }

    // Stop WGC — close session then reset context
    if (g_eng->useWGC && g_eng->wgcCtx) {
        try {
            g_eng->wgcCtx->session.Close(); // IClosable::Close()
        } catch (...) {}
        g_eng->wgcCtx.reset();
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
    if (outVideoBytes) *outVideoBytes = 0;
    if (outIsKeyFrame) *outIsKeyFrame = 0;
    if (outAudioBytes) *outAudioBytes = 0;

    if (!CaptureFrameToNV12(g_eng)) return S_OK;

    int vb = 0, ik = 0;
    HRESULT hr = EncodeNV12(g_eng, outVideoBuffer, videoBufferSize, &vb, &ik);
    if (outVideoBytes) *outVideoBytes = vb;
    if (outIsKeyFrame) *outIsKeyFrame = ik;

    if (outAudioBuffer && outAudioBytes && audioBufferSize > 0 && g_eng->params.enableAudio) {
        std::lock_guard<std::mutex> lk(g_eng->audioOutMtx);
        int al = (int)std::min((size_t)audioBufferSize, g_eng->audioOutBuf.size());
        if (al > 0) {
            memcpy(outAudioBuffer, g_eng->audioOutBuf.data(), al);
            g_eng->audioOutBuf.erase(g_eng->audioOutBuf.begin(),
                                     g_eng->audioOutBuf.begin() + al);
            *outAudioBytes = al;
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
    {
        ICodecAPI* pCA = nullptr;
        g_eng->encoder->QueryInterface(IID_PPV_ARGS(&pCA));
        codec.Attach(pCA);
    }
    if (codec) {
        VARIANT v = {}; v.vt = VT_UI4;
        v.uintVal = (UINT)(bitrateKbps * 1000);
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
        int fill = std::min(n, maxCount);
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
    struct Ctx { JC_DisplayInfo* info; int max; int count; };
    auto cb = [](HMONITOR hm, HDC, LPRECT, LPARAM lp) -> BOOL {
        auto* ctx = (Ctx*)lp;
        MONITORINFOEXA mi = {}; mi.cbSize = sizeof(mi);
        GetMonitorInfoA(hm, (MONITORINFO*)&mi);
        if (ctx->info && ctx->count < ctx->max) {
            auto& d = ctx->info[ctx->count];
            d.index     = ctx->count;
            d.width     = mi.rcMonitor.right  - mi.rcMonitor.left;
            d.height    = mi.rcMonitor.bottom - mi.rcMonitor.top;
            d.isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0;
            strncpy_s(d.friendlyName, mi.szDevice, _TRUNCATE);
        }
        ctx->count++;
        return TRUE;
    };
    Ctx ctx = { outInfo, maxCount, 0 };
    EnumDisplayMonitors(nullptr, nullptr, cb, (LPARAM)&ctx);
    return ctx.count;
}

JCAPI int JC_EnumWindows(JC_WindowInfo* outInfo, int maxCount)
{
    struct Ctx { JC_WindowInfo* info; int max; int count; };
    auto cb = [](HWND hwnd, LPARAM lp) -> BOOL {
        auto* ctx = (Ctx*)lp;
        if (!IsWindowVisible(hwnd)) return TRUE;
        char title[256] = {};
        GetWindowTextA(hwnd, title, 255);
        if (!title[0]) return TRUE;
        LONG_PTR style = GetWindowLongPtrA(hwnd, GWL_EXSTYLE);
        if (style & WS_EX_TOOLWINDOW) return TRUE;
        if (ctx->info && ctx->count < ctx->max) {
            auto& w = ctx->info[ctx->count];
            w.hwnd = hwnd;
            strncpy_s(w.title, title, _TRUNCATE);
            DWORD pid = 0; GetWindowThreadProcessId(hwnd, &pid);
            HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (h) {
                char exe[MAX_PATH] = {}; DWORD sz = MAX_PATH;
                QueryFullProcessImageNameA(h, 0, exe, &sz);
                CloseHandle(h);
                const char* sl = strrchr(exe, '\\');
                strncpy_s(w.processName, sl ? sl + 1 : exe, _TRUNCATE);
            }
        }
        ctx->count++;
        return TRUE;
    };
    Ctx ctx = { outInfo, maxCount, 0 };
    ::EnumWindows(cb, (LPARAM)&ctx);
    return ctx.count;
}

} // extern "C"
