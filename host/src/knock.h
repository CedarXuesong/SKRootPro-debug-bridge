#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace skpdb {

// The device only opens its TCP listener for peers that proved they hold the
// root key, so every connection starts with one signed UDP datagram sent to the
// knock port. Nothing is bound to that port on the device: a scanner sees no
// listener, and the module stays invisible until a valid knock arrives.
//
// Layout (60 bytes, big-endian), must match skpdb_probe/src/common/knock.h:
//   [0..8)    "SKPDBK01"
//   [8]       version (1)
//   [9]       flags (0)
//   [10..12)  reserved (0)
//   [12..20)  unix seconds
//   [20..28)  nonce
//   [28..60)  HMAC-SHA256(key, bytes [0..28))
constexpr size_t kKnockSize = 60;
constexpr uint16_t kDefaultKnockPort = 39877;

std::vector<uint8_t> BuildKnockPacket(const std::string& key, uint64_t nonce, uint64_t timestamp);

// Sends the datagram `attempts` times; UDP gives no confirmation, so the caller
// retries the TCP connection afterwards.
bool SendKnock(const std::string& host, uint16_t port, const std::string& key, int attempts,
               std::string& error);

}  // namespace skpdb
