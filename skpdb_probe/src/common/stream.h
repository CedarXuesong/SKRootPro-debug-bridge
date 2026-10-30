#pragma once

#include <poll.h>
#include <sys/types.h>

#include <cstddef>

namespace skpdb {

// Byte-stream abstraction for a device-side transport.
//
// The descriptor is owned by the stream and closed by the destructor, so one
// connection is scoped by one Stream object.
class Stream {
public:
    virtual ~Stream();

    Stream(const Stream&) = delete;
    Stream& operator=(const Stream&) = delete;

    // Reads exactly `size` bytes. Returns false on EOF, timeout, error, or
    // when Alive() reports the transport is gone.
    bool ReadExact(void* buffer, size_t size, int timeout_ms);

    // Writes exactly `size` bytes under the same failure rules.
    bool WriteAll(const void* buffer, size_t size, int timeout_ms);

    // False once the underlying transport is unusable. A TCP peer close
    // surfaces as EOF on read instead, so the socket stream keeps the default.
    virtual bool Alive() const { return true; }

    int fd() const { return fd_; }

protected:
    explicit Stream(int fd);

    // Blocks until `events` become ready on fd(). timeout_ms < 0 waits until
    // data arrives.
    virtual bool WaitReady(short events, int timeout_ms);

    // One read/write syscall. Non-blocking descriptors may return -1/EAGAIN,
    // which the callers above treat as "not ready yet".
    virtual ssize_t ReadSome(void* buffer, size_t size) = 0;
    virtual ssize_t WriteSome(const void* buffer, size_t size) = 0;

private:
    int fd_;
};

}  // namespace skpdb
