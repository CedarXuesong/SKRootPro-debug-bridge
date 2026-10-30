// Standalone userspace test. Does not install hooks or modify an input device.
#include "../src/input/touch_guard_payload.h"
#include "../src/input/touch_state.h"
#include <cassert>
#include <cstdio>
int main() {
    using namespace skpdb;
    using namespace skpdb::touch_guard;
    State s{};
    s.deadline = 1000;
    Event e{};
    e.now = 100;
    e.owner = 1;
    e.slot = 9;
    e.type = 3;
    e.code = 57;
    e.value = 16384;
    assert(!SkpdbTouchGuard(&s, &e));
    assert(s.owned == (1u << 9));
    e.owner = 0;
    e.injected = 1;
    e.tracking = 16384;
    e.value = -1;
    e.active = 1;
    for (int n = 0; n < 100; ++n)
        assert(SkpdbTouchGuard(&s, &e) == 1);
    e.slot = 0;
    e.tracking = -1;
    assert(!SkpdbTouchGuard(&s, &e));
    e.type = 1;
    e.code = 330;
    e.value = 0;
    assert(SkpdbTouchGuard(&s, &e) == 1);
    e.code = 325;
    assert(SkpdbTouchGuard(&s, &e) == 1);
    e.type = 3;
    e.code = 57;
    e.slot = 9;
    e.tracking = 99;
    assert(!SkpdbTouchGuard(&s, &e));
    assert(s.owned == 0);
    e.owner = 1;
    e.value = 16384;
    SkpdbTouchGuard(&s, &e);
    e.owner = 0;
    e.injected = 0;
    e.value = -1;
    e.tracking = 16384;
    assert(!SkpdbTouchGuard(&s, &e));
    assert(!s.owned); // genuine hardware UP
    e.owner = 1;
    e.value = 16385;
    SkpdbTouchGuard(&s, &e);
    e.owner = 0;
    e.injected = 1;
    e.value = -1;
    e.tracking = 16385;
    e.now = 1000;
    assert(!SkpdbTouchGuard(&s, &e));
    assert(!s.owned); // expired owner fails open
    e.owner = 1;
    e.type = 1;
    e.code = 330;
    e.value = 0;
    e.active = 1;
    assert(SkpdbTouchGuard(&s, &e));
    e.active = 0;
    assert(!SkpdbTouchGuard(&s, &e));
    std::puts("PASS scoped release guard: software cleanup blocked, "
              "hardware/foreign slots preserved, lease fails open");
    using namespace skpdb::touch_detail;
    uint32_t sequence = 0;
    std::array<bool, 16> down{};
    std::array<int32_t, 16> ids{}, actual{};
    std::array<int, 16> physical{};
    ids.fill(-1);
    actual.fill(-1);
    physical.fill(-1);
    actual[0] = 71;
    std::vector<FrameSpec> frames;
    std::string error;
    assert(PlanFrame(9, sequence, FrameKind::kDown, 0, 100, 200, 16384, down, ids, physical, actual,
                     frames, error));
    assert(physical[0] == 9 && actual[0] == 71);
    assert(PlanFrame(9, sequence, FrameKind::kDown, 1, 110, 210, 16385, down, ids, physical, actual,
                     frames, error));
    assert(physical[1] == 8 && physical[0] == 9);
    assert(PlanFrame(9, sequence, FrameKind::kDown, 0, 120, 220, 16386, down, ids, physical, actual,
                     frames, error));
    assert(frames.back().kind == FrameKind::kMove && ids[0] == 16384);
    assert(PlanFrame(9, sequence, FrameKind::kUp, 1, 0, 0, 0, down, ids, physical, actual, frames,
                     error));
    assert(!frames.back().emit_touch_key);
    assert(PlanFrame(9, sequence, FrameKind::kUp, 0, 0, 0, 0, down, ids, physical, actual, frames,
                     error));
    assert(!frames.back().emit_touch_key && actual[0] == 71);
    const auto count = frames.size();
    assert(PlanFrame(9, sequence, FrameKind::kMove, 1, 0, 0, 0, down, ids, physical, actual, frames,
                     error));
    assert(frames.size() == count);
    assert(!PlanFrame(9, sequence, FrameKind::kDown, 16, 0, 0, 1, down, ids, physical, actual,
                      frames, error));
    actual.fill(42);
    assert(!PlanFrame(9, sequence, FrameKind::kDown, 2, 0, 0, 16388, down, ids, physical, actual,
                      frames, error));
    actual[9] = -1;
    assert(PlanFrame(9, sequence, FrameKind::kDown, 2, 0, 0, 16388, down, ids, physical, actual,
                     frames, error));
    actual[9] = 88;
    assert(!PlanFrame(9, sequence, FrameKind::kMove, 2, 0, 0, 0, down, ids, physical, actual,
                      frames, error));
    assert(PlanFrame(9, sequence, FrameKind::kUp, 2, 0, 0, 0, down, ids, physical, actual, frames,
                     error));
    assert(actual[9] == 88);
    std::puts("PASS physical slot mapping: active fingers preserved, stable "
              "lease, duplicate down, stale move, capacity and takeover");
}
