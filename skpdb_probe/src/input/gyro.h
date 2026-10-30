#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

// Userspace gyro injection into the framework sensor dispatcher.
//
// The gyroscope of the target device is not necessarily visible to the Linux
// kernel: on Qualcomm SSC devices the samples are produced inside the sensor
// DSP and reach system_server through the vendor HAL. The only common point
// for ordinary event queues is SensorService dispatch. Only the selected UID's
// private copy is transformed (types 4/16/11/15). Direct-report buffers are not
// intercepted by this backend and must not be advertised as covered.
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

// Validates ownership/layout and arms a fresh stream for an explicit UID.
GyroStatus GyroInstall(std::string &error, uint32_t uid = 0);
bool GyroDisarm(std::string &error, uint32_t expected_stream = 0);
bool GyroApplyPacket(uint32_t stream, uint32_t sequence, float rx, float ry, float rz, uint64_t received_ns,
                     std::string &error);

// Restores the patched instruction and disables the state block.
GyroStatus GyroRemove(std::string &error);

// Reads the live hook state from system_server.
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
