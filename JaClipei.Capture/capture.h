#pragma once
#include <cstdint>

#ifdef JACLIPEI_CAPTURE_EXPORTS
#define JCAPI __declspec(dllexport)
#else
#define JCAPI __declspec(dllimport)
#endif

extern "C" {
    // Inicializa o DXGI Desktop Duplication no adapter/output indicado.
    // Retorna 0 em sucesso, negativo em erro.
    JCAPI int  JC_Init(int adapterIndex, int outputIndex);

    // Captura um frame e escreve bytes BGRA em outBuffer.
    // Retorna o número de bytes escritos, 0 se sem frame novo, negativo em erro.
    JCAPI int  JC_CaptureFrame(uint8_t* outBuffer, int bufferSize, int* outWidth, int* outHeight);

    // Libera todos os recursos DXGI.
    JCAPI void JC_Release();
}
