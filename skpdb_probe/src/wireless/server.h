#pragma once

#include <string>

namespace skpdb::wireless {

// Runs the bridge until the process is killed.
//
// Nothing listens on TCP until a peer proves it knows the root key by sending a
// signed UDP knock. The bridge then opens the TCP port for that address only,
// closes it again once the last session ends and the idle timeout expires, and
// keeps an administration channel on a filesystem socket inside
// `module_dir`, which is hidden and reachable by root only.
void RunBridge(const std::string& root_key, const std::string& module_dir);

}  // namespace skpdb::wireless
