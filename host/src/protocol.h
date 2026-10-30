#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "transport.h"

namespace skpdb {

constexpr size_t kMaxFrame = 1024 * 1024;

namespace opcode {

constexpr uint8_t kExec = 'E';
constexpr uint8_t kRead = 'R';
constexpr uint8_t kWrite = 'W';
constexpr uint8_t kList = 'L';
constexpr uint8_t kStat = 'T';
constexpr uint8_t kPing = 'K';
constexpr uint8_t kVersion = 'V';
constexpr uint8_t kClose = 'Q';
// Memory access: 'M' reads another process without ptrace, 'A' returns its
// /proc/<pid>/maps listing, 'G' reports which read backends are usable.
constexpr uint8_t kMemRead = 'M';
constexpr uint8_t kMemMaps = 'A';
constexpr uint8_t kMemInfo = 'G';
constexpr uint8_t kMemScatter = 'S';
constexpr uint8_t kMemDump = 'D';
constexpr uint8_t kMemResident = 'P';
// Writes, symbol lookups and pattern scans. The layouts mirror
// skpdb_probe/src/common/channel.cpp exactly.
constexpr uint8_t kMemWrite = 'X';
constexpr uint8_t kMemSymbols = 'F';
constexpr uint8_t kMemScan = 'Y';
// Touch injection: 'I' carries an action byte, a flags byte, a slot and the
// gesture geometry, and answers with a "key=value" report. Must match
// skpdb_probe/src/input/touch.h.
constexpr uint8_t kTouch = 'I';
// Framework gyro injection: 'Z' with an action byte (0 status, 1 install,
// 2 remove, 3 rate) and three big-endian floats for the rate action.
constexpr uint8_t kGyro = 'Z';
// Administration: 'B' carries an action byte followed by an optional body and
// answers with a "key=value" report.
constexpr uint8_t kAdmin = 'B';

}  // namespace opcode

// Actions carried as the second byte of an kAdmin request. Must match
// skpdb_probe/src/common/admin.h.
constexpr uint8_t kAdminInfo = 1;
constexpr uint8_t kAdminConfig = 2;
constexpr uint8_t kAdminWebUiStart = 3;
constexpr uint8_t kAdminWebUiStop = 4;

// Read modes accepted by kMemRead and kMemScatter. Must match the device side.
constexpr uint8_t kMemModeAuto = 0;       // strict: kernel path, never faults a page in
constexpr uint8_t kMemModeKernelOnly = 1; // same as auto, kept for old clients
constexpr uint8_t kMemModeUserspace = 2;  // skip the kernel path on purpose
constexpr uint8_t kMemModeFault = 3;      // allow the backends to fault pages in

// Ranges accepted in one kMemScatter request. Must match the device side.
constexpr uint32_t kMaxScatterRanges = 64;

// Write/scan limits and flags, matching skpdb_probe/src/common/memread.h.
constexpr uint32_t kMaxMemWrite = 512 * 1024 - 32;
constexpr uint32_t kMemScanMaxPattern = 64;
constexpr uint32_t kMemScanMaxHits = 256;
constexpr uint8_t kMemWriteAllowFault = 0x10;
constexpr uint8_t kMemWriteAllowShared = 0x20;
// Writing executable/read-only mappings (code patches). The kernel commits a
// private COW page, so the file on disk is untouched.
constexpr uint8_t kMemWriteAllowCode = 0x40;

// Page granularity of memdump/resident. Must match the device side.
constexpr uint64_t kMemPageBytes = 4096;

// Backend reported by the device for a successful read.
constexpr uint8_t kMemBackendNone = 0;
constexpr uint8_t kMemBackendKernel = 1;
constexpr uint8_t kMemBackendVmReadv = 2;
constexpr uint8_t kMemBackendProcMem = 3;

// Must match the device side: the frame holds opcode, status and backend bytes
// ahead of the data, so one kMemRead request moves at most kMaxFrame - 16 bytes.
constexpr uint32_t kMaxMemRead = 1024 * 1024 - 16;

const char* MemBackendName(uint8_t backend);
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

std::string HexEncode(const uint8_t* data, size_t size, bool upper = false);

// Returns the key entered by the user; only called when the device issues a
// challenge (i.e. no trust marker exists on the device).
using KeyProvider = std::function<std::string()>;

std::array<uint8_t, 32> HmacSha256(const std::string& key, const uint8_t* data, size_t size);

bool Authenticate(Transport& transport, const KeyProvider& key_provider, int timeout_ms,
                  std::string& error);

struct Response {
    uint8_t opcode = 0;
    std::vector<uint8_t> payload;
};

bool SendRequest(Transport& transport, const std::vector<uint8_t>& request, int timeout_ms);
bool ReadResponse(Transport& transport, uint8_t expected, Response& response, std::string& error,
                  int timeout_ms);

// Sends a request and returns its response; throws std::runtime_error on
// transport or device errors.
Response Request(Transport& transport, const std::vector<uint8_t>& request, uint8_t expected,
                 int timeout_ms);


}  // namespace skpdb
