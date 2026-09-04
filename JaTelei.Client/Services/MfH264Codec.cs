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
        const string AvCodec  = "avcodec-61";
        const string AvUtil   = "avutil-59";
        const string SwScale  = "swscale-8";

        // ---- avutil --------------------------------------------------------
        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void* av_malloc(ulong size);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_free(void* ptr);

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
        public static extern AVDictionary** av_dict_alloc();  // not used directly

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_dict_set(AVDictionary** pm, byte* key, byte* value, int flags);

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_dict_free(AVDictionary** m);

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

        [DllImport(AvCodec, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_parameters_from_context(AVCodecParameters* par, AVCodecContext* codec);

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

        // ---- error helper --------------------------------------------------
        public static string AvErrStr(int errnum)
        {
            byte* buf = stackalloc byte[64];
            av_strerror(errnum, buf, 64);
            return Marshal.PtrToStringAnsi((IntPtr)buf) ?? errnum.ToString();
        }

        [DllImport(AvUtil, CallingConvention = CallingConvention.Cdecl)]
        private static extern int av_strerror(int errnum, byte* errbuf, ulong errbuf_size);

        // ---- AV pixel formats ----------------------------------------------
        public const int AV_PIX_FMT_BGRA   = 28;   // Windows GDI native
        public const int AV_PIX_FMT_YUV420P = 0;   // libx264 preferred
        public const int AV_PIX_FMT_BGR24  = 3;

        // ---- Codec IDs -----------------------------------------------------
        public const uint AV_CODEC_ID_H264 = 27;

        // ---- swscale flags -------------------------------------------------
        public const int SWS_BILINEAR = 2;

        // ---- AVERROR constants (negative errno-style) ----------------------
        public const int AVERROR_EAGAIN = -11;      // EAGAIN on Linux, mapped
        public static int AVERROR_EOF   = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24)); // -541478725

        public static bool IsEagain(int ret)  => ret == AVERROR_EAGAIN || ret == -11;
        public static bool IsEof(int ret)     => ret == -541478725;
    }

    // -----------------------------------------------------------------------
    // Opaque FFmpeg structs – only the fields we access are declared
    // -----------------------------------------------------------------------
#pragma warning disable CS0649  // field never assigned
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodec { public byte* name; /* ... */ }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodecContext
    {
        public void*    av_class;
        public int      log_level_offset;
        public int      codec_type;         // AVMediaType
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
        public float    b_quant_factor;
        public float    b_quant_offset;
        public float    i_quant_factor;
        public float    i_quant_offset;
        public float    lumi_masking;
        public float    temporal_cplx_masking;
        public float    spatial_cplx_masking;
        public float    p_masking;
        public float    dark_masking;
        public int      slice_count;
        public int*     slice_offset;
        public AVRational sample_aspect_ratio;
        public int      me_cmp;
        public int      me_sub_cmp;
        public int      mb_cmp;
        public int      ildct_cmp;
        public int      dia_size;
        public int      last_predictor_count;
        public int      me_pre_cmp;
        public int      pre_dia_size;
        public int      me_subpel_quality;
        public int      me_range;
        public int      slice_flags;
        public int      mb_decision;
        public ushort*  intra_matrix;
        public ushort*  inter_matrix;
        public ushort*  chroma_intra_matrix;
        public int      intra_dc_precision;
        public int      skip_top;
        public int      skip_bottom;
        public int      mb_lmin;
        public int      mb_lmax;
        public int      bidir_refine;
        public int      keyint_min;
        public int      refs;
        public int      mv0_threshold;
        public int      color_primaries;
        public int      color_trc;
        public int      colorspace;
        public int      color_range;
        public int      chroma_sample_location;
        public int      slices;
        // ... remaining fields irrelevant for our use
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AVRational { public int num, den; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVFrame
    {
        public fixed byte* data_ptrs[8];   // data[0..7]
        public fixed int   linesize[8];
        public byte**      extended_data;
        public int         width, height;
        public int         nb_samples;
        public int         format;
        public int         key_frame;
        public int         pict_type;
        public AVRational  sample_aspect_ratio;
        public long        pts;
        public long        pkt_dts;
        public AVRational  time_base;
        // ... more fields we don't need
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
    internal unsafe struct AVDictionary { public int count; /* opaque */ }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodecParameters { /* opaque for our use */ }

    internal unsafe struct SwsContext { /* opaque */ }
#pragma warning restore CS0649

    // -----------------------------------------------------------------------
    // Helper to pin a managed byte[] and expose it as byte**  / int*
    // -----------------------------------------------------------------------
    internal static unsafe class PinHelper
    {
        public static byte[] BytesFromFrame(AVFrame* f, int planeCount)
        {
            // Read frame data from plane 0 only (used for packed formats like BGRA)
            int stride = f->linesize[0];
            int h      = f->height;
            byte* src  = f->data_ptrs[0];
            int   len  = stride * h;
            var   buf  = new byte[len];
            Marshal.Copy((IntPtr)src, buf, 0, len);
            return buf;
        }
    }

    // =======================================================================
    // MfH264Encoder – encodes BGRA frames to H264 Annex-B using libx264
    // (same public API as the old MF-based encoder)
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

            // Find libx264 encoder
            byte* name = stackalloc byte[16];
            WriteAscii(name, "libx264");
            AVCodec* codec = Ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null)
                throw new InvalidOperationException("FFmpeg libx264 encoder not found. Ensure avcodec-61.dll is present.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            _ctx->width      = width;
            _ctx->height     = height;
            _ctx->pix_fmt    = Ffmpeg.AV_PIX_FMT_YUV420P;
            _ctx->bit_rate   = bitrateBps;
            _ctx->time_base  = new AVRational { num = 1, den = _fps };
            _ctx->framerate  = new AVRational { num = _fps, den = 1 };
            _ctx->gop_size   = _fps * 2;   // keyframe every 2 s
            _ctx->max_b_frames = 0;         // no B-frames for low latency

            // Set x264 options for low latency
            AVDictionary* opts = null;
            SetDictEntry(&opts, "preset",   "ultrafast");
            SetDictEntry(&opts, "tune",     "zerolatency");
            SetDictEntry(&opts, "profile",  "baseline");

            int ret = Ffmpeg.avcodec_open2(_ctx, codec, &opts);
            Ffmpeg.av_dict_free(&opts);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2 failed: {Ffmpeg.AvErrStr(ret)}");

            // Allocate YUV frame
            _frame           = Ffmpeg.av_frame_alloc();
            _frame->format   = Ffmpeg.AV_PIX_FMT_YUV420P;
            _frame->width    = width;
            _frame->height   = height;
            ret = Ffmpeg.av_frame_get_buffer(_frame, 32);
            if (ret < 0)
                throw new InvalidOperationException($"av_frame_get_buffer failed: {Ffmpeg.AvErrStr(ret)}");

            _pkt = Ffmpeg.av_packet_alloc();

            // Color-space converter: BGRA → YUV420P
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
            if (_disposed) return null;
            if (bgra == null || bgra.Length == 0) return null;

            // Resize colorspace converter if resolution changed
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

            int ret = Ffmpeg.av_frame_make_writable(_frame);
            if (ret < 0) return null;

            fixed (byte* src = bgra)
            {
                byte*  srcPtrs0  = src;
                byte** srcSlice  = &srcPtrs0;
                int    srcStride = width * 4; // BGRA stride
                int*   srcSt     = &srcStride;

                byte** dstData    = (byte**)_frame->data_ptrs[0]; // trick – use via sws
                // sws_scale with frame planes
                byte*  dP0 = _frame->data_ptrs[0];
                byte*  dP1 = _frame->data_ptrs[1];
                byte*  dP2 = _frame->data_ptrs[2];
                byte** dPlanes = stackalloc byte*[4];
                dPlanes[0] = dP0; dPlanes[1] = dP1; dPlanes[2] = dP2; dPlanes[3] = null;

                int ls0 = _frame->linesize[0];
                int ls1 = _frame->linesize[1];
                int ls2 = _frame->linesize[2];
                int ls3 = 0;
                int* dStrides = stackalloc int[4];
                dStrides[0] = ls0; dStrides[1] = ls1; dStrides[2] = ls2; dStrides[3] = ls3;

                Ffmpeg.sws_scale(_swsCtx, srcSlice, srcSt, 0, height, dPlanes, dStrides);
            }

            _frame->pts = _pts++;

            ret = Ffmpeg.avcodec_send_frame(_ctx, _frame);
            if (ret < 0) return null;

            ret = Ffmpeg.avcodec_receive_packet(_ctx, _pkt);
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

        // ---- helpers -------------------------------------------------------
        private static void WriteAscii(byte* buf, string s)
        {
            for (int i = 0; i < s.Length; i++) buf[i] = (byte)s[i];
            buf[s.Length] = 0;
        }

        private static void SetDictEntry(AVDictionary** dict, string key, string value)
        {
            byte* k = stackalloc byte[64];
            byte* v = stackalloc byte[64];
            WriteAscii(k, key);
            WriteAscii(v, value);
            Ffmpeg.av_dict_set(dict, k, v, 0);
        }
    }

    // =======================================================================
    // MfH264Decoder – decodes H264 Annex-B to BGRA using FFmpeg libavcodec
    // (same public API as the old MF-based decoder)
    // =======================================================================
    public sealed unsafe class MfH264Decoder : IDisposable
    {
        private AVCodecContext* _ctx;
        private AVFrame*        _frame;
        private AVPacket*       _pkt;
        private SwsContext*     _swsCtx;
        private int             _swsW, _swsH;
        private bool            _disposed;

        public MfH264Decoder()
        {
            AVCodec* codec = Ffmpeg.avcodec_find_decoder(Ffmpeg.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException("FFmpeg H264 decoder not found. Ensure avcodec-61.dll is present.");

            _ctx = Ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null)
                throw new InvalidOperationException("Failed to allocate AVCodecContext.");

            int ret = Ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2 failed: {Ffmpeg.AvErrStr(ret)}");

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

                int ret = Ffmpeg.avcodec_send_packet(_ctx, _pkt);
                if (ret < 0) return (null, 0, 0);
            }

            int recvRet = Ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (recvRet < 0) return (null, 0, 0);

            int w = _frame->width;
            int h = _frame->height;
            if (w <= 0 || h <= 0) return (null, 0, 0);

            int srcFmt = _frame->format; // usually AV_PIX_FMT_YUV420P

            // (Re)create swscale context when resolution or format changes
            if (_swsCtx == null || _swsW != w || _swsH != h)
            {
                if (_swsCtx != null) Ffmpeg.sws_freeContext(_swsCtx);
                _swsW = w; _swsH = h;
                _swsCtx = Ffmpeg.sws_getContext(
                    w, h, srcFmt,
                    w, h, Ffmpeg.AV_PIX_FMT_BGRA,
                    Ffmpeg.SWS_BILINEAR, null, null, null);
            }

            byte[] bgra = new byte[w * h * 4];
            fixed (byte* dst = bgra)
            {
                byte*  dstPtr    = dst;
                byte** dstSlice  = &dstPtr;
                int    dstStride = w * 4;
                int*   dstSt     = &dstStride;

                byte*  sP0 = _frame->data_ptrs[0];
                byte*  sP1 = _frame->data_ptrs[1];
                byte*  sP2 = _frame->data_ptrs[2];
                byte** srcPlanes = stackalloc byte*[4];
                srcPlanes[0] = sP0; srcPlanes[1] = sP1; srcPlanes[2] = sP2; srcPlanes[3] = null;

                int ls0 = _frame->linesize[0];
                int ls1 = _frame->linesize[1];
                int ls2 = _frame->linesize[2];
                int* srcSt = stackalloc int[4];
                srcSt[0] = ls0; srcSt[1] = ls1; srcSt[2] = ls2; srcSt[3] = 0;

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
