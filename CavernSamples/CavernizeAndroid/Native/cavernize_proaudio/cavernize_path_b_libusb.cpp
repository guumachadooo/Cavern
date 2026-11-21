#include "cavernize_proaudio_api.h"
#include "cavernize_ringbuffer.h"
#include "cavernize_uac2_core.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>
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
static constexpr int kIsoTransferCount = 4;
static constexpr int kIsoPacketsPerTransfer = 8; /* 8 x 125 us = 1 ms per URB in High-Speed */

struct PathBLibUsbEngine {
    std::mutex lifecycle_mtx;
    std::mutex ctrl_mtx;

    libusb_context* ctx = nullptr;
    libusb_device_handle* devh = nullptr;
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
    std::atomic<int32_t> active_xfers{0};
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

    static double compute_iso_packets_per_sec(uint8_t bInterval, bool hs) {
        uint8_t clamped = bInterval < 1u ? 1u : (bInterval > 16u ? 16u : bInterval);
        double base = hs ? 8000.0 : 1000.0;
        return base / static_cast<double>(1u << (clamped - 1u));
    }

    static void LIBUSB_CALL on_out_complete(libusb_transfer* xfer) {
        if (!xfer) return;
        auto* self = static_cast<PathBLibUsbEngine*>(xfer->user_data);
        if (!self) return;

        if (!self->running.load(std::memory_order_acquire) ||
            self->stopping.load(std::memory_order_acquire) ||
            xfer->status == LIBUSB_TRANSFER_CANCELLED ||
            xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
            if (xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            return;
        }

        self->fill_out_transfer(xfer, false);
        int rc = libusb_submit_transfer(xfer);
        if (rc != 0) {
            self->out_xruns.fetch_add(1, std::memory_order_relaxed);
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            if (rc == LIBUSB_ERROR_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
        }
    }

    static void LIBUSB_CALL on_in_complete(libusb_transfer* xfer) {
        if (!xfer) return;
        auto* self = static_cast<PathBLibUsbEngine*>(xfer->user_data);
        if (!self) return;

        if (!self->running.load(std::memory_order_acquire) ||
            self->stopping.load(std::memory_order_acquire) ||
            xfer->status == LIBUSB_TRANSFER_CANCELLED ||
            xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
            if (xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
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

        int rc = libusb_submit_transfer(xfer);
        if (rc != 0) {
            self->in_xruns.fetch_add(1, std::memory_order_relaxed);
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            if (rc == LIBUSB_ERROR_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
        }
    }

    static void LIBUSB_CALL on_fb_complete(libusb_transfer* xfer) {
        if (!xfer) return;
        auto* self = static_cast<PathBLibUsbEngine*>(xfer->user_data);
        if (!self) return;

        if (!self->running.load(std::memory_order_acquire) ||
            self->stopping.load(std::memory_order_acquire) ||
            xfer->status == LIBUSB_TRANSFER_CANCELLED ||
            xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
            if (xfer->status == LIBUSB_TRANSFER_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            return;
        }

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
        int rc = libusb_submit_transfer(xfer);
        if (rc != 0) {
            self->active_xfers.fetch_sub(1, std::memory_order_acq_rel);
            if (rc == LIBUSB_ERROR_NO_DEVICE) {
                self->running.store(false, std::memory_order_release);
            }
        }
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
                uint32_t got = cav_rb_read(&out_rb, pack_scratch, frames, 1);
                if (got < frames) {
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
                for (uint32_t f = 0; f < frames && f * ch + 1u < 1024u; ++f) {
                    expanded[f * ch] = pack_scratch[f * 2];
                    if (ch >= 2u) expanded[f * ch + 1] = pack_scratch[f * 2 + 1];
                }
                bytes = cav_uac2_pack_float(expanded, static_cast<size_t>(frames) * ch, buf_ptr,
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

        /* Mandatory unrooted Android sequence: disable bus scanning before libusb_init */
        libusb_set_option(nullptr, LIBUSB_OPTION_NO_DEVICE_DISCOVERY);
        if (libusb_init(&ctx) != 0 || !ctx) {
            return -21;
        }
        if (libusb_wrap_sys_device(ctx, static_cast<intptr_t>(usb_fd), &devh) != 0 || !devh) {
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
        out_xruns.store(0, std::memory_order_relaxed);
        in_xruns.store(0, std::memory_order_relaxed);
        active_xfers.store(0, std::memory_order_release);
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

        running.store(true, std::memory_order_release);

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
                                     static_cast<int>(xfer_buffers.back().size()), pkts_per_xfer, on_out_complete, this, 1000);
            libusb_set_iso_packet_lengths(xfer, pb_stream.max_packet_size);
            fill_out_transfer(xfer, true);
            out_xfers.push_back(xfer);
            int sub_rc = libusb_submit_transfer(xfer);
            if (sub_rc == 0) {
                active_xfers.fetch_add(1, std::memory_order_acq_rel);
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
                                         static_cast<int>(xfer_buffers.back().size()), pkts_per_xfer, on_in_complete, this, 1000);
                libusb_set_iso_packet_lengths(xfer, cap_stream.max_packet_size);
                in_xfers.push_back(xfer);
                int sub_rc = libusb_submit_transfer(xfer);
                if (sub_rc == 0) {
                    active_xfers.fetch_add(1, std::memory_order_acq_rel);
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
                                         16, 4, on_fb_complete, this, 1000);
                libusb_set_iso_packet_lengths(xfer, 4);
                fb_xfers.push_back(xfer);
                int sub_rc = libusb_submit_transfer(xfer);
                if (sub_rc == 0) {
                    active_xfers.fetch_add(1, std::memory_order_acq_rel);
                } else {
                    CAV_USBLOGW("Path B FB submit[%d] failed: %d", i, sub_rc);
                }
            }
        }

        event_thread = std::thread([this]() {
#if defined(__ANDROID__)
            setpriority(PRIO_PROCESS, 0, -19); /* THREAD_PRIORITY_URGENT_AUDIO */
#endif
            struct timeval tv;
            int drain_iters = 0;
            while (running.load(std::memory_order_acquire) || active_xfers.load(std::memory_order_acquire) > 0) {
                tv.tv_sec = 0;
                tv.tv_usec = 10000; /* 10 ms poll ceiling */
                libusb_handle_events_timeout_completed(ctx, &tv, nullptr);
                if (!running.load(std::memory_order_acquire)) {
                    /* Bounded safety ceiling (~500 ms) while draining cancelled URBs on stop */
                    drain_iters++;
                    if (drain_iters >= 50) {
                        break;
                    }
                }
            }
        });

        CAV_USBLOGI("Path B libusb UAC2 started: SR=%u bits=%d duplex=%d sync=%d pkts/s=%.1f",
                    target_sr, active_bits.load(), has_cap ? 1 : 0, active_sync_mode.load(), pb_pkts_per_sec);
        return 0;
    }

    int32_t scarlett2_xfer(int32_t vendor_if, const uint8_t* tx, int32_t tx_len, uint8_t* rx, int32_t rx_cap) {
        std::lock_guard<std::mutex> lock(ctrl_mtx);
        if (!devh || !tx || tx_len < CAV_SCARLETT_PKT_HDR_SIZE) return -1;
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

        int sent = libusb_control_transfer(devh, 0x21, CAV_SCARLETT_USB_REQ_SEND, 0, wIndex,
                                           const_cast<uint8_t*>(tx), static_cast<uint16_t>(tx_len), 500);
        if (sent < 0) return sent;
        if (!rx || rx_cap < CAV_SCARLETT_PKT_HDR_SIZE) return sent;

        int recvd = libusb_control_transfer(devh, 0xA1, CAV_SCARLETT_USB_REQ_RECV, 0, wIndex,
                                            rx, static_cast<uint16_t>(rx_cap), 500);
        return recvd;
    }

    void stop() {
        std::lock_guard<std::mutex> lock(lifecycle_mtx);
        stop_locked();
    }

    void stop_locked() {
        stopping.store(true, std::memory_order_release);
        running.store(false, std::memory_order_release);
        for (auto* x : out_xfers) {
            if (x) libusb_cancel_transfer(x);
        }
        for (auto* x : in_xfers) {
            if (x) libusb_cancel_transfer(x);
        }
        for (auto* x : fb_xfers) {
            if (x) libusb_cancel_transfer(x);
        }

        /* Drain all cancelled URBs until active_xfers == 0 BEFORE freeing transfers/buffers (prevents UAF SIGSEGV) */
        if (event_thread.joinable()) {
            event_thread.join();
        } else if (ctx) {
            struct timeval tv;
            for (int iter = 0; iter < 50 && active_xfers.load(std::memory_order_acquire) > 0; ++iter) {
                tv.tv_sec = 0;
                tv.tv_usec = 10000;
                libusb_handle_events_timeout_completed(ctx, &tv, nullptr);
            }
        }

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
        active_xfers.store(0, std::memory_order_release);

        {
            std::lock_guard<std::mutex> ctrl_lock(ctrl_mtx);
            if (devh) {
                if (claimed_vendor_if >= 0) {
                    libusb_release_interface(devh, claimed_vendor_if);
                    claimed_vendor_if = -1;
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
                libusb_close(devh);
                devh = nullptr;
            }
        }
        if (ctx) {
            libusb_exit(ctx);
            ctx = nullptr;
        }
        has_pb = false;
        has_cap = false;
        full_duplex_flag.store(0, std::memory_order_release);
        stopping.store(false, std::memory_order_release);
    }

    void fill_status(CavProAudioStatus* st) {
        if (!st) return;
        st->active_backend = running.load(std::memory_order_acquire) ? CAV_PA_BACKEND_PATH_B_LIBUSB : CAV_PA_BACKEND_NONE;
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
int32_t path_b_write_playback(const float* data, int32_t frames) {
    return frames > 0 ? static_cast<int32_t>(cav_rb_write(&g_libusb.out_rb, data, static_cast<uint32_t>(frames))) : 0;
}
int32_t path_b_read_capture(float* data, int32_t max_frames) {
    return max_frames > 0 ? static_cast<int32_t>(cav_rb_read(&g_libusb.in_rb, data, static_cast<uint32_t>(max_frames), 0)) : 0;
}
int32_t path_b_free_frames() { return static_cast<int32_t>(cav_rb_avail_write_frames(&g_libusb.out_rb)); }
void path_b_get_status(CavProAudioStatus* st) { g_libusb.fill_status(st); }
int32_t path_b_scarlett2_xfer(int32_t vendor_if, const uint8_t* tx, int32_t tx_len, uint8_t* rx, int32_t rx_cap) {
    return g_libusb.scarlett2_xfer(vendor_if, tx, tx_len, rx, rx_cap);
}

#else

int32_t path_b_start(int32_t, int32_t, int32_t, int32_t) { return -29; }
void path_b_stop() {}
int32_t path_b_write_playback(const float*, int32_t) { return 0; }
int32_t path_b_read_capture(float*, int32_t) { return 0; }
int32_t path_b_free_frames() { return 0; }
void path_b_get_status(CavProAudioStatus* st) { if (st) memset(st, 0, sizeof(*st)); }
int32_t path_b_scarlett2_xfer(int32_t, const uint8_t*, int32_t, uint8_t*, int32_t) { return -29; }

#endif /* CAVERNIZE_HAVE_LIBUSB */

} /* namespace cavernize */
