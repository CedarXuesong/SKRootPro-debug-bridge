#include "common/build_info.h"
#include "display/cursor_surface.h"
#include "wireless/module_info.h"

#include <cstdlib>
#include <iostream>

namespace {
int starts = 0, overlays = 0, moves = 0;
void Check(bool value) {
    if (!value) std::abort();
}
}

namespace skpdb {
bool CursorSurfaceStart(std::string&) { ++starts; return true; }
void CursorSurfaceMove(int, int) { ++moves; }
void CursorSurfaceSetVisible(bool) {}
bool CursorSurfaceSetOverlay(const ControlOverlayState&, std::string&) {
    ++overlays;
    return true;
}
}

int main() {
    skpdb::ControlOverlayState state;
    state.enabled = true;
    const auto enabled = skpdb::ControlOverlaySet(state);
    Check(enabled.ok == bool(SKPDB_CANARY));
    Check(starts == SKPDB_CANARY && overlays == SKPDB_CANARY);
    if (!SKPDB_CANARY) Check(!enabled.error.empty());
    state.enabled = false;
    const auto disabled = skpdb::ControlOverlaySet(state);
    Check(disabled.ok && disabled.report == "hud=0");
    Check(overlays == 2 * SKPDB_CANARY);
    Check(skpdb::CursorSet(20, 30, true).ok && moves == 1);
    Check(skpdb::CursorDestroy().ok);
    Check(std::string(skpdb::wireless::kModuleVersion) ==
          (SKPDB_CANARY ? "0.12.15-canary" : "0.12.15"));
    std::cout << "PASS " << SKPDB_BUILD_CHANNEL << ": HUD policy, cursor and identity\n";
}
