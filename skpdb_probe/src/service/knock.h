#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <string>
#include <thread>
#include <vector>

namespace skpdb {

// A knock is a single signed UDP datagram. Nothing is bound to the knock port:
// the module only holds a raw socket that receives a copy of the packet, so
// port scanners find nothing listening and the kernel's ICMP port-unreachable
// reply is the only visible trace.
//
// Layout (60 bytes, big-endian):
//   [0..8)    "SKPDBK01"
//   [8]       version (1)
//   [9]       flags (0)
//   [10..12)  reserved (0)
//   [12..20)  unix seconds
//   [20..28)  nonce
//   [28..60)  HMAC-SHA256(key, bytes [0..28))
constexpr size_t kKnockMagicSize = 8;
constexpr size_t kKnockSize = 60;
extern const char kKnockMagic[kKnockMagicSize + 1];

// Builds the datagram for `timestamp` (unix seconds) and `nonce`.
std::vector<uint8_t> BuildKnockPacket(const std::string& key, uint64_t nonce, uint64_t timestamp);

// Sends knocks to `host`:`port`, retrying `attempts` times. Returns false and
// fills `error` only when the send itself failed; UDP gives no confirmation.
bool SendKnock(const std::string& host, uint16_t port, const std::string& key, int attempts,
               std::string& error);

// Receives knocks addressed to one port. Only one receiver may run per port.
class KnockReceiver {
public:
    // Called from the receiver thread with the source IPv4 address in network
    // byte order, after the datagram verified.
    using Callback = std::function<void(uint32_t source_address)>;

    KnockReceiver();
    ~KnockReceiver();

    KnockReceiver(const KnockReceiver&) = delete;
    KnockReceiver& operator=(const KnockReceiver&) = delete;

    bool Start(uint16_t port, const std::string& key, uint32_t window_seconds, Callback callback,
               std::string& error);
    void Stop();

    uint16_t port() const { return port_; }
    // Incremented once per receiver loop; a supervisor compares it to tell a
    // live receiver from one whose thread died.
    uint64_t ticks() const { return ticks_.load(std::memory_order_relaxed); }
    bool running() const { return running_.load(std::memory_order_relaxed); }

private:
    struct Seen {
        uint64_t timestamp;
        uint64_t nonce;
    };

    void Run();
    bool Verify(const uint8_t* packet, size_t size, uint64_t now);
    void Remember(uint64_t timestamp, uint64_t nonce, uint64_t now);

    int fd_ = -1;
    int wake_[2] = {-1, -1};
    uint16_t port_ = 0;
    uint32_t window_seconds_ = 30;
    std::string key_;
    Callback callback_;
    std::vector<Seen> seen_;
    std::thread thread_;
    std::atomic<bool> stopping_{false};
    std::atomic<bool> running_{false};
    std::atomic<uint64_t> ticks_{0};
};

}  // namespace skpdb
