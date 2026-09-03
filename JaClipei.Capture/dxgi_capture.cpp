#define JACLIPEI_CAPTURE_EXPORTS
#include "capture.h"

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <cstring>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;

static ComPtr<ID3D11Device>            g_device;
static ComPtr<ID3D11DeviceContext>     g_ctx;
static ComPtr<IDXGIOutputDuplication>  g_dup;
static int g_width  = 0;
static int g_height = 0;

int JC_Init(int adapterIndex, int outputIndex)
{
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return -1;

    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(factory->EnumAdapters1(adapterIndex, &adapter))) return -2;

    D3D_FEATURE_LEVEL fl;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0,
                                 nullptr, 0, D3D11_SDK_VERSION,
                                 &g_device, &fl, &g_ctx))) return -3;

    ComPtr<IDXGIOutput> output;
    if (FAILED(adapter->EnumOutputs(outputIndex, &output))) return -4;

    ComPtr<IDXGIOutput1> output1;
    if (FAILED(output.As(&output1))) return -5;

    DXGI_OUTPUT_DESC desc{};
    output->GetDesc(&desc);
    g_width  = desc.DesktopCoordinates.right  - desc.DesktopCoordinates.left;
    g_height = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;

    if (FAILED(output1->DuplicateOutput(g_device.Get(), &g_dup))) return -6;
    return 0;
}

int JC_CaptureFrame(uint8_t* outBuffer, int bufferSize, int* outWidth, int* outHeight)
{
    if (!g_dup) return -1;

    DXGI_OUTDUPL_FRAME_INFO info{};
    ComPtr<IDXGIResource> resource;

    HRESULT hr = g_dup->AcquireNextFrame(0, &info, &resource);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0; // sem frame novo
    if (FAILED(hr)) return -2;

    ComPtr<ID3D11Texture2D> tex;
    resource.As(&tex);

    // Cria textura de staging para leitura pela CPU
    D3D11_TEXTURE2D_DESC desc{};
    tex->GetDesc(&desc);
    desc.Usage          = D3D11_USAGE_STAGING;
    desc.BindFlags      = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags      = 0;

    ComPtr<ID3D11Texture2D> staging;
    g_device->CreateTexture2D(&desc, nullptr, &staging);
    g_ctx->CopyResource(staging.Get(), tex.Get());

    D3D11_MAPPED_SUBRESOURCE mapped{};
    g_ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);

    int needed = g_width * g_height * 4;
    if (needed > bufferSize) { g_ctx->Unmap(staging.Get(), 0); g_dup->ReleaseFrame(); return -3; }

    // Copia linha a linha (stride pode ser maior que width*4)
    for (int row = 0; row < g_height; row++)
        memcpy(outBuffer + row * g_width * 4,
               (uint8_t*)mapped.pData + row * mapped.RowPitch,
               g_width * 4);

    g_ctx->Unmap(staging.Get(), 0);
    g_dup->ReleaseFrame();

    *outWidth  = g_width;
    *outHeight = g_height;
    return needed;
}

void JC_Release()
{
    g_dup.Reset();
    g_ctx.Reset();
    g_device.Reset();
}
