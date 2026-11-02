#pragma once

#include <chrono>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <vector>

namespace skpdb {
// A control socket temporarily owns the conflicting system gesture setting.
// Other sockets cannot release/refresh it. EOF and a missed heartbeat restore
// it; the private journal recovers an interrupted restore after daemon restart.
class GestureLease {
public:
    using Command = std::function<bool(const std::vector<std::string>&, std::string&)>;
    using Clock = std::function<std::chrono::steady_clock::time_point()>;
    explicit GestureLease(std::string directory, Command command = {}, Clock clock = {});
    bool Apply(uint64_t owner, bool enabled, std::string& report, std::string& error, bool renew = false);
    void Release(uint64_t owner);
    void Tick();
    bool Recover();
private:
    bool Restore();
    bool Setting(const char* operation, const std::string& user, std::string& result, const char* value = nullptr);
    std::string path_, user_;
    Command command_;
    Clock clock_;
    std::mutex mutex_;
    uint64_t owner_ = 0;
    bool changed_ = false;
    std::chrono::steady_clock::time_point expires_{};
};
}
