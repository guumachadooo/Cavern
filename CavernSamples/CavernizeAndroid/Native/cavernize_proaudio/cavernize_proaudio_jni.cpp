#include "cavernize_proaudio_api.h"

#include <atomic>
#include <cstring>

namespace cavernize {
int32_t path_a_start(int32_t sr, int32_t buf_frames, int32_t enable_in, int32_t out_dev, int32_t in_dev);
void path_a_stop();
int32_t path_a_write_playback(const float* data, int32_t frames);
int32_t path_a_read_capture(float* data, int32_t max_frames);
int32_t path_a_free_frames();
void path_a_get_status(CavProAudioStatus* st);

int32_t path_b_start(int32_t usb_fd, int32_t sr, int32_t buf_frames, int32_t enable_in);
void path_b_stop();
int32_t path_b_write_playback(const float* data, int32_t frames);
int32_t path_b_read_capture(float* data, int32_t max_frames);
int32_t path_b_free_frames();
void path_b_get_status(CavProAudioStatus* st);
int32_t path_b_scarlett2_xfer(int32_t vendor_if, const uint8_t* tx, int32_t tx_len, uint8_t* rx, int32_t rx_cap);
} /* namespace cavernize */

static std::atomic<int32_t> g_active_backend{CAV_PA_BACKEND_NONE};

extern "C" {

int32_t cavernize_pa_get_version(void) {
    return 100; /* W10 v1.0 */
}

int32_t cavernize_pa_get_capabilities(void) {
    int32_t caps = 0;
#if defined(__ANDROID__)
    caps |= 1; /* Bit 0: Path A AAudio */
#endif
#if defined(CAVERNIZE_HAVE_LIBUSB)
    caps |= 2; /* Bit 1: Path B libusb UAC2 */
#endif
    return caps;
}

int32_t cavernize_pa_start_aaudio(
    int32_t sample_rate,
    int32_t buffer_frames,
    int32_t enable_input,
    int32_t output_device_id,
    int32_t input_device_id) {
    cavernize_pa_stop();
    int32_t rc = cavernize::path_a_start(sample_rate, buffer_frames, enable_input, output_device_id, input_device_id);
    if (rc == 0) {
        g_active_backend.store(CAV_PA_BACKEND_PATH_A_AAUDIO, std::memory_order_release);
    }
    return rc;
}

int32_t cavernize_pa_start_libusb(
    int32_t usb_fd,
    int32_t sample_rate,
    int32_t buffer_frames,
    int32_t enable_input) {
    cavernize_pa_stop();
    int32_t rc = cavernize::path_b_start(usb_fd, sample_rate, buffer_frames, enable_input);
    if (rc == 0) {
        g_active_backend.store(CAV_PA_BACKEND_PATH_B_LIBUSB, std::memory_order_release);
    }
    return rc;
}

void cavernize_pa_stop(void) {
    int32_t cur = g_active_backend.exchange(CAV_PA_BACKEND_NONE, std::memory_order_acq_rel);
    if (cur == CAV_PA_BACKEND_PATH_B_LIBUSB) {
        cavernize::path_b_stop();
    } else if (cur == CAV_PA_BACKEND_PATH_A_AAUDIO) {
        cavernize::path_a_stop();
    }
}

int32_t cavernize_pa_write_playback(const float* stereo_interleaved, int32_t frame_count) {
    int32_t cur = g_active_backend.load(std::memory_order_acquire);
    if (cur == CAV_PA_BACKEND_PATH_B_LIBUSB) {
        return cavernize::path_b_write_playback(stereo_interleaved, frame_count);
    }
    if (cur == CAV_PA_BACKEND_PATH_A_AAUDIO) {
        return cavernize::path_a_write_playback(stereo_interleaved, frame_count);
    }
    return 0;
}

int32_t cavernize_pa_read_capture(float* stereo_interleaved, int32_t max_frames) {
    int32_t cur = g_active_backend.load(std::memory_order_acquire);
    if (cur == CAV_PA_BACKEND_PATH_B_LIBUSB) {
        return cavernize::path_b_read_capture(stereo_interleaved, max_frames);
    }
    if (cur == CAV_PA_BACKEND_PATH_A_AAUDIO) {
        return cavernize::path_a_read_capture(stereo_interleaved, max_frames);
    }
    return 0;
}

int32_t cavernize_pa_get_playback_free_frames(void) {
    int32_t cur = g_active_backend.load(std::memory_order_acquire);
    if (cur == CAV_PA_BACKEND_PATH_B_LIBUSB) {
        return cavernize::path_b_free_frames();
    }
    if (cur == CAV_PA_BACKEND_PATH_A_AAUDIO) {
        return cavernize::path_a_free_frames();
    }
    return 0;
}

int32_t cavernize_pa_get_status(CavProAudioStatus* out_status) {
    if (!out_status) return -1;
    memset(out_status, 0, sizeof(*out_status));
    int32_t cur = g_active_backend.load(std::memory_order_acquire);
    if (cur == CAV_PA_BACKEND_PATH_B_LIBUSB) {
        cavernize::path_b_get_status(out_status);
        return 0;
    }
    if (cur == CAV_PA_BACKEND_PATH_A_AAUDIO) {
        cavernize::path_a_get_status(out_status);
        return 0;
    }
    return 0;
}

int32_t cavernize_pa_scarlett2_xfer(
    int32_t vendor_interface,
    const uint8_t* tx_packet,
    int32_t tx_len,
    uint8_t* rx_packet,
    int32_t rx_cap) {
    if (g_active_backend.load(std::memory_order_acquire) != CAV_PA_BACKEND_PATH_B_LIBUSB) {
        return -1;
    }
    return cavernize::path_b_scarlett2_xfer(vendor_interface, tx_packet, tx_len, rx_packet, rx_cap);
}

} /* extern "C" */
