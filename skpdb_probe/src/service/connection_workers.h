#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>
#include <unistd.h>

namespace skpdb {
struct ConnectionThread {
    template <typename Work> void operator()(std::vector<std::thread>& threads, Work work) const {
        threads.emplace_back(std::move(work));
    }
};

// The handler immediately takes ownership of its socket (Stream). The owner
// outlives its workers. Admission and last-client cleanup share a lock, so a
// retiring connection cannot disable a newly admitted control session.
class ConnectionWorkers {
public:
    explicit ConnectionWorkers(int limit) : limit_(limit) { threads_.reserve(limit); }
    ~ConnectionWorkers() {
        { std::lock_guard<std::mutex> guard(mutex_); stopping_ = true; }
        ready_.notify_all();
        for (auto& thread : threads_) thread.join();
    }
    int Active() const { return active_.load(); }
    uint64_t Failures() const { return failures_.load(); }
    uint64_t Rejected() const { return rejected_.load(); }

    template <typename Handler, typename Idle, typename Launch = ConnectionThread>
    bool Start(int fd, Handler handler, Idle idle, Launch launch = {}) noexcept {
        std::lock_guard<std::mutex> guard(mutex_);
        if (stopping_ || active_.load() >= limit_) {
            ++rejected_;
            close(fd);
            return false;
        }
        try {
            jobs_.push_back({fd, std::move(handler), std::move(idle)});
        } catch (...) {
            ++failures_; close(fd); return false;
        }
        ++active_;
        try {
            if (active_.load() > static_cast<int>(threads_.size()))
                launch(threads_, [this] { Run(); });
            ready_.notify_one();
            return true;
        } catch (...) {
            jobs_.pop_back();
            --active_;
            ++failures_;
            close(fd);
            return false;
        }
    }
private:
    struct Job { int fd; std::function<void(int)> handler; std::function<void()> idle; };
    void Run() {
        for (;;) {
            std::unique_lock<std::mutex> guard(mutex_);
            ready_.wait(guard, [this] { return stopping_ || !jobs_.empty(); });
            if (jobs_.empty()) return;
            auto job = std::move(jobs_.front()); jobs_.pop_front();
            guard.unlock();
            try { job.handler(job.fd); } catch (...) { ++failures_; }
            guard.lock();
            if (--active_ == 0) {
                try { job.idle(); } catch (...) { ++failures_; }
            }
        }
    }
    const int limit_;
    std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<Job> jobs_;
    std::vector<std::thread> threads_;
    bool stopping_ = false;
    std::atomic<int> active_{0};
    std::atomic<uint64_t> failures_{0}, rejected_{0};
};
} // namespace skpdb
