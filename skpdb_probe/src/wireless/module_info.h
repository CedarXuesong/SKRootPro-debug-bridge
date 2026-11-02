#pragma once

#include <string>
#include "common/build_info.h"

// Identity shared by the module descriptor, the capability record and the
// WebUI. The version is bumped whenever the wire behaviour changes: 0.9.0
// replaced the trust marker with a per-connection key proof and added the
// knock-gated listener plus the WebUI; 0.9.1 made strict (never-faulting)
// memory reads the default and moved the WebUI onto a page heartbeat; 0.10.0
// added touch injection into the real touchscreen input device; 0.11.1 removed
// the unstable kernel-level SYN gate and returned to the userspace gate; 0.11.2
// made touch injection multi-slot safe so independent fingers no longer clear
// BTN_TOUCH when one of them lifts; 0.11.3 enables TCP_NODELAY for low-latency
// touch streaming; 0.11.4 batches touch frames and omits redundant move fields
// to cut the per-sample kernel entry cost; 0.11.5 adds the trusted-device UDP
// move fast path; 0.11.6 bounds concurrent bridge sessions and throttles
// TCP move fallback so a lost fast path cannot exhaust server threads; 0.12.0
// adds the CPU-drawn SurfaceControl cursor that is skipped by screenshots;
// 0.12.12 refreshes the module descriptor and the WebUI copy.
#if SKPDB_CANARY
#define SKPDB_MODULE_NAME "SKPDB Canary Network Bridge"
#define SKPDB_MODULE_DESC "SKPDB Canary 调试桥：root shell、文件传输、无痕内存读写扫描、真实触摸屏注入、截图不可见光标与调试层。不 ptrace、不建 uinput、不挂载；端口按签名 knock 开启，每连接 HMAC 验证。"
#else
#define SKPDB_MODULE_NAME "SKPDB Network Bridge"
#define SKPDB_MODULE_DESC "SKPDB 调试桥：root shell、文件传输、无痕内存读写扫描、真实触摸屏注入与截图不可见光标。不 ptrace、不建 uinput、不挂载；端口按签名 knock 开启，每连接 HMAC 验证。"
#endif
#define SKPDB_MODULE_BASE_VERSION "0.12.17"
#define SKPDB_MODULE_VERSION SKPDB_MODULE_BASE_VERSION SKPDB_VERSION_SUFFIX "+" SKPDB_GIT_SHORT_COMMIT
#define SKPDB_MODULE_AUTHOR "雪松/CedarXuesong"
#define SKPDB_MODULE_ID32 "36c8b2dc7a8b4bb6a2b0e0ce54e05f31"

namespace skpdb::wireless {

constexpr char kModuleName[] = SKPDB_MODULE_NAME;
constexpr char kModuleVersion[] = SKPDB_MODULE_VERSION;
constexpr char kModuleId32[] = SKPDB_MODULE_ID32;

// The page beats into a file, the bridge daemon watches it and kills the WebUI
// loader when the beats stop: the page is a foreground tool, so it should not
// outlive a closed tab by more than a few seconds.
constexpr int kWebUiHeartbeatSeconds = 3;
// Before the first beat only this grace applies, so the link can be opened at
// leisure after the client printed it.
constexpr int kWebUiFirstBeatGraceSeconds = 120;

inline std::string WebUiBeatPath(const std::string& module_dir) {
    return module_dir + "/skpdb.webui.beat";
}

}  // namespace skpdb::wireless
