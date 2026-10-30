#include <iostream>
#include <vector>
#include "kernel_module_kit_umbrella.h"
extern "C" int skpdb_list_modules(const char* key, const char*) {
    std::vector<skroot_env::module_record> modules;
    auto err = skroot_env::get_all_modules_list(key, modules);
    std::cout << "err=" << to_string(err) << " count=" << modules.size() << std::endl;
    for (const auto& m : modules) std::cout << m.desc.name << " | " << m.desc.version << " | state=" << static_cast<unsigned>(m.state) << " | " << m.desc.id32 << std::endl;
    std::string log;
    auto le = skroot_env::read_skroot_log(key, log);
    std::cout << "logerr=" << to_string(le) << "\n" << log << std::endl;
    return is_ok(err) ? 0 : 1;
}
