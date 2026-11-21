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
#else
#include <stdatomic.h>
#define CAV_ATOMIC_U32 _Atomic uint32_t
#define CAV_ATOMIC_U64 _Atomic uint64_t
#define CAV_LOAD_ACQ(x) atomic_load_explicit(&(x), memory_order_acquire)
#define CAV_LOAD_RLX(x) atomic_load_explicit(&(x), memory_order_relaxed)
#define CAV_STORE_REL(x, v) atomic_store_explicit(&(x), (v), memory_order_release)
#define CAV_STORE_RLX(x, v) atomic_store_explicit(&(x), (v), memory_order_relaxed)
#define CAV_FETCH_ADD_RLX(x, v) atomic_fetch_add_explicit(&(x), (v), memory_order_relaxed)
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
    return 0;
}

static inline void cav_rb_reset(CavRingBuffer* rb) {
    if (!rb) return;
    CAV_STORE_REL(rb->read_idx, 0u);
    CAV_STORE_REL(rb->write_idx, 0u);
    CAV_STORE_RLX(rb->overruns, 0u);
    CAV_STORE_RLX(rb->underruns, 0u);
}

static inline uint32_t cav_rb_avail_read_frames(const CavRingBuffer* rb) {
    uint32_t w = CAV_LOAD_ACQ(rb->write_idx);
    uint32_t r = CAV_LOAD_RLX(rb->read_idx);
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
    uint32_t avail_frames = ((w - r) & rb->mask) / rb->channels;

    uint32_t take = frames;
    if (take > avail_frames) {
        CAV_FETCH_ADD_RLX(rb->underruns, 1u);
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

#endif /* CAVERNIZE_RINGBUFFER_H */
