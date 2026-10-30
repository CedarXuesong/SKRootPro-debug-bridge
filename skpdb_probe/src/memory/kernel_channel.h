#pragma once

// Internal kernel-channel plumbing shared by the memory modules. Nothing here
// is part of the wire protocol: memread.h is the public surface, this header is
// what memread.cpp, memwrite.cpp and memscan.cpp use to reach the kernel.

#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "module_err_def.h"

namespace skpdb {
namespace detail {

// Staging buffer for the kernel path: one order-4 allocation, reused for the
// whole process lifetime so no per-request slab trace is left behind.
constexpr uint32_t kStageBytes = 64 * 1024;

std::mutex& KernelMutex();

// Probe pattern read back by the self-test; it lives in .data of
// kernel_channel.cpp so a strict read of this process can find it.
constexpr uint32_t kProbeBytes = 32;
extern const char kProbeText[];

bool KernelReadyLocked(std::string& error);
// Strict (or faulting) read through the kernel plus the userspace fallbacks.
bool KernelRead(uint32_t pid, uint64_t address, uint32_t length, bool no_fault,
                std::vector<uint8_t>& out, std::string& error,
                bool* unavailable = nullptr);
bool KernelSelfTestLocked(std::string& error);
bool KernelReadLocked(uint32_t pid, uint64_t address, uint32_t length, bool no_fault,
                      std::vector<uint8_t>& out, std::string& error);
bool KernelWriteLocked(uint32_t pid, uint64_t address, const std::vector<uint8_t>& data,
                       bool no_fault, uint32_t& written, std::string& error);

bool VmReadvRead(uint32_t pid, uint64_t address, uint32_t length, std::vector<uint8_t>& out,
                 std::string& error);
bool ProcMemRead(uint32_t pid, uint64_t address, uint32_t length, std::vector<uint8_t>& out,
                 std::string& error);

std::string StepError(const char* step, KModErr err);
uint64_t ProbeAddress();
uint64_t ScratchAddress();
std::string Hex64(uint64_t value);
std::string Hex(const uint8_t* data, size_t size);

}  // namespace detail
}  // namespace skpdb
