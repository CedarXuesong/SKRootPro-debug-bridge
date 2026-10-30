#pragma once

#include <string>

#include "common/channel.h"

namespace skpdb {

class Stream;

// Per-session parameters. The protocol is transport independent; only the
// capability record and the administration handler differ.
struct SessionConfig {
    std::string capabilities;
    // Serves `[B]` requests; empty when the transport has no administration.
    AdminRequestHandler admin;
};

// Authenticates, then serves framed requests until the client sends kClose,
// the peer disconnects, or an I/O operation exceeds `timeout_ms`
// (negative waits indefinitely). The stream descriptor is closed by the
// caller through the Stream destructor.
void ServeSession(Stream& stream, const std::string& root_key, const SessionConfig& config,
                  int timeout_ms);

}  // namespace skpdb
