#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace skpdb {

class Stream;

// A frame is a 4-byte big-endian length followed by that many payload bytes.
// Both the request and the response direction use the same envelope.
constexpr size_t kMaxFrame = 1024 * 1024;

// Reads one frame. Returns false on transport error or when the declared
// length exceeds kMaxFrame.
bool RecvFrame(Stream& stream, std::vector<uint8_t>& frame, int timeout_ms);

// Writes one frame. Returns false when the payload exceeds kMaxFrame.
bool SendFrame(Stream& stream, const std::vector<uint8_t>& frame, int timeout_ms);

}  // namespace skpdb
