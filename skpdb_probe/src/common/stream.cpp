#include "common/stream.h"

#include <unistd.h>

#include <cerrno>

namespace skpdb {

Stream::Stream(int fd) : fd_(fd) {}

Stream::~Stream() {
    if (fd_ >= 0) {
        close(fd_);
    }
}

bool Stream::ReadExact(void* buffer, size_t size, int timeout_ms) {
    auto* cursor = static_cast<uint8_t*>(buffer);
    while (size) {
        if (!WaitReady(POLLIN, timeout_ms)) {
            return false;
        }
        ssize_t count = ReadSome(cursor, size);
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
            continue;
        }
        if (count <= 0) {
            return false;
        }
        cursor += count;
        size -= static_cast<size_t>(count);
    }
    return true;
}

bool Stream::WriteAll(const void* buffer, size_t size, int timeout_ms) {
    auto* cursor = static_cast<const uint8_t*>(buffer);
    while (size) {
        if (!WaitReady(POLLOUT, timeout_ms)) {
            return false;
        }
        ssize_t count = WriteSome(cursor, size);
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
            continue;
        }
        if (count <= 0) {
            return false;
        }
        cursor += count;
        size -= static_cast<size_t>(count);
    }
    return true;
}

bool Stream::WaitReady(short events, int timeout_ms) {
    for (;;) {
        struct pollfd descriptor{fd_, events, 0};
        int result = poll(&descriptor, 1, timeout_ms);
        if (result < 0 && errno == EINTR) {
            continue;
        }
        if (result <= 0) {
            return false;
        }
        // Drain pending bytes before treating a hangup as fatal; a peer that
        // sent a final frame and closed still gets that frame delivered.
        if (descriptor.revents & events) {
            return true;
        }
        if (descriptor.revents & (POLLERR | POLLHUP | POLLNVAL)) {
            return false;
        }
        return false;
    }
}

}  // namespace skpdb
