#include "knock.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <array>
#include <cstring>
#include <ctime>
#include <random>

#include "protocol.h"
#include "winsock.h"

namespace skpdb {
namespace {

constexpr char kKnockMagic[] = "SKPDBK01";
constexpr uint8_t kKnockVersion = 1;

void PutU16(std::vector<uint8_t>& output, uint16_t value) {
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}

uint64_t RandomNonce() {
    std::random_device device;
    uint64_t nonce = static_cast<uint64_t>(device()) << 32;
    nonce ^= static_cast<uint64_t>(device());
    return nonce;
}

}  // namespace

std::vector<uint8_t> BuildKnockPacket(const std::string& key, uint64_t nonce,
                                      uint64_t timestamp) {
    std::vector<uint8_t> packet;
    packet.reserve(kKnockSize);
    packet.insert(packet.end(), kKnockMagic, kKnockMagic + sizeof(kKnockMagic) - 1);
    packet.push_back(kKnockVersion);
    packet.push_back(0);
    PutU16(packet, 0);
    PutU64(packet, timestamp);
    PutU64(packet, nonce);
    std::array<uint8_t, 32> digest =
        HmacSha256(key, packet.data(), packet.size());
    packet.insert(packet.end(), digest.begin(), digest.end());
    return packet;
}

bool SendKnock(const std::string& host, uint16_t port, const std::string& key, int attempts,
               std::string& error) {
    if (!EnsureWinsock(error)) {
        error = "knock: " + error;
        return false;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    if (InetPtonA(AF_INET, host.c_str(), &address.sin_addr) != 1) {
        error = "knock: \"" + host + "\" is not an IPv4 address";
        return false;
    }
    SOCKET socket_handle = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_handle == INVALID_SOCKET) {
        error = "knock: cannot open a UDP socket, error " +
                std::to_string(WSAGetLastError());
        return false;
    }
    const std::vector<uint8_t> packet =
        BuildKnockPacket(key, RandomNonce(), static_cast<uint64_t>(std::time(nullptr)));
    bool sent = false;
    for (int attempt = 0; attempt < attempts && !sent; ++attempt) {
        const int count = sendto(socket_handle, reinterpret_cast<const char*>(packet.data()),
                                 static_cast<int>(packet.size()), 0,
                                 reinterpret_cast<const sockaddr*>(&address), sizeof(address));
        if (count == static_cast<int>(packet.size())) {
            sent = true;
        } else if (attempt + 1 < attempts) {
            Sleep(120);
        } else {
            error = "knock: sendto failed, error " + std::to_string(WSAGetLastError());
        }
    }
    closesocket(socket_handle);
    return sent;
}

}  // namespace skpdb
