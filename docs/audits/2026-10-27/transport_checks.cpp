#include "common/auth.h"
#include "wireless/socket_stream.h"

#include <sys/socket.h>
#include <unistd.h>
#include <chrono>
#include <cstdlib>
#include <future>
#include <iostream>

using namespace std::chrono_literals;

int main() {
    int pair[2];
    if (socketpair(AF_UNIX, SOCK_STREAM, 0, pair) != 0) return 2;
    timeval timeout{0, 50000};
    setsockopt(pair[0], SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    skpdb::wireless::SocketStream server(pair[0]);
    auto auth = std::async(std::launch::async, [&] {
        return skpdb::Authenticate(server, "offline-audit-key", -1);
    });
    unsigned char nonce[32];
    if (recv(pair[1], nonce, sizeof(nonce), MSG_WAITALL) != sizeof(nonce)) return 3;
    const bool stalled = auth.wait_for(250ms) == std::future_status::timeout;
    shutdown(pair[1], SHUT_RDWR);
    close(pair[1]);
    const bool accepted = auth.get();
    if (!stalled || accepted) return 4;
    std::cout << "REPRODUCED transport: 50 ms SO_RCVTIMEO did not expire poll(-1) after 250 ms\n";

    if (socketpair(AF_UNIX, SOCK_STREAM, 0, pair) != 0) return 5;
    skpdb::wireless::SocketStream bounded(pair[0]);
    const auto started = std::chrono::steady_clock::now();
    const bool bounded_result = skpdb::Authenticate(bounded, "offline-audit-key", 50);
    const auto elapsed = std::chrono::steady_clock::now() - started;
    close(pair[1]);
    if (bounded_result || elapsed > 1s) return 6;
    std::cout << "CONTROL transport: explicit 50 ms authentication timeout returned normally\n";
}
