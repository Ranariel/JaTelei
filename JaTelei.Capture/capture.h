#pragma once
#ifdef _WIN32
  #define JCAPI __declspec(dllexport)
#else
  #define JCAPI
#endif

#include <stdint.h>

// ---------------------------------------------------------------------------
// Codec
// ---------------------------------------------------------------------------
typedef enum JC_Codec {
    JC_CODEC_H264 = 0,   // H.264/AVC  (default)
    JC_CODEC_AV1  = 1,   // AV1        (falls back to H.264 if unavailable)
} JC_Codec;

// ---------------------------------------------------------------------------
// Hardware encoder vendor
// ---------------------------------------------------------------------------
typedef enum JC_EncoderVendor {
    JC_ENCODER_AUTO    = 0,  // auto-detect: NVENC > AMF > QSV > SW
    JC_ENCODER_NVENC   = 1,  // NVIDIA NVENC
    JC_ENCODER_AMF     = 2,  // AMD AMF
    JC_ENCODER_QSV     = 3,  // Intel Quick Sync Video
    JC_ENCODER_SOFTWARE= 4,  // Software (MFT fallback)
} JC_EncoderVendor;

// ---------------------------------------------------------------------------
// Capture mode
// ---------------------------------------------------------------------------
typedef enum JC_CaptureMode {
    JC_CAPTURE_AUTO    = 0,  // auto: WGC first, then DXGI DDup
    JC_CAPTURE_WGC     = 1,  // Windows Graphics Capture (supports windows + monitors)
    JC_CAPTURE_DXGI    = 2,  // DXGI Desktop Duplication (monitors only)
} JC_CaptureMode;

// ---------------------------------------------------------------------------
// Capture target
// ---------------------------------------------------------------------------
typedef enum JC_TargetKind {
    JC_TARGET_MONITOR = 0,   // capture a whole monitor (outputIndex)
    JC_TARGET_WINDOW  = 1,   // capture a specific HWND
} JC_TargetKind;

// ---------------------------------------------------------------------------
// Init parameters
// ---------------------------------------------------------------------------
typedef struct JC_InitParams {
    // --- capture ---
    JC_CaptureMode captureMode;
    JC_TargetKind  targetKind;
    int            adapterIndex;   // GPU adapter (0 = first)
    int            outputIndex;    // monitor index (JC_TARGET_MONITOR)
    void*          windowHandle;   // HWND (JC_TARGET_WINDOW); NULL = desktop

    // --- output resolution & rate ---
    int            dstWidth;
    int            dstHeight;
    int            fps;
    int            bitrateKbps;

    // --- codec / encoder ---
    JC_Codec          codec;
    JC_EncoderVendor  encoderVendor;

    // --- audio (set enableAudio=1 to activate WASAPI capture) ---
    int            enableAudio;    // 0=no audio, 1=capture default render device (loopback)
    int            audioBitrate;   // audio bitrate kbps (0 = default 128)
} JC_InitParams;

// ---------------------------------------------------------------------------
// Encoder info (for enumeration)
// ---------------------------------------------------------------------------
typedef struct JC_EncoderInfo {
    JC_EncoderVendor vendor;
    JC_Codec         codec;
    int              isHardware;
    char             name[128];
} JC_EncoderInfo;

// ---------------------------------------------------------------------------
// Display info
// ---------------------------------------------------------------------------
typedef struct JC_DisplayInfo {
    int  index;
    char friendlyName[128];
    int  width;
    int  height;
    int  isPrimary;
} JC_DisplayInfo;

// ---------------------------------------------------------------------------
// Window info
// ---------------------------------------------------------------------------
typedef struct JC_WindowInfo {
    void* hwnd;
    char  title[256];
    char  processName[128];
} JC_WindowInfo;

// ---------------------------------------------------------------------------
// Video frame result
// ---------------------------------------------------------------------------
typedef struct JC_VideoFrame {
    uint8_t* data;       // encoded video bytes (H264/AV1 Annex-B or ISOBMFF)
    int      size;       // byte count
    int      isKeyFrame; // 1 if IDR / key frame
    int64_t  pts;        // presentation timestamp (100-ns units, like MF)
} JC_VideoFrame;

// ---------------------------------------------------------------------------
// Audio frame result
// ---------------------------------------------------------------------------
typedef struct JC_AudioFrame {
    uint8_t* data;   // encoded audio bytes (AAC-LC raw)
    int      size;
    int64_t  pts;
} JC_AudioFrame;

// ---------------------------------------------------------------------------
// Combined output (one video + optionally one audio packet per call)
// ---------------------------------------------------------------------------
typedef struct JC_Output {
    JC_VideoFrame video;
    JC_AudioFrame audio;   // size==0 if no audio packet this call
} JC_Output;

#ifdef __cplusplus
extern "C" {
#endif

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

/**
 * Initialize the capture/encode engine.
 * Returns 0 on success, negative error code on failure.
 * Thread-safe: call once before any other JC_ function.
 */
JCAPI int  JC_Init(const JC_InitParams* params);

/**
 * Release all resources.  Safe to call multiple times.
 */
JCAPI void JC_Release(void);

// ---------------------------------------------------------------------------
// Capture + Encode
// ---------------------------------------------------------------------------

/**
 * Capture one frame and encode it.
 *   outVideoBuffer   – caller-allocated buffer for encoded video
 *   videoBufferSize  – buffer capacity in bytes
 *   outVideoBytes    – [out] number of bytes written (0 = no frame yet)
 *   outIsKeyFrame    – [out] 1 if IDR / key frame
 *   outAudioBuffer   – caller-allocated buffer for encoded audio (may be NULL)
 *   audioBufferSize  – capacity (bytes)
 *   outAudioBytes    – [out] bytes written; 0 if no audio or audio disabled
 *
 * Returns 0 (success / no frame yet), positive byte count, or negative error.
 * Non-blocking: returns immediately with 0 bytes if no new frame is ready.
 */
JCAPI int  JC_CaptureAndEncode(
    uint8_t* outVideoBuffer,  int videoBufferSize,  int* outVideoBytes, int* outIsKeyFrame,
    uint8_t* outAudioBuffer,  int audioBufferSize,  int* outAudioBytes);

// ---------------------------------------------------------------------------
// Control
// ---------------------------------------------------------------------------

/** Force IDR / key frame on the next encoded video frame. */
JCAPI void JC_ForceKeyframe(void);

/** Dynamically update video bitrate (kbps). */
JCAPI void JC_SetBitrate(int bitrateKbps);

/** Query actual encoded frame dimensions (may differ from requested). */
JCAPI void JC_GetOutputSize(int* width, int* height);

// ---------------------------------------------------------------------------
// Enumeration helpers (call before JC_Init)
// ---------------------------------------------------------------------------

/**
 * Enumerate available encoders.
 * outInfo    – caller array; may be NULL (returns count only)
 * maxCount   – capacity of outInfo array
 * Returns number of encoders found (may exceed maxCount).
 */
JCAPI int  JC_EnumEncoders(JC_EncoderInfo* outInfo, int maxCount);

/**
 * Enumerate monitors / displays.
 * Returns count; fills outInfo up to maxCount.
 */
JCAPI int  JC_EnumDisplays(JC_DisplayInfo* outInfo, int maxCount);

/**
 * Enumerate capturable windows (WGC-compatible).
 * Returns count; fills outInfo up to maxCount.
 */
JCAPI int  JC_EnumWindows(JC_WindowInfo* outInfo, int maxCount);

// ---------------------------------------------------------------------------
// Backward-compatible aliases (deprecated — use JC_Init / JC_CaptureAndEncode)
// ---------------------------------------------------------------------------

/** @deprecated Use JC_Init with JC_InitParams instead. */
static inline int JC_InitLegacy(
    int adapterIndex, int outputIndex,
    int dstWidth, int dstHeight, int fps, int bitrateKbps)
{
    JC_InitParams p = {0};
    p.captureMode   = JC_CAPTURE_AUTO;
    p.targetKind    = JC_TARGET_MONITOR;
    p.adapterIndex  = adapterIndex;
    p.outputIndex   = outputIndex;
    p.dstWidth      = dstWidth;
    p.dstHeight     = dstHeight;
    p.fps           = fps;
    p.bitrateKbps   = bitrateKbps;
    p.codec         = JC_CODEC_H264;
    p.encoderVendor = JC_ENCODER_AUTO;
    p.enableAudio   = 0;
    return JC_Init(&p);
}

#ifdef __cplusplus
}
#endif
