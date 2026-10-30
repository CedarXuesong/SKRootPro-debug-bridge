#include "touch_guard_payload.h"
namespace skpdb::touch_guard {
extern "C" uint32_t SkpdbTouchGuard(State *s, const Event *e) {
    const uint32_t bit = e->slot < 16 ? 1u << e->slot : 0;
    if (e->owner) {
        if (e->type == 3 && e->code == 57 && bit) {
            if (e->value >= 0) {
                s->tracking[e->slot] = e->value;
                s->owned |= bit;
            } else
                s->owned &= ~bit;
        }
        // Releasing our last contact must not release a real finger's key.
        if (e->type == 1 && (e->code == 330 || e->code == 325) && e->value == 0 && e->active)
            return 1;
        return 0;
    }
    // A hardware slot takeover/lift is authoritative. Never suppress it.
    if (!e->injected) {
        if (e->type == 3 && e->code == 57)
            s->owned &= ~bit;
        return 0;
    }
    const uint64_t deadline = __atomic_load_n(&s->deadline, __ATOMIC_ACQUIRE);
    if (!deadline || e->now >= deadline) {
        s->owned = 0;
        return 0;
    }
    const bool ours = bit && (s->owned & bit) && s->tracking[e->slot] == e->tracking;
    const bool slot_release = e->type == 3 && ((e->code == 57 && e->value < 0) ||
                                               ((e->code == 58 || e->code == 48) && e->value == 0));
    const bool key_release = e->type == 1 && (e->code == 330 || e->code == 325) && e->value == 0;
    // active is the current count of kernel tracking IDs, independent of our
    // local ownership bitmap. This also preserves keys for physical fingers.
    if ((ours && slot_release) || (key_release && s->owned && e->active)) {
        ++s->blocked;
        return 1;
    }
    if (e->type == 3 && e->code == 57)
        s->owned &= ~bit;
    return 0;
}
} // namespace skpdb::touch_guard
