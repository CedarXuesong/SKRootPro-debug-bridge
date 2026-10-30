#include "service/admin.h"

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <array>
#include <algorithm>
#include <cerrno>
#include <cstring>
#include <vector>

#include "common/bytes.h"
#include "common/channel.h"
#include "common/crypto.h"
#include "common/frame.h"

namespace skpdb {

std::string AdminSocketPath(const std::string& module_dir) {
    if (module_dir.empty()) {
        return std::string();
    }
    std::string path = module_dir;
    if (path.back() != '/') {
        path.push_back('/');
    }
    path += "skpdb.admin";
    return path;
}

ssize_t UnixStream::ReadSome(void* buffer, size_t size) { return read(fd(), buffer, size); }

ssize_t UnixStream::WriteSome(const void* buffer, size_t size) { return write(fd(), buffer, size); }

bool ClientAuthenticate(Stream& stream, const std::string& root_key, int timeout_ms,
                        std::string& error) {
    std::array<uint8_t, 32> nonce{};
    if (!stream.ReadExact(nonce.data(), nonce.size(), timeout_ms)) {
        error = "handshake: no nonce from the device";
        return false;
    }
    std::array<uint8_t, 32> proof = HmacSha256(root_key.c_str(), nonce.data(), nonce.size());
    if (!stream.WriteAll(proof.data(), proof.size(), timeout_ms)) {
        error = "handshake: could not send the proof";
        return false;
    }
    return true;
}

AdminClient::~AdminClient() { Close(); }

void AdminClient::Close() { stream_.reset(); }

bool AdminClient::Open(const std::string& socket_path, const std::string& root_key,
                       int timeout_ms, std::string& error) {
    Close();
    timeout_ms_ = timeout_ms <= 0 ? 10000 : timeout_ms;
    struct sockaddr_un address;
    std::memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    if (socket_path.size() >= sizeof(address.sun_path)) {
        error = "admin socket path is too long";
        return false;
    }
    std::memcpy(address.sun_path, socket_path.c_str(), socket_path.size());
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        error = std::string("socket: ") + std::strerror(errno);
        return false;
    }
    if (connect(fd, reinterpret_cast<struct sockaddr*>(&address), sizeof(address)) != 0) {
        error = std::string("connect ") + socket_path + ": " + std::strerror(errno);
        close(fd);
        return false;
    }
    struct timeval timeout;
    timeout.tv_sec = timeout_ms_ / 1000;
    timeout.tv_usec = (timeout_ms_ % 1000) * 1000;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    stream_.reset(new UnixStream(fd));
    return ClientAuthenticate(*stream_, root_key, timeout_ms_, error);
}

bool AdminClient::Call(uint8_t action, const std::string& body, std::string& reply,
                       std::string& error) {
    std::vector<uint8_t> request{opcode::kAdmin, action};
    request.insert(request.end(), body.begin(), body.end());
    std::vector<uint8_t> result;
    if (!Request(request, result, error)) return false;
    reply.assign(result.begin(), result.end());
    return true;
}

bool AdminClient::Request(const std::vector<uint8_t>& request, std::vector<uint8_t>& reply,
                         std::string& error) {
    if (!stream_) {
        error = "admin client is not connected";
        return false;
    }
    if (request.empty()) { error = "empty admin request"; return false; }
    std::vector<uint8_t> response;
    if (!SendFrame(*stream_, request, timeout_ms_) || !RecvFrame(*stream_, response, timeout_ms_)) {
        error = "admin request failed";
        Close();
        return false;
    }
    if (response.size() < 2 || response[0] != request[0]) {
        error = "malformed admin response";
        return false;
    }
    if (response[1] != 0) {
        if (response.size() < 6) {
            error = "admin error without a message";
        } else {
            uint32_t size = GetU32(response.data() + 2);
            size = std::min<uint32_t>(size, static_cast<uint32_t>(response.size() - 6));
            error.assign(reinterpret_cast<const char*>(response.data() + 6), size);
        }
        return false;
    }
    reply.assign(response.begin() + 2, response.end());
    return true;
}

}  // namespace skpdb
