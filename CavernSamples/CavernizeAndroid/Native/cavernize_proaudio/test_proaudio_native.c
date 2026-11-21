#include "cavernize_ringbuffer.h"
#include "cavernize_uac2_core.h"

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

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

int main(void) {
    test_ring_buffer_wraparound();
    test_uac2_descriptor_and_feedback();
    test_pcm_pack_unpack_and_scarlett2();
    return 0;
}
