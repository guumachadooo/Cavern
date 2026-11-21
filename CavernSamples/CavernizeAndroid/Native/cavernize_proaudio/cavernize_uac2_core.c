#include "cavernize_uac2_core.h"

#include <math.h>
#include <string.h>

static uint32_t read_u32_le(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t read_u16_le(const uint8_t* p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static void write_u16_le(uint8_t* p, uint16_t v) {
    p[0] = (uint8_t)(v & 0xFFu);
    p[1] = (uint8_t)((v >> 8) & 0xFFu);
}

static void write_u32_le(uint8_t* p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xFFu);
    p[1] = (uint8_t)((v >> 8) & 0xFFu);
    p[2] = (uint8_t)((v >> 16) & 0xFFu);
    p[3] = (uint8_t)((v >> 24) & 0xFFu);
}

static uint8_t classify_pcm_fmt(uint8_t subslot, uint8_t bits) {
    if (subslot == 2 && bits <= 16) return (uint8_t)CAV_UAC2_FMT_PCM16_LE;
    if (subslot == 3 && bits <= 24) return (uint8_t)CAV_UAC2_FMT_PCM24_3B_LE;
    if (subslot == 4) return (uint8_t)(bits <= 24 ? CAV_UAC2_FMT_PCM24_IN_32_LE : CAV_UAC2_FMT_PCM32_LE);
    return (uint8_t)CAV_UAC2_FMT_UNKNOWN;
}

static void flush_stream(CavUac2Topology* topo, const CavUac2StreamDesc* st, int has_stream) {
    if (!has_stream || !st || st->data_ep_addr == 0 || st->format == CAV_UAC2_FMT_UNKNOWN) {
        return;
    }
    if (st->is_input) {
        if (topo->capture_count < CAV_UAC2_MAX_STREAMS) {
            topo->capture[topo->capture_count++] = *st;
        }
    } else {
        if (topo->playback_count < CAV_UAC2_MAX_STREAMS) {
            topo->playback[topo->playback_count++] = *st;
        }
    }
}

int cav_uac2_parse_config_desc(const uint8_t* raw, size_t len, CavUac2Topology* out_topo) {
    if (!raw || len < 9 || !out_topo) {
        return -1;
    }
    memset(out_topo, 0, sizeof(*out_topo));
    out_topo->vendor_interface_num = -1;

    size_t pos = 0;
    int cur_class = -1, cur_sub = -1, cur_proto = -1;
    CavUac2StreamDesc cur_stream;
    int has_stream = 0;
    memset(&cur_stream, 0, sizeof(cur_stream));

    while (pos + 2 <= len) {
        uint8_t dlen = raw[pos];
        uint8_t dtype = raw[pos + 1];
        if (dlen < 2 || pos + dlen > len) {
            break;
        }

        if (dtype == 0x04 && dlen >= 9) { /* INTERFACE */
            flush_stream(out_topo, &cur_stream, has_stream);
            has_stream = 0;
            memset(&cur_stream, 0, sizeof(cur_stream));

            uint8_t if_num = raw[pos + 2];
            uint8_t alt_set = raw[pos + 3];
            cur_class = raw[pos + 5];
            cur_sub = raw[pos + 6];
            cur_proto = raw[pos + 7];

            if (cur_class == 0x01 && cur_sub == 0x01 && cur_proto == 0x20) {
                out_topo->is_uac2 = 1;
                out_topo->ac_interface_num = if_num;
            } else if (cur_class == 0xFF && out_topo->vendor_interface_num < 0) {
                out_topo->vendor_interface_num = (int8_t)if_num;
            } else if (cur_class == 0x01 && cur_sub == 0x02 && cur_proto == 0x20 && alt_set > 0) {
                has_stream = 1;
                cur_stream.interface_num = if_num;
                cur_stream.alt_setting = alt_set;
            }
        } else if (dtype == 0x24 && dlen >= 3) { /* CS_INTERFACE */
            uint8_t subtype = raw[pos + 2];
            if (cur_class == 0x01 && cur_sub == 0x01 && cur_proto == 0x20 && subtype == 0x0A && dlen >= 8) {
                uint8_t clk_id = raw[pos + 3];
                uint8_t bm_ctrl = raw[pos + 5];
                if (out_topo->clock_source_id == 0 || (bm_ctrl & 0x03u) == 0x03u) {
                    out_topo->clock_source_id = clk_id;
                    out_topo->clock_programmable = (uint8_t)((bm_ctrl & 0x03u) == 0x03u ? 1 : 0);
                }
            } else if (has_stream && subtype == 0x01 && dlen >= 16) { /* AS_GENERAL */
                uint8_t fmt_type = raw[pos + 5];
                uint32_t formats = read_u32_le(raw + pos + 6);
                cur_stream.channels = raw[pos + 10];
                if (fmt_type == 1 && (formats & 0x04u) != 0u) {
                    cur_stream.format = (uint8_t)CAV_UAC2_FMT_FLOAT32_LE;
                }
            } else if (has_stream && subtype == 0x02 && dlen >= 6) { /* FORMAT_TYPE */
                if (raw[pos + 3] == 1) {
                    cur_stream.subslot_bytes = raw[pos + 4];
                    cur_stream.bit_resolution = raw[pos + 5];
                    if (cur_stream.format != (uint8_t)CAV_UAC2_FMT_FLOAT32_LE) {
                        cur_stream.format = classify_pcm_fmt(cur_stream.subslot_bytes, cur_stream.bit_resolution);
                    }
                }
            }
        } else if (dtype == 0x05 && dlen >= 7 && has_stream) { /* ENDPOINT */
            uint8_t ep_addr = raw[pos + 2];
            uint8_t bm_attr = raw[pos + 3];
            uint16_t raw_max = read_u16_le(raw + pos + 4);
            uint16_t max_pkt = (uint16_t)((raw_max & 0x07FFu) * (1u + ((raw_max >> 11) & 0x03u)));
            uint8_t interval = raw[pos + 6];

            if ((bm_attr & 0x03u) == 0x01u) { /* ISO */
                uint8_t usage = (uint8_t)((bm_attr >> 4) & 0x03u);
                uint8_t is_in = (uint8_t)((ep_addr & 0x80u) ? 1 : 0);
                if (usage == 0x01u && is_in) {
                    cur_stream.feedback_ep_addr = ep_addr;
                } else if (cur_stream.data_ep_addr == 0) {
                    cur_stream.data_ep_addr = ep_addr;
                    cur_stream.is_input = is_in;
                    cur_stream.max_packet_size = max_pkt;
                    cur_stream.interval = interval;
                    cur_stream.sync_type = (uint8_t)((bm_attr >> 2) & 0x03u);
                    if (usage == 0x02u) {
                        cur_stream.implicit_feedback = 1;
                    }
                }
            }
        }
        pos += dlen;
    }

    flush_stream(out_topo, &cur_stream, has_stream);

    if (out_topo->capture_count > 0) {
        for (uint8_t i = 0; i < out_topo->playback_count; i++) {
            if (out_topo->playback[i].sync_type == CAV_ISO_SYNC_ASYNC &&
                out_topo->playback[i].feedback_ep_addr == 0) {
                out_topo->playback[i].implicit_feedback = 1;
            }
        }
    }
    return out_topo->is_uac2 ? 0 : -2;
}

int cav_uac2_select_best_stream(const CavUac2StreamDesc* list, uint8_t count) {
    if (!list || count == 0) return -1;
    int best_idx = -1;
    int best_score = -1;
    for (uint8_t i = 0; i < count; i++) {
        int score = (int)list[i].bit_resolution * 10 + (int)list[i].subslot_bytes * 4;
        if (list[i].format == CAV_UAC2_FMT_FLOAT32_LE) score += 40;
        else if (list[i].format == CAV_UAC2_FMT_PCM32_LE || list[i].format == CAV_UAC2_FMT_PCM24_IN_32_LE) score += 25;
        else if (list[i].format == CAV_UAC2_FMT_PCM24_3B_LE) score += 20;
        if (list[i].channels == 2) score += 15;
        if (score > best_score) {
            best_score = score;
            best_idx = (int)i;
        }
    }
    return best_idx;
}

int cav_uac2_parse_clock_ranges(const uint8_t* payload, size_t len, uint32_t* out_rates, int max_rates) {
    static const uint32_t std_rates[6] = { 44100u, 48000u, 88200u, 96000u, 176400u, 192000u };
    if (!payload || len < 14 || !out_rates || max_rates <= 0) return 0;

    uint16_t num_ranges = read_u16_le(payload);
    int written = 0;
    size_t off = 2;
    for (uint16_t r = 0; r < num_ranges && off + 12 <= len; r++) {
        uint32_t min_hz = read_u32_le(payload + off);
        uint32_t max_hz = read_u32_le(payload + off + 4);
        uint32_t res_hz = read_u32_le(payload + off + 8);
        off += 12;

        if (min_hz < 8000u || max_hz < min_hz) continue;
        if (min_hz == max_hz || res_hz == 0u) {
            if (written < max_rates) out_rates[written++] = min_hz;
        } else {
            for (int k = 0; k < 6 && written < max_rates; k++) {
                uint32_t cand = std_rates[k];
                if (cand >= min_hz && cand <= max_hz && ((cand - min_hz) % res_hz) == 0u) {
                    out_rates[written++] = cand;
                }
            }
        }
    }
    return written;
}

uint32_t cav_uac2_decode_feedback_1616(const uint8_t* packet, size_t len, int is_high_speed) {
    if (!packet) return 0u;
    if (is_high_speed && len >= 4) {
        return read_u32_le(packet);
    }
    if (!is_high_speed && len >= 3) {
        uint32_t raw1014 = (uint32_t)packet[0] | ((uint32_t)packet[1] << 8) | ((uint32_t)packet[2] << 16);
        return raw1014 << 2; /* Convert 10.14 -> 16.16 */
    }
    return 0u;
}

uint32_t cav_uac2_advance_packet_frames(uint32_t* phase_accum_1616, uint32_t rate_1616) {
    if (!phase_accum_1616) return 0u;
    uint64_t sum = (uint64_t)(*phase_accum_1616) + (uint64_t)rate_1616;
    uint32_t frames = (uint32_t)(sum >> 16);
    *phase_accum_1616 = (uint32_t)(sum & 0xFFFFu);
    return frames;
}

static float clamp_unit(float v) {
    if (v > 1.0f) return 1.0f;
    if (v < -1.0f) return -1.0f;
    return v;
}

size_t cav_uac2_pack_float(const float* src, size_t sample_count, uint8_t* dst, CavUac2Format fmt) {
    if (!src || !dst || sample_count == 0) return 0;
    size_t out = 0;
    switch (fmt) {
        case CAV_UAC2_FMT_PCM16_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int32_t s = (int32_t)lrintf(clamp_unit(src[i]) * 32767.0f);
                dst[out++] = (uint8_t)(s & 0xFF);
                dst[out++] = (uint8_t)((s >> 8) & 0xFF);
            }
            return sample_count * 2;
        case CAV_UAC2_FMT_PCM24_3B_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int32_t s = (int32_t)lrintf(clamp_unit(src[i]) * 8388607.0f);
                dst[out++] = (uint8_t)(s & 0xFF);
                dst[out++] = (uint8_t)((s >> 8) & 0xFF);
                dst[out++] = (uint8_t)((s >> 16) & 0xFF);
            }
            return sample_count * 3;
        case CAV_UAC2_FMT_PCM24_IN_32_LE:
        case CAV_UAC2_FMT_PCM32_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int32_t s = (int32_t)llround((double)clamp_unit(src[i]) * 2147483647.0);
                if (fmt == CAV_UAC2_FMT_PCM24_IN_32_LE) {
                    s = (int32_t)((uint32_t)s & 0xFFFFFF00u);
                }
                write_u32_le(dst + out, (uint32_t)s);
                out += 4;
            }
            return sample_count * 4;
        case CAV_UAC2_FMT_FLOAT32_LE:
            for (size_t i = 0; i < sample_count; i++) {
                float v = clamp_unit(src[i]);
                uint32_t bits = 0;
                memcpy(&bits, &v, sizeof(bits));
                write_u32_le(dst + out, bits);
                out += 4;
            }
            return sample_count * 4;
        default:
            return 0;
    }
}

size_t cav_uac2_unpack_to_float(const uint8_t* src, size_t sample_count, float* dst, CavUac2Format fmt) {
    if (!src || !dst || sample_count == 0) return 0;
    size_t in = 0;
    switch (fmt) {
        case CAV_UAC2_FMT_PCM16_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int16_t s = (int16_t)read_u16_le(src + in);
                in += 2;
                dst[i] = (float)s / 32768.0f;
            }
            return sample_count;
        case CAV_UAC2_FMT_PCM24_3B_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int32_t s = (int32_t)src[in] | ((int32_t)src[in + 1] << 8) | ((int32_t)src[in + 2] << 16);
                if (s & 0x800000) s |= (int32_t)0xFF000000u;
                in += 3;
                dst[i] = (float)s / 8388608.0f;
            }
            return sample_count;
        case CAV_UAC2_FMT_PCM24_IN_32_LE:
        case CAV_UAC2_FMT_PCM32_LE:
            for (size_t i = 0; i < sample_count; i++) {
                int32_t s = (int32_t)read_u32_le(src + in);
                in += 4;
                dst[i] = (float)((double)s / 2147483648.0);
            }
            return sample_count;
        case CAV_UAC2_FMT_FLOAT32_LE:
            for (size_t i = 0; i < sample_count; i++) {
                uint32_t bits = read_u32_le(src + in);
                in += 4;
                float v = 0.0f;
                memcpy(&v, &bits, sizeof(v));
                dst[i] = v;
            }
            return sample_count;
        default:
            return 0;
    }
}

size_t cav_scarlett2_build_packet(
    uint32_t cmd,
    uint16_t seq,
    const uint8_t* payload,
    uint16_t payload_len,
    uint8_t* out_buf,
    size_t out_cap) {
    size_t total = (size_t)CAV_SCARLETT_PKT_HDR_SIZE + (size_t)payload_len;
    if (!out_buf || out_cap < total) return 0;
    write_u32_le(out_buf + 0, cmd);
    write_u16_le(out_buf + 4, payload_len);
    write_u16_le(out_buf + 6, seq);
    write_u32_le(out_buf + 8, 0u);  /* error */
    write_u32_le(out_buf + 12, 0u); /* pad */
    if (payload && payload_len > 0) {
        memcpy(out_buf + CAV_SCARLETT_PKT_HDR_SIZE, payload, payload_len);
    }
    return total;
}

int cav_scarlett2_validate_response(
    const uint8_t* resp,
    size_t resp_len,
    uint32_t expected_cmd,
    uint16_t expected_seq,
    uint16_t* out_payload_len) {
    if (!resp || resp_len < CAV_SCARLETT_PKT_HDR_SIZE) return -1;
    uint32_t cmd = read_u32_le(resp + 0);
    uint16_t sz = read_u16_le(resp + 4);
    uint16_t seq = read_u16_le(resp + 6);
    uint32_t err = read_u32_le(resp + 8);
    if (cmd != expected_cmd || seq != expected_seq || err != 0u) return -2;
    if ((size_t)CAV_SCARLETT_PKT_HDR_SIZE + (size_t)sz > resp_len) return -3;
    if (out_payload_len) *out_payload_len = sz;
    return 0;
}
