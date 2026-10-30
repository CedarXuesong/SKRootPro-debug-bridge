#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace skpdb {

// Backend identifiers travel on the wire (byte 3 of a kMemRead response). They
// are part of the protocol and must never be renumbered.
constexpr uint8_t kMemBackendNone = 0;
constexpr uint8_t kMemBackendKernel = 1;   // access_process_vm() inside the kernel
constexpr uint8_t kMemBackendVmReadv = 2;  // process_vm_readv()
constexpr uint8_t kMemBackendProcMem = 3;  // /proc/<pid>/mem

// Request modes for kMemRead. The default is strict on purpose: a read must not
// leave a mark in the target, and faulting a page in is exactly such a mark (it
// shows up as a minor fault and as rss growth, both readable by the target
// itself). Only kMemModeFault and kMemModeUserspace can fault.
constexpr uint8_t kMemModeAuto = 0;        // strict: kernel path, holes reported
constexpr uint8_t kMemModeKernelOnly = 1;  // same as auto, kept for old clients
constexpr uint8_t kMemModeUserspace = 2;   // skip the kernel path on purpose
constexpr uint8_t kMemModeFault = 3;       // allow the backends to fault pages in

// Largest amount a single request may move. The response carries the opcode,
// the status byte and the backend byte ahead of the payload, so this stays
// inside one 1 MiB frame. Clients split longer reads themselves.
constexpr uint32_t kMaxMemRead = 1024 * 1024 - 16;

struct MemReadResult {
    std::vector<uint8_t> data;
    // kMemBackendNone means no backend produced bytes; `error` says why.
    uint8_t backend = kMemBackendNone;
    std::string error;
};

// Reads `length` bytes of another process' address space. In kMemModeAuto the
// kernel backend is used exclusively for as long as it stays healthy, which is
// what makes the read traceless: no ptrace, no TracerPid, no signal, no
// /proc/<pid>/mem descriptor in this process.
MemReadResult ReadProcessMemory(uint32_t pid, uint64_t address, uint32_t length, uint8_t mode);

struct MemMapsResult {
    std::string text;
    std::string error;
};

// Returns /proc/<pid>/maps verbatim. The target process is not touched: this
// reads a proc file owned by the module process, exactly like the kRead op.
MemMapsResult ListProcessMaps(uint32_t pid);

// One page is the granularity of the dump and of the residency bitmap; it is
// also what the kernel path lands on when a run has to be split at a hole.
constexpr uint64_t kMemPageBytes = 4096;

// A run of readable bytes found by DumpResidentRun(). `data` covers
// [data_start, data_start + data.size()); `next_cursor` is where the caller
// should continue, which is beyond the hole when the run ended at one.
struct MemDumpChunk {
    uint64_t data_start = 0;
    uint64_t next_cursor = 0;
    std::vector<uint8_t> data;
    std::string error;
};

// Walks [cursor, end) and returns the first contiguous run of resident bytes,
// capped at `max_bytes`. Holes are skipped, never faulted in: the walk uses the
// same strict path as kMemRead.
MemDumpChunk DumpResidentRun(uint32_t pid, uint64_t cursor, uint64_t end, uint32_t max_bytes,
                             uint8_t mode);

// Residency bitmap for `pages` pages starting at `start`, one bit per page,
// least significant bit of byte 0 = first page.
struct MemResidentMap {
    uint32_t pages = 0;
    std::vector<uint8_t> bitmap;
    std::string error;
};

MemResidentMap ProbeResidentPages(uint32_t pid, uint64_t start, uint32_t pages, uint8_t mode);

// Multi-line "key=value" report for the kMemInfo request. Every backend is
// probed live against this process, so the report is the verification record
// for a device: probe_pid/probe_address/probe_hex can be read back over the
// wire and compared byte for byte.
std::string MemoryDiagnostics();

// Optional flags for a kMemWrite request. Both are off by default because each
// one leaves a mark the target can notice on its own: faulting a page in and
// writing a MAP_SHARED file-backed page (page cache) are visible beyond the
// target's private address space.
constexpr uint8_t kMemWriteAllowFault = 0x10;
constexpr uint8_t kMemWriteAllowShared = 0x20;
// Patch an executable/read-only mapping. FOLL_FORCE commits a private COW page,
// so the mapped file on disk is never modified.
constexpr uint8_t kMemWriteAllowCode = 0x40;

// Largest payload a single kMemWrite request may carry. The request puts the
// opcode, pid, address, length, mode and flags ahead of the bytes.
constexpr uint32_t kMaxMemWrite = 512 * 1024 - 32;

struct MemWriteResult {
    uint32_t written = 0;
    uint8_t backend = kMemBackendNone;
    std::string error;
    // True when the range landed in a MAP_SHARED mapping, i.e. other processes
    // see the change too.
    bool shared = false;
};

// Writes `data` into another process. Without kMemWriteAllowFault only pages
// that are already there are written: a write that would have to fault a page
// in is refused, because that is a page-table and rss change in the target.
MemWriteResult WriteProcessMemory(uint32_t pid, uint64_t address,
                                  const std::vector<uint8_t>& data, uint8_t mode,
                                  uint8_t flags);

// Byte-pattern scan over [start, end). Only pages that are resident are
// searched, so a scan never faults anything in; `skipped` counts the bytes that
// had to be passed over for that reason.
constexpr uint32_t kMemScanMaxPattern = 64;
constexpr uint32_t kMemScanMaxHits = 256;

struct MemScanResult {
    std::vector<uint64_t> hits;
    uint64_t scanned = 0;
    uint64_t skipped = 0;
    std::string error;
};

// A mask of the same length as the pattern selects the bits that have to match:
// 0xff means "this byte must be equal", 0x00 means "any byte". An empty mask
// means the whole pattern is fixed.
MemScanResult ScanProcessMemory(uint32_t pid, uint64_t start, uint64_t end,
                                const std::vector<uint8_t>& pattern,
                                const std::vector<uint8_t>& mask, uint32_t max_hits,
                                uint8_t mode);

}  // namespace skpdb
