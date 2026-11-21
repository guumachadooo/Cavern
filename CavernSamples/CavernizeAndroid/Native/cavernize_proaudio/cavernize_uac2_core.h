#ifndef CAVERNIZE_UAC2_CORE_H
#define CAVERNIZE_UAC2_CORE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CAV_UAC2_MAX_STREAMS 16
#define CAV_SCARLETT_VID 0x1235
#define CAV_SCARLETT_2I2_GEN2_PID 0x8202
#define CAV_SCARLETT_SOLO_GEN2_PID 0x8205
#define CAV_SCARLETT_2I2_GEN3_PID 0x8210
#define CAV_SCARLETT_SOLO_GEN3_PID 0x8211
#define CAV_SCARLETT_SOLO_GEN4_PID 0x8218
#define CAV_SCARLETT_2I2_GEN4_PID 0x8219

#define CAV_SCARLETT_USB_REQ_SEND 0x02
#define CAV_SCARLETT_USB_REQ_RECV 0x03
#define CAV_SCARLETT_PKT_HDR_SIZE 16

#define CAV_SCARLETT_CMD_INIT_1 0x00000000u
#define CAV_SCARLETT_CMD_INIT_2 0x00000002u
#define CAV_SCARLETT_CMD_CONFIG_SAVE 0x00000006u
#define CAV_SCARLETT_CMD_GET_METER 0x00001001u
#define CAV_SCARLETT_CMD_GET_DATA 0x00800000u
#define CAV_SCARLETT_CMD_SET_DATA 0x00800001u

typedef enum CavUac2Format {
    CAV_UAC2_FMT_UNKNOWN = 0,
    CAV_UAC2_FMT_PCM16_LE = 1,
    CAV_UAC2_FMT_PCM24_3B_LE = 2,
    CAV_UAC2_FMT_PCM24_IN_32_LE = 3,
    CAV_UAC2_FMT_PCM32_LE = 4,
    CAV_UAC2_FMT_FLOAT32_LE = 5
} CavUac2Format;

typedef enum CavIsoSyncType {
    CAV_ISO_SYNC_NONE = 0,
    CAV_ISO_SYNC_ASYNC = 1,
    CAV_ISO_SYNC_ADAPTIVE = 2,
    CAV_ISO_SYNC_SYNC = 3
} CavIsoSyncType;

typedef struct CavUac2StreamDesc {
    uint8_t interface_num;
    uint8_t alt_setting;
    uint8_t is_input;
    uint8_t channels;
    uint8_t subslot_bytes;
    uint8_t bit_resolution;
    uint8_t format;           /* CavUac2Format */
    uint8_t data_ep_addr;
    uint16_t max_packet_size;
    uint8_t interval;
    uint8_t sync_type;        /* CavIsoSyncType */
    uint8_t feedback_ep_addr;
    uint8_t implicit_feedback;
} CavUac2StreamDesc;

typedef struct CavUac2Topology {
    uint8_t is_uac2;
    uint8_t ac_interface_num;
    uint8_t clock_source_id;
    uint8_t clock_programmable;
    int8_t vendor_interface_num;
    uint8_t playback_count;
    uint8_t capture_count;
    CavUac2StreamDesc playback[CAV_UAC2_MAX_STREAMS];
    CavUac2StreamDesc capture[CAV_UAC2_MAX_STREAMS];
} CavUac2Topology;

/* Parses a raw USB configuration descriptor into UAC2 topology (clock source, playback/capture alt-settings, feedback EPs). */
int cav_uac2_parse_config_desc(const uint8_t* raw, size_t len, CavUac2Topology* out_topo);

/* Selects the best playback or capture stream setting (prefers 32/24-bit and stereo). Returns index or -1. */
int cav_uac2_select_best_stream(const CavUac2StreamDesc* list, uint8_t count);

/* Parses a UAC2 Clock Source GET_RANGE payload into discrete sample rates (returns count written). */
int cav_uac2_parse_clock_ranges(const uint8_t* payload, size_t len, uint32_t* out_rates, int max_rates);

/* Decodes a UAC2 async feedback packet (HS 16.16 4B or FS 10.14 3B) into normalized 16.16 fixed-point frames/interval. */
uint32_t cav_uac2_decode_feedback_1616(const uint8_t* packet, size_t len, int is_high_speed);

/* Advances 16.16 fixed-point async packet frame phase accumulator. */
uint32_t cav_uac2_advance_packet_frames(uint32_t* phase_accum_1616, uint32_t rate_1616);

/* Bit-exact float32 <-> UAC2 wire PCM16/PCM24-3B/PCM24-in-32/PCM32/Float32 converters. */
size_t cav_uac2_pack_float(const float* src, size_t sample_count, uint8_t* dst, CavUac2Format fmt);
size_t cav_uac2_unpack_to_float(const uint8_t* src, size_t sample_count, float* dst, CavUac2Format fmt);

/* Clean-room Focusrite Scarlett2 vendor USB command packet builder & response validator. */
size_t cav_scarlett2_build_packet(uint32_t cmd, uint16_t seq, const uint8_t* payload, uint16_t payload_len, uint8_t* out_buf, size_t out_cap);
int cav_scarlett2_validate_response(const uint8_t* resp, size_t resp_len, uint32_t expected_cmd, uint16_t expected_seq, uint16_t* out_payload_len);

#ifdef __cplusplus
}
#endif

#endif /* CAVERNIZE_UAC2_CORE_H */
