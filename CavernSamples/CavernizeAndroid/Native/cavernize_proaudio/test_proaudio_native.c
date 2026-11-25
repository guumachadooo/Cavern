#define _POSIX_C_SOURCE 200809L
#include "cavernize_proaudio_api.h"
#include "cavernize_ringbuffer.h"
#include "cavernize_uac2_core.h"

#include <assert.h>
#include <errno.h>
#include <math.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static void test_ring_buffer_wraparound(void) {
    float backing[256]; /* 128 stereo frames, usable = 127 frames */
    CavRingBuffer rb;
    assert(cav_rb_init(&rb, backing, 256u, 2u) == 0);
    assert(cav_rb_avail_read_frames(&rb) == 0u);
    assert(cav_rb_avail_write_frames(&rb) == 127u);

    float in_blk[200];
    float out_blk[200];
    for (int round = 0; round < 20; round++) {
        for (int i = 0; i < 160; i++) {
            in_blk[i] = (float)(round * 1000 + i) * 0.0001f;
        }
        uint32_t w = cav_rb_write(&rb, in_blk, 80u);
        assert(w == 80u);
        uint32_t r = cav_rb_read(&rb, out_blk, 80u, 1);
        assert(r == 80u);
        for (int i = 0; i < 160; i++) {
            assert(fabsf(out_blk[i] - in_blk[i]) < 1e-7f);
        }
    }
    assert(rb.overruns == 0u);
    assert(rb.underruns == 0u);
    printf("[PASS] SPSC Lock-Free RingBuffer wrap-around (20 rounds x 80 stereo frames, 0 xruns)\n");
}

static void test_ring_buffer_discard_oldest(void) {
    float backing[256]; /* 128 stereo frames, usable = 127 frames */
    CavRingBuffer rb;
    assert(cav_rb_init(&rb, backing, 256u, 2u) == 0);
    assert(cav_rb_discard_oldest(NULL, 10u) == 0u);

    float in_blk[200];
    float out_blk[200];
    for (int i = 0; i < 200; i++) {
        in_blk[i] = (float)(i + 1);
    }
    assert(cav_rb_write(&rb, in_blk, 100u) == 100u);
    assert(cav_rb_read(&rb, out_blk, 50u, 0) == 50u); /* avail = 50, r = 100, w = 200 */

    /* Second write wraps around 256-sample backing array */
    for (int i = 0; i < 120; i++) {
        in_blk[i] = (float)(1000 + i);
    }
    assert(cav_rb_write(&rb, in_blk, 60u) == 60u); /* avail = 110 */
    assert(cav_rb_discard_oldest(&rb, 120u) == 0u);
    assert(cav_rb_avail_read_frames(&rb) == 110u);

    /* Discard oldest 94 frames across wrap boundary, keeping newest 16 frames */
    assert(cav_rb_discard_oldest(&rb, 16u) == 94u);
    assert(cav_rb_avail_read_frames(&rb) == 16u);
    assert(cav_rb_read(&rb, out_blk, 16u, 0) == 16u);
    for (int i = 0; i < 32; i++) {
        float expected = (float)(1000 + (60 - 16) * 2 + i);
        assert(fabsf(out_blk[i] - expected) < 1e-6f);
    }

    /* Discard down to 0 and flush parity */
    assert(cav_rb_write(&rb, in_blk, 32u) == 32u);
    assert(cav_rb_discard_oldest(&rb, 0u) == 32u);
    assert(cav_rb_avail_read_frames(&rb) == 0u);
    assert(cav_rb_avail_write_frames(&rb) == 127u);

    assert(cav_rb_write(&rb, in_blk, 40u) == 40u);
    cav_rb_request_flush(&rb);
    assert(cav_rb_discard_oldest(&rb, 10u) == 0u);
    assert(cav_rb_avail_read_frames(&rb) == 0u);
    assert(rb.overruns == 0u);
    assert(rb.underruns == 0u);
    printf("[PASS] SPSC RingBuffer cav_rb_discard_oldest wrap-around, zero-cap & flush parity\n");
}

static void test_uac2_descriptor_and_feedback(void) {
    /* Synthetic Focusrite Scarlett 2i2 Gen 3/4 style UAC2 config descriptor:
     * AC if=0 (clock_id=41 programmable) + AS OUT if=1 alt=1 (2ch 24-in-32 async EP 0x01)
     * + AS IN if=2 alt=1 (2ch 24-in-32 EP 0x82) + Vendor if=3 */
    const uint8_t desc[] = {
        /* Config header (9B) */
        9, 0x02, 100, 0, 4, 1, 0, 0x80, 250,
        /* AC Interface 0 (UAC2) */
        9, 0x04, 0, 0, 0, 0x01, 0x01, 0x20, 0,
        /* CS_INTERFACE Clock Source (id=41, internal programmable bmControls=0x03) */
        8, 0x24, 0x0A, 41, 0x01, 0x03, 0, 0,
        /* AS OUT Interface 1 Alt 1 */
        9, 0x04, 1, 1, 1, 0x01, 0x02, 0x20, 0,
        /* AS_GENERAL (16B): terminal=1, fmt_type=1, formats=PCM(1), channels=2 */
        16, 0x24, 0x01, 1, 0, 1, 1, 0, 0, 0, 2, 3, 0, 0, 0, 0,
        /* FORMAT_TYPE_I (6B): subslot=4, bitRes=24 */
        6, 0x24, 0x02, 1, 4, 24,
        /* ISO OUT Async Data Endpoint (0x01, bmAttr=0x05, maxPkt=256, interval=1) */
        7, 0x05, 0x01, 0x05, 0x00, 0x01, 1,
        /* AS IN Interface 2 Alt 1 */
        9, 0x04, 2, 1, 1, 0x01, 0x02, 0x20, 0,
        /* AS_GENERAL (16B): terminal=2, fmt_type=1, formats=PCM(1), channels=2 */
        16, 0x24, 0x01, 2, 0, 1, 1, 0, 0, 0, 2, 3, 0, 0, 0, 0,
        /* FORMAT_TYPE_I (6B): subslot=4, bitRes=24 */
        6, 0x24, 0x02, 1, 4, 24,
        /* ISO IN Async Data Endpoint (0x82, bmAttr=0x05, maxPkt=256, interval=1) */
        7, 0x05, 0x82, 0x05, 0x00, 0x01, 1,
        /* Vendor Control Interface 3 (Scarlett FCP) */
        9, 0x04, 3, 0, 0, 0xFF, 0x00, 0x00, 0
    };

    CavUac2Topology topo;
    assert(cav_uac2_parse_config_desc(desc, sizeof(desc), &topo) == 0);
    assert(topo.is_uac2 == 1);
    assert(topo.clock_source_id == 41);
    assert(topo.clock_programmable == 1);
    assert(topo.vendor_interface_num == 3);
    assert(topo.playback_count == 1);
    assert(topo.capture_count == 1);
    assert(topo.playback[0].format == CAV_UAC2_FMT_PCM24_IN_32_LE);
    assert(topo.playback[0].implicit_feedback == 1);
    assert(topo.capture[0].data_ep_addr == 0x82);

    /* Test 16.16 feedback pacing for 44.1 kHz @ 8000 HS uframes/sec = 5.5125 frames/uframe.
     * 5.5125 * 65536 = 361267.2 -> quantized to 361267 (16.16 resolution = 0.55 ppm -> 44099.976 frames/s). */
    uint32_t rate_44100 = (uint32_t)llround((44100.0 / 8000.0) * 65536.0);
    uint32_t accum = 0;
    uint32_t sum_frames = 0;
    for (int i = 0; i < 8000; i++) {
        uint32_t f = cav_uac2_advance_packet_frames(&accum, rate_44100);
        assert(f == 5u || f == 6u);
        sum_frames += f;
    }
    double effective_44100 = (double)sum_frames + ((double)accum / 65536.0);
    assert(fabs(effective_44100 - 44100.0) < 0.05);

    /* Exact 48 kHz @ 8000 HS uframes/sec = 6.0 frames/uframe */
    uint32_t rate_48000 = 6u << 16;
    uint32_t accum_48k = 0;
    uint32_t sum_48k = 0;
    for (int i = 0; i < 8000; i++) {
        sum_48k += cav_uac2_advance_packet_frames(&accum_48k, rate_48000);
    }
    assert(sum_48k == 48000u && accum_48k == 0u);

    /* Test 10.14 Full-Speed feedback decode -> 16.16 (48.0 frames/ms = 48 << 14 = 0x0C0000) */
    uint8_t fs_fb[3] = { 0x00, 0x00, 0x0C };
    uint32_t dec_fs = cav_uac2_decode_feedback_1616(fs_fb, 3, 0);
    assert(dec_fs == (48u << 16));
    printf("[PASS] UAC2 topology parser, implicit feedback detection, and 16.16/10.14 pacer (44.1k=%.3f, 48k=%u)\n",
           effective_44100, sum_48k);
}

static void test_pcm_pack_unpack_and_scarlett2(void) {
    const float src[4] = { -0.75f, -0.125f, 0.25f, 0.875f };
    uint8_t wire[32];
    float dst[4];

    /* 24-bit 3B packed round-trip */
    assert(cav_uac2_pack_float(src, 4, wire, CAV_UAC2_FMT_PCM24_3B_LE) == 12u);
    assert(cav_uac2_unpack_to_float(wire, 4, dst, CAV_UAC2_FMT_PCM24_3B_LE) == 4u);
    for (int i = 0; i < 4; i++) assert(fabsf(dst[i] - src[i]) < 1e-6f);

    /* 24-in-32bit round-trip */
    assert(cav_uac2_pack_float(src, 4, wire, CAV_UAC2_FMT_PCM24_IN_32_LE) == 16u);
    assert(cav_uac2_unpack_to_float(wire, 4, dst, CAV_UAC2_FMT_PCM24_IN_32_LE) == 4u);
    for (int i = 0; i < 4; i++) assert(fabsf(dst[i] - src[i]) < 1e-6f);

    /* Float32 bit-exact round-trip */
    assert(cav_uac2_pack_float(src, 4, wire, CAV_UAC2_FMT_FLOAT32_LE) == 16u);
    assert(cav_uac2_unpack_to_float(wire, 4, dst, CAV_UAC2_FMT_FLOAT32_LE) == 4u);
    for (int i = 0; i < 4; i++) assert(dst[i] == src[i]);

    /* Scarlett2 vendor packet codec */
    uint8_t pkt[32];
    uint8_t payload[4] = { 0x00, 0x00, 0x02, 0x00 };
    size_t n = cav_scarlett2_build_packet(CAV_SCARLETT_CMD_GET_METER, 42u, payload, 4u, pkt, sizeof(pkt));
    assert(n == 20u);
    uint16_t out_len = 0;
    assert(cav_scarlett2_validate_response(pkt, n, CAV_SCARLETT_CMD_GET_METER, 42u, &out_len) == 0);
    assert(out_len == 4u);
    printf("[PASS] UAC2 PCM24/PCM32/Float32 bit-exact packers & Scarlett2 packet codec\n");
}

static void test_sigpipe_shield(void) {
    assert(cav_sigpipe_shield_apply() == 0);
    struct sigaction cur;
    assert(sigaction(SIGPIPE, NULL, &cur) == 0);
    assert(cur.sa_handler == SIG_IGN);
    assert(raise(SIGPIPE) == 0);

    int pipefds[2] = { -1, -1 };
    assert(pipe(pipefds) == 0);
    assert(close(pipefds[0]) == 0);
    errno = 0;
    ssize_t wr = write(pipefds[1], "c", 1);
    assert(wr == 1 || (wr == -1 && errno == EPIPE));
    assert(close(pipefds[1]) == 0);
    printf("[PASS] SIGPIPE shield (SIG_IGN + raise(SIGPIPE) survival)\n");
}

static void test_scarlett2i2_gen4_exact_descriptor(void) {
    /* Exact live S25U Focusrite Scarlett 2i2 4th Gen (VID=0x1235 PID=0x8219) UAC2 descriptor:
     * IF 0 AC ClockID=41; IF 1 AS OUT EP 0x01 Sync (bmAttr=0x0D) 2ch 24-in-32 maxPkt=200;
     * IF 2 AS IN EP 0x81 Sync (bmAttr=0x0D) 4ch 24-in-32 maxPkt=400 (Mic1-2 + Loopback3-4);
     * IF 3 Vendor FCP (cls=0xFF sub=1 proto=0x20, EP 0x83 Interrupt 64B). */
    const uint8_t desc[] = {
        9, 0x02, 114, 0, 4, 1, 0, 0x80, 250,
        9, 0x04, 0, 0, 0, 0x01, 0x01, 0x20, 0,
        8, 0x24, 0x0A, 41, 0x01, 0x03, 0, 0,
        9, 0x04, 1, 1, 1, 0x01, 0x02, 0x20, 0,
        16, 0x24, 0x01, 1, 0, 1, 1, 0, 0, 0, 2, 3, 0, 0, 0, 0,
        6, 0x24, 0x02, 1, 4, 24,
        7, 0x05, 0x01, 0x0D, 0xC8, 0x00, 1, /* maxPkt = 200 (0x00C8), Sync = 0x0D */
        9, 0x04, 2, 1, 1, 0x01, 0x02, 0x20, 0,
        16, 0x24, 0x01, 2, 0, 1, 1, 0, 0, 0, 4, 15, 0, 0, 0, 0, /* 4ch capture */
        6, 0x24, 0x02, 1, 4, 24,
        7, 0x05, 0x81, 0x0D, 0x90, 0x01, 1, /* maxPkt = 400 (0x0190), Sync = 0x0D */
        9, 0x04, 3, 0, 1, 0xFF, 0x01, 0x20, 0,
        7, 0x05, 0x83, 0x03, 0x40, 0x00, 1
    };

    CavUac2Topology topo;
    assert(cav_uac2_parse_config_desc(desc, sizeof(desc), &topo) == 0);
    assert(topo.is_uac2 == 1);
    assert(topo.clock_source_id == 41);
    assert(topo.vendor_interface_num == 3);
    assert(topo.playback_count == 1);
    assert(topo.capture_count == 1);
    assert(topo.playback[0].data_ep_addr == 0x01);
    assert(topo.playback[0].channels == 2);
    assert(topo.playback[0].max_packet_size == 200);
    assert(topo.playback[0].implicit_feedback == 0); /* 0x0D Synchronous != Async */
    assert(topo.capture[0].data_ep_addr == 0x81);
    assert(topo.capture[0].channels == 4);
    assert(topo.capture[0].max_packet_size == 400);
    assert(topo.capture[0].format == CAV_UAC2_FMT_PCM24_IN_32_LE);
    printf("[PASS] Focusrite Scarlett 2i2 4th Gen exact UAC2 descriptor (2ch OUT 200B + 4ch IN 400B + IF3 FCP)\n");
}

static void test_ring_underrun_glitch_ramp(void) {
    float buf[128 * 2];
    int in_underrun = 0;
    float last[2] = { 0.8f, -0.6f };

    /* 1. Zero-frame starvation writes smooth decay tail instead of hard step to 0 */
    memset(buf, 0, sizeof(buf));
    cav_rb_apply_glitch_ramp(buf, 64u, 2u, 0u, &in_underrun, last);
    assert(in_underrun == 1);
    assert(buf[0] > 0.70f && buf[0] < 0.80f);
    assert(buf[1] < -0.50f && buf[1] > -0.60f);
    assert(fabsf(buf[24 * 2]) < 1e-6f);
    assert(fabsf(buf[24 * 2 + 1]) < 1e-6f);

    /* 2. Recovery fades smoothly in from silence over 24 frames */
    for (int i = 0; i < 64 * 2; ++i) buf[i] = 1.0f;
    cav_rb_apply_glitch_ramp(buf, 64u, 2u, 64u, &in_underrun, last);
    assert(in_underrun == 0);
    assert(buf[0] > 0.0f && buf[0] < 0.1f);
    assert(buf[23 * 2] > 0.90f && buf[23 * 2] < 1.0f);
    assert(fabsf(buf[24 * 2] - 1.0f) < 1e-6f);
    assert(fabsf(last[0] - 1.0f) < 1e-6f);

    /* 3. Partial underrun fades out trailing valid frames cleanly to 0 */
    for (int i = 0; i < 40 * 2; ++i) buf[i] = 0.5f;
    for (int i = 40 * 2; i < 64 * 2; ++i) buf[i] = 0.0f;
    cav_rb_apply_glitch_ramp(buf, 64u, 2u, 40u, &in_underrun, last);
    assert(in_underrun == 1);
    assert(fabsf(buf[10 * 2] - 0.5f) < 1e-6f);
    assert(buf[39 * 2] > 0.0f && buf[39 * 2] < 0.05f);
    assert(fabsf(buf[40 * 2]) < 1e-6f);
    printf("[PASS] Zero-click 24-frame RT underrun decay tail, partial fade-out, and fade-in recovery\n");
}

int main(void) {
    test_ring_buffer_wraparound();
    test_ring_buffer_discard_oldest();
    test_uac2_descriptor_and_feedback();
    test_scarlett2i2_gen4_exact_descriptor();
    test_ring_underrun_glitch_ramp();
    test_pcm_pack_unpack_and_scarlett2();
    test_sigpipe_shield();
    return 0;
}
