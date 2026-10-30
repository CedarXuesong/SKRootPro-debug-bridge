#ifndef SKPDB_HOST_WINSOCK_H
#define SKPDB_HOST_WINSOCK_H

#include <winsock2.h>

#include <string>

namespace skpdb {

// Winsock has to be initialised once per process before any socket call. Both
// the TCP transport and the knock sender race to be first, so this keeps the
// startup in one place instead of each translation unit guessing who went
// first. The process exits without WSACleanup, which is intentional: the
// reference count would otherwise be decremented while threads may still run.
inline bool EnsureWinsock(std::string& error) {
    static const bool started = [] {
        WSADATA data{};
        return WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();
    if (!started) {
        error = "WSAStartup failed";
    }
    return started;
}

}  // namespace skpdb

#endif  // SKPDB_HOST_WINSOCK_H
