// SKRoot module entry point for the network development bridge.

#include <unistd.h>

#include <chrono>
#include <thread>

#include <kernel_module_kit_umbrella.h>

#include "common/process.h"
#include "wireless/module_info.h"
#include "wireless/server.h"
#include "wireless/webui.h"

namespace {

// The kit runs the entry point before zygote; the bridge only needs the
// network stack, so it waits a little and then detaches.
constexpr int kStartupDelaySeconds = 12;

}  // namespace

int skroot_module_main(const char* root_key, const char* module_private_dir) {
    if (!root_key || !*root_key) {
        return 2;
    }

    pid_t child = skpdb::ForkDetachedChild();
    if (child < 0) {
        return 3;
    }
    if (child > 0) {
        return 0;
    }

    const std::string module_dir = module_private_dir ? module_private_dir : "";
    std::this_thread::sleep_for(std::chrono::seconds(kStartupDelaySeconds));
    skpdb::wireless::RunBridge(root_key, module_dir);
    _exit(0);
}

SKROOT_MODULE_NAME(SKPDB_MODULE_NAME)
SKROOT_MODULE_VERSION(SKPDB_MODULE_VERSION)
SKROOT_MODULE_DESC(SKPDB_MODULE_DESC)
SKROOT_MODULE_AUTHOR(SKPDB_MODULE_AUTHOR)
SKROOT_MODULE_ID32(SKPDB_MODULE_ID32)
SKROOT_MODULE_WEB_UI(skpdb::wireless::WebUiHandler)
