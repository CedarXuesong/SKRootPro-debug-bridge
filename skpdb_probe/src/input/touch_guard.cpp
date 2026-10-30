#include "input/touch_guard.h"
#include "input/touch_guard_bytes.h"
#include "input/touch_guard_payload.h"
#include <cstring>
#include <linux/input.h>
#include <ctime>
#include <kernel_module_kit_umbrella.h>
#include <unistd.h>
#include <vector>

namespace skpdb {
namespace {
using namespace asmjit::a64;
using namespace asmjit::a64::Predicate;
using namespace kernel_module;
HookHandle g_hook = nullptr, g_inject = nullptr;
#ifndef SKPDB_EXPERIMENTAL_TOUCH_GUARD
#define SKPDB_EXPERIMENTAL_TOUCH_GUARD 0
#endif
uint64_t g_state = 0, g_device = 0, g_renewed = 0;
std::string g_error;
uint64_t Now() {
    timespec t{};
    clock_gettime(CLOCK_MONOTONIC, &t);
    return uint64_t(t.tv_sec) * 1000000000ull + t.tv_nsec;
}
bool Offset(const char *type, const char *field, uint32_t &out, std::string &error) {
    const auto rc = btf_resolve_member_offset(type, field, out);
    if (is_failed(rc) || out > 8192) {
        error = std::string("touch guard requires BTF ") + type + "." + field;
        return false;
    }
    return true;
}
bool Symbol(const char *name, uint64_t &out, std::string &error) {
    const auto rc = kallsyms_lookup_name(name, out);
    if (is_failed(rc) || !out) {
        error = std::string("touch guard missing ") + name;
        return false;
    }
    return true;
}
// Scope a software event using the documented call-original API. The SDK
// changes LR in callbacks, so LR must never be used to identify the caller.
std::vector<uint8_t> InjectScope(uint64_t device, uint32_t handle_dev) {
    auto ctx = init_aarch64_asm();
    auto *a = ctx.assembler();
    auto pass = a->newLabel();
    arm64_before_hook_start(a);
    a->ldr(x9, ptr(x0, handle_dev));
    aarch64_asm_mov_x(a, x10, device);
    a->cmp(x9, x10);
    a->b(CondCode::kNE, pass);
    {
        RegProtectGuard preserved(a, x19, x20);
        {
            RegProtectGuard args(a, x0, x1, x2, x3, x4, x5, x6, x7, x30);
            export_symbol::get_current(a, x19);
            a->mov(w20, 0);
            auto busy = a->newLabel(), again = a->newLabel();
            a->cbz(x19, busy);
            aarch64_asm_mov_x(a, x9, g_state + offsetof(touch_guard::State, writer_task));
            a->mov(w11, 4);
            a->bind(again);
            a->ldaxr(x10, ptr(x9));
            a->cbnz(x10, busy);
            // This SDK's AsmJit requires matching operand widths; the encoded status is W10.
            a->stlxr(x10, x19, ptr(x9));
            auto acquired = a->newLabel();
            a->cbz(w10, acquired);
            a->sub(w11, w11, 1);
            a->cbnz(w11, again);
            a->b(busy);
            a->bind(acquired);
            a->mov(w20, 1);
            a->bind(busy);
            a->clrex(Imm(15));
        }
        arm64_emit_call_original(a);
        auto done = a->newLabel();
        a->cbz(w20, done);
        aarch64_asm_mov_x(a, x9, g_state + offsetof(touch_guard::State, writer_task));
        a->stlr(xzr, ptr(x9));
        a->bind(done);
    }
    arm64_before_hook_end(a, false);
    a->bind(pass);
    arm64_before_hook_end(a, true);
    return aarch64_asm_to_bytes(a);
}

} // namespace

bool TouchGuardRenew(std::string &error) {
    if (!SKPDB_EXPERIMENTAL_TOUCH_GUARD)
        return true;
    if (!g_hook || !g_state) {
        error = "touch guard is not installed";
        return false;
    }
    const auto now = Now();
    if (now - g_renewed < 250000000ull)
        return true;
    const auto rc = write_kernel_rw_mem_atomic64(g_state, now + 3000000000ull);
    if (is_failed(rc)) {
        error = "touch guard lease write failed: " + to_string(rc);
        g_error = error;
        return false;
    }
    g_renewed = now;
    return true;
}

bool TouchGuardInstall(uint64_t device, std::string &error) {
    if (!SKPDB_EXPERIMENTAL_TOUCH_GUARD)
        return true;
    if (!g_hook && g_inject) {
        error = "touch scope hook still needs detachment";
        return false;
    }
    if (g_hook) {
        if (device != g_device) {
            error = "touch guard belongs to another input device";
            return false;
        }
        return TouchGuardRenew(error);
    }
    uint32_t dev_mt = 0, mt_slot = 0, mt_num = 0, mt_slots = 0, slot_abs = 0, slot_size = 0,
             task_tgid = 0;
    uint32_t handle_dev = 0, thread_info = 0, preempt = 0;
    if (!Offset("input_handle", "dev", handle_dev, error) ||
        !Offset("task_struct", "thread_info", thread_info, error) ||
        !Offset("thread_info", "preempt_count", preempt, error) ||
        !Offset("input_dev", "mt", dev_mt, error) || !Offset("input_mt", "slot", mt_slot, error) ||
        !Offset("input_mt", "num_slots", mt_num, error) ||
        !Offset("input_mt", "slots", mt_slots, error) ||
        !Offset("input_mt_slot", "abs", slot_abs, error) ||
        !Offset("task_struct", "tgid", task_tgid, error) ||
        is_failed(btf_resolve_struct_size("input_mt_slot", slot_size)) || slot_size < 56 ||
        slot_size > 512) {
        if (error.empty())
            error = "touch guard cannot validate input_mt_slot layout";
        return false;
    }
    uint64_t mt = 0;
    uint32_t count = 0;
    if (is_failed(read_kernel_mem(device + dev_mt, &mt, sizeof(mt))) || !mt ||
        is_failed(read_kernel_mem(mt + mt_num, &count, sizeof(count))) || !count || count > 64) {
        error = "touch guard invalid touchscreen multitouch state";
        return false;
    }
    uint64_t event = 0, inject = 0, clock = 0;
    if (!Symbol("input_handle_event", event, error) ||
        !Symbol("input_inject_event", inject, error) ||
        !Symbol("ktime_get_mono_fast_ns", clock, error))
        return false;
    if (is_failed(alloc_kernel_mem(sizeof(touch_guard::State), g_state)) || !g_state) {
        error = "touch guard state allocation failed";
        return false;
    }
    touch_guard::State initial{};
    if (is_failed(write_kernel_mem(g_state, &initial, sizeof(initial)))) {
        free_kernel_mem(g_state);
        g_state = 0;
        error = "touch guard state initialization failed";
        return false;
    }
    auto ctx = init_aarch64_asm();
    auto *a = ctx.assembler();
    auto pass = a->newLabel(), drop = a->newLabel(), helper = a->newLabel();
    arm64_before_hook_start(a);
    aarch64_asm_mov_x(a, x8, device);
    a->cmp(x0, x8);
    a->b(CondCode::kNE, pass);
    {
        RegProtectGuard saved(a, x0, x1, x2, x3, x4, x5, x6, x7, x19, x30);
        // Preserve argument registers and our x19 explicitly. The helper uses
        // the normal callee-save ABI, no SIMD, allocation or sleeping calls.
        StackBufGuard input(a, 64, x19);
        a->str(w1, ptr(x19, offsetof(touch_guard::Event, type)));
        a->str(w2, ptr(x19, offsetof(touch_guard::Event, code)));
        a->str(w3, ptr(x19, offsetof(touch_guard::Event, value)));
        export_symbol::get_current(a, x8);
        a->mov(w9, 0);
        a->mov(w10, 0);
        auto source_done = a->newLabel();
        a->cbz(x8, source_done);
        // IRQ/softirq context must pass even if it interrupted a marked writer.
        a->ldr(w9, ptr(x8, thread_info + preempt));
        aarch64_asm_mov_w(a, w10, 0x00ffff00);
        a->tst(w9, w10);
        a->mov(w9, 0);
        a->mov(w10, 0);
        a->b(CondCode::kNE, source_done);
        aarch64_asm_mov_x(a, x11, g_state + offsetof(touch_guard::State, writer_task));
        a->ldar(x11, ptr(x11));
        a->cmp(x11, x8);
        a->cset(w10, CondCode::kEQ);
        a->ldr(w11, ptr(x8, task_tgid));
        aarch64_asm_mov_w(a, w12, getpid());
        a->cmp(w11, w12);
        a->cset(w9, CondCode::kEQ);
        a->bind(source_done);
        a->str(w9, ptr(x19, offsetof(touch_guard::Event, owner)));
        a->str(w10, ptr(x19, offsetof(touch_guard::Event, injected)));
        aarch64_asm_mov_x(a, x8, device);
        a->ldr(x8, ptr(x8, dev_mt));
        // Validated during installation; drivers keep the mt allocation for
        // the lifetime of this registered touchscreen.
        a->ldr(w9, ptr(x8, mt_slot));
        a->str(w9, ptr(x19, offsetof(touch_guard::Event, slot)));
        a->mov(w10, slot_size);
        a->umaddl(x10, w9, w10, x8);
        a->ldr(w10, ptr(x10, mt_slots + slot_abs + (ABS_MT_TRACKING_ID - ABS_MT_TOUCH_MAJOR) * sizeof(int32_t)));
        a->str(w10, ptr(x19, offsetof(touch_guard::Event, tracking)));
        a->mov(w10, 0);
        a->mov(w11, 0);
        auto loop = a->newLabel(), counted = a->newLabel();
        a->bind(loop);
        a->cmp(w11, count);
        a->b(CondCode::kHS, counted);
        a->mov(w12, slot_size);
        a->umaddl(x12, w11, w12, x8);
        a->ldr(w12, ptr(x12, mt_slots + slot_abs + (ABS_MT_TRACKING_ID - ABS_MT_TOUCH_MAJOR) * sizeof(int32_t)));
        a->cmp(w12, 0);
        a->cinc(w10, w10, CondCode::kGE);
        a->add(w11, w11, 1);
        a->b(loop);
        a->bind(counted);
        a->str(w10, ptr(x19, offsetof(touch_guard::Event, active)));
        aarch64_asm_mov_x(a, x11, clock);
        aarch64_asm_safe_blr(a, x11);
        a->str(x0, ptr(x19, offsetof(touch_guard::Event, now)));
        aarch64_asm_mov_x(a, x0, g_state);
        a->mov(x1, x19);
        a->adr(x11, helper);
        aarch64_asm_safe_blr(a, x11);
        a->mov(w8, w0);
    }
    a->cbnz(w8, drop);
    a->bind(pass);
    arm64_before_hook_end(a, true);
    a->bind(drop);
    arm64_before_hook_end(a, false);
    a->bind(helper);
    a->embed(kTouchGuardPayload, sizeof(kTouchGuardPayload));
    const auto bytes = aarch64_asm_to_bytes(a);
    auto rc =
        install_kernel_function_before_hook(inject, InjectScope(device, handle_dev), &g_inject);
    if (is_ok(rc) && g_inject)
        rc = install_kernel_function_before_hook(event, bytes, &g_hook);
    if (is_failed(rc) || !g_hook) {
        // No hook owns the state when installation reports failure.
        if (g_inject) {
            const auto detached = uninstall_kernel_hook(g_inject);
            if (is_failed(detached)) {
                error = "touch scope rollback failed: " + to_string(detached);
                return false;
            }
            g_inject = nullptr;
        }
        // Retain disabled state if callback grace has not been documented.
        g_state = 0;
        g_hook = nullptr;
        error = "touch guard installation failed: " + to_string(rc);
        return false;
    }
    g_device = device;
    g_renewed = 0;
    g_error.clear();
    return TouchGuardRenew(error);
}

bool TouchGuardRemove(std::string &error) {
    if (!g_hook && !g_inject)
        return true;
    // Fail open before requesting detachment. Never free memory still owned
    // by a hook when uninstallation fails.
    const auto disabled = write_kernel_rw_mem_atomic64(g_state, 0);
    const auto removed = g_hook ? uninstall_kernel_hook(g_hook) : KModErr::OK;
    if (is_failed(removed)) {
        error = "touch guard detach failed: " + to_string(removed);
        g_error = error;
        return false;
    }
    g_hook = nullptr;
    const auto scope_removed = g_inject ? uninstall_kernel_hook(g_inject) : KModErr::OK;
    if (is_failed(scope_removed)) {
        error = "touch scope detach failed: " + to_string(scope_removed);
        g_error = error;
        return false;
    }
    g_inject = nullptr;
    g_device = 0;
    g_renewed = 0;
    // Retain the tiny disabled state. The SDK's API does not
    // document a grace period for an in-flight callback.
    g_state = 0;
    if (is_failed(disabled)) {
        error = "touch guard lease disable failed";
        return false;
    }
    return true;
}
std::string TouchGuardReport() {
    touch_guard::State state{};
    const bool read = g_state && is_ok(read_kernel_mem(g_state, &state, sizeof(state)));
    return std::string("release_guard=") + (g_hook ? "active" : "off") +
           "\nrelease_guard_verification=pending-device-retest\n" +
           "release_guard_experimental=" + std::string(SKPDB_EXPERIMENTAL_TOUCH_GUARD ? "1" : "0") +
           "\nrelease_guard_blocked=" + std::to_string(read ? state.blocked : 0) +
           "\nrelease_guard_error=" + g_error + "\nvirtual_device=0\n";
}
} // namespace skpdb
