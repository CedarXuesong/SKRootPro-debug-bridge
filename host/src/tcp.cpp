#include "tcp.h"

#include <ws2tcpip.h>

#include <algorithm>
#include <cstring>

#include "winsock.h"

namespace skpdb {
namespace {

void SetSocketTimeouts(SOCKET socket, int timeout_ms) {
    DWORD timeout = static_cast<DWORD>(std::max(timeout_ms, 1));
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout),
               sizeof(timeout));
    setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout),
               sizeof(timeout));
}

}  // namespace

std::unique_ptr<TcpTransport> TcpTransport::Open(const std::string& host, int port,
                                                 int timeout_ms, std::string& error) {
    if (!EnsureWinsock(error)) {
        return nullptr;
    }

    addrinfo hints{};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* addresses = nullptr;
    std::string service = std::to_string(port);
    if (getaddrinfo(host.c_str(), service.c_str(), &hints, &addresses) != 0 || !addresses) {
        error = "cannot resolve " + host;
        return nullptr;
    }

    SOCKET socket = INVALID_SOCKET;
    for (addrinfo* address = addresses; address; address = address->ai_next) {
        socket = ::socket(address->ai_family, address->ai_socktype, address->ai_protocol);
        if (socket == INVALID_SOCKET) {
            continue;
        }
        if (::connect(socket, address->ai_addr, static_cast<int>(address->ai_addrlen)) == 0) {
            break;
        }
        closesocket(socket);
        socket = INVALID_SOCKET;
    }
    freeaddrinfo(addresses);
    if (socket == INVALID_SOCKET) {
        error = "cannot connect to " + host + ":" + std::to_string(port);
        return nullptr;
    }

    SetSocketTimeouts(socket, timeout_ms);
    int no_delay = 1;
    setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&no_delay),
               sizeof(no_delay));

    auto transport = std::unique_ptr<TcpTransport>(new TcpTransport());
    transport->socket_ = socket;
    transport->name_ = host + ":" + std::to_string(port);
    return transport;
}

TcpTransport::~TcpTransport() {
    Close();
}

void TcpTransport::Close() {
    if (socket_ != INVALID_SOCKET) {
        closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }
}

bool TcpTransport::ReadExact(void* buffer, size_t size, int timeout_ms) {
    if (socket_ == INVALID_SOCKET) {
        return false;
    }
    SetSocketTimeouts(socket_, timeout_ms);
    auto* cursor = static_cast<uint8_t*>(buffer);
    size_t total = 0;
    while (total < size) {
        int want = static_cast<int>(std::min<size_t>(size - total, 1u << 20));
        int received = recv(socket_, reinterpret_cast<char*>(cursor + total), want, 0);
        if (received <= 0) {
            return false;
        }
        total += static_cast<size_t>(received);
    }
    return true;
}

bool TcpTransport::WriteAll(const void* buffer, size_t size, int timeout_ms) {
    if (socket_ == INVALID_SOCKET) {
        return false;
    }
    SetSocketTimeouts(socket_, timeout_ms);
    auto* cursor = static_cast<const uint8_t*>(buffer);
    size_t total = 0;
    while (total < size) {
        int want = static_cast<int>(std::min<size_t>(size - total, 1u << 20));
        int sent = send(socket_, reinterpret_cast<const char*>(cursor + total), want, 0);
        if (sent <= 0) {
            return false;
        }
        total += static_cast<size_t>(sent);
    }
    return true;
}

}  // namespace skpdb
