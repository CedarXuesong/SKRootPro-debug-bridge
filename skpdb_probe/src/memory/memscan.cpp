#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

#include "memory/memread.h"

namespace skpdb {


namespace {

// Finds the first readable page at or after `cursor`. Walking a hole one page at
// a time costs one kernel entry per page, and an untouched Android heap reserves
// hundreds of megabytes; probing with doubling steps turns that into a handful
// of entries per hole, then a short binary search lands on the boundary.
uint64_t SkipHole(uint32_t pid, uint64_t cursor, uint64_t end, uint8_t mode,
                  uint64_t& skipped) {
    auto readable = [&](uint64_t page) {
        MemReadResult probe = ReadProcessMemory(pid, page, 16, mode);
        return !probe.data.empty();
    };
    uint64_t step = kMemPageBytes;
    uint64_t low = cursor;               // known unreadable
    uint64_t high = 0;                   // first readable page found, or 0
    while (step < end - cursor) {
        const uint64_t probe = cursor + step;
        if (probe >= end) {
            break;
        }
        if (readable(probe)) {
            high = probe;
            break;
        }
        low = probe;
        step *= 2;
    }
    if (high == 0) {
        skipped += end - cursor;
        return end;
    }
    while (high > low + kMemPageBytes) {
        const uint64_t middle = ((low + high) / 2) & ~(kMemPageBytes - 1);
        if (middle <= low || middle >= high) {
            break;
        }
        if (readable(middle)) {
            high = middle;
        } else {
            low = middle;
        }
    }
    skipped += high - cursor;
    return high;
}

}  // namespace

MemScanResult ScanProcessMemory(uint32_t pid, uint64_t start, uint64_t end,
                                const std::vector<uint8_t>& pattern,
                                const std::vector<uint8_t>& mask, uint32_t max_hits,
                                uint8_t mode) {
    MemScanResult result;
    if (pattern.empty() || pattern.size() > kMemScanMaxPattern) {
        result.error = "pattern must be 1.." + std::to_string(kMemScanMaxPattern) + " bytes";
        return result;
    }
    if (!mask.empty() && mask.size() != pattern.size()) {
        result.error = "mask must be the same length as the pattern";
        return result;
    }
    const bool wildcard = !mask.empty();
    auto matches = [&](const uint8_t* haystack) {
        for (size_t index = 0; index < pattern.size(); ++index) {
            const uint8_t bits = wildcard ? mask[index] : 0xff;
            if ((haystack[index] & bits) != (pattern[index] & bits)) {
                return false;
            }
        }
        return true;
    };
    if (end <= start) {
        result.error = "end must be greater than start";
        return result;
    }
    if (max_hits == 0 || max_hits > kMemScanMaxHits) {
        result.error = "max_hits must be 1.." + std::to_string(kMemScanMaxHits);
        return result;
    }

    const uint32_t chunk = std::min<uint32_t>(kMaxMemRead, 256 * 1024);
    std::vector<uint8_t> carry;
    uint64_t cursor = start;
    while (cursor < end) {
        const uint32_t want = static_cast<uint32_t>(
            std::min<uint64_t>(chunk, end - cursor));
        MemReadResult read = ReadProcessMemory(pid, cursor, want, mode);
        if (read.data.empty()) {
            // A hole: jump past it instead of walking it page by page.
            uint64_t next = SkipHole(pid, cursor, end, mode, result.skipped);
            if (next >= end) {
                break;
            }
            carry.clear();
            cursor = next;
            if (!read.error.empty()) {
                result.error = read.error;
            }
            continue;
        }
        std::vector<uint8_t> haystack;
        haystack.reserve(carry.size() + read.data.size());
        haystack.insert(haystack.end(), carry.begin(), carry.end());
        haystack.insert(haystack.end(), read.data.begin(), read.data.end());
        const uint64_t haystack_address = cursor - carry.size();
        for (size_t offset = 0; offset + pattern.size() <= haystack.size(); ++offset) {
            if (!matches(haystack.data() + offset)) {
                continue;
            }
            result.hits.push_back(haystack_address + offset);
            if (result.hits.size() >= max_hits) {
                break;
            }
        }
        result.scanned += read.data.size();
        const uint64_t consumed = cursor + read.data.size();
        if (result.hits.size() >= max_hits) {
            cursor = consumed;
            break;
        }
        if (!read.error.empty() && read.data.size() < want) {
            // A short run ended at a hole: keep the tail for the overlap and move
            // past the gap instead of retrying the same address.
            const size_t keep = std::min(pattern.size() - 1, haystack.size());
            carry.assign(haystack.end() - keep, haystack.end());
            cursor = SkipHole(pid, consumed, end, mode, result.skipped);
            continue;
        }
        const size_t keep = std::min(pattern.size() - 1, haystack.size());
        carry.assign(haystack.end() - keep, haystack.end());
        cursor = consumed;
    }
    return result;
}

}  // namespace skpdb
