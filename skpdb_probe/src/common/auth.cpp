#include "common/auth.h"

#include <sys/random.h>

#include <array>
#include <cstring>

#include "common/crypto.h"
#include "common/stream.h"

namespace skpdb {
namespace {

// Constant-time comparison; the digest length is public.
bool DigestsEqual(const uint8_t* left, const uint8_t* right, size_t size) {
    uint8_t difference = 0;
    for (size_t index = 0; index < size; ++index) {
        difference |= left[index] ^ right[index];
    }
    return difference == 0;
}

}  // namespace

bool Authenticate(Stream& stream, const std::string& root_key, int timeout_ms) {
    std::array<uint8_t, 32> nonce{};
    if (getrandom(nonce.data(), nonce.size(), 0) != static_cast<ssize_t>(nonce.size())) {
        return false;
    }
    if (!stream.WriteAll(nonce.data(), nonce.size(), timeout_ms)) {
        return false;
    }
    std::array<uint8_t, 32> expected = HmacSha256(root_key.c_str(), nonce.data(), nonce.size());
    std::array<uint8_t, 32> received{};
    if (!stream.ReadExact(received.data(), received.size(), timeout_ms)) {
        return false;
    }
    return DigestsEqual(received.data(), expected.data(), received.size());
}

}  // namespace skpdb
