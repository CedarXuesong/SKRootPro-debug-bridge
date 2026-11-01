#pragma once
#include <stdint.h>
#include <vector>
#include <set>
#include <array>
#include <limits>

namespace skpdb::gyro_layout {
// Verified QSH sensor::submit vector length calculation: (end-begin)/80.
// A different vendor ABI must fail closed rather than reuse this event layout.
inline bool HalEventStride80(const std::vector<uint8_t> &bytes) {
    constexpr uint32_t words[] = {0xb202e7e9, 0x9344fd08, 0xf29999a9, 0x9b097d05};
    for (size_t i = 0; i + sizeof(words) <= bytes.size(); i += 4) {
        bool match = true;
        for (size_t j = 0; j < 4; j++) {
            uint32_t word = 0;
            for (size_t k = 0; k < 4; k++)
                word |= uint32_t(bytes[i + j * 4 + k]) << (k * 8);
            match = match && word == words[j];
        }
        if (match)
            return true;
    }
    return false;
}
// Small data-flow recognizer: only verified argument-to-member stores in the
// constructor are accepted. Unknown instruction flow never supplies a guessed offset.
inline bool UidOffset(const std::vector<uint8_t> &bytes, uint32_t &result) {
    std::array<int64_t, 32> object;
    object.fill(INT64_MIN);
    object[0] = 0;
    std::array<bool, 32> uid{};
    uid[2] = true;
    std::set<uint32_t> candidates;
    for (size_t i = 0; i + 4 <= bytes.size(); i += 4) {
        uint32_t w = uint32_t(bytes[i]) | (uint32_t(bytes[i + 1]) << 8) | (uint32_t(bytes[i + 2]) << 16) |
                     (uint32_t(bytes[i + 3]) << 24);
        unsigned d = w & 31, n = (w >> 5) & 31, m = (w >> 16) & 31;
        if (w == 0xd65f03c0 || w == 0xd65f0bff)
            break;
        if ((w & 0xfc000000) == 0x94000000) {
            for (unsigned r = 0; r < 19; r++) {
                object[r] = INT64_MIN;
                uid[r] = false;
            }
            continue;
        }
        if ((w & 0x7fe0ffe0) == 0x2a0003e0) {
            uid[d] = uid[m];
            object[d] = (w >> 31) ? object[m] : INT64_MIN;
            continue;
        }
        if ((w & 0xff000000) == 0x91000000 || (w & 0xff000000) == 0xd1000000) {
            int64_t imm = (w >> 10) & 4095;
            if (w & (1 << 22))
                imm <<= 12;
            if (w & (1u << 30))
                imm = -imm;
            object[d] = object[n] == INT64_MIN ? INT64_MIN : object[n] + imm;
            uid[d] = false;
            continue;
        }
        if ((w & 0xffc00000) == 0xb9000000 && object[n] != INT64_MIN && uid[d]) {
            int64_t at = object[n] + int64_t((w >> 10) & 4095) * 4;
            if (at >= 0 && at < 2048)
                candidates.insert(uint32_t(at));
        }
        if ((w & 0xffc00000) == 0x29000000 && object[n] != INT64_MIN) {
            int32_t imm = int32_t((w >> 15) & 127);
            if (imm & 64)
                imm -= 128;
            int64_t at = object[n] + imm * 4;
            unsigned second = (w >> 10) & 31;
            if (uid[d] && at >= 0 && at < 2048)
                candidates.insert(uint32_t(at));
            if (uid[second] && at + 4 >= 0 && at + 4 < 2048)
                candidates.insert(uint32_t(at + 4));
        }
        // Pre/post-index scalar loads/stores update their object base.
        if ((w & 0x3b200000) == 0x38000000 && ((w >> 10) & 3) != 0 && object[n] != INT64_MIN) {
            int32_t imm = (w >> 12) & 511;
            if (imm & 256)
                imm -= 512;
            object[n] += imm;
        }
    }
    if (candidates.size() != 1)
        return false;
    result = *candidates.begin();
    return true;
}
inline bool Branch(uint64_t from, uint64_t to, uint32_t &word, bool link = false) {
    const int64_t delta = int64_t(to) - int64_t(from);
    if (delta % 4 || delta < -(1ll << 27) || delta >= (1ll << 27))
        return false;
    word = (link ? 0x94000000u : 0x14000000u) | (uint32_t(delta >> 2) & 0x03ffffff);
    return true;
}
inline bool Prologue(uint32_t first, uint32_t displaced) {
    return (first == 0xd503233f || first == 0xd503245f) && ((displaced & 0xffc003ff) == 0xd10003ff);
}
} // namespace skpdb::gyro_layout
