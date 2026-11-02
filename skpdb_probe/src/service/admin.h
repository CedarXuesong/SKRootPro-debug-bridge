#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "common/stream.h"

namespace skpdb {

// Actions carried as the second byte of a `[B]` request. Both the WebUI handler
// and the native client speak this vocabulary; the numbers are part of the
// wire contract.
constexpr uint8_t kAdminInfo = 1;         // no arguments  -> key=value report
constexpr uint8_t kAdminConfig = 2;       // key=value list -> applied settings
constexpr uint8_t kAdminWebUiStart = 3;   // no arguments  -> webui_port=<port>
constexpr uint8_t kAdminWebUiStop = 4;    // no arguments  -> stopped
constexpr uint8_t kAdminTouchStream = 5;  // stream/generation + reliable touch batch
constexpr uint8_t kAdminDiagnostics = 6; // read-only system/touch/memory capability checks
constexpr uint8_t kAdminGestureLease = 7; // [mode u8] 0 release, 1 acquire, 2 renew; socket-owned eight-second lease

// Administration runs over a filesystem socket in the module private
// directory. It is reachable by root only, is invisible to network scans, and
// keeps working while the TCP port is closed.
std::string AdminSocketPath(const std::string& module_dir);

// Stream over an AF_UNIX connection.
class UnixStream : public Stream {
public:
    explicit UnixStream(int fd) : Stream(fd) {}

protected:
    ssize_t ReadSome(void* buffer, size_t size) override;
    ssize_t WriteSome(const void* buffer, size_t size) override;
};

// Client half of the challenge/response exchange: reads the 32-byte nonce and
// answers with HMAC-SHA256(root_key, nonce). Every connection performs it, so
// no trust is carried over between connections.
bool ClientAuthenticate(Stream& stream, const std::string& root_key, int timeout_ms,
                        std::string& error);

// One-shot request/response client for `[B]` administration frames.
class AdminClient {
public:
    AdminClient() = default;
    ~AdminClient();

    AdminClient(const AdminClient&) = delete;
    AdminClient& operator=(const AdminClient&) = delete;

    bool Open(const std::string& socket_path, const std::string& root_key, int timeout_ms,
              std::string& error);
    bool Call(uint8_t action, const std::string& body, std::string& reply, std::string& error);
    bool Request(const std::vector<uint8_t>& request, std::vector<uint8_t>& reply, std::string& error);
    void Close();
    bool connected() const { return stream_ != nullptr; }

private:
    std::unique_ptr<Stream> stream_;
    int timeout_ms_ = 10000;
};

}  // namespace skpdb
