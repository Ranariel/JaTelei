// dxgi_capture.cpp
// Pipeline full-GPU: DXGI Desktop Duplication → D3D11VideoProcessor (BGRA→NV12 + resize)
//                   → MFT Hardware H264 Encoder → H264 Annex B
// Zero cópias CPU até o output H264. Usa NVENC/QuickSync/AMF quando disponível.

// Precisa estar antes de qualquer include do Windows SDK
#define _WIN32_WINNT 0x0A00
#define WINVER       0x0A00

#define JACLIPEI_CAPTURE_EXPORTS
#include "capture.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mftransform.h>
#include <mfidl.h>
#include <mferror.h>
#include <evr.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <vector>
#include <cstring>
#include <algorithm>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "evr.lib")

using Microsoft::WRL::ComPtr;

// ── Estado global ────────────────────────────────────────────────────────────

static ComPtr<ID3D11Device>           g_device;
static ComPtr<ID3D11DeviceContext>    g_ctx;
static ComPtr<IDXGIOutputDuplication> g_dup;

// VideoProcessor: converte BGRA→NV12 e redimensiona na GPU
static ComPtr<ID3D11VideoDevice>      g_videoDevice;
static ComPtr<ID3D11VideoContext>     g_videoCtx;
static ComPtr<ID3D11VideoProcessor>   g_vp;
static ComPtr<ID3D11VideoProcessorEnumerator> g_vpEnum;

// Texturas intermediárias (NV12, na resolução de saída)
static ComPtr<ID3D11Texture2D>        g_nv12Tex;     // output do VideoProcessor
static ComPtr<ID3D11VideoProcessorOutputView> g_vpOutputView;

// MFT encoder
static ComPtr<IMFTransform>           g_encoder;
static ComPtr<IMFDXGIDeviceManager>   g_devMgr;
static UINT                           g_devMgrToken = 0;

static int  g_srcWidth  = 0;
static int  g_srcHeight = 0;
static int  g_dstWidth  = 0;
static int  g_dstHeight = 0;
static int  g_fps       = 30;
static long long g_sampleTime = 0;
static bool g_streaming  = false;
static bool g_forceKey   = false;

// ── Helpers MFT (reutiliza lógica de vtable do MfH264Codec.cs mas em C++) ──

static HRESULT SetMFAttribute(IMFAttributes* attr, const GUID& key, UINT32 val) {
    return attr->SetUINT32(key, val);
}
static HRESULT SetMFAttribute(IMFAttributes* attr, const GUID& key, UINT64 val) {
    return attr->SetUINT64(key, val);
}
static HRESULT SetMFAttribute(IMFAttributes* attr, const GUID& key, const GUID& val) {
    return attr->SetGUID(key, val);
}

static UINT64 PackWH(UINT32 w, UINT32 h) { return ((UINT64)w << 32) | h; }
static UINT64 PackFrac(UINT32 n, UINT32 d) { return ((UINT64)n << 32) | d; }

// ── Encontrar encoder HW H264 ────────────────────────────────────────────────

static HRESULT FindHardwareEncoder(IMFTransform** ppEncoder)
{
    MFT_REGISTER_TYPE_INFO outInfo{};
    outInfo.guidMajorType = MFMediaType_Video;
    outInfo.guidSubtype   = MFVideoFormat_H264;

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;

    // Tenta hardware primeiro
    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        nullptr,
        &outInfo,
        &ppActivate,
        &count);

    if (SUCCEEDED(hr) && count == 0) {
        // Fallback: software (inbox MS encoder)
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            nullptr,
            &outInfo,
            &ppActivate,
            &count);
    }

    if (FAILED(hr) || count == 0) return MF_E_TOPO_CODEC_NOT_FOUND;

    hr = ppActivate[0]->ActivateObject(IID_PPV_ARGS(ppEncoder));
    for (UINT32 i = 0; i < count; i++) ppActivate[i]->Release();
    CoTaskMemFree(ppActivate);
    return hr;
}

// ── Configurar MFT encoder ───────────────────────────────────────────────────

static HRESULT SetupEncoder(int bitrateKbps)
{
    // Compartilha o device D3D11 com o encoder via DXGIDeviceManager
    HRESULT hr = MFCreateDXGIDeviceManager(&g_devMgrToken, &g_devMgr);
    if (FAILED(hr)) return hr;

    hr = g_devMgr->ResetDevice(g_device.Get(), g_devMgrToken);
    if (FAILED(hr)) return hr;

    // Tenta passar o device manager ao encoder (encoders HW exigem isso)
    ComPtr<IMFAttributes> encAttr;
    hr = g_encoder->GetAttributes(&encAttr);
    if (SUCCEEDED(hr)) {
        encAttr->SetUINT32(MF_SA_D3D11_AWARE, TRUE);
        // Ignora falha — nem todo encoder suporta
        g_encoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
            reinterpret_cast<ULONG_PTR>(g_devMgr.Get()));
    }

    // Tipo de saída: H264
    ComPtr<IMFMediaType> outType;
    hr = MFCreateMediaType(&outType);
    if (FAILED(hr)) return hr;

    outType->SetGUID(MF_MT_MAJOR_TYPE,     MFMediaType_Video);
    outType->SetGUID(MF_MT_SUBTYPE,        MFVideoFormat_H264);
    outType->SetUINT32(MF_MT_AVG_BITRATE,  (UINT32)(bitrateKbps * 1000));
    outType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, g_dstWidth, g_dstHeight);
    MFSetAttributeRatio(outType.Get(), MF_MT_FRAME_RATE, g_fps, 1);
    MFSetAttributeRatio(outType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = g_encoder->SetOutputType(0, outType.Get(), 0);
    if (FAILED(hr)) return hr;

    // Tipo de entrada: NV12
    ComPtr<IMFMediaType> inType;
    hr = MFCreateMediaType(&inType);
    if (FAILED(hr)) return hr;

    inType->SetGUID(MF_MT_MAJOR_TYPE,  MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE,     MFVideoFormat_NV12);
    inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, g_dstWidth, g_dstHeight);
    MFSetAttributeRatio(inType.Get(), MF_MT_FRAME_RATE, g_fps, 1);
    MFSetAttributeRatio(inType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = g_encoder->SetInputType(0, inType.Get(), 0);
    return hr;
}

// ── Configurar D3D11VideoProcessor (BGRA→NV12 + resize) ─────────────────────

static HRESULT SetupVideoProcessor()
{
    HRESULT hr = g_device.As(&g_videoDevice);
    if (FAILED(hr)) return hr;

    hr = g_ctx.As(&g_videoCtx);
    if (FAILED(hr)) return hr;

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC vpDesc{};
    vpDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    vpDesc.InputWidth       = g_srcWidth;
    vpDesc.InputHeight      = g_srcHeight;
    vpDesc.OutputWidth      = g_dstWidth;
    vpDesc.OutputHeight     = g_dstHeight;
    vpDesc.Usage            = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    hr = g_videoDevice->CreateVideoProcessorEnumerator(&vpDesc, &g_vpEnum);
    if (FAILED(hr)) return hr;

    hr = g_videoDevice->CreateVideoProcessor(g_vpEnum.Get(), 0, &g_vp);
    if (FAILED(hr)) return hr;

    // Cria textura NV12 de destino (output do VideoProcessor → input do encoder)
    D3D11_TEXTURE2D_DESC nv12Desc{};
    nv12Desc.Width              = g_dstWidth;
    nv12Desc.Height             = g_dstHeight;
    nv12Desc.MipLevels          = 1;
    nv12Desc.ArraySize          = 1;
    nv12Desc.Format             = DXGI_FORMAT_NV12;
    nv12Desc.SampleDesc.Count   = 1;
    nv12Desc.Usage              = D3D11_USAGE_DEFAULT;
    nv12Desc.BindFlags          = D3D11_BIND_RENDER_TARGET | D3D11_BIND_VIDEO_ENCODER;

    hr = g_device->CreateTexture2D(&nv12Desc, nullptr, &g_nv12Tex);
    if (FAILED(hr)) {
        // Se BIND_VIDEO_ENCODER não for suportado, tenta sem ele
        nv12Desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        hr = g_device->CreateTexture2D(&nv12Desc, nullptr, &g_nv12Tex);
        if (FAILED(hr)) return hr;
    }

    // View de output do VideoProcessor
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovDesc{};
    ovDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    ovDesc.Texture2D.MipSlice = 0;

    hr = g_videoDevice->CreateVideoProcessorOutputView(
        g_nv12Tex.Get(), g_vpEnum.Get(), &ovDesc, &g_vpOutputView);
    return hr;
}

// ── JC_Init ──────────────────────────────────────────────────────────────────

int JC_Init(int adapterIndex, int outputIndex,
            int dstWidth, int dstHeight,
            int fps, int bitrateKbps)
{
    MFStartup(MF_VERSION);

    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return -1;

    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(factory->EnumAdapters1(adapterIndex, &adapter))) return -2;

    // D3D11 com suporte a vídeo
    UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    D3D_FEATURE_LEVEL fl;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                 flags, nullptr, 0, D3D11_SDK_VERSION,
                                 &g_device, &fl, &g_ctx))) {
        // Fallback sem VIDEO_SUPPORT
        flags = 0;
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                     flags, nullptr, 0, D3D11_SDK_VERSION,
                                     &g_device, &fl, &g_ctx))) return -3;
    }

    // Multithread protection (MFT pode usar em outra thread)
    ComPtr<ID3D11Multithread> mt;
    if (SUCCEEDED(g_ctx->QueryInterface(IID_PPV_ARGS(&mt)))) mt->SetMultithreadProtected(TRUE);

    // DXGI Output Duplication
    ComPtr<IDXGIOutput> output;
    if (FAILED(adapter->EnumOutputs(outputIndex, &output))) return -4;

    ComPtr<IDXGIOutput1> output1;
    if (FAILED(output.As(&output1))) return -5;

    DXGI_OUTPUT_DESC desc{};
    output->GetDesc(&desc);
    g_srcWidth  = desc.DesktopCoordinates.right  - desc.DesktopCoordinates.left;
    g_srcHeight = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;

    if (FAILED(output1->DuplicateOutput(g_device.Get(), &g_dup))) return -6;

    // Resolução de destino
    g_fps = fps > 0 ? fps : 30;
    if (dstWidth > 0 && dstHeight > 0) {
        g_dstWidth  = dstWidth  % 2 == 0 ? dstWidth  : dstWidth  - 1;
        g_dstHeight = dstHeight % 2 == 0 ? dstHeight : dstHeight - 1;
    } else {
        g_dstWidth  = g_srcWidth;
        g_dstHeight = g_srcHeight;
    }

    // VideoProcessor: BGRA → NV12 na GPU
    if (FAILED(SetupVideoProcessor())) return -7;

    // Encoder H264 hardware
    if (FAILED(FindHardwareEncoder(&g_encoder))) return -8;
    if (FAILED(SetupEncoder(bitrateKbps > 0 ? bitrateKbps : 8000))) return -9;

    g_sampleTime = 0;
    g_streaming  = false;
    g_forceKey   = true; // primeiro frame sempre IDR
    return 0;
}

// ── Converter frame capturado BGRA → NV12 na GPU ────────────────────────────

static HRESULT ConvertFrameToNV12(ID3D11Texture2D* srcTex)
{
    // Input view (BGRA)
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivDesc{};
    ivDesc.FourCC          = 0;
    ivDesc.ViewDimension   = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ivDesc.Texture2D.MipSlice   = 0;
    ivDesc.Texture2D.ArraySlice = 0;

    ComPtr<ID3D11VideoProcessorInputView> ivView;
    HRESULT hr = g_videoDevice->CreateVideoProcessorInputView(
        srcTex, g_vpEnum.Get(), &ivDesc, &ivView);
    if (FAILED(hr)) return hr;

    // Stream de entrada
    D3D11_VIDEO_PROCESSOR_STREAM stream{};
    stream.Enable = TRUE;
    stream.pInputSurface = ivView.Get();

    // Configura rect de saída (full frame)
    RECT dstRect = { 0, 0, g_dstWidth, g_dstHeight };
    g_videoCtx->VideoProcessorSetOutputTargetRect(g_vp.Get(), TRUE, &dstRect);
    RECT srcRect = { 0, 0, g_srcWidth, g_srcHeight };
    g_videoCtx->VideoProcessorSetStreamSourceRect(g_vp.Get(), 0, TRUE, &srcRect);
    g_videoCtx->VideoProcessorSetStreamDestRect(g_vp.Get(), 0, TRUE, &dstRect);

    return g_videoCtx->VideoProcessorBlt(g_vp.Get(), g_vpOutputView.Get(), 0, 1, &stream);
}

// ── Encodar textura NV12 → H264 Annex B ─────────────────────────────────────

static HRESULT EncodeNV12(uint8_t* outBuffer, int bufferSize, int* outLen)
{
    *outLen = 0;

    if (!g_streaming) {
        g_streaming = true;
        g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    // Cria IMFSample a partir da textura NV12 via DXGI buffer
    ComPtr<IMFSample> sample;
    HRESULT hr = MFCreateVideoSampleFromSurface(nullptr, &sample);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaBuffer> buf;
    hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D),
                                   g_nv12Tex.Get(), 0, FALSE, &buf);
    if (FAILED(hr)) {
        // Fallback: copia NV12 para buffer de memória
        UINT nv12Bytes = g_dstWidth * g_dstHeight * 3 / 2;
        hr = MFCreateMemoryBuffer(nv12Bytes, &buf);
        if (FAILED(hr)) return hr;

        // Mapeia textura NV12 para CPU e copia
        D3D11_TEXTURE2D_DESC stagDesc{};
        g_nv12Tex->GetDesc(&stagDesc);
        stagDesc.Usage          = D3D11_USAGE_STAGING;
        stagDesc.BindFlags      = 0;
        stagDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        stagDesc.MiscFlags      = 0;

        ComPtr<ID3D11Texture2D> staging;
        hr = g_device->CreateTexture2D(&stagDesc, nullptr, &staging);
        if (FAILED(hr)) return hr;

        g_ctx->CopyResource(staging.Get(), g_nv12Tex.Get());

        D3D11_MAPPED_SUBRESOURCE mapped{};
        hr = g_ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return hr;

        BYTE* dst = nullptr;
        buf->Lock(&dst, nullptr, nullptr);

        // Copia plano Y
        for (int row = 0; row < g_dstHeight; row++)
            memcpy(dst + row * g_dstWidth,
                   (uint8_t*)mapped.pData + row * mapped.RowPitch,
                   g_dstWidth);

        // Copia plano UV
        uint8_t* srcUV = (uint8_t*)mapped.pData + mapped.RowPitch * stagDesc.Height;
        uint8_t* dstUV = dst + g_dstWidth * g_dstHeight;
        for (int row = 0; row < g_dstHeight / 2; row++)
            memcpy(dstUV + row * g_dstWidth,
                   srcUV + row * mapped.RowPitch,
                   g_dstWidth);

        buf->Unlock();
        buf->SetCurrentLength(nv12Bytes);
        g_ctx->Unmap(staging.Get(), 0);
    }

    sample->AddBuffer(buf.Get());

    long long dur = 10'000'000LL / g_fps;
    sample->SetSampleTime(g_sampleTime);
    sample->SetSampleDuration(dur);
    g_sampleTime += dur;

    // Keyframe forçado
    if (g_forceKey) {
        g_forceKey = false;
        ComPtr<IMFAttributes> sAttr;
        sample->QueryInterface(IID_PPV_ARGS(&sAttr));
        if (sAttr) sAttr->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
    }

    hr = g_encoder->ProcessInput(0, sample.Get(), 0);
    if (FAILED(hr)) return hr;

    // Drena output
    MFT_OUTPUT_STREAM_INFO si{};
    g_encoder->GetOutputStreamInfo(0, &si);
    bool mftOwns = (si.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

    ComPtr<IMFSample>      outSample;
    ComPtr<IMFMediaBuffer> outBuf;

    if (!mftOwns) {
        UINT sz = si.cbSize > 0 ? si.cbSize : 1'048'576u;
        MFCreateMemoryBuffer(sz, &outBuf);
        MFCreateSample(&outSample);
        outSample->AddBuffer(outBuf.Get());
    }

    MFT_OUTPUT_DATA_BUFFER db{};
    db.pSample = outSample.Get();
    DWORD status = 0;

    hr = g_encoder->ProcessOutput(0, 1, &db, &status);
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
    if (FAILED(hr)) return hr;

    IMFSample* smp = mftOwns ? db.pSample : outSample.Get();
    if (!smp) return S_OK;

    // Coleta todos os buffers do sample
    DWORD bufCount = 0;
    smp->GetBufferCount(&bufCount);
    int offset = 0;

    for (DWORD i = 0; i < bufCount && offset < bufferSize; i++) {
        ComPtr<IMFMediaBuffer> mb;
        smp->GetBufferByIndex(i, &mb);
        BYTE* data = nullptr; DWORD len = 0;
        mb->Lock(&data, nullptr, &len);
        int copy = (std::min)((int)len, bufferSize - offset);
        memcpy(outBuffer + offset, data, copy);
        offset += copy;
        mb->Unlock();
    }

    if (mftOwns && db.pSample) db.pSample->Release();
    *outLen = offset;
    return S_OK;
}

// ── JC_CaptureAndEncode ──────────────────────────────────────────────────────

int JC_CaptureAndEncode(uint8_t* outBuffer, int bufferSize)
{
    if (!g_dup || !g_vp || !g_encoder) return -1;

    DXGI_OUTDUPL_FRAME_INFO info{};
    ComPtr<IDXGIResource> resource;

    HRESULT hr = g_dup->AcquireNextFrame(0, &info, &resource);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0; // sem frame novo
    if (FAILED(hr)) {
        // Tenta recriar o duplicator (ex: mudança de resolução)
        g_dup.Reset();
        return -2;
    }

    ComPtr<ID3D11Texture2D> srcTex;
    resource.As(&srcTex);

    // Converte BGRA → NV12 + resize na GPU
    hr = ConvertFrameToNV12(srcTex.Get());
    g_dup->ReleaseFrame();
    if (FAILED(hr)) return -3;

    // Codifica NV12 → H264
    int outLen = 0;
    hr = EncodeNV12(outBuffer, bufferSize, &outLen);
    if (FAILED(hr)) return -4;

    return outLen;
}

// ── JC_ForceKeyframe ─────────────────────────────────────────────────────────

void JC_ForceKeyframe()
{
    g_forceKey = true;
}

// ── JC_GetOutputSize ─────────────────────────────────────────────────────────

void JC_GetOutputSize(int* width, int* height)
{
    if (width)  *width  = g_dstWidth;
    if (height) *height = g_dstHeight;
}

// ── JC_Release ───────────────────────────────────────────────────────────────

void JC_Release()
{
    if (g_encoder && g_streaming) {
        g_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    }
    g_streaming = false;

    g_vpOutputView.Reset();
    g_nv12Tex.Reset();
    g_vp.Reset();
    g_vpEnum.Reset();
    g_videoCtx.Reset();
    g_videoDevice.Reset();
    g_encoder.Reset();
    g_devMgr.Reset();
    g_dup.Reset();
    g_ctx.Reset();
    g_device.Reset();

    MFShutdown();
}
