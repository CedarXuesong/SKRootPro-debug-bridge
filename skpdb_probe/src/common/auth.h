#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

class Stream;

// Runs the challenge/response exchange on `stream`.
//
// The device always sends 32 random bytes and compares the client proof in
// constant time. There is no trust marker and no session resumption: every
// connection must present the root key, and a connection that does not is left
// without a single response byte.
bool Authenticate(Stream& stream, const std::string& root_key, int timeout_ms);

}  // namespace skpdb
