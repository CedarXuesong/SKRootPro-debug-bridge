#include "service/knock.h"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/random.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <cerrno>
#include <cstring>
#include <ctime>
#include <system_error>

#include "common/bytes.h"
#include "common/crypto.h"

namespace skpdb {
namespace {

constexpr uint8_t kKnockVersion = 1;

void PutU16(std::vector<uint8_t>& output, uint16_t value) {
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}

uint16_t GetU16(const uint8_t* data) {
    return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) | data[1]);
}

uint64_t RandomNonce() {
    uint64_t nonce = 0;
    if (getrandom(&nonce, sizeof(nonce), 0) != static_cast<ssize_t>(sizeof(nonce))) {
        nonce = static_cast<uint64_t>(time(nullptr)) << 20;
        nonce ^= static_cast<uint64_t>(getpid());
        nonce ^= static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&nonce));
    }
    return nonce;
}

}  // namespace

const char kKnockMagic[kKnockMagicSize + 1] = "SKPDBK01";

std::vector<uint8_t> BuildKnockPacket(const std::string& key, uint64_t nonce, uint64_t timestamp) {
    std::vector<uint8_t> packet;
    packet.reserve(kKnockSize);
    packet.insert(packet.end(), kKnockMagic, kKnockMagic + kKnockMagicSize);
    packet.push_back(kKnockVersion);
    packet.push_back(0);
    PutU16(packet, 0);
    PutU64(packet, timestamp);
    PutU64(packet, nonce);
    std::array<uint8_t, 32> digest = HmacSha256(key.c_str(), packet.data(), packet.size());
    packet.insert(packet.end(), digest.begin(), digest.end());
    return packet;
}

bool SendKnock(const std::string& host, uint16_t port, const std::string& key, int attempts,
               std::string& error) {
    struct sockaddr_in address;
    std::memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    if (inet_pton(AF_INET, host.c_str(), &address.sin_addr) != 1) {
        error = "knock: \"" + host + "\" is not an IPv4 address";
        return false;
    }
    int fd = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        error = std::string("knock: socket: ") + std::strerror(errno);
        return false;
    }
    std::vector<uint8_t> packet =
        BuildKnockPacket(key, RandomNonce(), static_cast<uint64_t>(time(nullptr)));
    bool sent = false;
    for (int attempt = 0; attempt < attempts && !sent; ++attempt) {
        ssize_t count = sendto(fd, packet.data(), packet.size(), 0,
                               reinterpret_cast<struct sockaddr*>(&address), sizeof(address));
        if (count == static_cast<ssize_t>(packet.size())) {
            sent = true;
        } else if (attempt + 1 < attempts) {
            usleep(120 * 1000);
        } else {
            error = std::string("knock: sendto: ") + std::strerror(errno);
        }
    }
    close(fd);
    return sent;
}

KnockReceiver::KnockReceiver() = default;

KnockReceiver::~KnockReceiver() { Stop(); }

bool KnockReceiver::Start(uint16_t port, const std::string& key, uint32_t window_seconds,
                          Callback callback, std::string& error) {
    Stop();
    fd_ = socket(AF_INET, SOCK_RAW | SOCK_CLOEXEC, IPPROTO_UDP);
    if (fd_ < 0) {
        error = std::string("raw socket: ") + std::strerror(errno);
        return false;
    }
    if (pipe2(wake_, O_CLOEXEC) != 0) {
        error = std::string("pipe: ") + std::strerror(errno);
        close(fd_);
        fd_ = -1;
        return false;
    }
    port_ = port;
    window_seconds_ = window_seconds == 0 ? 30 : window_seconds;
    key_ = key;
    callback_ = std::move(callback);
    seen_.clear();
    stopping_ = false;
    try { thread_ = std::thread(&KnockReceiver::Run, this); }
    catch (const std::system_error& failure) {
        error = std::string("knock thread: ") + failure.what();
        Stop();
        return false;
    }
    return true;
}

void KnockReceiver::Stop() {
    if (fd_ < 0 && !thread_.joinable()) {
        return;
    }
    stopping_ = true;
    if (wake_[1] >= 0) {
        ssize_t ignored = write(wake_[1], "x", 1);
        (void)ignored;
    }
    if (thread_.joinable()) {
        thread_.join();
    }
    if (fd_ >= 0) {
        close(fd_);
        fd_ = -1;
    }
    if (wake_[0] >= 0) {
        close(wake_[0]);
        close(wake_[1]);
        wake_[0] = -1;
        wake_[1] = -1;
    }
}

void KnockReceiver::Remember(uint64_t timestamp, uint64_t nonce, uint64_t now) {
    seen_.push_back(Seen{timestamp, nonce});
    const uint64_t horizon = window_seconds_ * 2 + 5;
    auto stale = [&](const Seen& entry) {
        return timestamp > entry.timestamp ? timestamp - entry.timestamp > horizon
                                           : entry.timestamp - timestamp > horizon;
    };
    seen_.erase(std::remove_if(seen_.begin(), seen_.end(), stale), seen_.end());
    (void)now;
}

bool KnockReceiver::Verify(const uint8_t* packet, size_t size, uint64_t now) {
    if (size < kKnockSize || packet[0] != 'S') {
        return false;
    }
    if (std::memcmp(packet, kKnockMagic, kKnockMagicSize) != 0) {
        return false;
    }
    if (packet[8] != kKnockVersion) {
        return false;
    }
    uint16_t pad = GetU16(packet + 10);
    if (pad != 0) {
        return false;
    }
    uint64_t timestamp = GetU64(packet + 12);
    uint64_t nonce = GetU64(packet + 20);
    if (timestamp > now) {
        if (timestamp - now > window_seconds_) {
            return false;
        }
    } else if (now - timestamp > window_seconds_) {
        return false;
    }
    std::array<uint8_t, 32> expected = HmacSha256(key_.c_str(), packet, 28);
    uint8_t difference = 0;
    for (size_t index = 0; index < expected.size(); ++index) {
        difference |= static_cast<uint8_t>(expected[index] ^ packet[28 + index]);
    }
    if (difference != 0) {
        return false;
    }
    for (const Seen& entry : seen_) {
        if (entry.nonce == nonce) {
            return false;
        }
    }
    Remember(timestamp, nonce, now);
    return true;
}

void KnockReceiver::Run() {
    std::vector<uint8_t> buffer(65536);
    running_.store(true, std::memory_order_relaxed);
    while (!stopping_) {
        ticks_.fetch_add(1, std::memory_order_relaxed);
        struct pollfd descriptors[2];
        descriptors[0] = {fd_, POLLIN, 0};
        descriptors[1] = {wake_[0], POLLIN, 0};
        int ready = poll(descriptors, 2, 20);
        if (ready < 0 && errno == EINTR) {
            continue;
        }
        if (ready <= 0) {
            continue;
        }
        if (descriptors[1].revents & POLLIN) {
            break;
        }
        if ((descriptors[0].revents & POLLIN) == 0) {
            continue;
        }
        struct sockaddr_in source;
        std::memset(&source, 0, sizeof(source));
        socklen_t source_size = sizeof(source);
        ssize_t count = recvfrom(fd_, buffer.data(), buffer.size(), 0,
                                 reinterpret_cast<struct sockaddr*>(&source), &source_size);
        if (count <= 0) {
            continue;
        }
        // A raw IP socket delivers the IP header first; the transport header
        // starts at IHL*4. Anything shorter than the minimum header is junk.
        if (count < 28) {
            continue;
        }
        size_t header_size = static_cast<size_t>(buffer[0] & 0x0f) * 4;
        if (header_size < 20 || static_cast<size_t>(count) < header_size + 8) {
            continue;
        }
        if (buffer[9] != 17) {
            continue;
        }
        const uint8_t* udp = buffer.data() + header_size;
        uint16_t destination_port = GetU16(udp + 2);
        if (destination_port != port_) {
            continue;
        }
        uint16_t payload_size = GetU16(udp + 4);
        if (payload_size < 8 || payload_size > count - header_size) {
            continue;
        }
        const uint8_t* payload = udp + 8;
        size_t available = payload_size - 8;
        int64_t now = static_cast<int64_t>(time(nullptr));
        if (!Verify(payload, available, static_cast<uint64_t>(now))) {
            continue;
        }
        if (callback_) {
            callback_(source.sin_addr.s_addr);
        }
    }
    running_.store(false, std::memory_order_relaxed);
}

}  // namespace skpdb
