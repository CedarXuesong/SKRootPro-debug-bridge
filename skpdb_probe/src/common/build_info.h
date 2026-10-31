#pragma once

#if __has_include("common/build_revision.h")
#include "common/build_revision.h"
#else
// Standalone policy tests do not run the module asset-generation step.
#define SKPDB_GIT_COMMIT "unknown"
#define SKPDB_GIT_SHORT_COMMIT "unknown"
#endif

#ifndef SKPDB_CANARY
#define SKPDB_CANARY 0
#endif
#if SKPDB_CANARY != 0 && SKPDB_CANARY != 1
#error SKPDB_CANARY must be 0 or 1
#endif

#if SKPDB_CANARY
#define SKPDB_BUILD_CHANNEL "Canary"
#define SKPDB_VERSION_SUFFIX "-canary"
#define SKPDB_HUD_CAPABILITY "transparent"
#define SKPDB_MODULE_UPDATE_URL "https://github.com/CedarXuesong/SKRootPro-debug-bridge/releases/download/updates/canary.json"
#else
#define SKPDB_BUILD_CHANNEL "Release"
#define SKPDB_VERSION_SUFFIX ""
#define SKPDB_HUD_CAPABILITY "disabled"
#define SKPDB_MODULE_UPDATE_URL "https://github.com/CedarXuesong/SKRootPro-debug-bridge/releases/download/updates/release.json"
#endif
