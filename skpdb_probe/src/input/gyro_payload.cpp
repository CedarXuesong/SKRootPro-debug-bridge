#include "gyro_payload.h"
namespace skpdb::gyro_payload {
static bool finite(float x) { return __builtin_isfinite(x); }
static void multiply(const float *a, const float *b, float *q) {
    q[0] = a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1];
    q[1] = a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0];
    q[2] = a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3];
    q[3] = a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2];
}
static bool normalize(float *q) {
    float n = q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3];
    if (!finite(n) || n < .000001f)
        return false;
    float inv = 1.f / __builtin_sqrtf(n);
    for (unsigned i = 0; i < 4; i++)
        q[i] *= inv;
    return true;
}
__attribute__((section(".text.entry"))) uint32_t Transform(State *s, uint32_t uid, Event *events,
                                                           size_t count, uint64_t now) {
    if (!s || s->magic != kMagic || s->version != kVersion || count > kMaxEvents)
        return 0;
    Control c{};
    bool stable = false;
    for (unsigned attempt = 0; attempt < 3; attempt++) {
        uint32_t before = __atomic_load_n(&s->control.sequence, __ATOMIC_ACQUIRE);
        if (before & 1)
            continue;
        c.enabled = s->control.enabled;
        c.uid = s->control.uid;
        c.stream = s->control.stream;
        c.updated = __atomic_load_n(&s->control.updated, __ATOMIC_RELAXED);
        c.valid_since = __atomic_load_n(&s->control.valid_since, __ATOMIC_RELAXED);
        c.epoch = s->control.epoch;
        for (unsigned i = 0; i < 3; i++)
            c.rate[i] = s->control.rate[i];
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (before == __atomic_load_n(&s->control.sequence, __ATOMIC_RELAXED)) {
            stable = true;
            break;
        }
    }
    if (!stable || c.enabled != 1 || uid != c.uid || !c.uid || !c.stream)
        return 0;
    for (unsigned i = 0; i < 3; i++)
        if (!finite(c.rate[i]) || c.rate[i] < -15 || c.rate[i] > 15)
            return 0;
    const bool live = now >= c.updated && now - c.updated <= kLeaseNs;
    uint32_t expected = 0;
    if (!__atomic_compare_exchange_n(&s->lock, &expected, 1, false, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED))
        return 0;
    uint32_t changed = 0;
    for (size_t n = 0; n < count; n++) {
        Event &e = events[n];
        if (e.version != 104 || e.timestamp <= 0)
            continue;
        const bool sampleLive = live && uint64_t(e.timestamp) >= c.valid_since &&
                                uint64_t(e.timestamp) <= now && now - uint64_t(e.timestamp) <= 50000000;
        if (e.type == 4 || e.type == 16) {
            if (!sampleLive)
                continue;
            bool good = true;
            for (unsigned i = 0; i < 3; i++)
                good = good && finite(e.data[i]);
            if (!good)
                continue;
            for (unsigned i = 0; i < 3; i++)
                e.data[i] += c.rate[i];
            ++changed;
        } else if (e.type == 11 || e.type == 15) {
            Fusion &f = s->fusion[e.type == 15];
            if (f.epoch != c.epoch) {
                f.epoch = c.epoch;
                f.timestamp = e.timestamp;
                f.q[0] = f.q[1] = f.q[2] = 0;
                f.q[3] = 1;
            }
            if (e.timestamp < f.timestamp)
                continue;
            uint64_t begin = uint64_t(f.timestamp);
            uint64_t finish = uint64_t(e.timestamp);
            if (finish > c.updated + kLeaseNs)
                finish = c.updated + kLeaseNs;
            if (sampleLive && finish > begin) {
                float dt = float(finish - begin) * 1e-9f;
                if (dt > .05f)
                    dt = .05f;
                float v[3] = {c.rate[0] * dt * .5f, c.rate[1] * dt * .5f, c.rate[2] * dt * .5f};
                float a = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
                float k = 1 - a / 6 + a * a / 120;
                float d[4] = {v[0] * k, v[1] * k, v[2] * k, 1 - a / 2 + a * a / 24};
                float q[4];
                multiply(f.q, d, q);
                if (normalize(q))
                    for (unsigned i = 0; i < 4; i++)
                        f.q[i] = q[i];
            }
            f.timestamp = e.timestamp;
            float original[4] = {e.data[0], e.data[1], e.data[2], e.data[3]};
            if (!finite(original[3]))
                continue;
            if (original[3] == 0) {
                float v =
                    1 - original[0] * original[0] - original[1] * original[1] - original[2] * original[2];
                if (v >= 0)
                    original[3] = __builtin_sqrtf(v);
            }
            if (!normalize(original))
                continue;
            float q[4];
            multiply(original, f.q, q);
            if (!normalize(q))
                continue;
            for (unsigned i = 0; i < 4; i++)
                e.data[i] = q[i];
            ++changed;
        }
    }
    __atomic_store_n(&s->lock, 0, __ATOMIC_RELEASE);
    __atomic_fetch_add(&s->hits, changed, __ATOMIC_RELAXED);
    return changed;
}
} // namespace skpdb::gyro_payload
