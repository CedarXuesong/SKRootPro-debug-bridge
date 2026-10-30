#pragma once

#include "common/stream.h"

namespace skpdb::wireless {

// Stream over an accepted TCP connection. recv/send are used directly so the
// socket error semantics (including MSG_NOSIGNAL) stay intact.
class SocketStream : public Stream {
public:
    explicit SocketStream(int fd);

protected:
    ssize_t ReadSome(void* buffer, size_t size) override;
    ssize_t WriteSome(const void* buffer, size_t size) override;
};

}  // namespace skpdb::wireless
