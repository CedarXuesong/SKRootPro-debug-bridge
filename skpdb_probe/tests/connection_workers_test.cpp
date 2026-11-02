#include "service/connection_workers.h"
#include <chrono>
#include <cstdio>
#include <fcntl.h>
#include <future>
#include <stdexcept>
#include <sys/socket.h>
#include <system_error>
using namespace std::chrono_literals;
void require(bool ok, const char* error) { if (!ok) throw std::runtime_error(error); }
template <typename Test> void until(Test test) {
    auto end = std::chrono::steady_clock::now() + 5s;
    while (!test()) {
        require(std::chrono::steady_clock::now() < end, "worker did not finish");
        std::this_thread::sleep_for(1ms);
    }
}
int main() {
    try {
        skpdb::ConnectionWorkers workers(2);
        int fd[2]; require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
        auto fail = [](auto&, auto) { throw std::system_error(EAGAIN, std::generic_category()); };
        require(!workers.Start(fd[0], [](int f) { close(f); }, [] {}, fail), "launch failure accepted");
        require(workers.Active() == 0 && workers.Failures() == 1, "failed launch leaked capacity");
        require(fcntl(fd[0], F_GETFD) == -1 && errno == EBADF, "failed launch leaked socket");
        close(fd[1]);
        std::atomic<int> handled{0}, created{0};
        auto launch = [&](auto& threads, auto work) { ++created; threads.emplace_back(std::move(work)); };
        for (int i = 0; i < 1200; ++i) {
            require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
            require(workers.Start(fd[0], [&](int f) { close(f); ++handled; }, [] {}, launch), "reconnect rejected");
            close(fd[1]); until([&] { return workers.Active() == 0; });
        }
        require(handled == 1200 && created == 1, "reconnect churn created more threads");
        std::promise<void> entered, proceed; auto released = proceed.get_future().share();
        require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
        require(workers.Start(fd[0], [](int f) { close(f); }, [&] { entered.set_value(); released.wait(); }), "idle test");
        close(fd[1]); entered.get_future().wait();
        require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
        auto start = std::async(std::launch::async, [&] {
            return workers.Start(fd[0], [](int f) { close(f); }, [] {});
        });
        require(start.wait_for(40ms) == std::future_status::timeout, "new session raced last-session cleanup");
        proceed.set_value(); require(start.get(), "admission did not resume"); close(fd[1]);
        until([&] { return workers.Active() == 0; });
        std::promise<void> go; auto wait = go.get_future().share();
        for (int i = 0; i < 2; ++i) {
            require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
            require(workers.Start(fd[0], [wait](int f) { close(f); wait.wait(); }, [] {}), "capacity setup");
            close(fd[1]);
        }
        require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
        require(!workers.Start(fd[0], [](int f) { close(f); }, [] {}), "capacity exceeded"); close(fd[1]);
        go.set_value(); until([&] { return workers.Active() == 0; });
        require(workers.Rejected() == 1, "rejection diagnostics lost");
        require(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0, "socketpair");
        require(workers.Start(fd[0], [](int f) { close(f); throw std::runtime_error("fixture"); }, [] {}), "failure setup");
        close(fd[1]); until([&] { return workers.Active() == 0; });
        require(workers.Failures() == 2, "handler failure escaped or leaked slot");
        puts("PASS bounded persistent workers: 1200 reconnects/one thread, EAGAIN rollback, capacity, handler exception and last-session admission fence");
        return 0;
    } catch (const std::exception& error) { fprintf(stderr, "FAIL %s\n", error.what()); return 1; }
}
