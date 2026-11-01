#pragma once
#include <stdint.h>
#include <stddef.h>

namespace skpdb::gyro_payload {
constexpr uint32_t kMagic = 0x32594753;
constexpr uint32_t kVersion = 3;
constexpr uint64_t kLeaseNs = 200000000;
constexpr uint32_t kMaxEvents = 512;
struct Event {
    int64_t timestamp;
    int32_t sensor, type;
    float data[16];
};
static_assert(sizeof(Event) == 80);
static_assert(offsetof(Event, type) == 12 && offsetof(Event, data) == 16);
struct Control {
    uint32_t sequence, enabled, uid, stream;
    uint64_t updated;
    uint64_t valid_since;
    float rate[3];
    uint32_t epoch;
};
struct Fusion {
    int64_t timestamp;
    float q[4];
    uint32_t epoch, reserved;
};
struct State {
    uint32_t magic, version, active_calls, hits;
    Control control;
    uint32_t lock, rejected;
    Fusion fusion[2];
};
static_assert(sizeof(Control) == 48);
static_assert(sizeof(State) <= 128 + 64);
// HAL V2_1 event vector, before SensorService fans it out to any application.
// The UID argument is retained in the helper ABI but never selects a consumer.
uint32_t Transform(State *, uint32_t uid, Event *, size_t count, uint64_t now);
} // namespace skpdb::gyro_payload
