#include "memory/memread.h"

#include <sys/mman.h>
#include <unistd.h>

#include <algorithm>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>

#include "common/frame.h"
#include "kernel_module_kit_umbrella.h"
#include "memory/kernel_channel.h"

namespace skpdb {

using detail::Hex;
using detail::Hex64;
using detail::KernelMutex;
using detail::KernelRead;
using detail::KernelSelfTestLocked;
using detail::kStageBytes;
using detail::KernelReadLocked;
using detail::kProbeBytes;
using detail::kProbeText;
using detail::KernelReadyLocked;
using detail::ProbeAddress;
using detail::ProcMemRead;
using detail::ScratchAddress;
using detail::VmReadvRead;

std::string CompareWithProbe(const std::vector<uint8_t>& bytes) {
    if (bytes.size() != kProbeBytes) {
        return "short";
    }
    return std::memcmp(bytes.data(), kProbeText, kProbeBytes) == 0 ? "match" : "mismatch";
}

// Two counters the target can read about itself: minor faults and resident
// pages. A strict read must leave both untouched, otherwise the read is
// observable from inside the target process.
struct SelfCounters {
    uint64_t minor_faults = 0;
    uint64_t resident_pages = 0;
    bool valid = false;
};

bool ReadSelfCounters(SelfCounters& out) {
    char buffer[1024];
    // Per-thread faults on purpose: /proc/self/stat counts the whole process,
    // and the daemon's other threads (gate loop, knock receiver, sessions) can
    // fault at any moment, which swamps the window we care about. The read runs
    // on this thread, so its own minflt is the honest counter.
    char path[64];
    std::snprintf(path, sizeof(path), "/proc/self/task/%d/stat", static_cast<int>(gettid()));
    int descriptor = open(path, O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) {
        return false;
    }
    const ssize_t count = read(descriptor, buffer, sizeof(buffer) - 1);
    close(descriptor);
    if (count <= 0) {
        return false;
    }
    buffer[count] = '\0';
    // comm can contain spaces and parentheses, so the fields start after the
    // last ')'. The first field after it is state, a single character; minflt
    // is field 10 overall, the 7th numeric field after state.
    const char* cursor = std::strrchr(buffer, ')');
    if (cursor == nullptr) {
        return false;
    }
    ++cursor;
    while (*cursor == ' ') {
        ++cursor;
    }
    ++cursor;  // state
    uint64_t value = 0;
    for (int field = 4; field <= 10; ++field) {
        while (*cursor == ' ') {
            ++cursor;
        }
        char* end = nullptr;
        value = std::strtoull(cursor, &end, 10);
        if (end == cursor) {
            return false;
        }
        cursor = end;
    }
    out.minor_faults = value;

    descriptor = open("/proc/self/statm", O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) {
        return false;
    }
    const ssize_t page_count = read(descriptor, buffer, sizeof(buffer) - 1);
    close(descriptor);
    if (page_count <= 0) {
        return false;
    }
    buffer[page_count] = '\0';
    char* end = nullptr;
    (void)std::strtoull(buffer, &end, 10);  // total program size, unused
    out.resident_pages = std::strtoull(end, nullptr, 10);
    out.valid = true;
    return true;
}

// Exercises both halves of the contract inside a 1 MiB mapping of this process:
// a strict read of the touched page must succeed, a strict read of an untouched
// page must fail instead of faulting it in, and the fault mode must then be able
// to read exactly that page.
std::string StrictGuardCheck() {
    const size_t span = 1024 * 1024;
    void* region = mmap(nullptr, span, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (region == MAP_FAILED) {
        return std::string("unavailable (mmap: ") + std::strerror(errno) + ")";
    }
    auto* bytes = static_cast<volatile uint8_t*>(region);
    bytes[0] = 1;  // bring the first page in, leave the second half untouched
    const uint64_t base = reinterpret_cast<uint64_t>(region);
    const uint64_t untouched = base + 512 * 1024;
    const uint32_t self = static_cast<uint32_t>(getpid());

    // KernelRead reports success when the kernel entry ran, even if it moved no
    // bytes, so every step is judged by the byte count.
    std::vector<uint8_t> out;
    std::string error;
    bool unavailable = false;
    const bool resident_ok =
        KernelRead(self, base, 64, true, out, error, &unavailable) && out.size() == 64;
    const bool strict_hole_refused =
        KernelRead(self, untouched, 64, true, out, error, &unavailable) && out.empty();
    const bool fault_hole_ok =
        KernelRead(self, untouched, 64, false, out, error, &unavailable) && out.size() == 64;
    munmap(region, span);

    if (!resident_ok) {
        return "broken: the resident page was refused (" + error + ")";
    }
    if (!strict_hole_refused) {
        return "broken: the strict read moved bytes out of an untouched page";
    }
    if (!fault_hole_ok) {
        return "broken: the fault mode could not read the untouched page";
    }
    return "ok";
}

MemReadResult ReadProcessMemory(uint32_t pid, uint64_t address, uint32_t length, uint8_t mode) {
    MemReadResult result;
    if (pid == 0) {
        result.error = "pid 0 is the idle task; pass a real pid";
        return result;
    }
    if (length == 0) {
        result.error = "length must be at least 1";
        return result;
    }
    if (address > UINT64_MAX - length) {
        result.error = "range wraps the address space";
        return result;
    }
    length = std::min(length, kMaxMemRead);

    std::string kernel_error;
    const bool strict = mode == kMemModeAuto || mode == kMemModeKernelOnly;
    if (strict) {
        // The strict modes never fault a page in: a hole is reported, not
        // filled. They deliberately do not fall back to the userspace
        // backends, because those reach the page through handle_mm_fault.
        std::vector<uint8_t> bytes;
        bool unavailable = false;
        if (KernelRead(pid, address, length, true, bytes, kernel_error, &unavailable)) {
            result.backend = kMemBackendKernel;
            result.data = std::move(bytes);
            return result;
        }
        result.error = kernel_error;
        if (unavailable) {
            result.error +=
                "; strict mode never falls back - use mode=fault if a page fault in the "
                "target is acceptable";
        }
        return result;
    }

    if (mode == kMemModeFault) {
        std::vector<uint8_t> bytes;
        if (KernelRead(pid, address, length, false, bytes, kernel_error, nullptr)) {
            result.backend = kMemBackendKernel;
            result.data = std::move(bytes);
            return result;
        }
    }

    std::string vm_error;
    std::vector<uint8_t> bytes;
    if (VmReadvRead(pid, address, length, bytes, vm_error)) {
        result.backend = kMemBackendVmReadv;
        result.data = std::move(bytes);
        return result;
    }

    std::string mem_error;
    if (ProcMemRead(pid, address, length, bytes, mem_error)) {
        result.backend = kMemBackendProcMem;
        result.data = std::move(bytes);
        return result;
    }

    // Report the failure of the path the caller actually asked for first.
    result.error = kernel_error.empty() ? vm_error + "; " + mem_error : kernel_error;
    return result;
}

MemDumpChunk DumpResidentRun(uint32_t pid, uint64_t cursor, uint64_t end, uint32_t max_bytes,
                             uint8_t mode) {
    MemDumpChunk chunk;
    chunk.data_start = cursor;
    chunk.next_cursor = cursor;
    if (end <= cursor || max_bytes == 0) {
        return chunk;
    }
    // A pure splice of resident runs, never a fault: ReadProcessMemory in the
    // strict modes reports a hole instead of filling it.
    constexpr uint64_t kScanWindow = 16 * 1024 * 1024;
    uint64_t position = cursor;
    bool have_data = false;
    while (position < end && chunk.data.size() < max_bytes) {
        const uint64_t room = max_bytes - chunk.data.size();
        const uint64_t span = std::min<uint64_t>({end - position, room, kStageBytes});
        const uint32_t want = static_cast<uint32_t>(span);
        MemReadResult read = ReadProcessMemory(pid, position, want, mode);
        if (!read.data.empty()) {
            if (!have_data) {
                chunk.data_start = position;
                have_data = true;
            }
            chunk.data.insert(chunk.data.end(), read.data.begin(), read.data.end());
            position += read.data.size();
            chunk.next_cursor = position;
            if (read.data.size() < want) {
                // The mapping ended mid-window; the next page is a hole.
                break;
            }
            continue;
        }

        // Hole at `position`: jump to the next page that is readable, scanning
        // at most kScanWindow ahead so one request cannot stall for long.
        uint64_t page = (position + kMemPageBytes - 1) & ~(kMemPageBytes - 1);
        const uint64_t limit = std::min<uint64_t>(end, position + kScanWindow);
        bool found = false;
        while (page < limit) {
            MemReadResult probe = ReadProcessMemory(pid, page, static_cast<uint32_t>(kMemPageBytes),
                                                    mode);
            if (!probe.data.empty()) {
                found = true;
                break;
            }
            page += kMemPageBytes;
        }
        if (!found) {
            chunk.next_cursor = limit;
            break;
        }
        if (have_data) {
            // End this run at the hole; the caller resumes at `page`.
            chunk.next_cursor = page;
            break;
        }
        position = page;
        chunk.data_start = position;
        chunk.next_cursor = position;
    }
    if (chunk.data.empty()) {
        chunk.data_start = chunk.next_cursor;
    }
    return chunk;
}

MemResidentMap ProbeResidentPages(uint32_t pid, uint64_t start, uint32_t pages, uint8_t mode) {
    MemResidentMap map;
    map.pages = pages;
    map.bitmap.assign((pages + 7) / 8, 0);
    if (pages == 0) {
        map.error = "pages must be at least 1";
        return map;
    }
    // Try 16 pages at a time first: a fully resident block costs one kernel
    // entry instead of sixteen, and dumps usually cover resident code or data.
    constexpr uint32_t kBlockPages = 16;
    uint32_t done = 0;
    while (done < pages) {
        const uint32_t take = std::min(kBlockPages, pages - done);
        const uint64_t address = start + static_cast<uint64_t>(done) * kMemPageBytes;
        MemReadResult block = ReadProcessMemory(
            pid, address, static_cast<uint32_t>(take * kMemPageBytes), mode);
        if (!block.data.empty() && block.data.size() == take * kMemPageBytes) {
            for (uint32_t index = 0; index < take; ++index) {
                const uint32_t page = done + index;
                map.bitmap[page / 8] |= static_cast<uint8_t>(1u << (page % 8));
            }
            done += take;
            continue;
        }
        for (uint32_t index = 0; index < take; ++index) {
            MemReadResult page = ReadProcessMemory(
                pid, address + static_cast<uint64_t>(index) * kMemPageBytes,
                static_cast<uint32_t>(kMemPageBytes), mode);
            if (!page.data.empty()) {
                const uint32_t index_page = done + index;
                map.bitmap[index_page / 8] |= static_cast<uint8_t>(1u << (index_page % 8));
            }
        }
        done += take;
    }
    return map;
}

MemMapsResult ListProcessMaps(uint32_t pid) {
    MemMapsResult result;
    if (pid == 0) {
        result.error = "pid 0 is the idle task; pass a real pid";
        return result;
    }
    const std::string path = "/proc/" + std::to_string(pid) + "/maps";
    int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        result.error = path + ": " + std::strerror(errno);
        return result;
    }
    const size_t limit = kMaxFrame - 8;
    std::vector<char> buffer(16 * 1024);
    while (result.text.size() < limit) {
        ssize_t count = read(fd, buffer.data(), buffer.size());
        if (count < 0) {
            if (errno == EINTR) {
                continue;
            }
            result.error = path + ": " + std::strerror(errno);
            close(fd);
            result.text.clear();
            return result;
        }
        if (count == 0) {
            break;
        }
        result.text.append(buffer.data(), static_cast<size_t>(count));
    }
    close(fd);
    if (result.text.size() > limit) {
        result.text.resize(limit);
    }
    return result;
}

std::string MemoryDiagnostics() {
    const uint32_t self = static_cast<uint32_t>(getpid());
    std::string report;
    char line[256];

    std::snprintf(line, sizeof(line), "probe_pid=%u\n", self);
    report += line;
    std::snprintf(line, sizeof(line), "probe_address=0x%llx\n",
                  static_cast<unsigned long long>(ProbeAddress()));
    report += line;
    std::snprintf(line, sizeof(line), "probe_bytes=%u\n", kProbeBytes);
    report += line;
    report += "probe_hex=" + Hex(reinterpret_cast<const uint8_t*>(kProbeText), kProbeBytes) + "\n";
    report += "scratch_address=0x" + Hex64(ScratchAddress()) + "\n";

    // Kernel backend: the same self-test the first read performs.
    {
        std::lock_guard<std::mutex> guard(KernelMutex());
        std::string probe_error;
        bool ok = KernelReadyLocked(probe_error);
        if (ok && !KernelSelfTestLocked(probe_error)) {
            ok = false;
        }
        if (ok) {
            report += "kernel=ok\n";
        } else {
            report += "kernel=failed: " + probe_error + "\n";
        }
        if (!kernel_module::get_kernel_version().empty()) {
            report += "kernel_version=" + kernel_module::get_kernel_version() + "\n";
        }
    }

    // Userspace backends, probed against this process as well.
    {
        std::vector<uint8_t> bytes;
        std::string error;
        if (VmReadvRead(self, ProbeAddress(), kProbeBytes, bytes, error)) {
            report += "vm_readv=ok/" + CompareWithProbe(bytes) + "\n";
        } else {
            report += "vm_readv=failed: " + error + "\n";
        }
    }
    {
        std::vector<uint8_t> bytes;
        std::string error;
        if (ProcMemRead(self, ProbeAddress(), kProbeBytes, bytes, error)) {
            report += "proc_mem=ok/" + CompareWithProbe(bytes) + "\n";
        } else {
            report += "proc_mem=failed: " + error + "\n";
        }
    }

    // Deterministic proof of the strict contract, on a mapping we own: the
    // touched page must be readable, the untouched one must not be, and the
    // fault mode must then read the same page. Nothing here depends on counters
    // that other threads or the loader can perturb.
    report += "strict_guard=" + StrictGuardCheck() + "\n";

    std::snprintf(line, sizeof(line), "stage_bytes=%u (persistent)\n", kStageBytes);
    report += line;
    std::snprintf(line, sizeof(line), "max_read=%u\n", kMaxMemRead);
    report += line;
    return report;
}

}  // namespace skpdb
