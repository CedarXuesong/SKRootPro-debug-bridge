#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

// CPU-rendered trusted SurfaceControl layers, excluded from screenshots and
// without input channels. Cursor visibility and the control HUD are independent.
constexpr uint8_t kCursorProbe = 0;
constexpr uint8_t kCursorSet = 1;
constexpr uint8_t kCursorDestroy = 2;
constexpr uint8_t kControlOverlay = 3;

struct ControlOverlayState {
    bool enabled = false;
    uint8_t rotation = 0;
    uint16_t width = 0, height = 0, active = 0, capacity = 0, pending = 0;
    uint16_t sampling_tenths = 0, rtt_tenths = 0;
    uint8_t mode = 0;
    bool input_active = false;
};

struct CursorResult {
    bool ok = false;
    std::string report;
    std::string error;
};

// Starts the renderer and waits for its first successfully posted buffer.
CursorResult CursorProbe();

// Creates the 64x64 cursor surface and draws the arrow sprite.
CursorResult CursorCreate();

// Sets the cursor position and visibility, starting the renderer if needed.
CursorResult CursorSet(uint16_t x, uint16_t y, bool visible);

// Hides only the arrow; an active control HUD remains visible.
CursorResult CursorDestroy();
CursorResult ControlOverlaySet(const ControlOverlayState& state);

}  // namespace skpdb
