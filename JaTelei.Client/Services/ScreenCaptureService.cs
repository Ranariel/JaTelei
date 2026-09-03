using System.Runtime.InteropServices;

namespace JaTelei.Client.Services;

/// <summary>
/// Bridge C# → C++ (JaTelei.Capture.dll).
/// Pipeline full-GPU: DXGI Desktop Duplication → D3D11VideoProcessor (BGRA→NV12) →
///                    MFT Hardware H264 Encoder → H264 Annex B.
/// </summary>
public static class ScreenCaptureService
{
    private const string DllName = "JaTelei.Capture.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int  JC_Init(int adapterIndex, int outputIndex,
                                       int dstWidth, int dstHeight,
                                       int fps, int bitrateKbps);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int  JC_CaptureAndEncode(IntPtr outBuffer, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_ForceKeyframe();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_GetOutputSize(out int width, out int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_Release();

    // ── Estado ──────────────────────────────────────────────────────────────

    private static bool _initialized;
    private static IntPtr _buffer = IntPtr.Zero;
    // 50 MB — suficiente para 4K H264 em pico de bitrate
    private const int BufferSize = 50 * 1024 * 1024;

    public static int OutputWidth  { get; private set; }
    public static int OutputHeight { get; private set; }

    // ── API pública ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inicializa o pipeline completo de captura + encode.
    /// </summary>
    /// <param name="dstWidth">Largura de saída (0 = nativa do monitor).</param>
    /// <param name="dstHeight">Altura de saída (0 = nativa do monitor).</param>
    /// <param name="fps">FPS alvo.</param>
    /// <param name="bitrateKbps">Bitrate H264 em kbps (ex: 8000 = 8 Mbps).</param>
    public static bool Initialize(int dstWidth = 0, int dstHeight = 0,
                                  int fps = 30, int bitrateKbps = 8000,
                                  int adapter = 0, int output = 0)
    {
        if (_initialized) return true;

        int hr = JC_Init(adapter, output, dstWidth, dstHeight, fps, bitrateKbps);
        if (hr != 0) return false;

        _initialized = true;
        _buffer = Marshal.AllocHGlobal(BufferSize);

        JC_GetOutputSize(out int w, out int h);
        OutputWidth  = w;
        OutputHeight = h;

        return true;
    }

    /// <summary>
    /// Captura e codifica um frame. Retorna bytes H264 Annex B, ou null se
    /// não há frame novo ou ocorreu erro.
    /// </summary>
    public static byte[]? CaptureAndEncode()
    {
        if (!_initialized || _buffer == IntPtr.Zero) return null;

        int len = JC_CaptureAndEncode(_buffer, BufferSize);
        if (len <= 0) return null;

        var data = new byte[len];
        Marshal.Copy(_buffer, data, 0, len);
        return data;
    }

    /// <summary>
    /// Solicita que o próximo frame seja um IDR keyframe.
    /// Chamar ao iniciar uma nova conexão WebRTC.
    /// </summary>
    public static void ForceKeyframe()
    {
        if (_initialized) JC_ForceKeyframe();
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        JC_Release();
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        _initialized = false;
        OutputWidth = OutputHeight = 0;
    }
}
