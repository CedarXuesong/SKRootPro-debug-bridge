#include <fcntl.h>
#include <sys/uio.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#include "kernel_module_kit_umbrella.h"
#include "memory/kernel_channel.h"
#include "memory/memread.h"

namespace skpdb {

using detail::Hex64;
using detail::KernelMutex;
using detail::KernelReadyLocked;
using detail::KernelWriteLocked;
using detail::StepError;

namespace {

struct MappingInfo {
    bool found = false;
    uint64_t start = 0;
    uint64_t end = 0;
    bool writable = false;
    bool shared = false;
    std::string path;
};

MappingInfo LookupMapping(uint32_t pid, uint64_t address) {
    MappingInfo info;
    std::ifstream maps("/proc/" + std::to_string(pid) + "/maps");
    if (!maps) {
        return info;
    }
    std::string line;
    while (std::getline(maps, line)) {
        unsigned long long start = 0;
        unsigned long long end = 0;
        char perms[8] = {0};
        int consumed = 0;
        if (std::sscanf(line.c_str(), "%llx-%llx %7s %*s %*s %*s %n", &start, &end, perms,
                        &consumed) < 3) {
            continue;
        }
        if (address < start || address >= end) {
            continue;
        }
        info.found = true;
        info.start = start;
        info.end = end;
        info.writable = perms[1] == 'w';
        info.shared = perms[3] == 's';
        if (consumed > 0 && static_cast<size_t>(consumed) < line.size()) {
            info.path = line.substr(consumed);
        }
        break;
    }
    return info;
}

// process_vm_writev() fallback. It needs the same ptrace credentials as the
// read fallback and leaves a SELinux denial in the audit log when it is refused,
// which is why the kernel path is tried first.
bool VmWritevWrite(uint32_t pid, uint64_t address, const std::vector<uint8_t>& data,
                   uint32_t& written, std::string& error) {
#if defined(__aarch64__)
    struct iovec local;
    local.iov_base = const_cast<uint8_t*>(data.data());
    local.iov_len = data.size();
    struct iovec remote;
    remote.iov_base = reinterpret_cast<void*>(static_cast<uintptr_t>(address));
    remote.iov_len = data.size();
    const ssize_t got = process_vm_writev(static_cast<pid_t>(pid), &local, 1, &remote, 1, 0);
    if (got > 0) {
        written = static_cast<uint32_t>(got);
        return true;
    }
    error = std::string("process_vm_writev: ") + std::strerror(errno);
    return false;
#else
    (void)pid;
    (void)address;
    (void)data;
    (void)written;
    error = "process_vm_writev is not available on this build";
    return false;
#endif
}

bool ProcMemWrite(uint32_t pid, uint64_t address, const std::vector<uint8_t>& data,
                  uint32_t& written, std::string& error) {
    const std::string path = "/proc/" + std::to_string(pid) + "/mem";
    const int fd = ::open(path.c_str(), O_WRONLY | O_CLOEXEC);
    if (fd < 0) {
        error = path + ": " + std::strerror(errno);
        return false;
    }
    const ssize_t got = ::pwrite(fd, data.data(), data.size(), static_cast<off_t>(address));
    const int saved = errno;
    ::close(fd);
    if (got > 0) {
        written = static_cast<uint32_t>(got);
        return true;
    }
    error = path + ": " + std::strerror(saved);
    return false;
}

}  // namespace

MemWriteResult WriteProcessMemory(uint32_t pid, uint64_t address,
                                  const std::vector<uint8_t>& data, uint8_t mode,
                                  uint8_t flags) {
    MemWriteResult result;
    if (pid == 0) {
        result.error = "pid 0 is not a process";
        return result;
    }
    if (data.empty() || data.size() > kMaxMemWrite) {
        result.error = "write length must be 1.." + std::to_string(kMaxMemWrite);
        return result;
    }
    const uint64_t last = address + data.size() - 1;
    if (last < address) {
        result.error = "write range wraps around the address space";
        return result;
    }

    // Refuse before touching anything: a write outside a writable mapping, or
    // into a MAP_SHARED file-backed page without permission, changes state the
    // caller did not ask for.
    const MappingInfo mapping = LookupMapping(pid, address);
    if (!mapping.found) {
        result.error = "0x" + Hex64(address) + " is not mapped in pid " + std::to_string(pid);
        return result;
    }
    const bool allow_code = (flags & kMemWriteAllowCode) != 0;
    if (!mapping.writable && !allow_code) {
        result.error = "0x" + Hex64(address) + " is in a mapping without write permission";
        return result;
    }
    if (last >= mapping.end) {
        result.error = "write range crosses the end of the mapping at 0x" + Hex64(mapping.end);
        return result;
    }
    result.shared = mapping.shared && !mapping.path.empty();
    if (result.shared && (flags & kMemWriteAllowShared) == 0) {
        result.error = "refusing to write a MAP_SHARED file-backed page (" + mapping.path +
                       "); pass the allow-shared flag to change it for every process";
        return result;
    }

    const bool allow_fault = (flags & kMemWriteAllowFault) != 0;
    const bool want_userspace = mode == kMemModeUserspace;
    const bool want_fault = allow_fault || allow_code || mode == kMemModeFault;

    std::string kernel_error;
    if (!want_userspace) {
        std::lock_guard<std::mutex> guard(KernelMutex());
        std::string ready_error;
        if (KernelReadyLocked(ready_error)) {
            uint32_t written = 0;
            if (KernelWriteLocked(pid, address, data, !want_fault, written, kernel_error)) {
                result.written = written;
                result.backend = kMemBackendKernel;
                if (written < data.size() && result.error.empty()) {
                    result.error = "short write";
                }
                return result;
            }
            result.written = written;
        } else {
            kernel_error = ready_error;
        }
        if (mode == kMemModeKernelOnly) {
            result.error = kernel_error;
            return result;
        }
    }

    uint32_t written = 0;
    std::string userspace_error;
    if (VmWritevWrite(pid, address, data, written, userspace_error) ||
        ProcMemWrite(pid, address, data, written, userspace_error)) {
        result.written = written;
        result.backend = kMemBackendVmReadv;
        if (written < data.size()) {
            result.error = "short write";
        }
        return result;
    }
    result.error = userspace_error.empty() ? kernel_error : userspace_error;
    return result;
}

}  // namespace skpdb
