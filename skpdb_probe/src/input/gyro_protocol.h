#pragma once
#include <stdint.h>
#include <cmath>
namespace skpdb::gyro_protocol {
inline bool Newer(uint32_t candidate, uint32_t previous) { return int32_t(candidate - previous) > 0; }
inline bool Rates(float x, float y, float z) {
    return std::isfinite(x) && std::isfinite(y) && std::isfinite(z) && std::fabs(x) <= 15 &&
           std::fabs(y) <= 15 && std::fabs(z) <= 15;
}
} // namespace skpdb::gyro_protocol
