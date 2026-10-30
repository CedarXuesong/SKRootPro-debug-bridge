#include "common/frame.h"

#include "common/bytes.h"
#include "common/stream.h"

namespace skpdb {

bool RecvFrame(Stream& stream, std::vector<uint8_t>& frame, int timeout_ms) {
    uint8_t header[4];
    if (!stream.ReadExact(header, sizeof(header), timeout_ms)) {
        return false;
    }
    uint32_t size = GetU32(header);
    if (size > kMaxFrame) {
        return false;
    }
    frame.resize(size);
    return size == 0 || stream.ReadExact(frame.data(), size, timeout_ms);
}

bool SendFrame(Stream& stream, const std::vector<uint8_t>& frame, int timeout_ms) {
    if (frame.size() > kMaxFrame) {
        return false;
    }
    std::vector<uint8_t> packet;
    packet.reserve(frame.size() + 4);
    PutU32(packet, static_cast<uint32_t>(frame.size()));
    packet.insert(packet.end(), frame.begin(), frame.end());
    return stream.WriteAll(packet.data(), packet.size(), timeout_ms);
}

}  // namespace skpdb
