#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

// Device-wide gyro injection at the verified Qualcomm QSH vendor HAL output.
// This runs before SensorService fan-out, without app/UID selection. It is not
// a Linux sensor driver hook: SSC/DSP direct-report buffers bypass this output
// and remain explicitly unsupported.
struct GyroStatus {
    bool installed = false;
    uint32_t pid = 0;
    uint64_t base = 0;
    uint64_t entry = 0;
    uint64_t patch = 0;
    uint64_t cave = 0;
    uint64_t state = 0;
    uint32_t hits = 0;
    uint64_t updates = 0;
    bool gateway = false;
    uint64_t gateway_addr = 0;
    std::string gateway_symbol;
    std::string error;
    uint32_t uid = 0;
    uint32_t stream = 0;
    bool enabled = false;
    std::string writer;
};

// Validates ownership/layout and arms a device-wide stream. Nonzero UID requests
// are rejected so old clients cannot silently install a different scope.
GyroStatus GyroInstall(std::string &error, uint32_t uid = 0);
bool GyroDisarm(std::string &error, uint32_t expected_stream = 0);
bool GyroApplyPacket(uint32_t stream, uint32_t sequence, float rx, float ry, float rz, uint64_t received_ns,
                     std::string &error);

// Restores the patched instruction and disables the state block.
GyroStatus GyroRemove(std::string &error);

// Reads the live hook state from the vendor sensors HAL.
GyroStatus GyroQuery(std::string &error);

// Publishes a CLOCK_BOOTTIME lease using a seqlock and confirms its version.
// Preferred backend is persistent /proc/<pid>/mem; the verified kernel fallback
// is explicitly reported as degraded. Unbounded legacy sticky rates are rejected.
bool GyroApplyRate(float rx, float ry, float rz, uint64_t now_ns, bool sticky, std::string &error);

// Same, without the error string (for the UDP fast path).
bool GyroApplyRate(float rx, float ry, float rz);

// True when a hook is currently installed and its state block is live.
bool GyroActive();
uint32_t GyroStream();

// Legacy diagnostics: the global syscall gateway was removed; Ping returns -1.
long GyroGatewayPing();

// Legacy counter is always zero. Report describes the confirmed writer instead.
uint64_t GyroGatewayHits();
std::string GyroGatewayReport();

} // namespace skpdb
