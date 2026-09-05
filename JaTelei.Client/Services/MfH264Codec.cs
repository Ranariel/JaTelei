// FFmpeg-based H264 encoder/decoder (replaces Windows Media Foundation implementation).
// Works on all Windows editions including N/KN without Media Feature Pack.
// Requires FFmpeg DLLs in the app's install folder (BtbN GPL shared build, win64).
// DLLs must be present with their original versioned names, e.g. avcodec-63.dll,
// avutil-59.dll, swscale-8.dll, swresample-5.dll.  Unversioned DLLs (avcodec.dll etc.)
// also work as a fallback.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace JaTelei.Client.Services
{
    // -----------------------------------------------------------------------
    // Minimal FFmpeg P/Invoke bindings (subset needed for H264 encode/decode)
    // -----------------------------------------------------------------------
    internal static unsafe class Ffmpeg
    {
        // Names used in [DllImport] — resolved by SetDllImportResolver below.
        const string AvCodec = "avcodec";
        const string AvUtil  = "avutil";
        const string SwScale = "swscale";

        // ── Static constructor ───────────────────────────────────────────────
        // Pre-loads the versioned FFmpeg DLLs from the install directory and
        // registers a SetDllImportResolver so that P/Invoke calls for "avcodec",
        // "avutil" and "swscale" are routed to the already-loaded handles.
        //
        // Why this is necessary:
        //   • Single-file publish extracts the managed EXE to a temp folder;
        //     AppContext.BaseDirectory still points to the real install folder.
        //   • The FFmpeg DLLs carry VERSIONED internal names (avutil-59.dll etc.).
        //     Renaming them to avutil.dll would break cross-DLL imports because
        //     avcodec-63.dll internally references "avutil-59.dll" by name.
        //     We must therefore keep the versioned filenames and resolve them here.
        static Ffmpeg()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "jaclipei_ffmpeg.log");

            try
            {
                var appDir = AppContext.BaseDirectory;
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss}] Ffmpeg init — appDir={appDir}\n");

                var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

                // Load order respects the dependency chain:
                //   avutil  (no FFmpeg deps)
                //   swresample (→ avutil)
                //   swscale    (→ avutil)
                //   avcodec    (→ avutil, swresample, swscale; includes libx264)
                foreach (var prefix in new[] { "avutil", "swresample", "swscale", "avcodec" })
                {
                    // Prefer versioned names (avutil-59.dll) then unversioned (avutil.dll).
                    var candidates = Directory.GetFiles(appDir, $"{prefix}-*.dll")
                                              .Concat(Directory.GetFiles(appDir, $"{prefix}.dll"))
                                              .ToArray();

                    if (candidates.Length == 0)
                    {
                        File.AppendAllText(logPath,
                            $"  WARNING: no DLL for '{prefix}' in {appDir}\n");
                        continue;
                    }

                    var path = candidates[0];
                    File.AppendAllText(logPath, $"  Loading {Path.GetFileName(path)} ...\n");

                    // LoadLibraryEx with the full path; Windows resolves cross-DLL
                    // dependencies by looking in the same directory first, so all the
                    // av*/sw* DLLs in {app} will find each other automatically.
                    var handle = NativeLibrary.Load(path);
                    handles[prefix] = handle;

                    File.AppendAllText(logPath,
                        $"  OK  handle=0x{handle:X}\n");
                }

                // Route every [DllImport("avcodec"/"avutil"/"swscale"/"swresample")]
                // to the already-loaded module handle.
                NativeLibrary.SetDllImportResolver(
                    typeof(Ffmpeg).Assembly,
                    (libName, _, _) =>
                        handles.TryGetValue(libName, out var h) ? h : IntPtr.Zero);

                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss}] Ffmpeg init complete\n");
            }
            catch (Exception ex)
            {
                // Write the full exception so it is visible even if no window appears.
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss}] EXCEPTION in Ffmpeg static init:\n{ex}\n\n");
                throw; // propagates as TypeInitializationException
            }
        }

        // ── avutil ──────────────────────────────────────────────────────────
        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern AVFrame* av_frame_alloc();

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_frame_free(AVFrame** frame);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_frame_get_buffer(AVFrame* frame, int align);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_frame_make_writable(AVFrame* frame);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_frame_unref(AVFrame* frame);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_dict_set(AVDictionary** pm, byte* key, byte* value, int flags);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_dict_free(AVDictionary** m);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        private static extern int av_strerror(int errnum, byte* errbuf, ulong errbuf_size);

        public static string AvErrStr(int errnum)
        {
            byte* buf = stackalloc byte[64];
            av_strerror(errnum, buf, 64);
            return Marshal.PtrToStringAnsi((IntPtr)buf) ?? errnum.ToString();
        }

        // ── av_opt (avutil) ─────────────────────────────────────────────────
        // av_opt_set (string) is used for ALL parameters — it internally converts
        // the string to the correct native type, avoiding calling-convention issues
        // with av_opt_set_int (int64 alignment) and av_opt_set_q (struct by value).
        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_set(void* obj, byte* name, byte* val, int search_flags);

        // Read back integer option value — used to verify options were set correctly.
        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_get_int(void* obj, byte* name, int search_flags, long* out_val);

        // av_opt_set_int — fallback para campos inteiros (width, height, pix_fmt, etc.)
        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_set_int(void* obj, byte* name, long val, int search_flags);

        // Busca recursiva em objetos filho (priv_data do codec).
        public const int AV_OPT_SEARCH_CHILDREN = 1;

        // ── avcodec ─────────────────────────────────────────────────────────
        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern AVCodec* avcodec_find_encoder_by_name(byte* name);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern AVCodec* avcodec_find_decoder(uint id);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern void avcodec_free_context(AVCodecContext** avctx);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, AVDictionary** options);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern AVPacket* av_packet_alloc();

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_packet_free(AVPacket** pkt);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_packet_unref(AVPacket* pkt);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_send_frame(AVCodecContext* avctx, AVFrame* frame);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_receive_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

        // ── swscale ─────────────────────────────────────────────────────────
        [DllImport(SwScale, CallingConvention = CallingConvention.Cdecl)]
        public static extern SwsContext* sws_getContext(
            int srcW, int srcH, int srcFormat,
            int dstW, int dstH, int dstFormat,
            int flags, void* srcFilter, void* dstFilter, double* param);

        [DllImport(SwScale, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sws_scale(SwsContext* c,
            byte** srcSlice, int* srcStride,
            int srcSliceY, int srcSliceH,
            byte** dst, int* dstStride);

        [DllImport(SwScale, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sws_freeContext(SwsContext* swsContext);

        // ── AV pixel formats / codec IDs / swscale flags ─────────────────────
        public const int  AV_PIX_FMT_BGRA    = 28;
        public const int  AV_PIX_FMT_YUV420P = 0;
        public const uint AV_CODEC_ID_H264    = 27;
        public const int  SWS_BILINEAR        = 2;
        public const int  SWS_BICUBIC         = 4;     // sharper text than bilinear when downscaling
        public const int  SWS_AREA            = 0x20;  // area averaging — ideal para downscale de tela (sem ringing)
        public const int  SWS_ACCURATE_RND    = 0x40000;
    }

    // -----------------------------------------------------------------------
    // Opaque FFmpeg structs — only fields we actually access are declared.
    // AVFrame.data[] holds byte pointers; C# fixed buffers don't support
    // pointer types, so each pointer is a separate named field.
    // -----------------------------------------------------------------------
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodec { public byte* name; }

    // AVCodecContext is treated as opaque — all fields are set via av_opt_set_*
    // to stay correct across FFmpeg major versions (field layout changes between versions).
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodecContext { public void* _placeholder; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AVRational { public int num, den; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVFrame
    {
        // data[0..7] — individual fields because fixed buffers can't hold pointer types
        public byte* data0;
        public byte* data1;
        public byte* data2;
        public byte* data3;
        public byte* data4;
        public byte* data5;
        public byte* data6;
        public byte* data7;
        // linesize[0..7]
        public fixed int linesize[8];
        public byte**    extended_data;
        public int       width, height;
        public int       nb_samples;
        public int       format;
        public int       key_frame;
        public int       pict_type;
        public AVRational sample_aspect_ratio;
        public long       pts;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVPacket
    {
        public void*  buf;
        public long   pts;
        public long   dts;
        public byte*  data;
        public int    size;
        public int    stream_index;
        public int    flags;
        public void*  side_data;
        public int    side_data_elems;
        public long   duration;
        public long   pos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVDictionary { public int count; }

    internal unsafe struct SwsContext { }
#pragma warning restore CS0649

    // -----------------------------------------------------------------------
    // Stack-allocated plane/stride arrays for YUV420P frames
    // -----------------------------------------------------------------------
    internal static unsafe class PlaneHelper
    {
        public static void FillYuvPlanes(AVFrame* f, byte** dst)
        {
            dst[0] = f->data0;
            dst[1] = f->data1;
            dst[2] = f->data2;
            dst[3] = null;
        }

        public static void FillYuvStrides(AVFrame* f, int* dst)
        {
            dst[0] = f->linesize[0];
            dst[1] = f->linesize[1];
            dst[2] = f->linesize[2];
            dst[3] = 0;
        }
    }

    // =======================================================================
    // MfH264Encoder — encodes BGRA frames to H264 Annex-B using libx264.
    // =======================================================================
    public sealed unsafe class MfH264Encoder : IDisposable
    {
        private AVCodecContext* _ctx;
        private AVFrame*        _frame;
        private AVPacket*       _pkt;
        private SwsContext*     _swsCtx;
        private int             _width, _height, _fps;
        private int             _srcW,  _srcH;  // dimensões do BGRA de entrada (podem diferir de _width/_height)
        private long            _pts;
        private bool            _disposed;
        private bool            _forceNextKeyframe;

        public MfH264Encoder(int width, int height, int fps = 30, int bitrateBps = 2_500_000)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "jaclipei_ffmpeg.log");

            // libx264/yuv420p requires dimensions to be multiples of 2;
            // align to 16 (macroblock size) to prevent bottom-row artifacts.
            if (width  % 16 != 0) width  = (width  / 16) * 16;
            if (height % 16 != 0) height = (height / 16) * 16;
            if (width  <= 0) width  = 16;
            if (height <= 0) height = 16;

            _width  = width;
            _height = height;
            _fps    = fps > 0 ? fps : 30;

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss}] MfH264Encoder init {width}x{height} fps={_fps} bps={bitrateBps}\n");

            byte* name = stackalloc byte[16];
            WriteAscii(name, "libx264");
            AVCodec* codec = Ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null)
                throw new InvalidOperationException(
                    "FFmpeg libx264 encoder not found — ensure avcodec DLL is present in the install folder.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            // In FFmpeg 7.x (avcodec-63.dll) width, height, pix_fmt and framerate were
            // removed from the AVCodecContext option table. av_opt_set returns
            // AVERROR_OPTION_NOT_FOUND for all four, the fields stay at zero/default
            // and avcodec_open2 returns EINVAL.
            //
            // Fix: write directly to the struct at offsets verified from avcodec.h of
            // BtbN FFmpeg 7.x win64 build (avcodec-63.dll):
            //   framerate.num +100, framerate.den +104
            //   width +112, height +116, pix_fmt +136 (AV_PIX_FMT_YUV420P = 0)
            byte* ctxBytes = (byte*)_ctx;
            *(int*)(ctxBytes + 100) = _fps; // framerate.num
            *(int*)(ctxBytes + 104) = 1;    // framerate.den
            *(int*)(ctxBytes + 112) = width;  // width
            *(int*)(ctxBytes + 116) = height; // height
            *(int*)(ctxBytes + 136) = Ffmpeg.AV_PIX_FMT_YUV420P; // pix_fmt = 0
            File.AppendAllText(logPath,
                "  direct: framerate=" + _fps + "/1 width=" + width + " height=" + height + " pix_fmt=0(yuv420p)\n");

            // Options that remain in the table (av_opt_set still works with SEARCH_CHILDREN):
            LogOpt(logPath, "b",         bitrateBps.ToString());
            LogOpt(logPath, "time_base", $"1/{_fps}");
            LogOpt(logPath, "g",         (_fps * 2).ToString()); // IDR a cada 2s — menos burst RTP, menos risco de congelamento
            LogOpt(logPath, "bf",        "0");
            // aq-mode=2 (variance AQ) distribui bits por variância local —
            // texto/borda recebe mais bits, áreas planas menos → menos pixelação em movimento
            // intra-refresh=0: desabilitar varredura top→bottom (causa do rodapé pixelado com zerolatency);
            // me=hex: motion estimation rápida — umh é pesada demais para 60fps em software
            LogOpt(logPath, "x264-params", "intra-refresh=0:aq-mode=1:me=hex:force-cfr=1:qpmax=30");

            // Verify the direct writes landed correctly.
            File.AppendAllText(logPath,
                "  verify: width=" + *(int*)(ctxBytes+112) + " height=" + *(int*)(ctxBytes+116) + " pix_fmt=" + *(int*)(ctxBytes+136) + "\n");

            AVDictionary* opts = null;
            SetDict(&opts, "preset",  "superfast"); // veryfast can exceed 60fps budget (~12ms); superfast ~5ms
            SetDict(&opts, "tune",    "zerolatency");
            SetDict(&opts, "profile", "main");

            File.AppendAllText(logPath, $"  calling avcodec_open2...\n");
            int ret = Ffmpeg.avcodec_open2(_ctx, codec, &opts);
            Ffmpeg.av_dict_free(&opts);
            File.AppendAllText(logPath, $"  avcodec_open2 → {ret} ({Ffmpeg.AvErrStr(ret)})\n");
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2: {Ffmpeg.AvErrStr(ret)}");

            _frame          = Ffmpeg.av_frame_alloc();
            _frame->format  = Ffmpeg.AV_PIX_FMT_YUV420P;
            _frame->width   = width;
            _frame->height  = height;
            ret = Ffmpeg.av_frame_get_buffer(_frame, 32);
            if (ret < 0)
                throw new InvalidOperationException($"av_frame_get_buffer: {Ffmpeg.AvErrStr(ret)}");

            _pkt = Ffmpeg.av_packet_alloc();

            // sws context criado lazily no primeiro Encode (src pode ter resolução maior).
            // _srcW/_srcH = 0 sinaliza "não criado ainda".
            _srcW = 0; _srcH = 0; _swsCtx = null;

            File.AppendAllText(logPath, $"  MfH264Encoder ready\n");

            // ── Helpers scoped to the constructor ───────────────────────────
            void LogOpt(string lp, string optName, string optVal)
            {
                byte* k = stackalloc byte[64];  WriteAscii(k, optName);
                byte* v = stackalloc byte[256]; WriteAscii(v, optVal);
                // Flag 1 = AV_OPT_SEARCH_CHILDREN: also searches codec priv_data.
                int r = Ffmpeg.av_opt_set(_ctx, k, v, Ffmpeg.AV_OPT_SEARCH_CHILDREN);
                File.AppendAllText(lp, $"  av_opt_set {optName}={optVal} → {r} ({Ffmpeg.AvErrStr(r)})\n");
            }


        }

        public byte[]? Encode(byte[] bgra, int width, int height)
        {
            if (_disposed || bgra == null || bgra.Length == 0) return null;

            // Recriar sws context se a resolução de entrada (captura) mudou.
            // A resolução de SAÍDA (_width×_height) é fixa no construtor;
            // sws_scale faz a escala src→enc em um único passo SIMD (evita GDI+ na chamada).
            if (width != _srcW || height != _srcH)
            {
                if (_swsCtx != null) Ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = Ffmpeg.sws_getContext(
                    width,   height,   Ffmpeg.AV_PIX_FMT_BGRA,
                    _width,  _height,  Ffmpeg.AV_PIX_FMT_YUV420P,
                    Ffmpeg.SWS_AREA, null, null, null);  // área — sem ringing em texto
                if (_swsCtx == null) return null;  // leave _srcW/_srcH=0; recreate next frame
                _srcW = width; _srcH = height;     // only mark as valid after successful init
            }

            if (Ffmpeg.av_frame_make_writable(_frame) < 0) return null;

            fixed (byte* src = bgra)
            {
                byte*  srcPtr    = src;
                byte** srcSlice  = &srcPtr;
                int    srcStride = width * 4;
                int*   srcSt     = &srcStride;

                byte** dstPlanes = stackalloc byte*[4];
                int*   dstSt     = stackalloc int[4];
                PlaneHelper.FillYuvPlanes(_frame, dstPlanes);
                PlaneHelper.FillYuvStrides(_frame, dstSt);

                Ffmpeg.sws_scale(_swsCtx, srcSlice, srcSt, 0, height, dstPlanes, dstSt);
            }

            _frame->pts = _pts++;

            if (_forceNextKeyframe)
            {
                _forceNextKeyframe = false;
                _frame->pict_type = 1; // AV_PICTURE_TYPE_I — forces IDR
            }
            else
            {
                _frame->pict_type = 0; // AV_PICTURE_TYPE_NONE — let encoder decide
            }

            if (Ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return null;
            int ret = Ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (ret < 0) { Ffmpeg.av_packet_unref(_pkt); return null; }

            byte[] output = new byte[_pkt->size];
            Marshal.Copy((IntPtr)_pkt->data, output, 0, _pkt->size);
            Ffmpeg.av_packet_unref(_pkt);
            return output;
        }

        public void ForceKeyframe() => _forceNextKeyframe = true;

        // (av_opt helpers are now inlined as local functions in the constructor)

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_swsCtx != null) { Ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
            if (_pkt    != null) { fixed (AVPacket**       p = &_pkt)   Ffmpeg.av_packet_free(p);      _pkt   = null; }
            if (_frame  != null) { fixed (AVFrame**        f = &_frame) Ffmpeg.av_frame_free(f);       _frame = null; }
            if (_ctx    != null) { fixed (AVCodecContext** c = &_ctx)   Ffmpeg.avcodec_free_context(c); _ctx  = null; }
        }

        private static void WriteAscii(byte* buf, string s)
        {
            for (int i = 0; i < s.Length; i++) buf[i] = (byte)s[i];
            buf[s.Length] = 0;
        }

        private static void SetDict(AVDictionary** d, string key, string val)
        {
            byte* k = stackalloc byte[64];
            byte* v = stackalloc byte[64];
            WriteAscii(k, key);
            WriteAscii(v, val);
            Ffmpeg.av_dict_set(d, k, v, 0);
        }
    }

    // =======================================================================
    // MfH264Decoder — decodes H264 Annex-B to BGRA using FFmpeg libavcodec.
    // =======================================================================
    public sealed unsafe class MfH264Decoder : IDisposable
    {
        private AVCodecContext* _ctx;
        private AVFrame*        _frame;
        private AVPacket*       _pkt;
        private SwsContext*     _swsCtx;
        private int             _swsW, _swsH, _swsFmt;
        private bool            _disposed;

        public MfH264Decoder()
        {
            AVCodec* codec = Ffmpeg.avcodec_find_decoder(Ffmpeg.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException(
                    "FFmpeg H264 decoder not found — ensure avcodec DLL is present in the install folder.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            int ret = Ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2: {Ffmpeg.AvErrStr(ret)}");

            _frame = Ffmpeg.av_frame_alloc();
            _pkt   = Ffmpeg.av_packet_alloc();
        }

        public (byte[]? bgra, int width, int height) Decode(byte[] h264, int width = 0, int height = 0)
        {
            if (_disposed || h264 == null || h264.Length == 0) return (null, 0, 0);

            fixed (byte* data = h264)
            {
                _pkt->data = data;
                _pkt->size = h264.Length;
                if (Ffmpeg.avcodec_send_packet(_ctx, _pkt) < 0) return (null, 0, 0);
            }

            if (Ffmpeg.avcodec_receive_frame(_ctx, _frame) < 0) return (null, 0, 0);

            int w      = _frame->width;
            int h      = _frame->height;
            int srcFmt = _frame->format;
            if (w <= 0 || h <= 0) return (null, 0, 0);

            if (_swsCtx == null || _swsW != w || _swsH != h || _swsFmt != srcFmt)
            {
                if (_swsCtx != null) Ffmpeg.sws_freeContext(_swsCtx);
                _swsW = w; _swsH = h; _swsFmt = srcFmt;
                _swsCtx = Ffmpeg.sws_getContext(
                    w, h, srcFmt,
                    w, h, Ffmpeg.AV_PIX_FMT_BGRA,
                    Ffmpeg.SWS_BILINEAR, null, null, null);
            }

            byte[] bgra = new byte[w * h * 4];
            fixed (byte* dst = bgra)
            {
                byte*  dstPtr   = dst;
                byte** dstSlice = &dstPtr;
                int    dstStr   = w * 4;
                int*   dstSt    = &dstStr;

                byte** srcPlanes = stackalloc byte*[4];
                int*   srcSt     = stackalloc int[4];
                PlaneHelper.FillYuvPlanes(_frame, srcPlanes);
                PlaneHelper.FillYuvStrides(_frame, srcSt);

                Ffmpeg.sws_scale(_swsCtx, srcPlanes, srcSt, 0, h, dstSlice, dstSt);
            }

            Ffmpeg.av_frame_unref(_frame);
            return (bgra, w, h);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_swsCtx != null) { Ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
            if (_pkt    != null) { fixed (AVPacket**       p = &_pkt)   Ffmpeg.av_packet_free(p);      _pkt   = null; }
            if (_frame  != null) { fixed (AVFrame**        f = &_frame) Ffmpeg.av_frame_free(f);       _frame = null; }
            if (_ctx    != null) { fixed (AVCodecContext** c = &_ctx)   Ffmpeg.avcodec_free_context(c); _ctx  = null; }
        }
    }
}
