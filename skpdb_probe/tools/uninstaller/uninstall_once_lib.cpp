#include <iostream>
#include <string>
#include "kernel_module_kit_umbrella.h"
extern "C" __attribute__((visibility("default"))) int skpdb_uninstall_once(const char* key, const char* module_id) {
    auto err = skroot_env::uninstall_module(key, module_id);
    std::cout << "err=" << to_string(err) << std::endl;
    return is_ok(err) ? 0 : 1;
}
