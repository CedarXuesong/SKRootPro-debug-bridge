#include "memory/kernel_channel.h"
#include "memory/memread.h"

#include <sys/mman.h>
#include <unistd.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <mutex>

namespace skpdb { std::string StrictGuardCheck(); }

namespace {
size_t page_size;
bool strict_write_attempted = false;
bool Resident(uint64_t address) {
    unsigned char status = 0;
    const auto base = address & ~(static_cast<uint64_t>(page_size) - 1);
    return mincore(reinterpret_cast<void*>(base), page_size, &status) == 0 && (status & 1);
}
void* Region(size_t pages) {
    void* p = mmap(nullptr, pages * page_size, PROT_READ | PROT_WRITE,
                   MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (p == MAP_FAILED) std::abort();
    madvise(p, pages * page_size, MADV_NOHUGEPAGE);
    madvise(p, pages * page_size, MADV_DONTNEED);
    return p;
}
}

// The real SKRoot backend cannot run on a desktop. This substitute preserves
// its false+empty result for a strict hole, and only accesses this process.
namespace skpdb::detail {
std::mutex& KernelMutex() { static std::mutex mutex; return mutex; }
bool KernelReadyLocked(std::string&) { return true; }
bool KernelWriteLocked(uint32_t, uint64_t, const std::vector<uint8_t>&,
                       bool no_fault, uint32_t& written, std::string& error) {
    strict_write_attempted = no_fault;
    written = 0;
    error = "offline kernel substitute refused the strict write";
    return false;
}
std::string Hex64(uint64_t v) {
    char text[32];
    std::snprintf(text, sizeof(text), "%llx", static_cast<unsigned long long>(v));
    return text;
}
bool KernelRead(uint32_t pid, uint64_t address, uint32_t length, bool no_fault,
                std::vector<uint8_t>& out, std::string& error, bool* unavailable) {
    out.clear();
    error.clear();
    if (unavailable) *unavailable = false;
    if (pid != static_cast<uint32_t>(getpid())) return false;
    uint32_t copied = 0;
    while (copied < length) {
        const uint64_t at = address + copied;
        if (no_fault && !Resident(at)) { error = "strict hole"; break; }
        const size_t amount = std::min<size_t>(length - copied, page_size - at % page_size);
        const auto* data = reinterpret_cast<const uint8_t*>(at);
        out.insert(out.end(), data, data + amount);
        copied += amount;
    }
    return !out.empty();
}
bool VmReadvRead(uint32_t, uint64_t, uint32_t, std::vector<uint8_t>&, std::string&) { return false; }
bool ProcMemRead(uint32_t, uint64_t, uint32_t, std::vector<uint8_t>&, std::string&) { return false; }
}

int main() {
    page_size = static_cast<size_t>(sysconf(_SC_PAGESIZE));
    if (page_size != skpdb::kMemPageBytes) return 2;
    const auto self = static_cast<uint32_t>(getpid());
    auto* region = static_cast<uint8_t*>(Region(1));
    const auto base = reinterpret_cast<uint64_t>(region);
    if (Resident(base)) return 3;
    const auto strict = skpdb::WriteProcessMemory(self, base, {0x42}, skpdb::kMemModeKernelOnly, 0);
    if (strict.written || Resident(base)) return 4;
    const auto automatic = skpdb::WriteProcessMemory(self, base, {0x42}, skpdb::kMemModeAuto, 0);
    if (!strict_write_attempted || automatic.written != 1 || !Resident(base) || region[0] != 0x42)
        return 5;
    std::cout << "REPRODUCED memory: Auto/flags=0 used real /proc/self/mem fallback; resident 0 -> 1; backend="
              << static_cast<int>(automatic.backend) << "\n";
    munmap(region, page_size);

    const std::string guard = skpdb::StrictGuardCheck();
    if (guard != "broken: the strict read moved bytes out of an untouched page") return 6;
    std::cout << "REPRODUCED guard: a correctly refused strict hole is reported as broken\n";

    region = static_cast<uint8_t*>(Region(16));
    region[3 * page_size] = 0x5a;
    const auto island = skpdb::ScanProcessMemory(self, reinterpret_cast<uint64_t>(region),
        reinterpret_cast<uint64_t>(region) + 16 * page_size, {0x5a}, {}, 16, skpdb::kMemModeAuto);
    if (!island.hits.empty() || !Resident(reinterpret_cast<uint64_t>(region + 3 * page_size))) return 7;
    std::cout << "REPRODUCED scan: exponential hole skipping missed the resident page at index 3\n";
    munmap(region, 16 * page_size);

    region = static_cast<uint8_t*>(Region(3));
    region[page_size - 1] = 0x41;
    region[2 * page_size] = 0x42;
    const auto gap = skpdb::ScanProcessMemory(self, reinterpret_cast<uint64_t>(region),
        reinterpret_cast<uint64_t>(region) + 3 * page_size, {0x41, 0x42}, {}, 16, skpdb::kMemModeAuto);
    if (gap.hits.size() != 1 || gap.hits[0] != reinterpret_cast<uint64_t>(region) + 2 * page_size - 1)
        return 8;
    std::cout << "REPRODUCED scan: carry joined bytes across an unreadable page into a false hit\n";
    munmap(region, 3 * page_size);
}
