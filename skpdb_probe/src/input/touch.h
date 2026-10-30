#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace skpdb {

// Touch injection into the real touchscreen input device. The fast backend
// writes protocol B events to its existing evdev node; devices that refuse
// that node use the kernel input_event backend. Neither creates a uinput
// device.
//
// Coordinates in this API are screen pixels; the module maps them to the raw
// range reported by the touchscreen's ABS_MT_POSITION_X/Y.

constexpr uint8_t kTouchInfo = 0;
constexpr uint8_t kTouchDown = 1;
constexpr uint8_t kTouchMove = 2;
constexpr uint8_t kTouchUp = 3;
constexpr uint8_t kTouchTap = 4;
constexpr uint8_t kTouchSwipe = 5;

// Request layout: [I][action][flags][slot u16][x u32][y u32][x2 u32][y2 u32]
//                 [duration_ms u32]
constexpr size_t kTouchRequestBytes = 25;

struct TouchResult {
    bool ok = false;
    std::string report;
    std::string error;
};

// Device report. Resolving the kernel input_dev is the expensive part; it is
// cached after the first call and reported here as key=value lines.
TouchResult TouchDiagnostics();
void TouchHeartbeat();
void TouchShutdown();

// Device discovery and one-time kernel binding. Called by every gesture.
TouchResult TouchDown(uint32_t x, uint32_t y, uint16_t slot);
TouchResult TouchMove(uint32_t x, uint32_t y, uint16_t slot);
TouchResult TouchUp(uint16_t slot);
TouchResult TouchTap(uint32_t x, uint32_t y, uint32_t hold_ms, uint16_t slot);
TouchResult TouchSwipe(uint32_t x1, uint32_t y1, uint32_t x2, uint32_t y2, uint32_t duration_ms,
                       uint16_t slot);

struct TouchBatchItem {
    uint8_t action = 0;
    uint16_t slot = 0;
    uint32_t x = 0;
    uint32_t y = 0;
};

// Executes several down/move/up frames in one kernel entry. A batch keeps the
// per-frame timestamps close together while paying the shellcode entry cost
// once instead of once per frame.
TouchResult TouchBatch(const std::vector<TouchBatchItem> &items);

} // namespace skpdb
