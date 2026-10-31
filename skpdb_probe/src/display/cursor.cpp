#include "display/cursor.h"

#include "display/cursor_surface.h"
#include "common/build_info.h"

#include <cstdio>
#include <mutex>
#include <string>

namespace skpdb {
namespace {

std::mutex g_mutex;

void Log(const std::string& line) {
    FILE* file = std::fopen("/data/local/tmp/skpdb_cursor.log", "a");
    if (file == nullptr) {
        return;
    }
    std::fprintf(file, "%s\n", line.c_str());
    std::fclose(file);
}

}  // namespace

CursorResult CursorProbe() {
    std::lock_guard<std::mutex> guard(g_mutex);
    CursorResult result;
    std::string error;
    if (!CursorSurfaceStart(error)) {
        result.error = error;
        Log("SURFACE FAIL " + error);
        return result;
    }
    result.ok = true;
    result.report = "surface=ready\nmode=cpu\nscreenshot=skip";
    Log("SURFACE OK");
    return result;
}

CursorResult CursorCreate() {
    std::lock_guard<std::mutex> guard(g_mutex);
    CursorResult result;
    std::string error;
    if (!CursorSurfaceStart(error)) {
        result.error = error;
        return result;
    }
    result.ok = true;
    result.report = "cursor surface ready";
    return result;
}

CursorResult CursorSet(uint16_t x, uint16_t y, bool visible) {
    std::lock_guard<std::mutex> guard(g_mutex);
    CursorResult result;
    std::string error;
    if (!CursorSurfaceStart(error)) {
        result.error = error;
        return result;
    }
    CursorSurfaceMove(x, y);
    CursorSurfaceSetVisible(visible);
    result.ok = true;
    result.report = "cursor x=" + std::to_string(x) + " y=" + std::to_string(y) +
                    " visible=" + (visible ? "1" : "0") + " mode=cpu";
    Log(result.report);
    return result;
}

CursorResult CursorDestroy() {
    std::lock_guard<std::mutex> guard(g_mutex);
    CursorSurfaceSetVisible(false);
    CursorResult result;
    result.ok = true;
    result.report = "cursor destroyed";
    return result;
}

CursorResult ControlOverlaySet(const ControlOverlayState& state) {
    std::lock_guard<std::mutex> guard(g_mutex);
    CursorResult result;
#if !SKPDB_CANARY
    result.ok = !state.enabled;
    if (result.ok) result.report = "hud=0";
    else result.error = "debug overlay is disabled in Release builds";
    return result;
#else
    if (state.enabled && !CursorSurfaceStart(result.error)) return result;
    result.ok = CursorSurfaceSetOverlay(state, result.error);
    if (result.ok) result.report = state.enabled ? "hud=1" : "hud=0";
    return result;
#endif
}

}  // namespace skpdb
