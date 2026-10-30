#include "wireless/socket_stream.h"

#include <sys/socket.h>
#include <unistd.h>

namespace skpdb::wireless {

SocketStream::SocketStream(int fd) : Stream(fd) {}

ssize_t SocketStream::ReadSome(void* buffer, size_t size) {
    return recv(fd(), buffer, size, 0);
}

ssize_t SocketStream::WriteSome(const void* buffer, size_t size) {
    // MSG_NOSIGNAL keeps a closed peer from raising SIGPIPE in the service.
    return send(fd(), buffer, size, MSG_NOSIGNAL);
}

}  // namespace skpdb::wireless
