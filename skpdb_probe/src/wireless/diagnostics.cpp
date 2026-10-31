#include "wireless/diagnostics.h"
#include <sys/utsname.h>
#include <sys/system_properties.h>
#include <ifaddrs.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <sstream>
#include <set>
#include "input/touch.h"
#include "memory/memread.h"
#include "display/cursor_surface.h"
#include "common/build_info.h"

namespace skpdb::wireless {
namespace {
std::string Property(const char* name) {
    char text[PROP_VALUE_MAX] = {};
    __system_property_get(name, text); return text;
}
std::string Prefix(const std::string& prefix, const std::string& report, bool memory = false) {
    std::string result, line; std::istringstream input(report);
    while (std::getline(input, line)) {
        if (line.find('=') == std::string::npos) continue;
        if (memory && (line.rfind("probe_", 0) == 0 || line.rfind("scratch_address=", 0) == 0)) continue;
        if (line.rfind("input_dev=", 0) == 0 || line.rfind("input_event=", 0) == 0) continue;
        result += prefix + line + "\n";
    }
    return result;
}
}
std::string SystemReport() {
    utsname name{}; uname(&name);
    std::string text = "system.android=" + Property("ro.build.version.release") +
        "\nsystem.sdk=" + Property("ro.build.version.sdk") + "\nsystem.kernel=" + name.release +
        "\nsystem.arch=" + name.machine + "\nsystem.manufacturer=" + Property("ro.product.manufacturer") +
        "\nsystem.model=" + Property("ro.product.model") +
        "\nsystem.btf_file=" + (access("/sys/kernel/btf/vmlinux", R_OK) == 0 ? "readable\n" : "unavailable\n");
    ifaddrs* addresses = nullptr;
    if (getifaddrs(&addresses) == 0) {
        std::set<std::string> seen;
        for (auto* a = addresses; a; a = a->ifa_next) {
            if (!a->ifa_addr || a->ifa_addr->sa_family != AF_INET) continue;
            char ip[INET_ADDRSTRLEN] = {};
            auto* addr = reinterpret_cast<sockaddr_in*>(a->ifa_addr);
            inet_ntop(AF_INET, &addr->sin_addr, ip, sizeof(ip));
            std::string row = std::string(a->ifa_name) + " " + ip;
            if (seen.insert(row).second) text += "network." + std::string(a->ifa_name) + "=" + ip + "\n";
        }
        freeifaddrs(addresses);
    }
    return text;
}
std::string CapabilityDiagnostics() {
    std::string text = SystemReport();
    const auto touch = TouchDiagnostics();
    text += "touch.check=" + std::string(touch.ok ? "ready\n" : "failed\n");
    text += Prefix("touch.", touch.report);
    if (!touch.ok) text += "touch.error=" + touch.error + "\n";
    text += Prefix("overlay.", CursorSurfaceReport());
    text += Prefix("memory.", MemoryDiagnostics(), true);
    text += "capability.shell=exec\ncapability.files=list,read,write\ncapability.touch=sampled,batch,dynamic-client-slots\n";
    text += "capability.look=additive-multitouch\ncapability.fast=SKF2\ncapability.hud=" SKPDB_HUD_CAPABILITY "\n";
    return text;
}
}
