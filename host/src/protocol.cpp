#include "protocol.h"

#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace skpdb {
namespace {

constexpr char kTrustedHandshake[] = "SKPT";

bool RecvFrame(Transport& transport, std::vector<uint8_t>& frame, int timeout_ms) {
    uint8_t header[4];
    if (!transport.ReadExact(header, sizeof(header), timeout_ms)) {
        return false;
    }
    uint32_t size = GetU32(header);
    if (size > kMaxFrame) {
        return false;
    }
    frame.resize(size);
    return size == 0 || transport.ReadExact(frame.data(), size, timeout_ms);
}

bool SendFrame(Transport& transport, const std::vector<uint8_t>& frame, int timeout_ms) {
    if (frame.size() > kMaxFrame) {
        return false;
    }
    std::vector<uint8_t> packet;
    packet.reserve(frame.size() + 4);
    PutU32(packet, static_cast<uint32_t>(frame.size()));
    packet.insert(packet.end(), frame.begin(), frame.end());
    return transport.WriteAll(packet.data(), packet.size(), timeout_ms);
}

}  // namespace

const char* MemBackendName(uint8_t backend) {
    switch (backend) {
        case kMemBackendKernel:
            return "kernel";
        case kMemBackendVmReadv:
            return "vm_readv";
        case kMemBackendProcMem:
            return "proc_mem";
        default:
            return "none";
    }
}

std::string HexEncode(const uint8_t* data, size_t size, bool upper) {    const char* digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
    std::string encoded;
    encoded.reserve(size * 2);
    for (size_t index = 0; index < size; ++index) {
        encoded.push_back(digits[data[index] >> 4]);
        encoded.push_back(digits[data[index] & 0x0f]);
    }
    return encoded;
}

std::array<uint8_t, 32> HmacSha256(const std::string& key, const uint8_t* data, size_t size) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr,
                                                  BCRYPT_ALG_HANDLE_HMAC_FLAG);
    if (status < 0 || !algorithm) {
        throw std::runtime_error("BCryptOpenAlgorithmProvider failed");
    }
    std::array<uint8_t, 32> digest{};
    status = BCryptHash(algorithm, reinterpret_cast<PUCHAR>(const_cast<char*>(key.data())),
                        static_cast<ULONG>(key.size()),
                        reinterpret_cast<PUCHAR>(const_cast<uint8_t*>(data)),
                        static_cast<ULONG>(size), digest.data(),
                        static_cast<ULONG>(digest.size()));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0) {
        throw std::runtime_error("BCryptHash failed");
    }
    return digest;
}

bool Authenticate(Transport& transport, const KeyProvider& key_provider, int timeout_ms,
                  std::string& error) {
    std::array<uint8_t, 32> nonce{};
    if (!transport.ReadExact(nonce.data(), nonce.size(), timeout_ms)) {
        error = "no authentication challenge";
        return false;
    }
    std::string key = key_provider ? key_provider() : std::string();
    if (key.empty()) {
        error = "a root key is required for this device";
        return false;
    }
    std::array<uint8_t, 32> digest = HmacSha256(key, nonce.data(), nonce.size());
    if (!transport.WriteAll(digest.data(), digest.size(), timeout_ms)) {
        error = "cannot send the proof";
        return false;
    }
    return true;
}

bool SendRequest(Transport& transport, const std::vector<uint8_t>& request, int timeout_ms) {
    return SendFrame(transport, request, timeout_ms);
}

bool ReadResponse(Transport& transport, uint8_t expected, Response& response, std::string& error,
                  int timeout_ms) {
    std::vector<uint8_t> frame;
    if (!RecvFrame(transport, frame, timeout_ms)) {
        error = "connection closed";
        return false;
    }
    if (frame.size() < 2 || frame[0] != expected) {
        error = "unexpected response";
        return false;
    }
    response.opcode = frame[0];
    if (frame[1] != 0) {
        size_t size = frame.size() >= 6 ? GetU32(frame.data() + 2) : 0;
        size_t available = frame.size() > 6 ? frame.size() - 6 : 0;
        size = std::min(size, available);
        error.assign(reinterpret_cast<const char*>(frame.data() + 6), size);
        return false;
    }
    response.payload.assign(frame.begin() + 2, frame.end());
    return true;
}

Response Request(Transport& transport, const std::vector<uint8_t>& request, uint8_t expected,
                 int timeout_ms) {
    if (!SendRequest(transport, request, timeout_ms)) {
        throw std::runtime_error("send failed");
    }
    Response response;
    std::string error;
    if (!ReadResponse(transport, expected, response, error, timeout_ms)) {
        throw std::runtime_error(error.empty() ? "request failed" : error);
    }
    return response;
}

}  // namespace skpdb
