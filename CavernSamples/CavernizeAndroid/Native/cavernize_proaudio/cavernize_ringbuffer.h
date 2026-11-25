#ifndef CAVERNIZE_RINGBUFFER_H
#define CAVERNIZE_RINGBUFFER_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>

#ifdef __cplusplus
#include <atomic>
#define CAV_ATOMIC_U32 std::atomic<uint32_t>
#define CAV_ATOMIC_U64 std::atomic<uint64_t>
#define CAV_LOAD_ACQ(x) (x).load(std::memory_order_acquire)
#define CAV_LOAD_RLX(x) (x).load(std::memory_order_relaxed)
#define CAV_STORE_REL(x, v) (x).store((v), std::memory_order_release)
#define CAV_STORE_RLX(x, v) (x).store((v), std::memory_order_relaxed)
#define CAV_FETCH_ADD_RLX(x, v) (x).fetch_add((v), std::memory_order_relaxed)
#define CAV_CAS_WEAK_U64(x, expected, desired) (x).compare_exchange_weak((expected), (desired), std::memory_order_acq_rel, std::memory_order_acquire)
#else
#include <stdatomic.h>
#define CAV_ATOMIC_U32 _Atomic uint32_t
#define CAV_ATOMIC_U64 _Atomic uint64_t
#define CAV_LOAD_ACQ(x) atomic_load_explicit(&(x), memory_order_acquire)
#define CAV_LOAD_RLX(x) atomic_load_explicit(&(x), memory_order_relaxed)
#define CAV_STORE_REL(x, v) atomic_store_explicit(&(x), (v), memory_order_release)
#define CAV_STORE_RLX(x, v) atomic_store_explicit(&(x), (v), memory_order_relaxed)
#define CAV_FETCH_ADD_RLX(x, v) atomic_fetch_add_explicit(&(x), (v), memory_order_relaxed)
#define CAV_CAS_WEAK_U64(x, expected, desired) atomic_compare_exchange_weak_explicit(&(x), &(expected), (desired), memory_order_acq_rel, memory_order_acquire)
#endif

/* Cache-line aligned (64B) zero-allocation Single-Producer Single-Consumer (SPSC)
 * interleaved float32 audio ring buffer for real-time full-duplex audio callbacks. */
typedef struct CavRingBuffer {
    float* data;
    uint32_t capacity_samples; /* Always power of 2 */
    uint32_t mask;
    uint32_t channels;

    uint8_t pad0[48];
    CAV_ATOMIC_U32 write_idx;
    uint8_t pad1[60];
    CAV_ATOMIC_U32 read_idx;
    uint8_t pad2[60];
    CAV_ATOMIC_U64 overruns;
    CAV_ATOMIC_U64 underruns;
    uint8_t pad3[48];
    /* Packed (generation << 32) | snapshot_write_idx for lock-free SPSC consumer flush */
    CAV_ATOMIC_U64 flush_state;
    uint8_t pad4[56];
    CAV_ATOMIC_U64 consumer_flush_state;
} CavRingBuffer;

static inline int cav_rb_init(CavRingBuffer* rb, float* backing_storage, uint32_t power_of_two_samples, uint32_t channels) {
    if (!rb || !backing_storage || power_of_two_samples < 64 || (power_of_two_samples & (power_of_two_samples - 1u)) != 0u) {
        return -1;
    }
    if (channels < 1u || channels > 8u) {
        return -1;
    }
    rb->data = backing_storage;
    rb->capacity_samples = power_of_two_samples;
    rb->mask = power_of_two_samples - 1u;
    rb->channels = channels;
    memset(backing_storage, 0, (size_t)power_of_two_samples * sizeof(float));
    CAV_STORE_RLX(rb->write_idx, 0u);
    CAV_STORE_RLX(rb->read_idx, 0u);
    CAV_STORE_RLX(rb->overruns, 0u);
    CAV_STORE_RLX(rb->underruns, 0u);
    CAV_STORE_RLX(rb->flush_state, 0u);
    CAV_STORE_RLX(rb->consumer_flush_state, 0u);
    return 0;
}

static inline void cav_rb_reset(CavRingBuffer* rb) {
    if (!rb) return;
    CAV_STORE_REL(rb->read_idx, 0u);
    CAV_STORE_REL(rb->write_idx, 0u);
    CAV_STORE_REL(rb->flush_state, 0u);
    CAV_STORE_REL(rb->consumer_flush_state, 0u);
    CAV_STORE_RLX(rb->overruns, 0u);
    CAV_STORE_RLX(rb->underruns, 0u);
}

static inline void cav_rb_request_flush(CavRingBuffer* rb) {
    if (!rb || rb->capacity_samples == 0u) return;
    uint64_t old_state = CAV_LOAD_ACQ(rb->flush_state);
    uint64_t desired;
    do {
        uint32_t next_gen = (uint32_t)(old_state >> 32) + 1u;
        if (next_gen == 0u) next_gen = 1u;
        uint32_t w = CAV_LOAD_ACQ(rb->write_idx);
        desired = (((uint64_t)next_gen) << 32) | (uint64_t)w;
    } while (!CAV_CAS_WEAK_U64(rb->flush_state, old_state, desired));
}

static inline uint32_t cav_rb_avail_read_frames(const CavRingBuffer* rb) {
    if (!rb || rb->channels == 0u) return 0u;
    uint64_t fs = CAV_LOAD_ACQ(rb->flush_state);
    uint64_t cfs = CAV_LOAD_ACQ(rb->consumer_flush_state);
    uint32_t r = CAV_LOAD_ACQ(rb->read_idx);
    uint32_t w = CAV_LOAD_ACQ(rb->write_idx);
    if (fs != cfs) {
        uint32_t target_w = (uint32_t)(fs & 0xFFFFFFFFu);
        if (target_w < rb->capacity_samples) {
            r = target_w;
        }
    }
    return ((w - r) & rb->mask) / rb->channels;
}

static inline uint32_t cav_rb_avail_write_frames(const CavRingBuffer* rb) {
    uint32_t w = CAV_LOAD_RLX(rb->write_idx);
    uint32_t r = CAV_LOAD_ACQ(rb->read_idx);
    uint32_t used = (w - r) & rb->mask;
    return (rb->capacity_samples - 1u - used) / rb->channels;
}

static inline uint32_t cav_rb_write(CavRingBuffer* rb, const float* src, uint32_t frames) {
    if (!rb || !src || frames == 0u) return 0u;
    uint32_t w = CAV_LOAD_RLX(rb->write_idx);
    uint32_t r = CAV_LOAD_ACQ(rb->read_idx);
    uint32_t used = (w - r) & rb->mask;
    uint32_t free_frames = (rb->capacity_samples - 1u - used) / rb->channels;
    if (frames > free_frames) {
        CAV_FETCH_ADD_RLX(rb->overruns, 1u);
        frames = free_frames;
        if (frames == 0u) return 0u;
    }

    uint32_t samples = frames * rb->channels;
    uint32_t first = rb->capacity_samples - w;
    if (first > samples) first = samples;
    memcpy(rb->data + w, src, (size_t)first * sizeof(float));
    uint32_t second = samples - first;
    if (second > 0u) {
        memcpy(rb->data, src + first, (size_t)second * sizeof(float));
    }
    CAV_STORE_REL(rb->write_idx, (w + samples) & rb->mask);
    return frames;
}

static inline uint32_t cav_rb_read(CavRingBuffer* rb, float* dst, uint32_t frames, int zero_fill_missing) {
    if (!rb || !dst || frames == 0u) return 0u;
    uint32_t w = CAV_LOAD_ACQ(rb->write_idx);
    uint32_t r = CAV_LOAD_RLX(rb->read_idx);
    uint64_t fs = CAV_LOAD_ACQ(rb->flush_state);
    uint64_t cfs = CAV_LOAD_RLX(rb->consumer_flush_state);
    int flushed = 0;
    if (fs != cfs) {
        uint32_t target_w = (uint32_t)(fs & 0xFFFFFFFFu);
        if (target_w < rb->capacity_samples) {
            r = target_w;
            CAV_STORE_REL(rb->read_idx, r);
        }
        CAV_STORE_REL(rb->consumer_flush_state, fs);
        flushed = 1;
    }
    uint32_t avail_frames = ((w - r) & rb->mask) / rb->channels;

    uint32_t take = frames;
    if (take > avail_frames) {
        if (!flushed) {
            CAV_FETCH_ADD_RLX(rb->underruns, 1u);
        }
        take = avail_frames;
    }

    if (take > 0u) {
        uint32_t samples = take * rb->channels;
        uint32_t first = rb->capacity_samples - r;
        if (first > samples) first = samples;
        memcpy(dst, rb->data + r, (size_t)first * sizeof(float));
        uint32_t second = samples - first;
        if (second > 0u) {
            memcpy(dst + first, rb->data, (size_t)second * sizeof(float));
        }
        CAV_STORE_REL(rb->read_idx, (r + samples) & rb->mask);
    }

    if (zero_fill_missing && take < frames) {
        memset(dst + (size_t)take * rb->channels, 0, (size_t)(frames - take) * rb->channels * sizeof(float));
    }
    return take;
}

/* Consumer-thread-only SPSC-safe discard of oldest queued frames, keeping at most keep_max_frames newest frames. */
static inline uint32_t cav_rb_discard_oldest(CavRingBuffer* rb, uint32_t keep_max_frames) {
    if (!rb || rb->capacity_samples == 0u || rb->channels == 0u) return 0u;
    uint32_t w = CAV_LOAD_ACQ(rb->write_idx);
    uint32_t r = CAV_LOAD_RLX(rb->read_idx);
    uint64_t fs = CAV_LOAD_ACQ(rb->flush_state);
    uint64_t cfs = CAV_LOAD_RLX(rb->consumer_flush_state);
    if (fs != cfs) {
        uint32_t target_w = (uint32_t)(fs & 0xFFFFFFFFu);
        if (target_w < rb->capacity_samples) {
            r = target_w;
            CAV_STORE_REL(rb->read_idx, r);
        }
        CAV_STORE_REL(rb->consumer_flush_state, fs);
    }
    uint32_t avail_frames = ((w - r) & rb->mask) / rb->channels;
    if (avail_frames <= keep_max_frames) {
        return 0u;
    }
    uint32_t drop_frames = avail_frames - keep_max_frames;
    uint32_t drop_samples = drop_frames * rb->channels;
    CAV_STORE_REL(rb->read_idx, (r + drop_samples) & rb->mask);
    return drop_frames;
}

/* Zero-allocation RT click-suppression micro-ramp (24 frames) for underrun entry & recovery.
 * Eliminates hard step discontinuities when SPSC ring starves or resumes from silence. */
static inline void cav_rb_apply_glitch_ramp(
    float* interleaved,
    uint32_t frames,
    uint32_t channels,
    uint32_t valid_frames,
    int* in_underrun_state,
    float* last_sample_frame) {
    if (!interleaved || frames == 0u || channels == 0u || channels > 8u || !in_underrun_state || !last_sample_frame) {
        return;
    }
    const uint32_t kRampFrames = 24u;
    if (valid_frames >= frames) {
        if (*in_underrun_state != 0) {
            uint32_t ramp = frames < kRampFrames ? frames : kRampFrames;
            float inv = 1.0f / (float)(ramp + 1u);
            for (uint32_t f = 0u; f < ramp; ++f) {
                float g = (float)(f + 1u) * inv;
                for (uint32_t c = 0u; c < channels; ++c) {
                    interleaved[f * channels + c] *= g;
                }
            }
            *in_underrun_state = 0;
        }
        for (uint32_t c = 0u; c < channels; ++c) {
            last_sample_frame[c] = interleaved[(frames - 1u) * channels + c];
        }
        return;
    }

    if (valid_frames > 0u) {
        if (*in_underrun_state != 0) {
            uint32_t up = valid_frames < kRampFrames ? valid_frames : kRampFrames;
            float inv_up = 1.0f / (float)(up + 1u);
            for (uint32_t f = 0u; f < up; ++f) {
                float g = (float)(f + 1u) * inv_up;
                for (uint32_t c = 0u; c < channels; ++c) {
                    interleaved[f * channels + c] *= g;
                }
            }
        }
        uint32_t down = valid_frames < kRampFrames ? valid_frames : kRampFrames;
        uint32_t start = valid_frames - down;
        float inv_down = 1.0f / (float)(down + 1u);
        for (uint32_t i = 0u; i < down; ++i) {
            float g = (float)(down - i) * inv_down;
            uint32_t f = start + i;
            for (uint32_t c = 0u; c < channels; ++c) {
                interleaved[f * channels + c] *= g;
            }
        }
    } else if (*in_underrun_state == 0) {
        /* Write a short decay tail from the previous block's final sample into the zero-filled gap */
        uint32_t down = frames < kRampFrames ? frames : kRampFrames;
        float inv_down = 1.0f / (float)(down + 1u);
        for (uint32_t i = 0u; i < down; ++i) {
            float g = (float)(down - i) * inv_down;
            for (uint32_t c = 0u; c < channels; ++c) {
                interleaved[i * channels + c] = last_sample_frame[c] * g;
            }
        }
    }

    for (uint32_t c = 0u; c < channels; ++c) {
        last_sample_frame[c] = 0.0f;
    }
    *in_underrun_state = 1;
}

#endif /* CAVERNIZE_RINGBUFFER_H */
