#include "memory/kernel_channel.h"

#include "memory/memread.h"

#include <fcntl.h>
#include <sys/mman.h>
#include <sys/uio.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "kernel_module_kit_umbrella.h"

namespace skpdb {
namespace detail {

using namespace asmjit::a64;

// The FOLL_* flags live in include/linux/mm.h and became an enum in
// include/linux/mm_types.h in 6.3, which moved every bit. Three eras matter:
//
//   < 5.6      FOLL_FORCE = 0x10, no FOLL_NOFAULT at all
//   5.6 .. 6.2 FOLL_FORCE = 0x10, FOLL_NOFAULT = 0x80
//   >= 6.3     FOLL_FORCE = 0x08, FOLL_NOFAULT = 0x20
//
// FOLL_FORCE is what makes COW and private pages readable, exactly as
// fs/proc/base.c passes it for a read; a read is any flags value with
// FOLL_WRITE (0x01) clear. FOLL_NOWAIT must never be set (0x20 in 5.6..6.2,
// 0x10 from 6.3 on): a strict read wants the opposite of a background fault.
constexpr uint32_t kFollForceLegacy = 0x10;    // < 6.3.0
constexpr uint32_t kFollForceModern = 0x08;    // >= 6.3.0
constexpr uint32_t kFollNoFaultLegacy = 0x80;  // 5.6 .. 6.2
constexpr uint32_t kFollNoFaultModern = 0x20;  // >= 6.3.0

// FOLL_NOFAULT makes get_user_pages report a hole instead of faulting the page
// in, which is what keeps a read invisible to the target: no minor fault, no
// rss growth, no page table update. Below 5.6 the bit does not exist, so a
// strict read can only be promised from 5.6 on, which the diagnostics report.
uint32_t FollFlags(bool no_fault) {
    const bool enum_bits = !kernel_module::is_kernel_version_less("6.3.0");
    const bool has_no_fault = !kernel_module::is_kernel_version_less("5.6.0");
    uint32_t flags = enum_bits ? kFollForceModern : kFollForceLegacy;
    if (no_fault && has_no_fault) {
        flags |= enum_bits ? kFollNoFaultModern : kFollNoFaultLegacy;
    }
    return flags;
}

// Probe pattern for the kernel self-test. It must stay non-const and volatile:
// the self-test reads these bytes back out of this process through
// access_process_vm, so they have to exist in .data at runtime.
volatile uint8_t g_probe_pattern[kProbeBytes] = {
    'S', 'K', 'P', 'D', 'B', '-', 'm', 'e', 'm', 'r', 'e', 'a', 'd', '-', 'p', 'r',
    'o', 'b', 'e', '-', 'a', 'r', 'm', '6', '4', '-', 's', 'e', 'l', 'f', 't', '!',
};

const char kProbeText[] = "SKPDB-memread-probe-arm64-selft!";

// Scratch bytes a client may poke to prove the write path works without
// touching anything that matters. Reported by MemoryDiagnostics().
volatile uint8_t g_scratch[256] = {0};

uint64_t ScratchAddress() {
    return static_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(const_cast<uint8_t*>(g_scratch)));
}

// The kernel side is entered through one shared driver channel, so concurrent
// wireless sessions must not interleave shellcode. Everything below that
// touches the kernel runs with this mutex held.
std::mutex& KernelMutex() {
    static std::mutex mutex;
    return mutex;
}

enum class State { kUnknown, kReady, kFailed };

// Kernel backend health. Guarded by KernelMutex().
State g_kernel_state = State::kUnknown;
std::string g_kernel_error;

std::string StepError(const char* step, KModErr err) {
    return std::string(step) + ": " + to_string(err);
}

uint64_t ProbeAddress() {
    return static_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(const_cast<uint8_t*>(g_probe_pattern)));
}

std::string Hex64(uint64_t value) {
    char text[19];
    std::snprintf(text, sizeof(text), "%llx", static_cast<unsigned long long>(value));
    return std::string(text);
}

std::string Hex(const uint8_t* data, size_t size) {
    static constexpr char kDigits[] = "0123456789abcdef";
    std::string text;
    text.reserve(size * 2);
    for (size_t index = 0; index < size; ++index) {
        text.push_back(kDigits[data[index] >> 4]);
        text.push_back(kDigits[data[index] & 0x0f]);
    }
    return text;
}

// find_get_pid(pid) -> pid_task(PIDTYPE_PID) -> access_process_vm(task, addr,
// stage, len, FOLL_FORCE) -> put_pid(pid). All of it happens inside one kernel
// entry, so no task_struct pointer is ever held across two entries and a target
// that exits mid-read cannot turn into a stale pointer.
//
// A missing pid or a task that is being torn down returns a negative count
// instead of being dereferenced.
bool KernelTransferChunk(uint32_t pid, uint64_t address, uint64_t stage, uint32_t length,
                         bool write, bool no_fault, int32_t& out_bytes, std::string& error) {
    aarch64_asm_ctx asm_ctx = init_aarch64_asm();
    Assembler* a = asm_ctx.assembler();
    kernel_module::arm64_module_asm_func_start(a);

    KModErr err_find = KModErr::OK;
    KModErr err_task = KModErr::OK;
    KModErr err_read = KModErr::OK;
    KModErr err_put = KModErr::OK;

    Label no_pid = a->newLabel();
    Label no_task = a->newLabel();
    Label release_pid = a->newLabel();
    Label finished = a->newLabel();

    aarch64_asm_mov_w(a, w1, pid);
    kernel_module::export_symbol::find_get_pid(a, err_find, w1);
    // x0 = struct pid *, NULL when no such pid exists.
    a->mov(x11, x0);
    a->cbz(x11, no_pid);

    if (kernel_module::is_kernel_version_less("4.19.0")) {
        kernel_module::export_symbol::linux_older::pid_task(
            a, err_task, x11,
            kernel_module::export_symbol::linux_older::PidType::PIDTYPE_PID);
    } else {
        kernel_module::export_symbol::linux_above_4_19_0::pid_task(
            a, err_task, x11,
            kernel_module::export_symbol::linux_above_4_19_0::PidType::PIDTYPE_PID);
    }
    // x0 = task_struct of the thread group leader.
    a->mov(x12, x0);
    a->cbz(x12, no_task);

    aarch64_asm_mov_x(a, x2, address);
    aarch64_asm_mov_x(a, x3, stage);
    aarch64_asm_mov_w(a, w4, length);
    if (kernel_module::is_kernel_version_less("4.9.0")) {
        // Before 4.9 the fifth argument is an int write flag.
        aarch64_asm_mov_w(a, w5, write ? 1 : 0);
        kernel_module::export_symbol::linux_older::access_process_vm(a, err_read, x12, x2, x3, w4,
                                                                   w5);
    } else {
        // FOLL_WRITE (0x01) turns the very same call into a write; with
        // FOLL_FORCE it lands on COW pages exactly like /proc/<pid>/mem.
        aarch64_asm_mov_w(a, w5, FollFlags(no_fault) | (write ? 0x01u : 0u));
        kernel_module::export_symbol::linux_above_4_9_0::access_process_vm(a, err_read, x12, x2,
                                                                         x3, w4, w5);
    }
    // The return value is an int, so only w0 is meaningful.
    a->mov(x13, x0);
    a->b(release_pid);

    // pid_task() on a dying task can yield NULL; skip the dereference and
    // report the failure, but still drop the pid reference below.
    a->bind(no_task);
    aarch64_asm_mov_x(a, x13, static_cast<uint64_t>(-3));

    a->bind(release_pid);
    kernel_module::export_symbol::put_pid(a, err_put, x11);
    a->b(finished);

    // No struct pid * was taken, so put_pid() must not be called here.
    a->bind(no_pid);
    aarch64_asm_mov_x(a, x13, static_cast<uint64_t>(-2));
    a->bind(finished);

    a->mov(x0, x13);
    kernel_module::arm64_module_asm_func_end(a, x0);

    if (is_failed(err_find)) {
        error = StepError("find_get_pid", err_find);
        return false;
    }
    if (is_failed(err_task)) {
        error = StepError("pid_task", err_task);
        return false;
    }
    if (is_failed(err_read)) {
        error = StepError("access_process_vm", err_read);
        return false;
    }
    if (is_failed(err_put)) {
        error = StepError("put_pid", err_put);
        return false;
    }

    std::vector<uint8_t> code = aarch64_asm_to_bytes(a);
    if (code.empty()) {
        error = "shellcode assembly failed";
        return false;
    }
    uint64_t value = 0;
    KModErr rc = kernel_module::execute_kernel_asm_func(code, value);
    if (is_failed(rc)) {
        error = StepError("execute_kernel_asm_func", rc);
        return false;
    }
    out_bytes = static_cast<int32_t>(value);
    return true;
}

// One staging buffer for the whole process lifetime. Reallocating it per chunk
// would leave a slab trace and cost latency for nothing: every kernel entry
// already runs under KernelMutex(), so a single buffer is enough.
uint64_t g_stage = 0;
uint32_t g_stage_bytes = 0;

bool EnsureStageLocked(uint64_t& stage, uint32_t& stage_bytes, std::string& error) {
    if (g_stage != 0) {
        stage = g_stage;
        stage_bytes = g_stage_bytes;
        return true;
    }
    uint64_t allocated = 0;
    const KModErr result = kernel_module::alloc_kernel_mem(kStageBytes, allocated);
    if (is_failed(result)) {
        error = StepError("alloc_kernel_mem", result);
        return false;
    }
    if (allocated == 0) {
        error = "alloc_kernel_mem returned a null address";
        return false;
    }
    g_stage = allocated;
    g_stage_bytes = kStageBytes;
    stage = allocated;
    stage_bytes = kStageBytes;
    return true;
}

// Reads through the kernel into the shared stage and copies the bytes back out.
// Requires KernelMutex().
bool KernelReadLocked(uint32_t pid, uint64_t address, uint32_t length, bool no_fault,
                      std::vector<uint8_t>& out, std::string& error) {
    uint64_t stage = 0;
    uint32_t stage_bytes = 0;
    if (!EnsureStageLocked(stage, stage_bytes, error)) {
        return false;
    }

    // One userspace copy buffer, kept for the process lifetime: a fresh 64 KiB
    // allocation per read would churn the heap and, worse, add pages of our own
    // to the counter window the diagnostics measures.
    static std::vector<uint8_t> buffer(kStageBytes);
    out.clear();
    out.reserve(std::min(length, kMaxMemRead));
    bool ok = true;
    uint32_t done = 0;
    while (done < length) {
        uint32_t want = std::min(length - done, stage_bytes);
        int32_t got = 0;
        std::string chunk_error;
        if (!KernelTransferChunk(pid, address + done, stage, want, false, no_fault, got,
                                 chunk_error)) {
            error = chunk_error;
            g_kernel_state = State::kFailed;
            g_kernel_error = chunk_error;
            ok = false;
            break;
        }
        if (got == -2) {
            error = "find_get_pid(" + std::to_string(pid) + ") found no process";
            break;
        }
        if (got == -3) {
            error = "pid_task(" + std::to_string(pid) + ") returned no task (exiting?)";
            break;
        }
        if (got <= 0) {
            // A mapped-but-unreadable page is not a backend failure: the same
            // address fails through every path. Keep whatever was read. With
            // FOLL_NOFAULT this is also what a non-resident page looks like.
            error = no_fault ? "0x" + Hex64(address) +
                                   " is not readable without faulting it in (strict mode: "
                                   "non-resident or PROT_NONE page)"
                             : "access_process_vm returned 0 at 0x" + Hex64(address) +
                                   " (unmapped or PROT_NONE)";
            break;
        }
        uint32_t take = std::min(static_cast<uint32_t>(got), want);
        KModErr copy_err = kernel_module::read_kernel_mem(stage, buffer.data(), take);
        if (is_failed(copy_err)) {
            error = StepError("read_kernel_mem", copy_err);
            g_kernel_state = State::kFailed;
            g_kernel_error = error;
            ok = false;
            break;
        }
        out.insert(out.end(), buffer.begin(), buffer.begin() + take);
        done += take;
        if (take < want) {
            error = "short read";
            break;
        }
    }
    // A read that moved no bytes is a failure for the caller: the error string
    // already says why (hole, PROT_NONE, dying task), and reporting success
    // with empty data left the wire message blank.
    return ok && !out.empty();
}

// Reads the probe pattern back through the kernel path. A mismatch means the
// shellcode channel or this kernel's symbol layout is wrong, so the backend is
// latched as failed once instead of silently returning garbage.
bool KernelSelfTestLocked(std::string& error) {
    std::vector<uint8_t> bytes;
    std::string read_error;
    // The self-test validates the shellcode channel, not residency, so it runs
    // with faults allowed: the probe lives in this very process, and a strict
    // read would fail here whenever the kernel happened to reclaim that page.
    if (!KernelReadLocked(static_cast<uint32_t>(getpid()), ProbeAddress(), kProbeBytes, false,
                          bytes, read_error)) {
        error = read_error;
        return false;
    }
    if (bytes.size() != kProbeBytes) {
        error = "self-test read " + std::to_string(bytes.size()) + " of " +
                std::to_string(kProbeBytes) + " bytes";
        if (!read_error.empty()) {
            error += " (" + read_error + ")";
        }
        return false;
    }
    if (std::memcmp(bytes.data(), kProbeText, kProbeBytes) != 0) {
        error = "self-test pattern mismatch";
        return false;
    }
    return true;
}

// Ensures the kernel backend has been validated once. Requires KernelMutex().
bool KernelReadyLocked(std::string& error) {
    if (g_kernel_state == State::kFailed) {
        error = g_kernel_error;
        return false;
    }
    if (g_kernel_state == State::kReady) {
        return true;
    }
    std::string probe_error;
    if (!KernelSelfTestLocked(probe_error)) {
        g_kernel_state = State::kFailed;
        g_kernel_error = probe_error;
        error = probe_error;
        return false;
    }
    g_kernel_state = State::kReady;
    return true;
}

bool KernelRead(uint32_t pid, uint64_t address, uint32_t length, bool no_fault,
                std::vector<uint8_t>& out, std::string& error, bool* unavailable) {
    std::lock_guard<std::mutex> guard(KernelMutex());
    const bool ready = KernelReadyLocked(error);
    if (unavailable != nullptr) {
        *unavailable = !ready;
    }
    if (!ready) {
        return false;
    }
    const bool ok = KernelReadLocked(pid, address, length, no_fault, out, error);
    if (!ok && unavailable != nullptr && g_kernel_state == State::kFailed) {
        *unavailable = true;
    }
    return ok;
}

bool VmReadvRead(uint32_t pid, uint64_t address, uint32_t length, std::vector<uint8_t>& out,
                 std::string& error) {
    std::vector<uint8_t> buffer(length);
    struct iovec local{};
    local.iov_base = buffer.data();
    local.iov_len = buffer.size();
    struct iovec remote{};
    remote.iov_base = reinterpret_cast<void*>(static_cast<uintptr_t>(address));
    remote.iov_len = buffer.size();

    ssize_t count = process_vm_readv(static_cast<pid_t>(pid), &local, 1, &remote, 1, 0);
    int saved_errno = errno;
    if (count <= 0) {
        error = "process_vm_readv: " +
                (count == 0 ? std::string("read 0 bytes") : std::string(std::strerror(saved_errno)));
        return false;
    }
    out.assign(buffer.begin(), buffer.begin() + count);
    return true;
}

bool ProcMemRead(uint32_t pid, uint64_t address, uint32_t length, std::vector<uint8_t>& out,
                 std::string& error) {
    const std::string path = "/proc/" + std::to_string(pid) + "/mem";
    int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        error = path + ": " + std::strerror(errno);
        return false;
    }
    std::vector<uint8_t> buffer(length);
    ssize_t count = pread(fd, buffer.data(), buffer.size(), static_cast<off_t>(address));
    int saved_errno = errno;
    close(fd);
    if (count <= 0) {
        error = path + ": " +
                (count == 0 ? std::string("read 0 bytes") : std::string(std::strerror(saved_errno)));
        return false;
    }
    out.assign(buffer.begin(), buffer.begin() + count);
    return true;
}

bool KernelWriteLocked(uint32_t pid, uint64_t address, const std::vector<uint8_t>& data,
                       bool no_fault, uint32_t& written, std::string& error) {
    uint64_t stage = 0;
    uint32_t stage_bytes = 0;
    if (!EnsureStageLocked(stage, stage_bytes, error)) {
        return false;
    }
    written = 0;
    while (written < data.size()) {
        uint32_t want = std::min<uint32_t>(static_cast<uint32_t>(data.size() - written),
                                           stage_bytes);
        KModErr copy = kernel_module::write_kernel_mem(stage, data.data() + written, want);
        if (is_failed(copy)) {
            error = StepError("write_kernel_mem", copy);
            return false;
        }
        int32_t got = 0;
        std::string chunk_error;
        if (!KernelTransferChunk(pid, address + written, stage, want, true, no_fault, got,
                                 chunk_error)) {
            error = chunk_error;
            g_kernel_state = State::kFailed;
            g_kernel_error = chunk_error;
            return false;
        }
        if (got == -2) {
            error = "find_get_pid(" + std::to_string(pid) + ") found no process";
            break;
        }
        if (got == -3) {
            error = "pid_task(" + std::to_string(pid) + ") returned no task (exiting?)";
            break;
        }
        if (got <= 0) {
            error = no_fault
                        ? "0x" + Hex64(address + written) +
                              " is not writable without faulting it in (strict mode: the page "
                              "is not resident, or not writable)"
                        : "access_process_vm wrote 0 bytes at 0x" + Hex64(address + written) +
                              " (not writable)";
            break;
        }
        written += static_cast<uint32_t>(got);
        if (static_cast<uint32_t>(got) < want) {
            error = "short write";
            break;
        }
    }
    return written > 0;
}

}  // namespace detail
}  // namespace skpdb
