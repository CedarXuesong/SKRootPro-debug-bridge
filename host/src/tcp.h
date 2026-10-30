#pragma once

#include <winsock2.h>

#include <memory>
#include <string>

#include "transport.h"

namespace skpdb {

// TCP transport for the wireless (development) module.
class TcpTransport : public Transport {
public:
    static std::unique_ptr<TcpTransport> Open(const std::string& host, int port, int timeout_ms,
                                              std::string& error);
    ~TcpTransport() override;

    TcpTransport(const TcpTransport&) = delete;
    TcpTransport& operator=(const TcpTransport&) = delete;

    bool ReadExact(void* buffer, size_t size, int timeout_ms) override;
    bool WriteAll(const void* buffer, size_t size, int timeout_ms) override;
    void Close() override;
    std::string Name() const override { return name_; }

private:
    TcpTransport() = default;

    SOCKET socket_ = INVALID_SOCKET;
    std::string name_;
};

}  // namespace skpdb
