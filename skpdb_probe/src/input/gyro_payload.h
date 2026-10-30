#pragma once
#include <stdint.h>
#include <stddef.h>

namespace skpdb::gyro_payload {
constexpr uint32_t kMagic = 0x32594753;
constexpr uint32_t kVersion = 2;
constexpr uint64_t kLeaseNs = 200000000;
constexpr uint32_t kMaxEvents = 512;
struct Event {
    int32_t version, sensor, type, reserved;
    int64_t timestamp;
    float data[16];
    uint32_t flags, tail[3];
};
static_assert(sizeof(Event) == 104);
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
// Edits only the caller-owned copy. Original dispatcher input remains untouched.
uint32_t Transform(State *, uint32_t uid, Event *, size_t count, uint64_t now);
} // namespace skpdb::gyro_payload
