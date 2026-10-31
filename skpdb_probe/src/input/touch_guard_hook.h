#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace skpdb::touch_guard {

// The SDK may skip an initial PAC/BTI instruction. Record both words without
// depending on the private HookContext layout or guessing original opcodes.
template <class Handle> class RestorableHook {
  public:
    bool Pending() const { return pending_; }
    bool Active() const { return pending_ && confirmed_ && !disabled_; }

    template <class Backend>
    bool Install(Backend &backend, uint64_t address, const std::vector<uint8_t> &code,
                 std::string &error) {
        error.clear();
        if (pending_) {
            error = "hook still needs cleanup";
            return false;
        }
        Words before{};
        if (!backend.Read(address, before, error))
            return false;
        address_ = address;
        before_ = before;
        pending_ = true;
        const bool installed = backend.Install(address, code, handle_, error);
        const std::string install_error = error;
        std::string capture_error;
        if (!backend.Read(address_, after_, capture_error)) {
            error = installed ? capture_error : install_error + "; " + capture_error;
            return false;
        }
        if (after_ == before_) {
            // Even an unexpected success without a text change must disable
            // its returned callback handle before forgetting it.
            if (handle_ == Handle{})
                *this = {};
            error = installed ? "hook installation did not change its entry" : install_error;
            return false;
        }
        unsigned changes = 0;
        for (unsigned i = 0; i < before_.size(); ++i) {
            if (before_[i] != after_[i]) {
                ++changes;
                word_ = i;
            }
        }
        confirmed_ = changes == 1 && (after_[word_] & 0xfc000000u) == 0x14000000u;
        if (!confirmed_) {
            error = "cannot confirm the SDK's single branch patch";
            return false;
        }
        if (!installed || handle_ == Handle{}) {
            error = installed ? "hook installation returned no handle" : install_error;
            return false;
        }
        return true;
    }

    template <class Backend> bool Remove(Backend &backend, std::string &error) {
        error.clear();
        if (!pending_)
            return true;
        // Disabling a callback is not text restoration. Keep the handle and
        // snapshots until restoration is read back, including on retry.
        if (!disabled_) {
            if (handle_ != Handle{} && !backend.Disable(handle_, error))
                return false;
            disabled_ = true;
        }
        Words current{};
        if (!backend.Read(address_, current, error))
            return false;
        if (current == before_ && !write_retry_) {
            *this = {};
            return true;
        }
        if (!confirmed_ || (current != after_ && current != before_)) {
            error = "hook entry ownership changed or is unconfirmed; refusing text restore";
            return false;
        }
        // KMP_X can write text and then fail cache synchronization. A matching
        // read alone must not turn that failed write into success on retry.
        write_retry_ = true;
        if (!backend.WriteInstruction(address_ + word_ * sizeof(uint32_t), before_[word_], error))
            return false;
        write_retry_ = false;
        if (!backend.Read(address_, current, error))
            return false;
        if (current != before_) {
            error = "hook entry restoration readback failed";
            return false;
        }
        *this = {};
        return true;
    }

  private:
    using Words = std::array<uint32_t, 2>;
    Handle handle_{};
    uint64_t address_ = 0;
    Words before_{}, after_{};
    unsigned word_ = 0;
    bool pending_ = false, confirmed_ = false, disabled_ = false, write_retry_ = false;
};

} // namespace skpdb::touch_guard
