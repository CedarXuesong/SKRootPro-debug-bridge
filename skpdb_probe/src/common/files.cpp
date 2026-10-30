#include "common/files.h"

#include <fcntl.h>
#include <unistd.h>

#include <array>
#include <cerrno>
#include <cstring>

namespace skpdb {

std::string ReadTextFile(const std::string& path) {
    int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return {};
    }
    char buffer[4096]{};
    ssize_t length;
    do {
        length = read(fd, buffer, sizeof(buffer) - 1);
    } while (length < 0 && errno == EINTR);
    close(fd);
    if (length <= 0) {
        return {};
    }
    std::string value(buffer, static_cast<size_t>(length));
    while (!value.empty() && (value.back() == '\n' || value.back() == '\r')) {
        value.pop_back();
    }
    return value;
}

std::string ReadFileFully(const std::string& path) {
    int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return {};
    }
    std::string data;
    std::array<char, 512> buffer{};
    for (;;) {
        ssize_t count = read(fd, buffer.data(), buffer.size());
        if (count == 0) {
            break;
        }
        if (count < 0) {
            if (errno == EINTR) {
                continue;
            }
            close(fd);
            return {};
        }
        data.append(buffer.data(), static_cast<size_t>(count));
    }
    close(fd);
    return data;
}

bool WritePrivateFile(const std::string& path, const std::string& data) {
    if (path.empty()) {
        return false;
    }
    const std::string temporary = path + ".tmp";
    int fd = open(temporary.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0) {
        return false;
    }
    size_t offset = 0;
    while (offset < data.size()) {
        ssize_t count = write(fd, data.data() + offset, data.size() - offset);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            break;
        }
        offset += static_cast<size_t>(count);
    }
    bool ok = offset == data.size() && fsync(fd) == 0;
    close(fd);
    if (ok) {
        ok = rename(temporary.c_str(), path.c_str()) == 0;
    }
    if (!ok) {
        unlink(temporary.c_str());
    }
    return ok;
}

bool WriteAttribute(const std::string& path, const char* text) {
    int fd = open(path.c_str(), O_WRONLY | O_CLOEXEC);
    if (fd < 0) {
        return false;
    }
    size_t size = std::strlen(text);
    const char* data = text;
    if (size == 0) {
        data = "\n";
        size = 1;
    }
    bool ok = write(fd, data, size) == static_cast<ssize_t>(size);
    close(fd);
    return ok;
}

}  // namespace skpdb
