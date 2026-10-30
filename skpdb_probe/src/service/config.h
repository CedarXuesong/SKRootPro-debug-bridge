#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

// Settings that survive a reboot. The file lives in the module private
// directory, which SKRoot hides, and never holds the root key itself.
struct BridgeConfig {
    // TCP port the bridge opens after a valid knock. Closed unless a peer has
    // proven possession of the key, so the port is absent from port scans.
    uint16_t service_port = 39876;
    // UDP port the knock datagrams are addressed to. Nothing is bound there:
    // a raw socket only receives a copy, so the port never appears as open.
    uint16_t knock_port = 39877;
    // Seconds without a session after which the TCP port is closed again.
    uint32_t idle_timeout_seconds = 30;
    // Accepted clock skew, in seconds, for signed UDP knocks.
    uint32_t auth_window_seconds = 30;
    // Keeps the service port open all the time instead of waiting for a knock.
    // Needed when the module is reached through a TCP-only port forwarder
    // (frp, ssh -L), because the UDP knock cannot be delivered there. Costs the
    // "the port does not exist" property on the device's own network; the
    // per-connection key challenge still applies.
    bool always_listen = false;
};

std::string BridgeConfigPath(const std::string& module_dir);

// Missing or malformed values fall back to the defaults field by field, so a
// truncated file cannot keep the bridge from starting.
BridgeConfig LoadBridgeConfig(const std::string& module_dir);

// Writes through a temporary file and renames it into place.
bool SaveBridgeConfig(const std::string& module_dir, const BridgeConfig& config,
                      std::string& error);

// Parses "key=value;key=value" updates and applies the recognised ones.
// Returns false and fills `error` when a value is out of range.
bool ApplyConfigUpdate(BridgeConfig& config, const std::string& update, std::string& error);

}  // namespace skpdb
