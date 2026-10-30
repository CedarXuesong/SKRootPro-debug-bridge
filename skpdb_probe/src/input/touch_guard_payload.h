#pragma once
#include <stdint.h>

namespace skpdb::touch_guard {
// All fields except deadline are accessed under input_dev.event_lock. The
// userspace owner renews deadline atomically; an abandoned guard fails open.
struct State {
    uint64_t deadline;
    uint32_t owned;
    uint32_t blocked;
    int32_t tracking[16];
    uint64_t writer_task;
};
struct Event {
    uint64_t now;
    uint32_t owner;
    uint32_t injected;
    uint32_t slot;
    int32_t tracking;
    uint32_t type;
    uint32_t code;
    int32_t value;
    uint32_t active;
};
// Return 1 only when this event must be suppressed. Hardware events always
// pass.
extern "C" uint32_t SkpdbTouchGuard(State *state, const Event *event);
} // namespace skpdb::touch_guard
