#ifndef CAVERNIZE_PROAUDIO_API_H
#define CAVERNIZE_PROAUDIO_API_H

#include <stddef.h>
#include <stdint.h>

#if !defined(_WIN32) && (defined(__ANDROID__) || defined(__linux__) || defined(__unix__) || defined(__APPLE__))
#include <signal.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Installs process-wide SIGPIPE -> SIG_IGN shield so broken pipe writes (e.g. libffmpeg.so stdin) return EPIPE. */
int32_t cav_install_sigpipe_shield(void);

#if !defined(_WIN32) && defined(SIGPIPE) && (defined(__ANDROID__) || defined(__linux__) || defined(_POSIX_C_SOURCE) || defined(SA_RESTART))
static inline int32_t cav_sigpipe_shield_apply(void) {
    struct sigaction sa;
    sa.sa_handler = SIG_IGN;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_RESTART;
    return sigaction(SIGPIPE, &sa, NULL) == 0 ? 0 : -1;
}
#endif

#define CAV_PA_BACKEND_NONE 0
#define CAV_PA_BACKEND_PATH_A_AAUDIO 1
#define CAV_PA_BACKEND_PATH_B_LIBUSB 2

typedef struct CavProAudioStatus {
    int32_t active_backend;       /* CAV_PA_BACKEND_* */
    int32_t sample_rate;
    int32_t bit_depth;
    int32_t buffer_frames;
    int32_t is_exclusive;         /* 1 = AAudio EXCLUSIVE MMAP or Path B Direct USB */
    int32_t is_full_duplex;       /* 1 = live mic capture active */
    int32_t sync_mode;            /* 0 = N/A, 1 = Async Explicit FB, 2 = Async Implicit FB, 3 = Adaptive */
    int32_t output_xruns;
    int32_t input_xruns;
    float estimated_rtt_ms;
    float async_drift_ppm;
    float callback_p95_us;        /* 95th-percentile RT callback duration in microseconds */
    float callback_max_us;        /* Max observed RT callback duration in microseconds */
    float callback_cpu_p95_pct;   /* 95th-percentile RT callback CPU load % of burst duration */
    int32_t hold_capture_no_discard; /* 1 = unbroken-duplex capture hold (skip 8 ms discard_oldest) */
} CavProAudioStatus;

/* Returns library ABI version (101 = W60 v1.1). */
int32_t cavernize_pa_get_version(void);

/* Returns bitmask of compiled capabilities: bit 0 = Path A AAudio, bit 1 = Path B libusb UAC2. */
int32_t cavernize_pa_get_capabilities(void);

/* Path A: Opens & starts native AAudio LOW_LATENCY + EXCLUSIVE full-duplex streams. */
int32_t cavernize_pa_start_aaudio(
    int32_t sample_rate,
    int32_t buffer_frames,
    int32_t enable_input,
    int32_t output_device_id,
    int32_t input_device_id);

/* Path B: Opens & starts unrooted libusb-1.0 UAC2 isochronous full-duplex driver via UsbManager FD. */
int32_t cavernize_pa_start_libusb(
    int32_t usb_fd,
    int32_t sample_rate,
    int32_t buffer_frames,
    int32_t enable_input);

/* Stops whichever native backend (Path A or Path B) is currently running. */
void cavernize_pa_stop(void);

/* Lock-free notification that the USB audio device was detached by Android UsbManager. */
void cavernize_pa_notify_usb_detached(void);

/* Writes stereo interleaved float32 playback frames into the native lock-free OUT ring buffer. */
int32_t cavernize_pa_write_playback(const float* stereo_interleaved, int32_t frame_count);

/* Reads captured interleaved float32 microphone frames from the native lock-free IN ring buffer. */
int32_t cavernize_pa_read_capture(float* stereo_interleaved, int32_t max_frames);

/* Arms/disarms lock-free unbroken-duplex capture hold (skips 8 ms cav_rb_discard_oldest during sweep calibration). */
void cavernize_pa_set_hold_capture_no_discard(int32_t enable);

/* Returns 1 if unbroken-duplex capture hold is currently armed, 0 otherwise. */
int32_t cavernize_pa_get_hold_capture_no_discard(void);

/* Returns free frames available in the playback ring buffer (for non-blocking producer pacing). */
int32_t cavernize_pa_get_playback_free_frames(void);

/* Returns frames currently buffered in the playback ring buffer (for watermark capping & live latency). */
int32_t cavernize_pa_get_playback_buffered_frames(void);

/* Requests a lock-free SPSC-safe flush of all currently buffered playback frames. */
void cavernize_pa_flush_playback(void);

/* Queries live hardware status, latency, clock drift ppm, callback CPU%, and xrun counts. */
int32_t cavernize_pa_get_status(CavProAudioStatus* out_status);

/* Sends Focusrite Scarlett2/Gen4 vendor init step 0 (bmRequestType=0xA1, bRequest=0x00, 24B read) over Path B. */
int32_t cavernize_pa_scarlett2_init_step0(
    int32_t vendor_interface,
    uint8_t* rx_packet,
    int32_t rx_cap);

/* Sends a Focusrite Scarlett2 vendor USB control command over the active Path B libusb handle. */
int32_t cavernize_pa_scarlett2_xfer(
    int32_t vendor_interface,
    const uint8_t* tx_packet,
    int32_t tx_len,
    uint8_t* rx_packet,
    int32_t rx_cap);

#ifdef __cplusplus
}
#endif

#endif /* CAVERNIZE_PROAUDIO_API_H */
