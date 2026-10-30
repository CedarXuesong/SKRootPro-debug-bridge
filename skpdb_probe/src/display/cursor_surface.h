#pragma once

#include <cstdint>
#include <string>
#include "display/cursor.h"

namespace skpdb {

// A root SurfaceControl overlay whose layer carries eSkipScreenshot. The
// pixels are drawn by the CPU into an ANativeWindow buffer; no GPU renderer and
// no application Surface are involved.
bool CursorSurfaceStart(std::string& error);
bool CursorSurfaceStarted();
std::string CursorSurfaceReport();
void CursorSurfaceMove(int x, int y);
void CursorSurfaceSetVisible(bool visible);
bool CursorSurfaceSetOverlay(const ControlOverlayState& state, std::string& error);
void CursorSurfaceStop();

}  // namespace skpdb
