#include "common/crypto.h"

#include <dlfcn.h>

#include <algorithm>
#include <cstring>

namespace skpdb {
namespace {

// Each block is 64 bytes; the 64-entry message schedule is expanded in place
// with the standard recurrence defined by FIPS 180-4 section 6.2.2.
constexpr uint32_t kRoundConstants[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x2431851c, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
};

}  // namespace

uint32_t Sha256::RotateRight(uint32_t value, int count) {
    return (value >> count) | (value << (32 - count));
}

void Sha256::Update(const uint8_t* data, size_t size) {
    bit_count_ += static_cast<uint64_t>(size) * 8;
    while (size) {
        size_t take = std::min(size, 64 - used_);
        std::memcpy(block_.data() + used_, data, take);
        used_ += take;
        data += take;
        size -= take;
        if (used_ == 64) {
            Transform(block_.data());
            used_ = 0;
        }
    }
}

std::array<uint8_t, 32> Sha256::Finish() {
    // Padding: 0x80, zeros up to byte 56 of the final block, then the message
    // length in bits as a big-endian 64-bit value.
    const uint64_t bits = bit_count_;
    uint8_t marker = 0x80;
    Update(&marker, 1);
    uint8_t zero = 0;
    while (used_ != 56) {
        Update(&zero, 1);
    }
    uint8_t length[8];
    for (int index = 0; index < 8; ++index) {
        length[7 - index] = static_cast<uint8_t>(bits >> (index * 8));
    }
    Update(length, sizeof(length));

    std::array<uint8_t, 32> result{};
    for (int index = 0; index < 8; ++index) {
        for (int byte = 0; byte < 4; ++byte) {
            result[index * 4 + byte] = static_cast<uint8_t>(state_[index] >> (24 - byte * 8));
        }
    }
    return result;
}

void Sha256::Transform(const uint8_t* input) {
    uint32_t words[64];
    for (int index = 0; index < 16; ++index) {
        words[index] = (static_cast<uint32_t>(input[index * 4]) << 24) |
                       (static_cast<uint32_t>(input[index * 4 + 1]) << 16) |
                       (static_cast<uint32_t>(input[index * 4 + 2]) << 8) |
                       static_cast<uint32_t>(input[index * 4 + 3]);
    }
    for (int index = 16; index < 64; ++index) {
        uint32_t sigma0 = RotateRight(words[index - 15], 7) ^ RotateRight(words[index - 15], 18) ^
                          (words[index - 15] >> 3);
        uint32_t sigma1 = RotateRight(words[index - 2], 17) ^ RotateRight(words[index - 2], 19) ^
                          (words[index - 2] >> 10);
        words[index] = words[index - 16] + sigma0 + words[index - 7] + sigma1;
    }

    uint32_t a = state_[0], b = state_[1], c = state_[2], d = state_[3];
    uint32_t e = state_[4], f = state_[5], g = state_[6], h = state_[7];
    for (int index = 0; index < 64; ++index) {
        uint32_t sigma1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
        uint32_t choose = (e & f) ^ (~e & g);
        uint32_t temp1 = h + sigma1 + choose + kRoundConstants[index] + words[index];
        uint32_t sigma0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
        uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
        uint32_t temp2 = sigma0 + majority;
        h = g;
        g = f;
        f = e;
        e = d + temp1;
        d = c;
        c = b;
        b = a;
        a = temp1 + temp2;
    }
    state_[0] += a;
    state_[1] += b;
    state_[2] += c;
    state_[3] += d;
    state_[4] += e;
    state_[5] += f;
    state_[6] += g;
    state_[7] += h;
}

std::array<uint8_t, 32> HmacSha256(const char* key, const uint8_t* message, size_t size) {
    using HmacFn = unsigned char* (*)(const void*, const void*, int, const unsigned char*, size_t,
                                      unsigned char*, unsigned int*);
    using EvpSha256Fn = const void* (*)();

    // libcrypto is resolved lazily; dlopen failure simply selects the local
    // implementation. Static locals make the lookup happen once per process.
    static void* crypto = dlopen("/system/lib64/libcrypto.so", RTLD_NOW | RTLD_LOCAL);
    static HmacFn platform_hmac =
        crypto ? reinterpret_cast<HmacFn>(dlsym(crypto, "HMAC")) : nullptr;
    static EvpSha256Fn platform_sha256 =
        crypto ? reinterpret_cast<EvpSha256Fn>(dlsym(crypto, "EVP_sha256")) : nullptr;
    if (platform_hmac && platform_sha256) {
        std::array<uint8_t, 32> result{};
        unsigned int result_size = 0;
        if (platform_hmac(platform_sha256(), key, static_cast<int>(std::strlen(key)), message, size,
                          result.data(), &result_size) &&
            result_size == result.size()) {
            return result;
        }
    }

    // RFC 2104: keys longer than the block size are hashed first; the block is
    // then XORed with the inner and outer pads.
    uint8_t key_block[64]{};
    size_t key_size = std::strlen(key);
    if (key_size > sizeof(key_block)) {
        Sha256 hash;
        hash.Update(reinterpret_cast<const uint8_t*>(key), key_size);
        auto digest = hash.Finish();
        std::memcpy(key_block, digest.data(), digest.size());
    } else {
        std::memcpy(key_block, key, key_size);
    }

    uint8_t inner_pad[64];
    uint8_t outer_pad[64];
    for (size_t index = 0; index < sizeof(key_block); ++index) {
        inner_pad[index] = key_block[index] ^ 0x36;
        outer_pad[index] = key_block[index] ^ 0x5c;
    }

    Sha256 inner;
    inner.Update(inner_pad, sizeof(inner_pad));
    inner.Update(message, size);
    auto inner_digest = inner.Finish();

    Sha256 outer;
    outer.Update(outer_pad, sizeof(outer_pad));
    outer.Update(inner_digest.data(), inner_digest.size());
    return outer.Finish();
}

}  // namespace skpdb
