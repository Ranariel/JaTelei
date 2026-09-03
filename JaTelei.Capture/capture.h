#pragma once
#include <cstdint>

#ifdef JACLIPEI_CAPTURE_EXPORTS
#define JCAPI __declspec(dllexport)
#else
#define JCAPI __declspec(dllimport)
#endif

extern "C" {
    // Inicializa o pipeline completo: DXGI Duplication + D3D11VideoProcessor + MFT HW H264 encoder.
    // dstWidth/dstHeight: resolução de saída (0 = usar resolução nativa do monitor).
    // fps: frames por segundo alvo.
    // bitrateKbps: bitrate alvo do encoder H264 (ex: 8000 = 8 Mbps).
    // Retorna 0 em sucesso, negativo em erro.
    JCAPI int  JC_Init(int adapterIndex, int outputIndex,
                       int dstWidth, int dstHeight,
                       int fps, int bitrateKbps);

    // Captura um frame e codifica em H264 Annex B usando a pipeline full-GPU.
    // outBuffer: buffer destino para os bytes H264.
    // bufferSize: tamanho máximo do buffer.
    // Retorna bytes escritos (>0), 0 se não há frame novo, <0 em erro.
    JCAPI int  JC_CaptureAndEncode(uint8_t* outBuffer, int bufferSize);

    // Solicita que o próximo frame seja um IDR (keyframe) — útil na conexão inicial.
    JCAPI void JC_ForceKeyframe();

    // Retorna largura e altura da saída configurada.
    JCAPI void JC_GetOutputSize(int* width, int* height);

    // Libera todos os recursos D3D11/DXGI/MFT.
    JCAPI void JC_Release();
}
