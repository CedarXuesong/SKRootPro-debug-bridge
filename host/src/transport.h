#pragma once

#include <cstddef>
#include <string>

namespace skpdb {

// Byte stream between the host tool and the device. The stream carries the
// framed protocol; the device issues a 32-byte authentication preamble before
// the first frame.
class Transport {
public:
    virtual ~Transport() = default;

    virtual bool ReadExact(void* buffer, size_t size, int timeout_ms) = 0;
    virtual bool WriteAll(const void* buffer, size_t size, int timeout_ms) = 0;
    virtual void Close() = 0;
    virtual std::string Name() const = 0;
};

}  // namespace skpdb
