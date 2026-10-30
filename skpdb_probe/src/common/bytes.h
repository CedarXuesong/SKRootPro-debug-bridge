#pragma once

#include <cstdint>
#include <vector>

namespace skpdb {

// Wire integers are big-endian. Every field that crosses the link (frame
// length, file offset, transfer size, stat fields) uses these helpers.

inline void PutU32(std::vector<uint8_t>& output, uint32_t value) {
    output.push_back(static_cast<uint8_t>(value >> 24));
    output.push_back(static_cast<uint8_t>(value >> 16));
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}

inline void PutU64(std::vector<uint8_t>& output, uint64_t value) {
    for (int shift = 7; shift >= 0; --shift) {
        output.push_back(static_cast<uint8_t>(value >> (shift * 8)));
    }
}

inline uint32_t GetU32(const uint8_t* data) {
    return (static_cast<uint32_t>(data[0]) << 24) | (static_cast<uint32_t>(data[1]) << 16) |
           (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
}

inline uint64_t GetU64(const uint8_t* data) {
    uint64_t value = 0;
    for (int index = 0; index < 8; ++index) {
        value = (value << 8) | data[index];
    }
    return value;
}

}  // namespace skpdb
