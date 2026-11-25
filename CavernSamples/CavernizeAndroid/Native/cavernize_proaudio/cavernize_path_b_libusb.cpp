#include "cavernize_proaudio_api.h"
#include "cavernize_ringbuffer.h"
#include "cavernize_uac2_core.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>
#include <unistd.h>
#include <vector>

#if defined(CAVERNIZE_HAVE_LIBUSB)
#include <sys/time.h>
#if __has_include(<libusb.h>)
#include <libusb.h>
#else
#include <libusb-1.0/libusb.h>
#endif
#endif

#if defined(__ANDROID__)
#include <android/log.h>
#include <sys/resource.h>
#define CAV_USBLOGI(...) __android_log_print(ANDROID_LOG_INFO, "CavernizeLibUsb", __VA_ARGS__)
#define CAV_USBLOGW(...) __android_log_print(ANDROID_LOG_WARN, "CavernizeLibUsb", __VA_ARGS__)
#else
#define CAV_USBLOGI(...) ((void)0)
#define CAV_USBLOGW(...) ((void)0)
#endif

namespace cavernize {

#if defined(CAVERNIZE_HAVE_LIBUSB)

static constexpr uint32_t kUsbRingSamples = 32768u;
static constexpr int kIsoTransferCount = 6;
static constexpr int kIsoPacketsPerTransfer = 8; /* 8 x 125 us = 1 ms per URB in High-Speed */
static constexpr int kTransientErrorAbortThreshold = 8;
static constexpr int kUsbDrainCeilingMs = 1200;
static constexpr int kUsbDetachedDrainCeilingMs = 150;
static constexpr int kScarlettControlTimeoutMs = 250;
static constexpr uint8_t kScarlettUsbCmdInit = 0x00;

struct PathBLibUsbEngine;

struct UsbTransferToken {
    PathBLibUsbEngine* engine = nullptr;
    std::atomic<bool> detached{false};
    std::atomic<int32_t> active_xfers{0};
};

struct QuarantinedUsbSession {
    libusb_context* ctx = nullptr;
    libusb_device_handle* devh = nullptr;
    int owned_usb_fd = -1;
    std::vector<libusb_transfer*> xfers;
    std::vector<std::vector<uint8_t>> buffers;
    UsbTransferToken* token = nullptr;
};

struct PathBLibUsbEngine {
    std::mutex lifecycle_mtx;
    std::mutex ctrl_mtx;

    libusb_context* ctx = nullptr;
    libusb_device_handle* devh = nullptr;
    int owned_usb_fd = -1;
    UsbTransferToken* current_token = nullptr;
    std::vector<QuarantinedUsbSession> quarantined_sessions;
    CavUac2Topology topo{};
    CavUac2StreamDesc pb_stream{};
    CavUac2StreamDesc cap_stream{};
    bool has_pb = false;
    bool has_cap = false;
    bool is_high_speed = true;
    int claimed_vendor_if = -1;
    double pb_pkts_per_sec = 8000.0;
    double cap_pkts_per_sec = 8000.0;

    float out_storage[kUsbRingSamples];
    float in_storage[kUsbRingSamples];
    CavRingBuffer out_rb;
    CavRingBuffer in_rb;

    std::vector<libusb_transfer*> out_xfers;
    std::vector<libusb_transfer*> in_xfers;
    std::vector<libusb_transfer*> fb_xfers;
    std::vector<std::vector<uint8_t>> xfer_buffers;

    std::thread event_thread;
    std::atomic<bool> running{false};
    std::atomic<bool> stopping{false};
    std::atomic<bool> device_gone{false};
    std::atomic<bool> playback_primed{false};
    std::atomic<uint64_t> usb_detach_seq{0};
    std::atomic<int32_t> io_inflight{0};
    std::atomic<int32_t> consec_transfer_errors{0};
    std::atomic<int32_t> full_duplex_flag{0};
    std::atomic<uint32_t> rate_1616{0};
    uint32_t phase_accum_1616 = 0;

    std::atomic<int32_t> active_sr{48000};
    std::atomic<int32_t> active_bits{24};
    std::atomic<int32_t> active_buf_frames{128};
    std::atomic<int32_t> active_sync_mode{0};
    std::atomic<int32_t> out_xruns{0};
    std::atomic<int32_t> in_xruns{0};
    std::atomic<float> drift_ppm{0.0f};
    std::atomic<float> est_rtt_ms{0.0f};

    float pack_scratch[1024];
    float unpack_scratch[1024];
    int underrun_ramp_state = 1;
    float last_out_sample[2] = {0.0f, 0.0f};

    struct IoGuard {
        std::atomic<int32_t>& counter;
        explicit IoGuard(std::atomic<int32_t>& c) : counter(c) {
            counter.fetch_add(1, std::memory_order_acq_rel);
        }
        ~IoGuard() {
            counter.fetch_sub(1, std::memory_order_release);
        }
    };

    enum class TransferDisposition {
        Abort,
        Completed,
        TransientError
    };

    static double compute_iso_packets_per_sec(uint8_t bInterval, bool hs) {
        uint8_t clamped = bInterval < 1u ? 1u : (bInterval > 16u ? 16u : bInterval);
        double base = hs ? 8000.0 : 1000.0;
        return base / static_cast<double>(1u << (clamped - 1u));
    }

    static TransferDisposition classify_transfer(
        libusb_transfer* xfer,
        UsbTransferToken* tok,
        PathBLibUsbEngine** out_self) {
        *out_self = nullptr;
        if (!xfer || !tok) return TransferDisposition::Abort;
        PathBLibUsbEngine* self = tok->engine;
        if (tok->detached.load(std::memory_order_acquire) || !self ||
            !self->running.load(std::memory_order_acquire) ||
            self->stopping.load(std::memory_order_acquire) ||
            self->device_gone.load(std::memory_order_acquire) ||
            xfer->status == LIBUSB_TRANSFER_CANCELLED ||
            xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
            if (xfer->status == LIBUSB_TRANSFER_NO_DEVICE && self &&
                !tok->detached.load(std::memory_order_acquire)) {
                self->device_gone.store(true, std::memory_order_release);
                self->running.store(false, std::memory_order_release);
            }
            tok->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            return TransferDisposition::Abort;
        }

        bool has_error = (xfer->status != LIBUSB_TRANSFER_COMPLETED);
        if (!has_error && xfer->num_iso_packets > 0) {
            int err_pkts = 0;
            for (int i = 0; i < xfer->num_iso_packets; ++i) {
                const auto pst = xfer->iso_packet_desc[i].status;
                if (pst == LIBUSB_TRANSFER_NO_DEVICE) {
                    self->device_gone.store(true, std::memory_order_release);
                    self->running.store(false, std::memory_order_release);
                    tok->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
                    return TransferDisposition::Abort;
                }
                if (pst != LIBUSB_TRANSFER_COMPLETED) {
                    err_pkts++;
                }
            }
            if (err_pkts == xfer->num_iso_packets) {
                has_error = true;
            }
        }

        if (has_error) {
            int32_t errs = self->consec_transfer_errors.fetch_add(1, std::memory_order_acq_rel) + 1;
            if (errs >= kTransientErrorAbortThreshold) {
                self->device_gone.store(true, std::memory_order_release);
                self->running.store(false, std::memory_order_release);
                tok->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
                return TransferDisposition::Abort;
            }
            *out_self = self;
            return TransferDisposition::TransientError;
        }

        self->consec_transfer_errors.store(0, std::memory_order_relaxed);
        *out_self = self;
        return TransferDisposition::Completed;
    }

    static bool resubmit_or_retire(libusb_transfer* xfer, UsbTransferToken* tok, PathBLibUsbEngine* self, std::atomic<int32_t>* xrun_counter) {
        if (tok->detached.load(std::memory_order_acquire) ||
            !self->running.load(std::memory_order_acquire) ||
            self->stopping.load(std::memory_order_acquire) ||
            self->device_gone.load(std::memory_order_acquire)) {
            tok->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            return false;
        }
        int rc = libusb_submit_transfer(xfer);
        if (rc != 0) {
            if (xrun_counter) {
                xrun_counter->fetch_add(1, std::memory_order_relaxed);
            }
            tok->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            int32_t errs = self->consec_transfer_errors.fetch_add(1, std::memory_order_acq_rel) + 1;
            if (rc == LIBUSB_ERROR_NO_DEVICE || errs >= kTransientErrorAbortThreshold) {
                self->device_gone.store(true, std::memory_order_release);
                self->running.store(false, std::memory_order_release);
            }
            return false;
        }
        return true;
    }

    static void LIBUSB_CALL on_out_complete(libusb_transfer* xfer) {
        auto* tok = xfer ? static_cast<UsbTransferToken*>(xfer->user_data) : nullptr;
        PathBLibUsbEngine* self = nullptr;
        TransferDisposition disp = classify_transfer(xfer, tok, &self);
        if (disp == TransferDisposition::Abort) return;

        if (disp == TransferDisposition::TransientError) {
            self->out_xruns.fetch_add(1, std::memory_order_relaxed);
        }
        self->fill_out_transfer(xfer, false);
        resubmit_or_retire(xfer, tok, self, &self->out_xruns);
    }

    static void LIBUSB_CALL on_in_complete(libusb_transfer* xfer) {
        auto* tok = xfer ? static_cast<UsbTransferToken*>(xfer->user_data) : nullptr;
        PathBLibUsbEngine* self = nullptr;
        TransferDisposition disp = classify_transfer(xfer, tok, &self);
        if (disp == TransferDisposition::Abort) return;

        if (disp == TransferDisposition::TransientError) {
            self->in_xruns.fetch_add(1, std::memory_order_relaxed);
            resubmit_or_retire(xfer, tok, self, &self->in_xruns);
            return;
        }

        const uint32_t ch = self->cap_stream.channels > 0 ? self->cap_stream.channels : 2u;
        const uint32_t bpf = ch * (self->cap_stream.subslot_bytes > 0 ? self->cap_stream.subslot_bytes : 3u);
        uint32_t total_in_frames = 0;
        uint32_t completed_pkts = 0;

        for (int i = 0; i < xfer->num_iso_packets; ++i) {
            const auto& pkt = xfer->iso_packet_desc[i];
            if (pkt.status == LIBUSB_TRANSFER_COMPLETED && pkt.actual_length > 0 && bpf > 0) {
                const uint8_t* ptr = libusb_get_iso_packet_buffer_simple(xfer, static_cast<unsigned int>(i));
                uint32_t frames = pkt.actual_length / bpf;
                total_in_frames += frames;
                completed_pkts++;
                if (frames > 0 && frames * ch <= 1024u) {
                    cav_uac2_unpack_to_float(ptr, static_cast<size_t>(frames) * ch, self->unpack_scratch,
                                             static_cast<CavUac2Format>(self->cap_stream.format));
                    if (ch == 2u) {
                        cav_rb_write(&self->in_rb, self->unpack_scratch, frames);
                    } else {
                        /* Fold mono or take first 2 channels into stereo SPSC capture ring */
                        float stereo_tmp[1024];
                        uint32_t capped = frames < 512u ? frames : 512u;
                        for (uint32_t f = 0; f < capped; ++f) {
                            stereo_tmp[f * 2] = self->unpack_scratch[f * ch];
                            stereo_tmp[f * 2 + 1] = ch >= 2u ? self->unpack_scratch[f * ch + 1] : stereo_tmp[f * 2];
                        }
                        cav_rb_write(&self->in_rb, stereo_tmp, capped);
                    }
                }
            }
        }

        /* Implicit feedback clock tracking: divide by actual completed_pkts and scale if IN/OUT bIntervals differ */
        if (self->pb_stream.implicit_feedback && total_in_frames > 0 && completed_pkts > 0) {
            double frames_per_cap_pkt = static_cast<double>(total_in_frames) / static_cast<double>(completed_pkts);
            double frames_per_pb_pkt = self->pb_pkts_per_sec > 0.0
                ? frames_per_cap_pkt * (self->cap_pkts_per_sec / self->pb_pkts_per_sec)
                : frames_per_cap_pkt;
            uint32_t measured_1616 = static_cast<uint32_t>(llround(frames_per_pb_pkt * 65536.0));
            if (measured_1616 > 0x00010000u && measured_1616 < 0x04000000u) {
                uint32_t prev = self->rate_1616.load(std::memory_order_relaxed);
                uint32_t smoothed = prev > 0 ? ((prev * 15u + measured_1616) >> 4) : measured_1616;
                self->rate_1616.store(smoothed, std::memory_order_relaxed);
                self->update_ppm(smoothed);
            }
        }

        resubmit_or_retire(xfer, tok, self, &self->in_xruns);
    }

    static void LIBUSB_CALL on_fb_complete(libusb_transfer* xfer) {
        auto* tok = xfer ? static_cast<UsbTransferToken*>(xfer->user_data) : nullptr;
        PathBLibUsbEngine* self = nullptr;
        TransferDisposition disp = classify_transfer(xfer, tok, &self);
        if (disp == TransferDisposition::Abort) return;

        if (disp == TransferDisposition::Completed) {
            for (int i = 0; i < xfer->num_iso_packets; ++i) {
                const auto& pkt = xfer->iso_packet_desc[i];
                if (pkt.status == LIBUSB_TRANSFER_COMPLETED && pkt.actual_length >= 3) {
                    const uint8_t* ptr = libusb_get_iso_packet_buffer_simple(xfer, static_cast<unsigned int>(i));
                    uint32_t fb_1616 = cav_uac2_decode_feedback_1616(ptr, pkt.actual_length, self->is_high_speed ? 1 : 0);
                    /* UAC2 explicit feedback is in frames per (micro)frame; scale to frames per OUT bInterval packet */
                    double base_rate = self->is_high_speed ? 8000.0 : 1000.0;
                    double interval_scale = self->pb_pkts_per_sec > 0.0 ? (base_rate / self->pb_pkts_per_sec) : 1.0;
                    uint32_t scaled_1616 = static_cast<uint32_t>(llround(static_cast<double>(fb_1616) * interval_scale));
                    if (scaled_1616 > 0x00010000u && scaled_1616 < 0x04000000u) {
                        self->rate_1616.store(scaled_1616, std::memory_order_relaxed);
                        self->update_ppm(scaled_1616);
                    }
                }
            }
        }
        resubmit_or_retire(xfer, tok, self, nullptr);
    }

    void update_ppm(uint32_t actual_1616) {
        int32_t sr = active_sr.load(std::memory_order_relaxed);
        double pkts_per_sec = pb_pkts_per_sec > 0.0 ? pb_pkts_per_sec : (is_high_speed ? 8000.0 : 1000.0);
        double nominal = static_cast<double>(sr) / pkts_per_sec;
        double actual = static_cast<double>(actual_1616) / 65536.0;
        if (nominal > 0.01) {
            float ppm = static_cast<float>(((actual - nominal) / nominal) * 1e6);
            if (ppm > -5000.0f && ppm < 5000.0f) {
                drift_ppm.store(ppm, std::memory_order_relaxed);
            }
        }
    }

    void fill_out_transfer(libusb_transfer* xfer, bool initial_silence) {
        const uint32_t ch = pb_stream.channels > 0 ? pb_stream.channels : 2u;
        const uint32_t subslot = pb_stream.subslot_bytes > 0 ? pb_stream.subslot_bytes : 3u;
        const uint32_t bpf = ch * subslot;
        const uint32_t cur_rate = rate_1616.load(std::memory_order_relaxed);
        uint8_t* buf_ptr = xfer->buffer;

        for (int i = 0; i < xfer->num_iso_packets; ++i) {
            uint32_t frames = cav_uac2_advance_packet_frames(&phase_accum_1616, cur_rate);
            if (frames * bpf > pb_stream.max_packet_size && bpf > 0) {
                frames = pb_stream.max_packet_size / bpf;
            }
            if (frames * 2u > 1024u) {
                frames = 512u;
            }

            if (initial_silence) {
                /* Pre-zero initial startup URBs cleanly without pulling from empty ring or tripping bogus XRuns */
                memset(pack_scratch, 0, static_cast<size_t>(frames) * 2u * sizeof(float));
            } else {
                bool had_flush = out_rb.flush_state.load(std::memory_order_acquire) !=
                                 out_rb.consumer_flush_state.load(std::memory_order_relaxed);
                uint32_t got = cav_rb_read(&out_rb, pack_scratch, frames, 1);
                cav_rb_apply_glitch_ramp(pack_scratch, frames, 2u, got, &underrun_ramp_state, last_out_sample);
                if (got < frames && !had_flush && playback_primed.load(std::memory_order_acquire)) {
                    out_xruns.fetch_add(1, std::memory_order_relaxed);
                }
            }

            size_t bytes = 0;
            if (ch == 2u) {
                bytes = cav_uac2_pack_float(pack_scratch, static_cast<size_t>(frames) * 2u, buf_ptr,
                                            static_cast<CavUac2Format>(pb_stream.format));
            } else {
                float expanded[1024];
                memset(expanded, 0, sizeof(expanded));
                uint32_t capped = (ch > 0u && frames * ch > 1024u) ? (1024u / ch) : frames;
                for (uint32_t f = 0; f < capped; ++f) {
                    expanded[f * ch] = pack_scratch[f * 2];
                    if (ch >= 2u) expanded[f * ch + 1] = pack_scratch[f * 2 + 1];
                }
                bytes = cav_uac2_pack_float(expanded, static_cast<size_t>(capped) * ch, buf_ptr,
                                            static_cast<CavUac2Format>(pb_stream.format));
            }

            xfer->iso_packet_desc[i].length = static_cast<unsigned int>(bytes);
            buf_ptr += bytes;
        }
        xfer->length = static_cast<int>(buf_ptr - xfer->buffer);
    }

    int set_uac2_sample_rate(uint32_t hz) {
        if (!devh || topo.clock_source_id == 0) return -1;
        uint8_t payload[4] = {
            static_cast<uint8_t>(hz & 0xFFu),
            static_cast<uint8_t>((hz >> 8) & 0xFFu),
            static_cast<uint8_t>((hz >> 16) & 0xFFu),
            static_cast<uint8_t>((hz >> 24) & 0xFFu)
        };
        uint16_t wValue = static_cast<uint16_t>(0x01u << 8); /* CS_SAM_FREQ_CONTROL */
        uint16_t wIndex = static_cast<uint16_t>((static_cast<uint16_t>(topo.clock_source_id) << 8) | topo.ac_interface_num);
        int r = libusb_control_transfer(devh, 0x21, 0x01 /* SET_CUR */, wValue, wIndex, payload, 4, 500);
        return r == 4 ? 0 : r;
    }

    int32_t start(int32_t usb_fd, int32_t sample_rate, int32_t buffer_frames, int32_t enable_input) {
        std::lock_guard<std::mutex> lock(lifecycle_mtx);
        stop_locked();

        if (usb_fd < 0) return -20;

        const uint64_t start_detach_seq = usb_detach_seq.load(std::memory_order_seq_cst);
        /* Duplicate UsbManager FD so Android fdsan UsbDeviceConnection.Close() can never race libusb */
        int dup_fd = dup(usb_fd);
        if (dup_fd < 0) return -20;
        owned_usb_fd = dup_fd;
        device_gone.store(false, std::memory_order_seq_cst);
        consec_transfer_errors.store(0, std::memory_order_relaxed);
        stopping.store(false, std::memory_order_release);

        /* Mandatory unrooted Android sequence: disable bus scanning before libusb_init */
        libusb_set_option(nullptr, LIBUSB_OPTION_NO_DEVICE_DISCOVERY);
        if (libusb_init(&ctx) != 0 || !ctx) {
            stop_locked();
            return -21;
        }
        if (libusb_wrap_sys_device(ctx, static_cast<intptr_t>(owned_usb_fd), &devh) != 0 || !devh) {
            stop_locked();
            return -22;
        }

        libusb_device* dev = libusb_get_device(devh);
        int speed = libusb_get_device_speed(dev);
        is_high_speed = (speed >= LIBUSB_SPEED_HIGH);

        /* Fetch active raw configuration descriptor and parse UAC2 topology */
        libusb_config_descriptor* cfg = nullptr;
        if (libusb_get_active_config_descriptor(dev, &cfg) != 0 || !cfg) {
            stop_locked();
            return -23;
        }

        /* Read raw config descriptor bytes via GET_DESCRIPTOR for exact UAC2 CS_INTERFACE parsing */
        std::vector<uint8_t> raw_cfg(cfg->wTotalLength);
        int got_desc = libusb_get_descriptor(devh, LIBUSB_DT_CONFIG, 0, raw_cfg.data(), static_cast<int>(raw_cfg.size()));
        libusb_free_config_descriptor(cfg);
        if (got_desc < 9 || cav_uac2_parse_config_desc(raw_cfg.data(), static_cast<size_t>(got_desc), &topo) != 0) {
            stop_locked();
            return -24;
        }

        int pb_idx = cav_uac2_select_best_stream(topo.playback, topo.playback_count);
        if (pb_idx < 0) {
            stop_locked();
            return -25;
        }
        pb_stream = topo.playback[pb_idx];
        has_pb = true;

        int cap_idx = (enable_input || pb_stream.implicit_feedback)
            ? cav_uac2_select_best_stream(topo.capture, topo.capture_count)
            : -1;
        if (cap_idx >= 0) {
            cap_stream = topo.capture[cap_idx];
            has_cap = true;
        }

        libusb_set_auto_detach_kernel_driver(devh, 1);
        libusb_claim_interface(devh, topo.ac_interface_num);
        if (libusb_claim_interface(devh, pb_stream.interface_num) != 0) {
            stop_locked();
            return -26;
        }
        if (has_cap && cap_stream.interface_num != pb_stream.interface_num) {
            if (libusb_claim_interface(devh, cap_stream.interface_num) != 0) {
                has_cap = false;
            }
        }

        uint32_t target_sr = (sample_rate >= 44100 && sample_rate <= 192000) ? static_cast<uint32_t>(sample_rate) : 48000u;
        if (topo.clock_programmable) {
            if (set_uac2_sample_rate(target_sr) != 0 && target_sr != 48000u) {
                target_sr = 48000u;
                set_uac2_sample_rate(target_sr);
            }
        }

        if (libusb_set_interface_alt_setting(devh, pb_stream.interface_num, pb_stream.alt_setting) != 0) {
            stop_locked();
            return -27;
        }
        if (has_cap) {
            if (libusb_set_interface_alt_setting(devh, cap_stream.interface_num, cap_stream.alt_setting) != 0) {
                has_cap = false;
            }
        }

        cav_rb_init(&out_rb, out_storage, kUsbRingSamples, 2u);
        cav_rb_init(&in_rb, in_storage, kUsbRingSamples, 2u);
        playback_primed.store(false, std::memory_order_relaxed);
        out_xruns.store(0, std::memory_order_relaxed);
        in_xruns.store(0, std::memory_order_relaxed);
        underrun_ramp_state = 1;
        last_out_sample[0] = 0.0f;
        last_out_sample[1] = 0.0f;
        stopping.store(false, std::memory_order_release);
        phase_accum_1616 = 0;

        pb_pkts_per_sec = compute_iso_packets_per_sec(pb_stream.interval, is_high_speed);
        cap_pkts_per_sec = has_cap
            ? compute_iso_packets_per_sec(cap_stream.interval, is_high_speed)
            : pb_pkts_per_sec;
        uint32_t nom_1616 = static_cast<uint32_t>(llround((static_cast<double>(target_sr) / pb_pkts_per_sec) * 65536.0));
        rate_1616.store(nom_1616, std::memory_order_release);

        int pkts_per_xfer = kIsoPacketsPerTransfer;
        if (buffer_frames <= 64) pkts_per_xfer = 4;
        else if (buffer_frames >= 256) pkts_per_xfer = 16;

        active_sr.store(static_cast<int32_t>(target_sr), std::memory_order_release);
        active_bits.store(pb_stream.bit_resolution > 0 ? pb_stream.bit_resolution : 24, std::memory_order_release);
        active_buf_frames.store(buffer_frames > 0 ? buffer_frames : 128, std::memory_order_release);
        active_sync_mode.store(pb_stream.feedback_ep_addr != 0 ? 1 : (pb_stream.implicit_feedback ? 2 : 3), std::memory_order_release);
        full_duplex_flag.store(has_cap ? 1 : 0, std::memory_order_release);

        float packet_ms = static_cast<float>(static_cast<double>(pkts_per_xfer) * 1000.0 / pb_pkts_per_sec);
        est_rtt_ms.store((kIsoTransferCount * packet_ms) + (has_cap ? packet_ms * 2.0f : 0.5f), std::memory_order_release);

        if (device_gone.load(std::memory_order_seq_cst) ||
            usb_detach_seq.load(std::memory_order_seq_cst) != start_detach_seq) {
            stop_locked();
            return -28;
        }
        current_token = new UsbTransferToken();
        current_token->engine = this;
        running.store(true, std::memory_order_seq_cst);
        if (device_gone.load(std::memory_order_seq_cst) ||
            usb_detach_seq.load(std::memory_order_seq_cst) != start_detach_seq) {
            running.store(false, std::memory_order_seq_cst);
            stop_locked();
            return -28;
        }

        /* Reserve buffer storage up front so vector reallocation cannot invalidate submitted URB pointers */
        xfer_buffers.reserve(static_cast<size_t>(kIsoTransferCount * 2 + 2));

        /* Allocate & submit async ISO OUT ring (pre-zeroed without triggering bogus startup XRuns) */
        int submitted_out = 0;
        for (int i = 0; i < kIsoTransferCount; ++i) {
            xfer_buffers.emplace_back(static_cast<size_t>(pb_stream.max_packet_size) * pkts_per_xfer, 0);
            libusb_transfer* xfer = libusb_alloc_transfer(pkts_per_xfer);
            if (!xfer) {
                continue;
            }
            libusb_fill_iso_transfer(xfer, devh, pb_stream.data_ep_addr, xfer_buffers.back().data(),
                                     static_cast<int>(xfer_buffers.back().size()), pkts_per_xfer, on_out_complete, current_token, 1000);
            libusb_set_iso_packet_lengths(xfer, pb_stream.max_packet_size);
            fill_out_transfer(xfer, true);
            out_xfers.push_back(xfer);
            int sub_rc = libusb_submit_transfer(xfer);
            if (sub_rc == 0) {
                current_token->active_xfers.fetch_add(1, std::memory_order_acq_rel);
                submitted_out++;
            } else {
                CAV_USBLOGW("Path B ISO OUT submit[%d] failed: %d", i, sub_rc);
            }
        }
        if (submitted_out == 0) {
            stop_locked();
            return -28;
        }

        /* Allocate & submit async ISO IN ring when full-duplex or implicit feedback is active */
        if (has_cap) {
            int submitted_in = 0;
            for (int i = 0; i < kIsoTransferCount; ++i) {
                xfer_buffers.emplace_back(static_cast<size_t>(cap_stream.max_packet_size) * pkts_per_xfer, 0);
                libusb_transfer* xfer = libusb_alloc_transfer(pkts_per_xfer);
                if (!xfer) {
                    continue;
                }
                libusb_fill_iso_transfer(xfer, devh, cap_stream.data_ep_addr, xfer_buffers.back().data(),
                                         static_cast<int>(xfer_buffers.back().size()), pkts_per_xfer, on_in_complete, current_token, 1000);
                libusb_set_iso_packet_lengths(xfer, cap_stream.max_packet_size);
                in_xfers.push_back(xfer);
                int sub_rc = libusb_submit_transfer(xfer);
                if (sub_rc == 0) {
                    current_token->active_xfers.fetch_add(1, std::memory_order_acq_rel);
                    submitted_in++;
                } else {
                    CAV_USBLOGW("Path B ISO IN submit[%d] failed: %d", i, sub_rc);
                }
            }
            if (submitted_in == 0) {
                has_cap = false;
                full_duplex_flag.store(0, std::memory_order_release);
            }
        }

        /* Allocate & submit explicit 16.16/10.14 feedback endpoint if advertised */
        if (pb_stream.feedback_ep_addr != 0) {
            for (int i = 0; i < 2; ++i) {
                xfer_buffers.emplace_back(4u * 4u, 0);
                libusb_transfer* xfer = libusb_alloc_transfer(4);
                if (!xfer) {
                    continue;
                }
                libusb_fill_iso_transfer(xfer, devh, pb_stream.feedback_ep_addr, xfer_buffers.back().data(),
                                         16, 4, on_fb_complete, current_token, 1000);
                libusb_set_iso_packet_lengths(xfer, 4);
                fb_xfers.push_back(xfer);
                int sub_rc = libusb_submit_transfer(xfer);
                if (sub_rc == 0) {
                    current_token->active_xfers.fetch_add(1, std::memory_order_acq_rel);
                } else {
                    CAV_USBLOGW("Path B FB submit[%d] failed: %d", i, sub_rc);
                }
            }
        }

        UsbTransferToken* tok = current_token;
        libusb_context* loop_ctx = ctx;
        event_thread = std::thread([this, tok, loop_ctx]() {
#if defined(__ANDROID__)
            setpriority(PRIO_PROCESS, 0, -19); /* THREAD_PRIORITY_URGENT_AUDIO */
#endif
            struct timeval tv;
            bool draining = false;
            std::chrono::steady_clock::time_point drain_start{};
            while (running.load(std::memory_order_acquire) || tok->active_xfers.load(std::memory_order_acquire) > 0) {
                tv.tv_sec = 0;
                tv.tv_usec = 10000; /* 10 ms poll ceiling */
                int rc = libusb_handle_events_timeout_completed(loop_ctx, &tv, nullptr);
                if (rc < 0 && rc != LIBUSB_ERROR_INTERRUPTED) {
                    break;
                }
                if (!running.load(std::memory_order_acquire)) {
                    auto now = std::chrono::steady_clock::now();
                    if (!draining) {
                        draining = true;
                        drain_start = now;
                    } else {
                        auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now - drain_start).count();
                        const int ceiling = (tok->detached.load(std::memory_order_acquire) ||
                                             device_gone.load(std::memory_order_acquire))
                            ? kUsbDetachedDrainCeilingMs
                            : kUsbDrainCeilingMs;
                        if (elapsed_ms >= ceiling) {
                            break;
                        }
                    }
                } else {
                    draining = false;
                }
            }
        });

        CAV_USBLOGI("Path B libusb UAC2 started: SR=%u bits=%d duplex=%d sync=%d pkts/s=%.1f",
                    target_sr, active_bits.load(), has_cap ? 1 : 0, active_sync_mode.load(), pb_pkts_per_sec);
        return 0;
    }

    int32_t scarlett2_init_step0(int32_t vendor_if, uint8_t* rx, int32_t rx_cap) {
        if (rx_cap < 0 || (rx_cap > 0 && !rx)) {
            return -1;
        }
        if (!running.load(std::memory_order_acquire) ||
            stopping.load(std::memory_order_acquire) ||
            device_gone.load(std::memory_order_acquire)) {
            return -1;
        }
        std::lock_guard<std::mutex> lock(ctrl_mtx);
        if (!devh ||
            !running.load(std::memory_order_acquire) ||
            stopping.load(std::memory_order_acquire) ||
            device_gone.load(std::memory_order_acquire)) {
            return -1;
        }
        int target_if = vendor_if >= 0 ? vendor_if : (topo.vendor_interface_num >= 0 ? topo.vendor_interface_num : 0);
        if (claimed_vendor_if != target_if) {
            if (claimed_vendor_if >= 0) {
                libusb_release_interface(devh, claimed_vendor_if);
                claimed_vendor_if = -1;
            }
            if (libusb_claim_interface(devh, target_if) == 0) {
                claimed_vendor_if = target_if;
            }
        }
        uint16_t wIndex = static_cast<uint16_t>(target_if);
        uint8_t step0_buf[24] = {0};
        int recvd = libusb_control_transfer(devh, 0xA1, kScarlettUsbCmdInit, 0, wIndex,
                                            step0_buf, static_cast<uint16_t>(sizeof(step0_buf)), kScarlettControlTimeoutMs);
        if (recvd == LIBUSB_ERROR_NO_DEVICE) {
            device_gone.store(true, std::memory_order_release);
            running.store(false, std::memory_order_release);
            return recvd;
        }
        if (recvd > 0 && rx && rx_cap > 0) {
            int32_t copy_len = recvd < rx_cap ? recvd : rx_cap;
            memcpy(rx, step0_buf, static_cast<size_t>(copy_len));
        }
        return recvd;
    }

    int32_t scarlett2_xfer(int32_t vendor_if, const uint8_t* tx, int32_t tx_len, uint8_t* rx, int32_t rx_cap) {
        if (!tx || tx_len < CAV_SCARLETT_PKT_HDR_SIZE ||
            !running.load(std::memory_order_acquire) ||
            stopping.load(std::memory_order_acquire) ||
            device_gone.load(std::memory_order_acquire)) {
            return -1;
        }
        std::lock_guard<std::mutex> lock(ctrl_mtx);
        if (!devh ||
            !running.load(std::memory_order_acquire) ||
            stopping.load(std::memory_order_acquire) ||
            device_gone.load(std::memory_order_acquire)) {
            return -1;
        }
        int target_if = vendor_if >= 0 ? vendor_if : (topo.vendor_interface_num >= 0 ? topo.vendor_interface_num : 0);
        if (claimed_vendor_if != target_if) {
            if (claimed_vendor_if >= 0) {
                libusb_release_interface(devh, claimed_vendor_if);
                claimed_vendor_if = -1;
            }
            if (libusb_claim_interface(devh, target_if) == 0) {
                claimed_vendor_if = target_if;
            }
        }
        uint16_t wIndex = static_cast<uint16_t>(target_if);

        int sent = -1;
        for (int attempt = 0; attempt < 3; ++attempt) {
            if (stopping.load(std::memory_order_acquire) || device_gone.load(std::memory_order_acquire)) {
                return -1;
            }
            sent = libusb_control_transfer(devh, 0x21, CAV_SCARLETT_USB_REQ_SEND, 0, wIndex,
                                           const_cast<uint8_t*>(tx), static_cast<uint16_t>(tx_len), kScarlettControlTimeoutMs);
            if (sent == LIBUSB_ERROR_NO_DEVICE) {
                device_gone.store(true, std::memory_order_release);
                running.store(false, std::memory_order_release);
                return sent;
            }
            if (sent >= 0) break;
            /* Linux mixer_scarlett2.c parity: retry transient -EPROTO / -EPIPE / -EIO with 10ms backoff */
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        if (sent < 0) return sent;
        if (!rx || rx_cap < CAV_SCARLETT_PKT_HDR_SIZE) return sent;

        int recvd = -1;
        for (int attempt = 0; attempt < 3; ++attempt) {
            if (stopping.load(std::memory_order_acquire) || device_gone.load(std::memory_order_acquire)) {
                return -1;
            }
            recvd = libusb_control_transfer(devh, 0xA1, CAV_SCARLETT_USB_REQ_RECV, 0, wIndex,
                                            rx, static_cast<uint16_t>(rx_cap), kScarlettControlTimeoutMs);
            if (recvd == LIBUSB_ERROR_NO_DEVICE) {
                device_gone.store(true, std::memory_order_release);
                running.store(false, std::memory_order_release);
                return recvd;
            }
            if (recvd >= 0) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        return recvd;
    }

    void notify_usb_detached() {
        usb_detach_seq.fetch_add(1, std::memory_order_seq_cst);
        device_gone.store(true, std::memory_order_seq_cst);
        running.store(false, std::memory_order_seq_cst);
    }

    void stop() {
        std::lock_guard<std::mutex> lock(lifecycle_mtx);
        stop_locked();
    }

    void reap_quarantined_locked() {
        for (size_t idx = 0; idx < quarantined_sessions.size();) {
            auto& q = quarantined_sessions[idx];
            if (q.token && q.token->active_xfers.load(std::memory_order_acquire) > 0 && q.ctx) {
                struct timeval tv{0, 0};
                for (int i = 0; i < 16 && q.token->active_xfers.load(std::memory_order_acquire) > 0; ++i) {
                    int32_t before = q.token->active_xfers.load(std::memory_order_acquire);
                    int rc = libusb_handle_events_timeout_completed(q.ctx, &tv, nullptr);
                    if (rc < 0 || q.token->active_xfers.load(std::memory_order_acquire) == before) {
                        break;
                    }
                }
            }
            if (!q.token || q.token->active_xfers.load(std::memory_order_acquire) <= 0) {
                for (auto* x : q.xfers) {
                    if (x) libusb_free_transfer(x);
                }
                q.xfers.clear();
                q.buffers.clear();
                delete q.token;
                q.token = nullptr;
                if (q.devh) {
                    libusb_close(q.devh);
                    q.devh = nullptr;
                }
                if (q.ctx) {
                    libusb_exit(q.ctx);
                    q.ctx = nullptr;
                }
                if (q.owned_usb_fd >= 0) {
                    close(q.owned_usb_fd);
                    q.owned_usb_fd = -1;
                }
                quarantined_sessions.erase(quarantined_sessions.begin() + static_cast<ptrdiff_t>(idx));
            } else {
                ++idx;
            }
        }
    }

    void stop_locked() {
        reap_quarantined_locked();

        stopping.store(true, std::memory_order_release);
        running.store(false, std::memory_order_release);
        if (current_token) {
            current_token->detached.store(true, std::memory_order_release);
        }

        bool gone = device_gone.load(std::memory_order_acquire);
        const int drain_ceiling_ms = gone ? kUsbDetachedDrainCeilingMs : kUsbDrainCeilingMs;
        if (!gone) {
            auto cancel_list = [&](std::vector<libusb_transfer*>& list) {
                for (auto* x : list) {
                    if (!x) continue;
                    int crc = libusb_cancel_transfer(x);
                    if (crc == LIBUSB_ERROR_NO_DEVICE) {
                        device_gone.store(true, std::memory_order_release);
                        gone = true;
                        break;
                    }
                }
            };
            cancel_list(out_xfers);
            if (!gone) cancel_list(in_xfers);
            if (!gone) cancel_list(fb_xfers);
        }

        /* Wait for active io_inflight callers (including scarlett2_xfer) BEFORE tearing down devh/ctx */
        while (io_inflight.load(std::memory_order_acquire) > 0) {
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }

        /* Drain all cancelled URBs up to bounded ceiling (150ms on USB_DETACHED, 1200ms normal) until active_xfers <= 0 */
        if (event_thread.joinable()) {
            event_thread.join();
        } else if (ctx && current_token && current_token->active_xfers.load(std::memory_order_acquire) > 0) {
            auto drain_start = std::chrono::steady_clock::now();
            struct timeval tv;
            while (current_token->active_xfers.load(std::memory_order_acquire) > 0) {
                auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - drain_start).count();
                if (elapsed_ms >= drain_ceiling_ms) {
                    break;
                }
                tv.tv_sec = 0;
                tv.tv_usec = 10000;
                int rc = libusb_handle_events_timeout_completed(ctx, &tv, nullptr);
                if (rc < 0 && rc != LIBUSB_ERROR_INTERRUPTED) {
                    break;
                }
            }
        }

        const int32_t remaining_xfers = current_token
            ? current_token->active_xfers.load(std::memory_order_acquire)
            : 0;
        const bool safe_to_free = (remaining_xfers <= 0);

        if (safe_to_free) {
            for (auto* x : out_xfers) {
                if (x) libusb_free_transfer(x);
            }
            for (auto* x : in_xfers) {
                if (x) libusb_free_transfer(x);
            }
            for (auto* x : fb_xfers) {
                if (x) libusb_free_transfer(x);
            }
            out_xfers.clear();
            in_xfers.clear();
            fb_xfers.clear();
            xfer_buffers.clear();
            delete current_token;
            current_token = nullptr;

            gone = device_gone.load(std::memory_order_acquire);
            {
                std::lock_guard<std::mutex> ctrl_lock(ctrl_mtx);
                if (devh) {
                    if (!gone) {
                        if (claimed_vendor_if >= 0) {
                            libusb_release_interface(devh, claimed_vendor_if);
                        }
                        if (has_pb) {
                            libusb_set_interface_alt_setting(devh, pb_stream.interface_num, 0);
                            libusb_release_interface(devh, pb_stream.interface_num);
                        }
                        if (has_cap && cap_stream.interface_num != pb_stream.interface_num) {
                            libusb_set_interface_alt_setting(devh, cap_stream.interface_num, 0);
                            libusb_release_interface(devh, cap_stream.interface_num);
                        }
                        libusb_release_interface(devh, topo.ac_interface_num);
                    }
                    claimed_vendor_if = -1;
                    libusb_close(devh);
                    devh = nullptr;
                }
            }
            if (ctx) {
                libusb_exit(ctx);
                ctx = nullptr;
            }
            /* Close dup'd FD strictly AFTER libusb_close + libusb_exit */
            if (owned_usb_fd >= 0) {
                close(owned_usb_fd);
                owned_usb_fd = -1;
            }
        } else {
            /* NEVER call libusb_close(devh) or libusb_exit(ctx) with in-flight URBs; quarantine safely */
            CAV_USBLOGW("Path B stop: %d URBs still in-flight after %d ms drain; quarantining devh/ctx/fd to avoid UAF/SIGABRT",
                        remaining_xfers, kUsbDrainCeilingMs);
            QuarantinedUsbSession q;
            q.ctx = ctx;
            q.devh = devh;
            q.owned_usb_fd = owned_usb_fd;
            q.token = current_token;
            q.xfers.reserve(out_xfers.size() + in_xfers.size() + fb_xfers.size());
            for (auto* x : out_xfers) { if (x) q.xfers.push_back(x); }
            for (auto* x : in_xfers) { if (x) q.xfers.push_back(x); }
            for (auto* x : fb_xfers) { if (x) q.xfers.push_back(x); }
            q.buffers = std::move(xfer_buffers);
            quarantined_sessions.push_back(std::move(q));

            std::lock_guard<std::mutex> ctrl_lock(ctrl_mtx);
            claimed_vendor_if = -1;
            devh = nullptr;
            ctx = nullptr;
            owned_usb_fd = -1;
            current_token = nullptr;
            out_xfers.clear();
            in_xfers.clear();
            fb_xfers.clear();
            xfer_buffers.clear();
        }

        has_pb = false;
        has_cap = false;
        device_gone.store(false, std::memory_order_release);
        consec_transfer_errors.store(0, std::memory_order_relaxed);
        full_duplex_flag.store(0, std::memory_order_release);
        stopping.store(false, std::memory_order_release);
    }

    void fill_status(CavProAudioStatus* st) {
        if (!st) return;
        const bool active = running.load(std::memory_order_acquire) &&
                            !stopping.load(std::memory_order_acquire) &&
                            !device_gone.load(std::memory_order_acquire);
        st->active_backend = active ? CAV_PA_BACKEND_PATH_B_LIBUSB : CAV_PA_BACKEND_NONE;
        st->sample_rate = active_sr.load(std::memory_order_relaxed);
        st->bit_depth = active_bits.load(std::memory_order_relaxed);
        st->buffer_frames = active_buf_frames.load(std::memory_order_relaxed);
        st->is_exclusive = 1;
        st->is_full_duplex = full_duplex_flag.load(std::memory_order_relaxed);
        st->sync_mode = active_sync_mode.load(std::memory_order_relaxed);
        st->output_xruns = out_xruns.load(std::memory_order_relaxed);
        st->input_xruns = in_xruns.load(std::memory_order_relaxed);
        st->estimated_rtt_ms = est_rtt_ms.load(std::memory_order_relaxed);
        st->async_drift_ppm = drift_ppm.load(std::memory_order_relaxed);
    }
};

static PathBLibUsbEngine g_libusb;

int32_t path_b_start(int32_t usb_fd, int32_t sr, int32_t buf_frames, int32_t enable_in) {
    return g_libusb.start(usb_fd, sr, buf_frames, enable_in);
}
void path_b_stop() { g_libusb.stop(); }
void path_b_notify_usb_detached() { g_libusb.notify_usb_detached(); }
int32_t path_b_write_playback(const float* data, int32_t frames) {
    if (!data || frames <= 0) return 0;
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    if (!g_libusb.running.load(std::memory_order_acquire) ||
        g_libusb.stopping.load(std::memory_order_acquire) ||
        g_libusb.device_gone.load(std::memory_order_acquire)) {
        return -1;
    }
    int32_t written = static_cast<int32_t>(cav_rb_write(&g_libusb.out_rb, data, static_cast<uint32_t>(frames)));
    if (written > 0) {
        g_libusb.playback_primed.store(true, std::memory_order_release);
    }
    return written;
}
int32_t path_b_read_capture(float* data, int32_t max_frames) {
    if (!data || max_frames <= 0) return 0;
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    if (!g_libusb.running.load(std::memory_order_acquire) ||
        g_libusb.stopping.load(std::memory_order_acquire) ||
        g_libusb.device_gone.load(std::memory_order_acquire)) {
        return 0;
    }
    int32_t sr = g_libusb.active_sr.load(std::memory_order_relaxed);
    int32_t burst = g_libusb.active_buf_frames.load(std::memory_order_relaxed);
    if (sr <= 0) sr = 48000;
    if (burst <= 0) burst = 128;
    int32_t min_burst_cap = burst + (burst / 2);
    int32_t sr_cap = sr / 125; /* 8 ms RT cap */
    int32_t cap_frames = min_burst_cap > sr_cap ? min_burst_cap : sr_cap;
    if (cap_frames < 64) cap_frames = 64;
    if (cap_frames > 4096) cap_frames = 4096;
    cav_rb_discard_oldest(&g_libusb.in_rb, static_cast<uint32_t>(cap_frames));
    return static_cast<int32_t>(cav_rb_read(&g_libusb.in_rb, data, static_cast<uint32_t>(max_frames), 0));
}
int32_t path_b_free_frames() {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    if (!g_libusb.running.load(std::memory_order_acquire) ||
        g_libusb.stopping.load(std::memory_order_acquire) ||
        g_libusb.device_gone.load(std::memory_order_acquire)) {
        return 0;
    }
    return static_cast<int32_t>(cav_rb_avail_write_frames(&g_libusb.out_rb));
}
int32_t path_b_buffered_frames() {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    if (!g_libusb.running.load(std::memory_order_acquire) ||
        g_libusb.stopping.load(std::memory_order_acquire) ||
        g_libusb.device_gone.load(std::memory_order_acquire)) {
        return 0;
    }
    return static_cast<int32_t>(cav_rb_avail_read_frames(&g_libusb.out_rb));
}
void path_b_flush_playback() {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    if (g_libusb.running.load(std::memory_order_acquire) &&
        !g_libusb.stopping.load(std::memory_order_acquire) &&
        !g_libusb.device_gone.load(std::memory_order_acquire)) {
        g_libusb.playback_primed.store(false, std::memory_order_release);
        cav_rb_request_flush(&g_libusb.out_rb);
    }
}
void path_b_get_status(CavProAudioStatus* st) {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    g_libusb.fill_status(st);
}
int32_t path_b_scarlett2_init_step0(int32_t vendor_if, uint8_t* rx, int32_t rx_cap) {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    return g_libusb.scarlett2_init_step0(vendor_if, rx, rx_cap);
}
int32_t path_b_scarlett2_xfer(int32_t vendor_if, const uint8_t* tx, int32_t tx_len, uint8_t* rx, int32_t rx_cap) {
    PathBLibUsbEngine::IoGuard guard(g_libusb.io_inflight);
    return g_libusb.scarlett2_xfer(vendor_if, tx, tx_len, rx, rx_cap);
}

#else

int32_t path_b_start(int32_t, int32_t, int32_t, int32_t) { return -29; }
void path_b_stop() {}
void path_b_notify_usb_detached() {}
int32_t path_b_write_playback(const float*, int32_t) { return 0; }
int32_t path_b_read_capture(float*, int32_t) { return 0; }
int32_t path_b_free_frames() { return 0; }
int32_t path_b_buffered_frames() { return 0; }
void path_b_flush_playback() {}
void path_b_get_status(CavProAudioStatus* st) { if (st) memset(st, 0, sizeof(*st)); }
int32_t path_b_scarlett2_init_step0(int32_t, uint8_t*, int32_t) { return -29; }
int32_t path_b_scarlett2_xfer(int32_t, const uint8_t*, int32_t, uint8_t*, int32_t) { return -29; }

#endif /* CAVERNIZE_HAVE_LIBUSB */

} /* namespace cavernize */
