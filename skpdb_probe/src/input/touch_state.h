#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <string>
#include <vector>
namespace skpdb::touch_detail {
enum class FrameKind { kDown, kMove, kUp };
struct FrameSpec {
    FrameKind kind;
    uint32_t slot;
    int32_t x;
    int32_t y;
    uint32_t tracking_id;
    uint32_t frame;
    bool emit_touch_key;
    bool emit_slot;
    bool emit_metrics;
};
inline bool PlanFrame(int slot_max, uint32_t &sequence, FrameKind kind, uint32_t logical, int32_t x,
                      int32_t y, uint32_t tracking, std::array<bool, 16> &down,
                      std::array<int32_t, 16> &ids, std::array<int, 16> &physical,
                      std::array<int32_t, 16> &actual, std::vector<FrameSpec> &frames,
                      std::string &error) {
    if (slot_max < 0 || slot_max >= 16 || logical >= down.size() ||
        logical > static_cast<uint32_t>(slot_max)) {
        error = "touch slot exceeds device capacity";
        return false;
    }
    const bool held = down[logical];
    if (!held && kind != FrameKind::kDown)
        return true;
    int slot = physical[logical];
    if (held &&
        (slot < 0 || slot >= static_cast<int>(actual.size()) || actual[slot] != ids[logical])) {
        // A hardware finger took the slot. Do not emit an UP on its behalf.
        if (kind != FrameKind::kUp) {
            error = "physical touchscreen contact changed; release and retry";
            return false;
        }
        down[logical] = false;
        ids[logical] = -1;
        physical[logical] = -1;
        return true;
    }
    if (kind == FrameKind::kDown && held)
        kind = FrameKind::kMove;
    if (kind == FrameKind::kDown) {
        slot = -1;
        for (int candidate = slot_max; candidate >= 0; --candidate)
            if (actual[candidate] < 0) {
                slot = candidate;
                break;
            }
        if (slot < 0) {
            error = "physical touchscreen has no free contact slot";
            return false;
        }
        if (tracking > 65535) {
            error = "invalid physical tracking ID";
            return false;
        }
        // Tracking IDs are unique within one input device, including real fingers.
        while (std::find(actual.begin(), actual.end(), static_cast<int32_t>(tracking)) !=
               actual.end())
            tracking = tracking == 65535 ? 16384 : tracking + 1;
        physical[logical] = slot;
        ids[logical] = tracking;
        down[logical] = true;
        actual[slot] = tracking;
    } else if (kind == FrameKind::kUp) {
        actual[slot] = -1;
        down[logical] = false;
        ids[logical] = -1;
        physical[logical] = -1;
    }
    const bool any = std::any_of(actual.begin(), actual.end(), [](int id) { return id >= 0; });
    frames.push_back({kind, static_cast<uint32_t>(slot), x, y, tracking, ++sequence,
                      kind == FrameKind::kDown || (kind == FrameKind::kUp && !any), true,
                      kind == FrameKind::kDown});
    return true;
}

} // namespace skpdb::touch_detail
