// Offline lifecycle tests. No SDK, device, or kernel writes are involved.
#include "../src/input/touch_guard_hook.h"
#include <cassert>
#include <cstdio>

namespace {
using Hook = skpdb::touch_guard::RestorableHook<int>;
using Words = std::array<uint32_t, 2>;
constexpr uint64_t kEntry = 0x1000;
constexpr uint32_t kBranch = 0x14000040, kForeignBranch = 0x14000080;

struct Backend {
    Words original{0xd503233f, 0xa9bf7bfd}, text = original;
    unsigned patch_word = 0, reads = 0, disables = 0, writes = 0, installs = 0;
    unsigned fail_read = 0;
    uint32_t branch = kBranch;
    bool install_ok = true, return_handle = true, change_text = true;
    bool disable_ok = true, write_ok = true, ignore_write = false, sdk_restores = false;
    bool write_before_failure = false;
    bool Read(uint64_t address, Words &out, std::string &error) {
        assert(address == kEntry);
        if (++reads == fail_read) {
            error = "read failed";
            return false;
        }
        out = text;
        return true;
    }
    bool Install(uint64_t address, const std::vector<uint8_t> &, int &handle, std::string &error) {
        assert(address == kEntry);
        ++installs;
        handle = return_handle ? 7 : 0;
        if (change_text)
            text[patch_word] = branch;
        if (!install_ok)
            error = "install failed";
        return install_ok;
    }
    bool Disable(int handle, std::string &error) {
        assert(handle == 7);
        ++disables;
        if (!disable_ok) {
            error = "disable failed";
            return false;
        }
        if (sdk_restores)
            text = original;
        return true;
    }
    bool WriteInstruction(uint64_t address, uint32_t word, std::string &error) {
        assert(address == kEntry + patch_word * sizeof(uint32_t));
        assert(word == original[patch_word]);
        assert(disables || !return_handle);
        ++writes;
        if (!write_ok) {
            if (write_before_failure)
                text[patch_word] = word;
            error = "write failed";
            return false;
        }
        if (!ignore_write)
            text[patch_word] = word;
        return true;
    }
};

void entry_restoration() {
    // Entry, PAC/BTI + entry, and an existing unrelated branch are all restored
    // to their exact pre-install bytes, rather than an assumed clean prologue.
    for (unsigned word : {0u, 1u}) {
        for (uint32_t original : {0xd503233fu, kForeignBranch}) {
            Backend backend;
            backend.patch_word = word;
            backend.original[word] = original;
            backend.text = backend.original;
            Hook hook;
            std::string error;
            assert(hook.Install(backend, kEntry, {}, error) && hook.Active());
            assert(hook.Remove(backend, error) && !hook.Pending());
            assert(backend.text == backend.original && backend.writes == 1);
            assert(hook.Remove(backend, error) && backend.disables == 1);
            assert(hook.Install(backend, kEntry, {}, error));
            assert(hook.Remove(backend, error) && backend.text == backend.original);
        }
    }
}

void retry_and_ownership() {
    Backend backend;
    Hook hook;
    std::string error;
    assert(hook.Install(backend, kEntry, {}, error));
    backend.disable_ok = false;
    assert(!hook.Remove(backend, error) && hook.Pending() && backend.writes == 0);
    backend.disable_ok = true;
    backend.write_ok = false;
    assert(!hook.Remove(backend, error) && hook.Pending() && !hook.Active());
    assert(!hook.Install(backend, kEntry, {}, error) && backend.installs == 1);
    backend.write_ok = true;
    // A newer owner's branch must never be overwritten.
    backend.text[0] = kForeignBranch;
    assert(!hook.Remove(backend, error) && backend.writes == 1);
    assert(backend.text[0] == kForeignBranch);
    backend.text[0] = kBranch;
    backend.text[1] ^= 1;
    assert(!hook.Remove(backend, error) && backend.writes == 1);
    backend.text[1] = backend.original[1];
    backend.fail_read = backend.reads + 2; // Restore succeeds, readback fails.
    assert(!hook.Remove(backend, error) && hook.Pending());
    assert(backend.text == backend.original);
    assert(hook.Remove(backend, error) && !hook.Pending());
    assert(backend.disables == 2 && backend.writes == 2);
}

void rollback_and_confirmation() {
    // The second hook can fail after its entry was patched. Roll back both.
    Backend scope, event;
    event.patch_word = 1;
    event.install_ok = false;
    Hook scope_hook, event_hook;
    std::string error;
    assert(scope_hook.Install(scope, kEntry, {}, error));
    assert(!event_hook.Install(event, kEntry, {}, error) && event_hook.Pending());
    assert(event_hook.Remove(event, error) && scope_hook.Remove(scope, error));
    assert(scope.text == scope.original && event.text == event.original);

    Backend backend;
    Hook hook;
    assert(hook.Install(backend, kEntry, {}, error));
    backend.ignore_write = true;
    assert(!hook.Remove(backend, error) && hook.Pending());
    backend.ignore_write = false;
    assert(hook.Remove(backend, error) && backend.disables == 1);

    Backend future_sdk;
    future_sdk.sdk_restores = true;
    assert(hook.Install(future_sdk, kEntry, {}, error));
    assert(hook.Remove(future_sdk, error) && future_sdk.writes == 0);

    Backend cache_failure;
    assert(hook.Install(cache_failure, kEntry, {}, error));
    cache_failure.write_ok = false;
    cache_failure.write_before_failure = true;
    assert(!hook.Remove(cache_failure, error) && hook.Pending());
    assert(cache_failure.text == cache_failure.original);
    cache_failure.write_ok = true;
    assert(hook.Remove(cache_failure, error) && cache_failure.writes == 2);
    assert(cache_failure.disables == 1);
}

void incomplete_installation() {
    Backend backend;
    Hook hook;
    std::string error;
    backend.fail_read = 1; // No install without a readable original.
    assert(!hook.Install(backend, kEntry, {}, error) && !hook.Pending());
    assert(backend.installs == 0);
    backend.fail_read = backend.reads + 2; // Lost post-install snapshot.
    assert(!hook.Install(backend, kEntry, {}, error) && hook.Pending());
    assert(!hook.Remove(backend, error) && backend.disables == 1 && backend.writes == 0);
    assert(!hook.Install(backend, kEntry, {}, error) && backend.installs == 1);
    // Recovery can finish once an external owner has restored our baseline.
    backend.text = backend.original;
    assert(hook.Remove(backend, error));

    Backend no_handle;
    no_handle.return_handle = false;
    assert(!hook.Install(no_handle, kEntry, {}, error) && hook.Pending());
    assert(hook.Remove(no_handle, error) && no_handle.text == no_handle.original);

    Backend unchanged;
    unchanged.change_text = false;
    unchanged.install_ok = false;
    unchanged.return_handle = false;
    assert(!hook.Install(unchanged, kEntry, {}, error) && !hook.Pending());
    assert(hook.Remove(unchanged, error) && unchanged.writes == 0 && unchanged.disables == 0);

    Backend unexpected;
    unexpected.change_text = false;
    assert(!hook.Install(unexpected, kEntry, {}, error) && hook.Pending());
    assert(hook.Remove(unexpected, error) && unexpected.disables == 1);

    Backend unsupported;
    unsupported.branch = 0xd503201f; // Not the SDK's documented B instruction.
    assert(!hook.Install(unsupported, kEntry, {}, error) && hook.Pending());
    assert(!hook.Remove(unsupported, error) && unsupported.writes == 0);
    assert(!hook.Active() && unsupported.disables == 1);
    unsupported.text = unsupported.original;
    assert(hook.Remove(unsupported, error));
}
} // namespace

int main() {
    entry_restoration();
    retry_and_ownership();
    rollback_and_confirmation();
    incomplete_installation();
    std::puts("PASS hook cleanup: entry/+4 snapshots, exact restoration, rollback, "
              "retry, ownership changes and readback failures");
}
