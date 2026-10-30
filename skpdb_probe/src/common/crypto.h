#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

namespace skpdb {

// SHA-256 as specified by FIPS 180-4. The class is single-use: Update() any
// number of times, then Finish() once.
class Sha256 {
public:
    void Update(const uint8_t* data, size_t size);
    std::array<uint8_t, 32> Finish();

private:
    static uint32_t RotateRight(uint32_t value, int count);
    void Transform(const uint8_t* block);

    std::array<uint32_t, 8> state_{
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    };
    std::array<uint8_t, 64> block_{};
    size_t used_ = 0;
    uint64_t bit_count_ = 0;
};

// HMAC-SHA256 (RFC 2104) over a NUL-terminated key.
//
// The implementation prefers libcrypto.so when the platform exposes it and
// falls back to the in-tree SHA-256 above otherwise, so the module keeps
// working on builds without a public libcrypto symbol table.
std::array<uint8_t, 32> HmacSha256(const char* key, const uint8_t* message, size_t size);

}  // namespace skpdb
