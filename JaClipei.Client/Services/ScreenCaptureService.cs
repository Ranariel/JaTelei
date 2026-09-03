using System.Runtime.InteropServices;

namespace JaClipei.Client.Services;

/// <summary>
/// Bridge C# → C++ (JaClipei.Capture.dll).
/// A DLL em C++ usa DXGI Desktop Duplication para capturar frames.
/// </summary>
public static class ScreenCaptureService
{
    private const string DllName = "JaClipei.Capture.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int  JC_Init(int adapterIndex, int outputIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int  JC_CaptureFrame(IntPtr outBuffer, int bufferSize, out int width, out int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void JC_Release();

    // ── Helpers para uso no ViewModel ───────────────────────────────────────

    private static bool _initialized = false;

    public static bool Initialize(int adapter = 0, int output = 0)
    {
        if (_initialized) return true;
        _initialized = JC_Init(adapter, output) == 0;
        return _initialized;
    }

    /// <summary>
    /// Captura um frame e retorna como array de bytes BGRA.
    /// Retorna null se nenhum frame novo estiver disponível.
    /// </summary>
    public static byte[]? CaptureFrame(out int width, out int height)
    {
        width = height = 0;
        const int MaxSize = 3840 * 2160 * 4; // 4K BGRA
        var buf = Marshal.AllocHGlobal(MaxSize);
        try
        {
            int result = JC_CaptureFrame(buf, MaxSize, out width, out height);
            if (result <= 0) return null;
            var data = new byte[result];
            Marshal.Copy(buf, data, 0, result);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static void Shutdown() => JC_Release();
}
