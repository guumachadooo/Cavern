#include "cavernize_proaudio_api.h"
#include "cavernize_ringbuffer.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>

#if defined(__ANDROID__)
#include <aaudio/AAudio.h>
#include <android/log.h>
#define CAV_LOGI(...) __android_log_print(ANDROID_LOG_INFO, "CavernizeAAudio", __VA_ARGS__)
#define CAV_LOGW(...) __android_log_print(ANDROID_LOG_WARN, "CavernizeAAudio", __VA_ARGS__)
#else
#define CAV_LOGI(...) ((void)0)
#define CAV_LOGW(...) ((void)0)
#endif

namespace cavernize {

static constexpr uint32_t kRingSamples = 32768u; /* 16384 stereo frames (~341 ms @ 48 kHz), power-of-2 */
static constexpr uint32_t kMaxCallbackFrames = 2048u;

#if defined(__ANDROID__)

struct PathAAudioEngine {
    std::mutex lifecycle_mtx;
    AAudioStream* out_stream = nullptr;
    AAudioStream* in_stream = nullptr;

    float out_storage[kRingSamples];
    float in_storage[kRingSamples];
    float in_callback_scratch[kMaxCallbackFrames * 2];

    CavRingBuffer out_rb;
    CavRingBuffer in_rb;

    std::atomic<bool> running{false};
    std::atomic<bool> input_drained{false};
    std::atomic<int32_t> active_sr{48000};
    std::atomic<int32_t> active_burst{128};
    std::atomic<int32_t> is_exclusive{0};
    std::atomic<int32_t> is_full_duplex{0};
    std::atomic<int32_t> last_xruns{0};
    std::atomic<int32_t> tuner_cooldown{0};
    std::atomic<float> measured_rtt_ms{0.0f};

    static aaudio_data_callback_result_t on_output_ready(
        AAudioStream* stream,
        void* user_data,
        void* audio_data,
        int32_t num_frames) {
        auto* self = static_cast<PathAAudioEngine*>(user_data);
        if (!self || !self->running.load(std::memory_order_acquire) || num_frames <= 0) {
            if (audio_data && num_frames > 0) {
                memset(audio_data, 0, static_cast<size_t>(num_frames) * 2u * sizeof(float));
            }
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        const uint32_t frames = static_cast<uint32_t>(num_frames);

        /* 1. Output-driven full-duplex capture (google/oboe golden rule: non-blocking read inside output callback) */
        if (self->in_stream && self->is_full_duplex.load(std::memory_order_relaxed)) {
            uint32_t to_read = frames < kMaxCallbackFrames ? frames : kMaxCallbackFrames;
            if (!self->input_drained.load(std::memory_order_relaxed)) {
                /* Drain initial startup backlog so capture stays sample-locked to output burst */
                int32_t drained = 0;
                int32_t iter = 0;
                do {
                    drained = AAudioStream_read(self->in_stream, self->in_callback_scratch, static_cast<int32_t>(to_read), 0);
                    iter++;
                } while (drained == static_cast<int32_t>(to_read) && iter < 8);
                self->input_drained.store(true, std::memory_order_release);
            } else {
                int32_t got = AAudioStream_read(self->in_stream, self->in_callback_scratch, static_cast<int32_t>(to_read), 0);
                if (got > 0) {
                    const int32_t in_ch = AAudioStream_getChannelCount(self->in_stream);
                    if (in_ch == 1) {
                        /* Up-duplicate mono capture in-place (backwards) to stereo SPSC input ring */
                        for (int32_t i = got - 1; i >= 0; --i) {
                            float s = self->in_callback_scratch[i];
                            self->in_callback_scratch[i * 2] = s;
                            self->in_callback_scratch[i * 2 + 1] = s;
                        }
                    }
                    cav_rb_write(&self->in_rb, self->in_callback_scratch, static_cast<uint32_t>(got));
                }
            }
        }

        /* 2. Pull stereo playback frames from lock-free OUT ring (zero-fill on underrun for glitch safety) */
        auto* out_f32 = static_cast<float*>(audio_data);
        cav_rb_read(&self->out_rb, out_f32, frames, 1);

        /* 3. Dynamic LatencyTuner (oboe-style: grow buffer by 1 burst on XRun, up to capacity) */
        int32_t xruns = AAudioStream_getXRunCount(stream);
        int32_t prev_x = self->last_xruns.load(std::memory_order_relaxed);
        int32_t cd = self->tuner_cooldown.load(std::memory_order_relaxed);
        if (xruns > prev_x && cd <= 0) {
            self->last_xruns.store(xruns, std::memory_order_relaxed);
            int32_t burst = AAudioStream_getFramesPerBurst(stream);
            int32_t cur_buf = AAudioStream_getBufferSizeInFrames(stream);
            int32_t cap = AAudioStream_getBufferCapacityInFrames(stream);
            if (burst > 0 && cur_buf + burst <= cap) {
                AAudioStream_setBufferSizeInFrames(stream, cur_buf + burst);
            }
            self->tuner_cooldown.store(64, std::memory_order_relaxed);
        } else if (cd > 0) {
            self->tuner_cooldown.store(cd - 1, std::memory_order_relaxed);
        }

        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    static void on_stream_error(AAudioStream* /*stream*/, void* user_data, aaudio_result_t err) {
        auto* self = static_cast<PathAAudioEngine*>(user_data);
        CAV_LOGW("AAudio stream error callback: %d (%s)", static_cast<int>(err), AAudio_convertResultToText(err));
        if (self && err == AAUDIO_ERROR_DISCONNECTED) {
            self->running.store(false, std::memory_order_release);
        }
    }

    bool open_single_stream(
        aaudio_direction_t dir,
        int32_t sample_rate,
        int32_t buffer_frames,
        int32_t device_id,
        int32_t channels,
        bool with_callback,
        AAudioStream** out_handle) {
        AAudioStreamBuilder* builder = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder) {
            return false;
        }

        AAudioStreamBuilder_setDirection(builder, dir);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_FLOAT);
        AAudioStreamBuilder_setChannelCount(builder, channels);
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
        AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_EXCLUSIVE);
        if (sample_rate > 0) {
            AAudioStreamBuilder_setSampleRate(builder, sample_rate);
        }
        if (device_id > 0) {
            AAudioStreamBuilder_setDeviceId(builder, device_id);
        }
        if (dir == AAUDIO_DIRECTION_OUTPUT) {
            AAudioStreamBuilder_setUsage(builder, AAUDIO_USAGE_GAME);
            AAudioStreamBuilder_setContentType(builder, AAUDIO_CONTENT_TYPE_MUSIC);
        } else {
            AAudioStreamBuilder_setInputPreset(builder, AAUDIO_INPUT_PRESET_UNPROCESSED);
        }
        if (with_callback) {
            AAudioStreamBuilder_setDataCallback(builder, on_output_ready, this);
            AAudioStreamBuilder_setErrorCallback(builder, on_stream_error, this);
            if (buffer_frames == 64 || buffer_frames == 128 || buffer_frames == 256) {
                AAudioStreamBuilder_setFramesPerDataCallback(builder, buffer_frames);
            }
        }

        aaudio_result_t res = AAudioStreamBuilder_openStream(builder, out_handle);
        if (res != AAUDIO_OK || !(*out_handle)) {
            /* Graceful fallback: EXCLUSIVE -> SHARED, and drop fixed callback frame count if HAL rejects it */
            AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
            if (with_callback) {
                AAudioStreamBuilder_setFramesPerDataCallback(builder, AAUDIO_UNSPECIFIED);
            }
            res = AAudioStreamBuilder_openStream(builder, out_handle);
        }
        AAudioStreamBuilder_delete(builder);
        if (res != AAUDIO_OK || !(*out_handle)) {
            *out_handle = nullptr;
            return false;
        }

        /* Target 2 hardware bursts initially for minimum glitch-free latency */
        int32_t burst = AAudioStream_getFramesPerBurst(*out_handle);
        if (burst > 0) {
            int32_t target = burst * 2;
            if (buffer_frames > target) target = buffer_frames * 2;
            int32_t cap = AAudioStream_getBufferCapacityInFrames(*out_handle);
            if (target > cap) target = cap;
            AAudioStream_setBufferSizeInFrames(*out_handle, target);
        }
        return true;
    }

    int32_t start(int32_t sample_rate, int32_t buffer_frames, int32_t enable_input, int32_t out_dev_id, int32_t in_dev_id) {
        std::lock_guard<std::mutex> lock(lifecycle_mtx);
        stop_locked();

        cav_rb_init(&out_rb, out_storage, kRingSamples, 2u);
        cav_rb_init(&in_rb, in_storage, kRingSamples, 2u);
        input_drained.store(false, std::memory_order_relaxed);
        last_xruns.store(0, std::memory_order_relaxed);
        tuner_cooldown.store(0, std::memory_order_relaxed);

        if (!open_single_stream(AAUDIO_DIRECTION_OUTPUT, sample_rate, buffer_frames, out_dev_id, 2, true, &out_stream)) {
            return -10;
        }

        int32_t actual_sr = AAudioStream_getSampleRate(out_stream);
        int32_t actual_burst = AAudioStream_getFramesPerBurst(out_stream);
        int32_t out_excl = (AAudioStream_getSharingMode(out_stream) == AAUDIO_SHARING_MODE_EXCLUSIVE) ? 1 : 0;

        bool duplex_ok = false;
        if (enable_input) {
            /* Try stereo first (Scarlett 2i2 2-ch), fall back to mono if device/phone mic is 1-ch */
            if (open_single_stream(AAUDIO_DIRECTION_INPUT, actual_sr, buffer_frames, in_dev_id, 2, false, &in_stream) ||
                open_single_stream(AAUDIO_DIRECTION_INPUT, actual_sr, buffer_frames, in_dev_id, 1, false, &in_stream)) {
                if (AAudioStream_requestStart(in_stream) == AAUDIO_OK) {
                    duplex_ok = true;
                } else {
                    AAudioStream_close(in_stream);
                    in_stream = nullptr;
                }
            }
        }

        active_sr.store(actual_sr > 0 ? actual_sr : 48000, std::memory_order_release);
        active_burst.store(actual_burst > 0 ? actual_burst : buffer_frames, std::memory_order_release);
        is_exclusive.store(out_excl, std::memory_order_release);
        is_full_duplex.store(duplex_ok ? 1 : 0, std::memory_order_release);
        running.store(true, std::memory_order_release);

        if (AAudioStream_requestStart(out_stream) != AAUDIO_OK) {
            stop_locked();
            return -11;
        }

        int32_t buf_sz = AAudioStream_getBufferSizeInFrames(out_stream);
        float est_out_ms = actual_sr > 0 ? (buf_sz * 1000.0f / static_cast<float>(actual_sr)) : 12.0f;
        float est_rtt = duplex_ok ? (est_out_ms * 2.0f + (out_excl ? 2.5f : 8.0f)) : (est_out_ms + (out_excl ? 2.0f : 6.0f));
        measured_rtt_ms.store(est_rtt, std::memory_order_release);

        CAV_LOGI("AAudio started: SR=%d burst=%d excl=%d duplex=%d estRTT=%.2f ms",
                 actual_sr, actual_burst, out_excl, duplex_ok ? 1 : 0, est_rtt);
        return 0;
    }

    void stop() {
        std::lock_guard<std::mutex> lock(lifecycle_mtx);
        stop_locked();
    }

    void stop_locked() {
        running.store(false, std::memory_order_release);
        if (out_stream) {
            AAudioStream_requestStop(out_stream);
            AAudioStream_close(out_stream);
            out_stream = nullptr;
        }
        if (in_stream) {
            AAudioStream_requestStop(in_stream);
            AAudioStream_close(in_stream);
            in_stream = nullptr;
        }
        is_full_duplex.store(0, std::memory_order_release);
        is_exclusive.store(0, std::memory_order_release);
    }

    void fill_status(CavProAudioStatus* st) {
        if (!st) return;
        st->active_backend = running.load(std::memory_order_acquire) ? CAV_PA_BACKEND_PATH_A_AAUDIO : CAV_PA_BACKEND_NONE;
        st->sample_rate = active_sr.load(std::memory_order_relaxed);
        st->bit_depth = 32;
        st->buffer_frames = active_burst.load(std::memory_order_relaxed);
        st->is_exclusive = is_exclusive.load(std::memory_order_relaxed);
        st->is_full_duplex = is_full_duplex.load(std::memory_order_relaxed);
        st->sync_mode = 0;
        st->output_xruns = out_stream ? AAudioStream_getXRunCount(out_stream) : 0;
        st->input_xruns = in_stream ? AAudioStream_getXRunCount(in_stream) : 0;
        st->estimated_rtt_ms = measured_rtt_ms.load(std::memory_order_relaxed);
        st->async_drift_ppm = 0.0f;
    }
};

static PathAAudioEngine g_aaudio;

int32_t path_a_start(int32_t sr, int32_t buf_frames, int32_t enable_in, int32_t out_dev, int32_t in_dev) {
    return g_aaudio.start(sr, buf_frames, enable_in, out_dev, in_dev);
}
void path_a_stop() { g_aaudio.stop(); }
int32_t path_a_write_playback(const float* data, int32_t frames) {
    return frames > 0 ? static_cast<int32_t>(cav_rb_write(&g_aaudio.out_rb, data, static_cast<uint32_t>(frames))) : 0;
}
int32_t path_a_read_capture(float* data, int32_t max_frames) {
    return max_frames > 0 ? static_cast<int32_t>(cav_rb_read(&g_aaudio.in_rb, data, static_cast<uint32_t>(max_frames), 0)) : 0;
}
int32_t path_a_free_frames() { return static_cast<int32_t>(cav_rb_avail_write_frames(&g_aaudio.out_rb)); }
void path_a_get_status(CavProAudioStatus* st) { g_aaudio.fill_status(st); }

#else

int32_t path_a_start(int32_t, int32_t, int32_t, int32_t, int32_t) { return -1; }
void path_a_stop() {}
int32_t path_a_write_playback(const float*, int32_t) { return 0; }
int32_t path_a_read_capture(float*, int32_t) { return 0; }
int32_t path_a_free_frames() { return 0; }
void path_a_get_status(CavProAudioStatus* st) { if (st) memset(st, 0, sizeof(*st)); }

#endif /* __ANDROID__ */

} /* namespace cavernize */
