// FFmpeg-based H264 encoder/decoder (replaces Windows Media Foundation implementation)
// Works on all Windows editions including N/KN without Media Feature Pack.
// Requires FFmpeg DLLs alongside the EXE: avcodec-61.dll, avutil-59.dll,
// swscale-8.dll, swresample-5.dll (from BtbN GPL shared build).

using System;
using System.Runtime.InteropServices;

namespace JaTelei.Client.Services
{
    // -----------------------------------------------------------------------
    // Minimal FFmpeg P/Invoke bindings (subset needed for H264 encode/decode)
    // -----------------------------------------------------------------------
    internal static unsafe class Ffmpeg
    {
        const string AvCodec = "avcodec";
        const string AvUtil  = "avutil";
        const string SwScale = "swscale";

        // ---- avutil --------------------------------------------------------
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

        // ---- avcodec -------------------------------------------------------
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

        // ---- swscale -------------------------------------------------------
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

        // ---- AV pixel formats ----------------------------------------------
        public const int AV_PIX_FMT_BGRA    = 28;
        public const int AV_PIX_FMT_YUV420P = 0;

        // ---- Codec IDs -----------------------------------------------------
        public const uint AV_CODEC_ID_H264 = 27;

        // ---- swscale flags -------------------------------------------------
        public const int SWS_BILINEAR = 2;
    }

    // -----------------------------------------------------------------------
    // Opaque FFmpeg structs — only fields we actually access are declared.
    // AVFrame.data[] contains byte pointers; C# fixed buffers only support
    // primitive types, so each pointer is an individual field.
    // -----------------------------------------------------------------------
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodec { public byte* name; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodecContext
    {
        // We only need to set a handful of fields; the rest are accessed by
        // name only at open time via avcodec_open2 and are opaque after that.
        // Layout must match libavcodec exactly through the fields we use.
        public void*    av_class;
        public int      log_level_offset;
        public int      codec_type;
        public AVCodec* codec;
        public uint     codec_id;
        public uint     codec_tag;
        public void*    priv_data;
        public void*    @internal;
        public void*    opaque;
        public long     bit_rate;
        public int      bit_rate_tolerance;
        public int      global_quality;
        public int      compression_level;
        public int      flags;
        public int      flags2;
        public byte*    extradata;
        public int      extradata_size;
        public AVRational time_base;
        public AVRational pkt_timebase;
        public AVRational framerate;
        public int      ticks_per_frame;
        public int      delay;
        public int      width, height;
        public int      coded_width, coded_height;
        public int      gop_size;
        public int      pix_fmt;
        public void*    draw_horiz_band;
        public void*    get_format;
        public int      max_b_frames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AVRational { public int num, den; }

    /// <summary>
    /// AVFrame — only the fields we read/write are listed.
    /// data[0..7] are byte pointers; in C# fixed buffers can't hold pointer
    /// types, so they are declared as individual named fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVFrame
    {
        // data[0..7]
        public byte* data0;
        public byte* data1;
        public byte* data2;
        public byte* data3;
        public byte* data4;
        public byte* data5;
        public byte* data6;
        public byte* data7;
        // linesize[0..7]  — int is a valid fixed-buffer type
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
    // Small helper to build a 4-element plane/stride array on the stack
    // -----------------------------------------------------------------------
    internal static unsafe class PlaneHelper
    {
        /// <summary>Fill dst[0..3] with the four plane pointers of a YUV420P frame.</summary>
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
    // Keeps the same public API as the original MF-based encoder.
    // =======================================================================
    public sealed unsafe class MfH264Encoder : IDisposable
    {
        private AVCodecContext* _ctx;
        private AVFrame*        _frame;
        private AVPacket*       _pkt;
        private SwsContext*     _swsCtx;
        private int             _width, _height, _fps;
        private long            _pts;
        private bool            _disposed;

        public MfH264Encoder(int width, int height, int fps = 30, int bitrateBps = 2_500_000)
        {
            _width  = width;
            _height = height;
            _fps    = fps > 0 ? fps : 30;

            byte* name = stackalloc byte[16];
            WriteAscii(name, "libx264");
            AVCodec* codec = Ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null)
                throw new InvalidOperationException(
                    "FFmpeg libx264 encoder not found — ensure avcodec-61.dll is present.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            _ctx->width       = width;
            _ctx->height      = height;
            _ctx->pix_fmt     = Ffmpeg.AV_PIX_FMT_YUV420P;
            _ctx->bit_rate    = bitrateBps;
            _ctx->time_base   = new AVRational { num = 1, den = _fps };
            _ctx->framerate   = new AVRational { num = _fps, den = 1 };
            _ctx->gop_size    = _fps * 2;
            _ctx->max_b_frames = 0;

            AVDictionary* opts = null;
            SetDict(&opts, "preset",  "ultrafast");
            SetDict(&opts, "tune",    "zerolatency");
            SetDict(&opts, "profile", "baseline");

            int ret = Ffmpeg.avcodec_open2(_ctx, codec, &opts);
            Ffmpeg.av_dict_free(&opts);
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

            _swsCtx = Ffmpeg.sws_getContext(
                width, height, Ffmpeg.AV_PIX_FMT_BGRA,
                width, height, Ffmpeg.AV_PIX_FMT_YUV420P,
                Ffmpeg.SWS_BILINEAR, null, null, null);
            if (_swsCtx == null)
                throw new InvalidOperationException("sws_getContext failed.");
        }

        /// <summary>Encode one BGRA frame; returns Annex-B H264 bytes or null on error.</summary>
        public byte[]? Encode(byte[] bgra, int width, int height)
        {
            if (_disposed || bgra == null || bgra.Length == 0) return null;

            if (width != _width || height != _height)
            {
                Ffmpeg.sws_freeContext(_swsCtx);
                _width  = width;
                _height = height;
                _ctx->width  = width;
                _ctx->height = height;
                _frame->width  = width;
                _frame->height = height;
                _swsCtx = Ffmpeg.sws_getContext(
                    width, height, Ffmpeg.AV_PIX_FMT_BGRA,
                    width, height, Ffmpeg.AV_PIX_FMT_YUV420P,
                    Ffmpeg.SWS_BILINEAR, null, null, null);
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

            if (Ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return null;
            int ret = Ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (ret < 0) { Ffmpeg.av_packet_unref(_pkt); return null; }

            byte[] output = new byte[_pkt->size];
            Marshal.Copy((IntPtr)_pkt->data, output, 0, _pkt->size);
            Ffmpeg.av_packet_unref(_pkt);
            return output;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_swsCtx != null) { Ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
            if (_pkt    != null) { fixed (AVPacket** p = &_pkt) Ffmpeg.av_packet_free(p); _pkt = null; }
            if (_frame  != null) { fixed (AVFrame**  f = &_frame) Ffmpeg.av_frame_free(f); _frame = null; }
            if (_ctx    != null) { fixed (AVCodecContext** c = &_ctx) Ffmpeg.avcodec_free_context(c); _ctx = null; }
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
    // Keeps the same public API as the original MF-based decoder.
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
                    "FFmpeg H264 decoder not found — ensure avcodec-61.dll is present.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            int ret = Ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2: {Ffmpeg.AvErrStr(ret)}");

            _frame = Ffmpeg.av_frame_alloc();
            _pkt   = Ffmpeg.av_packet_alloc();
        }

        /// <summary>Decode H264 Annex-B; returns (BGRA bytes, width, height) or (null,0,0).</summary>
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
            if (_pkt    != null) { fixed (AVPacket** p = &_pkt) Ffmpeg.av_packet_free(p); _pkt = null; }
            if (_frame  != null) { fixed (AVFrame**  f = &_frame) Ffmpeg.av_frame_free(f); _frame = null; }
            if (_ctx    != null) { fixed (AVCodecContext** c = &_ctx) Ffmpeg.avcodec_free_context(c); _ctx = null; }
        }
    }
}
