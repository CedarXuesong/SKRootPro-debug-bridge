#pragma once

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
#else
#define SKPDB_BUILD_CHANNEL "Release"
#define SKPDB_VERSION_SUFFIX ""
#define SKPDB_HUD_CAPABILITY "disabled"
#endif
