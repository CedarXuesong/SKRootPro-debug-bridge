#include <iostream>
#include <string>
#include "kernel_module_kit_umbrella.h"
extern "C" __attribute__((visibility("default"))) int skpdb_install_once(const char* key, const char* path) {
    std::string reason;
    auto err = skroot_env::install_module(key, path, reason);
    std::cout << "err=" << to_string(err) << "\nreason=" << reason << std::endl;
    return is_ok(err) ? 0 : 1;
}
