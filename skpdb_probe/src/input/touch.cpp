#include "input/touch.h"
#include "input/touch_guard.h"
#include "input/touch_state.h"

#include <dirent.h>
#include <fcntl.h>
#include <linux/input.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <kernel_module_kit_umbrella.h>

namespace skpdb {
namespace {

using namespace asmjit::a64;
using namespace touch_detail;

// A gesture is a sequence of complete input frames. Each frame is one kernel
// entry that calls input_event() several times and ends with SYN_REPORT, so a
// frame reaches evdev atomically from the target's point of view.

struct TouchBackend {
    bool ready = false;
    int write_fd = -1;

    std::string name;
    std::string event_path;
    int32_t x_min = 0;
    int32_t x_max = 0;
    int32_t y_min = 0;
    int32_t y_max = 0;
    int32_t pressure_max = 0;
    int32_t touch_major_max = 0;
    int32_t slot_max = 0;
    bool has_btn_touch = false;
    bool has_btn_tool_finger = false;
    bool has_pressure = false;
    bool has_touch_major = false;
    uint32_t screen_width = 0;
    uint32_t screen_height = 0;

    // Kernel side. input_dev is the real touchscreen, input_event_addr the
    // resolved EXPORT_SYMBOL address used by the generated frame.
    uint64_t input_dev = 0;
    uint64_t input_event_addr = 0;
};

std::mutex &TouchMutex() {
    static std::mutex mutex;
    return mutex;
}

TouchBackend g_backend;

// The input core's BTN_TOUCH state is global, not per slot. Emitting a lift for
// one finger must therefore not clear BTN_TOUCH while another finger is still
// down. Track live slots so multi-touch gestures stay independent.
std::array<bool, 16> g_slot_down{};
int g_slots_down = 0;
std::array<int32_t, 16> g_tracking{};
std::array<int, 16> g_physical{};
uint32_t g_frame_sequence = 0;

void ResetSlotState() {
    g_slot_down.fill(false);
    g_tracking.fill(-1);
    g_physical.fill(-1);
    g_slots_down = 0;
}

std::string ErrText(const char *step, KModErr err) {
    return std::string(step) + ": " + to_string(err);
}

bool MaskBit(const std::vector<std::string> &words, unsigned bit) {
    // /proc/bus/input/devices prints the bitmap most significant word first;
    // the last word holds bits 0..63.
    if (words.empty()) {
        return false;
    }
    const size_t index = words.size() - 1 - bit / 64;
    if (index >= words.size()) {
        return false;
    }
    const unsigned long long value = std::strtoull(words[index].c_str(), nullptr, 16);
    return ((value >> (bit % 64)) & 1u) != 0;
}

bool TestBit(const uint8_t *bits, unsigned bit) { return (bits[bit / 8] >> (bit % 8)) & 1u; }

int EventIndex(const std::string &handlers) {
    size_t at = handlers.find("event");
    while (at != std::string::npos) {
        size_t end = at + 5;
        while (end < handlers.size() && handlers[end] >= '0' && handlers[end] <= '9') {
            ++end;
        }
        if (end > at + 5) {
            return std::atoi(handlers.substr(at + 5, end - at - 5).c_str());
        }
        at = handlers.find("event", at + 5);
    }
    return -1;
}

struct InputBlock {
    std::string name;
    std::string handlers;
    uint32_t prop = 0;
    std::vector<std::string> abs_words;

    bool IsTouchscreen() const {
        if (EventIndex(handlers) < 0) {
            return false;
        }
        return MaskBit(abs_words, ABS_MT_POSITION_X) && MaskBit(abs_words, ABS_MT_POSITION_Y);
    }

    bool Direct() const { return (prop & INPUT_PROP_DIRECT) != 0; }
};

std::vector<std::string> SplitWords(const std::string &text) {
    std::vector<std::string> words;
    size_t at = 0;
    while (at < text.size()) {
        while (at < text.size() && (text[at] == ' ' || text[at] == '\t')) {
            ++at;
        }
        size_t end = at;
        while (end < text.size() && text[end] != ' ' && text[end] != '\t') {
            ++end;
        }
        if (end > at) {
            words.push_back(text.substr(at, end - at));
        }
        at = end;
    }
    return words;
}

bool DiscoverEventDevice(TouchBackend &backend, std::string &error) {
    std::ifstream input("/proc/bus/input/devices");
    if (!input) {
        error = "/proc/bus/input/devices: " + std::string(std::strerror(errno));
        return false;
    }

    std::vector<InputBlock> blocks;
    InputBlock current;
    std::string line;
    auto flush = [&]() {
        if (!current.name.empty()) {
            blocks.push_back(current);
        }
        current = InputBlock();
    };
    while (std::getline(input, line)) {
        if (line.empty()) {
            flush();
            continue;
        }
        if (line.rfind("N: Name=\"", 0) == 0) {
            size_t end = line.rfind('"');
            if (end > 9) {
                current.name = line.substr(9, end - 9);
            }
        } else if (line.rfind("H: Handlers=", 0) == 0) {
            current.handlers = line.substr(12);
        } else if (line.rfind("B: PROP=", 0) == 0) {
            current.prop = static_cast<uint32_t>(std::strtoul(line.c_str() + 8, nullptr, 16));
        } else if (line.rfind("B: ABS=", 0) == 0) {
            current.abs_words = SplitWords(line.substr(7));
        }
    }
    flush();

    const InputBlock *chosen = nullptr;
    for (const InputBlock &block : blocks) {
        if (!block.IsTouchscreen()) {
            continue;
        }
        if (chosen == nullptr || (block.Direct() && !chosen->Direct())) {
            chosen = &block;
        }
    }
    if (chosen == nullptr) {
        error = "no touchscreen with ABS_MT_POSITION_X/Y in /proc/bus/input/devices";
        return false;
    }

    const int index = EventIndex(chosen->handlers);
    if (index < 0) {
        error = "touchscreen '" + chosen->name + "' has no event handler";
        return false;
    }

    backend.name = chosen->name;
    backend.event_path = "/dev/input/event" + std::to_string(index);

    const int fd = open(backend.event_path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        error = backend.event_path + ": " + std::strerror(errno);
        return false;
    }

    auto read_abs = [&](unsigned code, input_absinfo &info) {
        std::memset(&info, 0, sizeof(info));
        return ioctl(fd, EVIOCGABS(code), &info) == 0;
    };

    input_absinfo axis{};
    if (!read_abs(ABS_MT_POSITION_X, axis)) {
        error = backend.event_path + ": EVIOCGABS(ABS_MT_POSITION_X): " + std::strerror(errno);
        close(fd);
        return false;
    }
    backend.x_min = axis.minimum;
    backend.x_max = axis.maximum;

    if (!read_abs(ABS_MT_POSITION_Y, axis)) {
        error = backend.event_path + ": EVIOCGABS(ABS_MT_POSITION_Y): " + std::strerror(errno);
        close(fd);
        return false;
    }
    backend.y_min = axis.minimum;
    backend.y_max = axis.maximum;

    if (read_abs(ABS_MT_SLOT, axis)) {
        backend.slot_max = axis.maximum;
    }
    if (read_abs(ABS_MT_PRESSURE, axis)) {
        backend.pressure_max = axis.maximum;
        backend.has_pressure = backend.pressure_max > 0;
    }
    if (read_abs(ABS_MT_TOUCH_MAJOR, axis)) {
        backend.touch_major_max = axis.maximum;
        backend.has_touch_major = backend.touch_major_max > 0;
    }

    uint8_t keybits[(KEY_MAX / 8) + 1] = {0};
    if (ioctl(fd, EVIOCGBIT(EV_KEY, sizeof(keybits)), keybits) >= 0) {
        backend.has_btn_touch = TestBit(keybits, BTN_TOUCH);
        backend.has_btn_tool_finger = TestBit(keybits, BTN_TOOL_FINGER);
    }
    close(fd);
    return true;
}

std::string ReadWholeFile(const std::string &path) {
    std::ifstream input(path);
    if (!input) {
        return std::string();
    }
    return std::string((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
}

void ProbeScreenSize(uint32_t &width, uint32_t &height) {
    width = 0;
    height = 0;
    DIR *directory = opendir("/sys/class/drm");
    if (directory == nullptr) {
        return;
    }

    // The internal panel is the connector the touchscreen is glued to. Virtual
    // connectors can be larger (a 5120x2560 virtual display exists on some
    // phones), so DSI/eDP/LVDS wins over the largest mode.
    struct Candidate {
        bool internal;
        uint64_t area;
        uint32_t width;
        uint32_t height;
    };
    Candidate best{};
    while (dirent *entry = readdir(directory)) {
        const std::string name = entry->d_name;
        if (name.rfind("card", 0) != 0 || name.find('-') == std::string::npos) {
            continue;
        }
        const bool internal = name.find("-DSI-") != std::string::npos ||
                              name.find("-eDP-") != std::string::npos ||
                              name.find("-LVDS-") != std::string::npos;
        const std::string base = std::string("/sys/class/drm/") + name;
        std::string status = ReadWholeFile(base + "/status");
        while (!status.empty() && (status.back() == '\n' || status.back() == '\r')) {
            status.pop_back();
        }
        if (status != "connected") {
            continue;
        }
        std::istringstream lines(ReadWholeFile(base + "/modes"));
        std::string mode;
        while (std::getline(lines, mode)) {
            unsigned mode_width = 0;
            unsigned mode_height = 0;
            if (std::sscanf(mode.c_str(), "%ux%u", &mode_width, &mode_height) == 2) {
                const uint64_t area = static_cast<uint64_t>(mode_width) * mode_height;
                const bool better =
                    (internal && !best.internal) || (internal == best.internal && area > best.area);
                if (better) {
                    best.internal = internal;
                    best.area = area;
                    best.width = mode_width;
                    best.height = mode_height;
                }
            }
        }
    }
    closedir(directory);
    width = best.width;
    height = best.height;
}

// Resolves the kernel input_dev of the discovered touchscreen by walking the
// input_dev_list HEAD. The list links struct input_dev.node, whose offset comes
// from BTF; the name field (offset 0, also confirmed by BTF when available)
// identifies the device.
bool BindKernelDevice(TouchBackend &backend, std::string &error) {
    uint64_t list = 0;
    KModErr err = kernel_module::kallsyms_lookup_name("input_dev_list", list);
    if (is_failed(err)) {
        error = ErrText("kallsyms_lookup_name(input_dev_list)", err);
        return false;
    }
    uint64_t input_event = 0;
    err = kernel_module::kallsyms_lookup_name("input_event", input_event);
    if (is_failed(err)) {
        error = ErrText("kallsyms_lookup_name(input_event)", err);
        return false;
    }

    uint32_t node_offset = 0;
    err = kernel_module::btf_resolve_member_offset("input_dev", "node", node_offset);
    if (is_failed(err)) {
        error = ErrText("btf_resolve_member_offset(input_dev.node)", err);
        return false;
    }
    uint32_t name_offset = 0;
    if (is_failed(kernel_module::btf_resolve_member_offset("input_dev", "name", name_offset))) {
        name_offset = 0;
    }

    uint64_t node = 0;
    err = kernel_module::read_kernel_mem(list, &node, sizeof(node));
    if (is_failed(err)) {
        error = ErrText("read_kernel_mem(input_dev_list)", err);
        return false;
    }

    for (int step = 0; step < 256 && node != list && node != 0; ++step) {
        const uint64_t device = node - node_offset;
        uint64_t name_pointer = 0;
        if (is_failed(kernel_module::read_kernel_mem(device + name_offset, &name_pointer,
                                                     sizeof(name_pointer)))) {
            break;
        }
        char name[128] = {0};
        if (is_ok(kernel_module::read_kernel_mem(name_pointer, name, sizeof(name) - 1)) &&
            backend.name == name) {
            backend.input_dev = device;
            backend.input_event_addr = input_event;
            return true;
        }
        uint64_t next = 0;
        if (is_failed(kernel_module::read_kernel_mem(node, &next, sizeof(next)))) {
            break;
        }
        node = next;
    }

    error = "touchscreen '" + backend.name + "' was not found in input_dev_list";
    return false;
}

bool EnsureReadyLocked(std::string &error) {
    if (g_backend.ready) {
        return TouchGuardInstall(g_backend.input_dev, error);
    }

    // A failure is not cached: the touchscreen is only unavailable while the
    // panel is being re-probed (early boot, resume), and the next call should
    // try again instead of pinning the bridge in a failed state forever.
    TouchBackend backend;
    if (!DiscoverEventDevice(backend, error)) {
        return false;
    }
    ProbeScreenSize(backend.screen_width, backend.screen_height);
    if (!BindKernelDevice(backend, error)) {
        return false;
    }
    if (backend.slot_max < 0 || backend.slot_max >= 16) {
        error = "unsupported physical slot capacity";
        return false;
    }
    if (!TouchGuardInstall(backend.input_dev, error))
        return false;
    backend.write_fd = open(backend.event_path.c_str(), O_WRONLY | O_CLOEXEC);
    backend.ready = true;
    g_backend = backend;
    ResetSlotState();
    return true;
}

int32_t MapAxis(uint32_t pixel, int32_t minimum, int32_t maximum, uint32_t screen) {
    if (screen == 0 || maximum <= minimum) {
        return static_cast<int32_t>(pixel);
    }
    const int64_t span = static_cast<int64_t>(maximum) - minimum + 1;
    int64_t raw = minimum + (static_cast<int64_t>(pixel) * span) / screen;
    raw = std::max<int64_t>(raw, minimum);
    raw = std::min<int64_t>(raw, maximum);
    return static_cast<int32_t>(raw);
}

struct CallArg {
    uint8_t reg;
    bool wide;
    uint64_t value;
};

void EmitCall(Assembler *a, uint64_t target, std::initializer_list<CallArg> args) {
    for (const CallArg &arg : args) {
        if (arg.wide) {
            aarch64_asm_mov_w(a, w(arg.reg), static_cast<uint32_t>(arg.value));
        } else {
            aarch64_asm_mov_x(a, x(arg.reg), arg.value);
        }
    }
    aarch64_asm_mov_x(a, x(11), target);
    aarch64_asm_safe_blr(a, x(11));
}

template <typename Emit>
void EmitTouchEvents(const TouchBackend &backend, const std::vector<FrameSpec> &frames, Emit emit) {
    for (const FrameSpec &frame : frames) {
        // The key events come first on a press and last on a lift, exactly like
        // the panel driver's own frames; InputFlinger derives the finger tool
        // from them when the driver advertises BTN_TOOL_FINGER.
        if (frame.kind == FrameKind::kDown && frame.emit_touch_key) {
            if (backend.has_btn_touch) {
                emit(EV_KEY, BTN_TOUCH, 1);
            }
            if (backend.has_btn_tool_finger) {
                emit(EV_KEY, BTN_TOOL_FINGER, 1);
            }
        }

        if (frame.emit_slot) {
            emit(EV_ABS, ABS_MT_SLOT, frame.slot);
        }
        if (frame.kind == FrameKind::kUp) {
            emit(EV_ABS, ABS_MT_TRACKING_ID, 0xffffffffu);
            if (frame.emit_touch_key) {
                if (backend.has_btn_touch) {
                    emit(EV_KEY, BTN_TOUCH, 0);
                }
                if (backend.has_btn_tool_finger) {
                    emit(EV_KEY, BTN_TOOL_FINGER, 0);
                }
            }
        } else {
            if (frame.kind == FrameKind::kDown) {
                emit(EV_ABS, ABS_MT_TRACKING_ID, frame.tracking_id);
            }
            emit(EV_ABS, ABS_MT_POSITION_X, static_cast<uint32_t>(frame.x));
            emit(EV_ABS, ABS_MT_POSITION_Y, static_cast<uint32_t>(frame.y));
            if (frame.emit_metrics && backend.has_pressure) {
                // A constant pressure is both implausible and silently dropped
                // by the input core when it repeats the slot's previous value,
                // so each down frame carries a small variation around the
                // middle of the range.
                const int32_t jitter = static_cast<int32_t>(frame.frame % 41) - 20;
                int32_t pressure = backend.pressure_max / 2 + jitter;
                pressure = std::max(1, std::min(pressure, backend.pressure_max));
                emit(EV_ABS, ABS_MT_PRESSURE, static_cast<uint32_t>(pressure));
            }
            if (frame.emit_metrics && backend.has_touch_major) {
                const int32_t jitter = static_cast<int32_t>(frame.frame % 7) - 3;
                int32_t major = backend.touch_major_max / 8 + jitter;
                major = std::max(1, std::min(major, backend.touch_major_max));
                emit(EV_ABS, ABS_MT_TOUCH_MAJOR, static_cast<uint32_t>(major));
            }
        }
        emit(EV_SYN, SYN_REPORT, 0);
    }
}

std::vector<uint8_t> BuildFrames(const TouchBackend &backend,
                                 const std::vector<FrameSpec> &frames) {
    if (frames.empty()) {
        return {};
    }
    aarch64_asm_ctx asm_ctx = init_aarch64_asm();
    Assembler *a = asm_ctx.assembler();
    kernel_module::arm64_module_asm_func_start(a);

    auto emit = [&](unsigned type, unsigned code, uint32_t value) {
        EmitCall(
            a, backend.input_event_addr,
            {{0, false, backend.input_dev}, {1, true, type}, {2, true, code}, {3, true, value}});
    };

    EmitTouchEvents(backend, frames, emit);

    kernel_module::arm64_module_asm_func_end(a);
    return aarch64_asm_to_bytes(a);
}

// evdev_write enters input_inject_event() on the same physical input_dev.
// One write replaces per-sample shellcode assembly and SDK dispatch. Devices
// whose policy refuses the real node retain the existing kernel-asm backend.
bool SubmitFramesLocked(const std::vector<FrameSpec> &frames, std::string &error) {
    if (!TouchGuardRenew(error))
        return false;
    if (g_backend.write_fd >= 0) {
        std::vector<input_event> events;
        events.reserve(frames.size() * 10);
        EmitTouchEvents(g_backend, frames, [&](unsigned type, unsigned code, uint32_t value) {
            input_event event{};
            event.type = static_cast<uint16_t>(type);
            event.code = static_cast<uint16_t>(code);
            event.value = static_cast<int32_t>(value);
            events.push_back(event);
        });
        const auto *bytes = reinterpret_cast<const uint8_t *>(events.data());
        size_t left = events.size() * sizeof(input_event);
        while (left) {
            const ssize_t count = write(g_backend.write_fd, bytes, left);
            if (count < 0 && errno == EINTR)
                continue;
            if (count <= 0 || static_cast<size_t>(count) % sizeof(input_event)) {
                error = std::string("touch evdev write: ") +
                        (count < 0 ? std::strerror(errno) : "short event");
                return false;
            }
            bytes += count;
            left -= static_cast<size_t>(count);
        }
        return true;
    }
    const auto code = BuildFrames(g_backend, frames);
    if (code.empty()) {
        error = "touch frame assembly failed";
        return false;
    }
    uint64_t result = 0;
    const KModErr err = kernel_module::execute_kernel_asm_func(code, result);
    if (is_failed(err)) {
        error = ErrText("execute_kernel_asm_func", err);
        return false;
    }
    return true;
}

bool ReadTrackingLocked(std::array<int32_t, 16> &tracking, std::string &error) {
    std::array<int32_t, 17> values{};
    values[0] = ABS_MT_TRACKING_ID;
    const int fd = open(g_backend.event_path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        error = "touch tracking read open failed";
        return false;
    }
    const int rc =
        ioctl(fd, EVIOCGMTSLOTS(sizeof(int32_t) * (g_backend.slot_max + 2)), values.data());
    close(fd);
    if (rc) {
        error = std::string("touch EVIOCGMTSLOTS: ") + std::strerror(errno);
        return false;
    }
    tracking.fill(-1);
    for (int slot = 0; slot <= g_backend.slot_max; ++slot)
        tracking[slot] = values[slot + 1];
    return true;
}

// Keep logical client slots stable while assigning free physical slots. A
// finger already using logical slot zero is excluded during planning. Hardware
// may still take a slot later; subsequent transitions validate the tracking ID.

bool SendFrameLocked(FrameKind kind, uint32_t slot, int32_t x, int32_t y, uint32_t tracking,
                     std::string &error) {
    std::array<int32_t, 16> actual{};
    if (!ReadTrackingLocked(actual, error))
        return false;
    auto down = g_slot_down;
    auto ids = g_tracking;
    auto physical = g_physical;
    std::vector<FrameSpec> frames;
    if (!PlanFrame(g_backend.slot_max, g_frame_sequence, kind, slot, x, y, tracking, down, ids,
                   physical, actual, frames, error))
        return false;
    if (!frames.empty() && !SubmitFramesLocked(frames, error))
        return false;
    g_slot_down = down;
    g_tracking = ids;
    g_physical = physical;
    g_slots_down = std::count(down.begin(), down.end(), true);
    return true;
}

uint32_t NextTrackingId() {
    static uint32_t next = 0x3fff;
    ++next;
    if (next == 0 || next > 0xffff) {
        next = 0x4000;
    }
    return next;
}

std::string ReportLocked() {
    std::string text;
    text += "touch=ready\n";
    text += std::string("inject_backend=") +
            (g_backend.write_fd >= 0 ? "real-evdev" : "kernel-asm") + "\n";
    text += TouchGuardReport();
    text += "name=" + g_backend.name + "\n";
    text += "event=" + g_backend.event_path + "\n";
    char pointer[32];
    std::snprintf(pointer, sizeof(pointer), "0x%llx",
                  static_cast<unsigned long long>(g_backend.input_dev));
    text += std::string("input_dev=") + pointer + "\n";
    text += "screen=" + std::to_string(g_backend.screen_width) + "x" +
            std::to_string(g_backend.screen_height) + "\n";
    text +=
        "abs_x=" + std::to_string(g_backend.x_min) + ".." + std::to_string(g_backend.x_max) + "\n";
    text +=
        "abs_y=" + std::to_string(g_backend.y_min) + ".." + std::to_string(g_backend.y_max) + "\n";
    text += "slots=0.." + std::to_string(std::max(0, g_backend.slot_max)) + "\n";
    text += "pressure=0.." + std::to_string(g_backend.pressure_max) + "\n";
    text += std::string("btn_touch=") + (g_backend.has_btn_touch ? "1" : "0") + "\n";
    text += std::string("btn_tool_finger=") + (g_backend.has_btn_tool_finger ? "1" : "0") + "\n";
    return text;
}

TouchResult Failure(const std::string &error) {
    TouchResult result;
    result.error = error;
    return result;
}

TouchResult Success(const std::string &report) {
    TouchResult result;
    result.ok = true;
    result.report = report;
    return result;
}

} // namespace

void TouchHeartbeat() {
    std::lock_guard<std::mutex> guard(TouchMutex());
    if (g_backend.ready && g_slots_down) {
        std::string ignored;
        TouchGuardRenew(ignored);
    }
}
void TouchShutdown() {
    std::lock_guard<std::mutex> guard(TouchMutex());
    if (g_backend.ready) {
        for (uint32_t slot = 0; slot < g_slot_down.size(); ++slot)
            if (g_slot_down[slot]) {
                std::string ignored;
                SendFrameLocked(FrameKind::kUp, slot, 0, 0, 0, ignored);
            }
    }
    std::string ignored;
    if (!TouchGuardRemove(ignored))
        return;
    if (g_backend.write_fd >= 0)
        close(g_backend.write_fd);
    g_backend = TouchBackend{};
    ResetSlotState();
}

TouchResult TouchDiagnostics() {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    return Success(ReportLocked());
}

TouchResult TouchDown(uint32_t x, uint32_t y, uint16_t slot) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (slot > g_backend.slot_max)
        return Failure("touch slot exceeds device capacity");
    const uint32_t use_slot = slot;
    const int32_t raw_x = MapAxis(x, g_backend.x_min, g_backend.x_max, g_backend.screen_width);
    const int32_t raw_y = MapAxis(y, g_backend.y_min, g_backend.y_max, g_backend.screen_height);
    const uint32_t tracking = NextTrackingId();
    if (!SendFrameLocked(FrameKind::kDown, use_slot, raw_x, raw_y, tracking, error)) {
        return Failure(error);
    }
    return Success("action=down slot=" + std::to_string(use_slot) + " x=" + std::to_string(raw_x) +
                   " y=" + std::to_string(raw_y) +
                   " tracking=" + std::to_string(g_tracking[use_slot]) +
                   " physical_slot=" + std::to_string(g_physical[use_slot]) + "\n");
}

TouchResult TouchMove(uint32_t x, uint32_t y, uint16_t slot) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (slot > g_backend.slot_max)
        return Failure("touch slot exceeds device capacity");
    const uint32_t use_slot = slot;
    const int32_t raw_x = MapAxis(x, g_backend.x_min, g_backend.x_max, g_backend.screen_width);
    const int32_t raw_y = MapAxis(y, g_backend.y_min, g_backend.y_max, g_backend.screen_height);
    if (!SendFrameLocked(FrameKind::kMove, use_slot, raw_x, raw_y, 0, error)) {
        return Failure(error);
    }
    return Success("action=move slot=" + std::to_string(use_slot) + " x=" + std::to_string(raw_x) +
                   " y=" + std::to_string(raw_y) + "\n");
}

TouchResult TouchUp(uint16_t slot) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (slot > g_backend.slot_max)
        return Failure("touch slot exceeds device capacity");
    const uint32_t use_slot = slot;
    if (!SendFrameLocked(FrameKind::kUp, use_slot, 0, 0, 0, error)) {
        return Failure(error);
    }
    return Success("action=up slot=" + std::to_string(use_slot) + "\n");
}

TouchResult TouchTap(uint32_t x, uint32_t y, uint32_t hold_ms, uint16_t slot) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (slot > g_backend.slot_max)
        return Failure("touch slot exceeds device capacity");
    const uint32_t use_slot = slot;
    const int32_t raw_x = MapAxis(x, g_backend.x_min, g_backend.x_max, g_backend.screen_width);
    const int32_t raw_y = MapAxis(y, g_backend.y_min, g_backend.y_max, g_backend.screen_height);
    const uint32_t tracking = NextTrackingId();

    if (!SendFrameLocked(FrameKind::kDown, use_slot, raw_x, raw_y, tracking, error)) {
        return Failure(error);
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(std::max<uint32_t>(hold_ms, 1)));
    if (!SendFrameLocked(FrameKind::kUp, use_slot, raw_x, raw_y, tracking, error)) {
        return Failure(error);
    }
    return Success("action=tap slot=" + std::to_string(use_slot) + " x=" + std::to_string(raw_x) +
                   " y=" + std::to_string(raw_y) + " hold_ms=" + std::to_string(hold_ms) + "\n");
}

TouchResult TouchSwipe(uint32_t x1, uint32_t y1, uint32_t x2, uint32_t y2, uint32_t duration_ms,
                       uint16_t slot) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (slot > g_backend.slot_max)
        return Failure("touch slot exceeds device capacity");
    const uint32_t use_slot = slot;
    const uint32_t tracking = NextTrackingId();
    const uint32_t duration = std::max<uint32_t>(duration_ms, 16);
    const uint32_t steps = std::max<uint32_t>(duration / 16, 2);
    const uint32_t step_ms = std::max<uint32_t>(duration / steps, 1);

    if (!SendFrameLocked(FrameKind::kDown, use_slot,
                         MapAxis(x1, g_backend.x_min, g_backend.x_max, g_backend.screen_width),
                         MapAxis(y1, g_backend.y_min, g_backend.y_max, g_backend.screen_height),
                         tracking, error)) {
        return Failure(error);
    }
    for (uint32_t step = 1; step <= steps; ++step) {
        std::this_thread::sleep_for(std::chrono::milliseconds(step_ms));
        // int64_t keeps a right-to-left or bottom-to-top swipe from wrapping
        // the unsigned subtraction before the interpolation.
        const int64_t x = static_cast<int64_t>(x1) + (static_cast<int64_t>(x2) - x1) * step / steps;
        const int64_t y = static_cast<int64_t>(y1) + (static_cast<int64_t>(y2) - y1) * step / steps;
        if (!SendFrameLocked(FrameKind::kMove, use_slot,
                             MapAxis(static_cast<uint32_t>(x), g_backend.x_min, g_backend.x_max,
                                     g_backend.screen_width),
                             MapAxis(static_cast<uint32_t>(y), g_backend.y_min, g_backend.y_max,
                                     g_backend.screen_height),
                             0, error)) {
            return Failure(error);
        }
    }
    if (!SendFrameLocked(FrameKind::kUp, use_slot, 0, 0, tracking, error)) {
        return Failure(error);
    }
    return Success("action=swipe slot=" + std::to_string(use_slot) + " from=" + std::to_string(x1) +
                   "," + std::to_string(y1) + " to=" + std::to_string(x2) + "," +
                   std::to_string(y2) + " duration_ms=" + std::to_string(duration) + "\n");
}

TouchResult TouchBatch(const std::vector<TouchBatchItem> &items) {
    std::lock_guard<std::mutex> guard(TouchMutex());
    std::string error;
    if (!EnsureReadyLocked(error)) {
        return Failure(error);
    }
    if (items.empty()) {
        return Failure("touch batch is empty");
    }
    if (items.size() > 32) {
        return Failure("touch batch is too large");
    }

    std::array<int32_t, 16> actual{};
    if (!ReadTrackingLocked(actual, error))
        return Failure(error);
    auto down = g_slot_down;
    auto ids = g_tracking;
    auto physical = g_physical;
    std::vector<FrameSpec> frames;
    for (const auto &item : items) {
        if (item.action != kTouchDown && item.action != kTouchMove && item.action != kTouchUp)
            return Failure("unsupported batch action");
        auto kind = item.action == kTouchDown   ? FrameKind::kDown
                    : item.action == kTouchMove ? FrameKind::kMove
                                                : FrameKind::kUp;
        if (!PlanFrame(g_backend.slot_max, g_frame_sequence, kind, item.slot,
                       MapAxis(item.x, g_backend.x_min, g_backend.x_max, g_backend.screen_width),
                       MapAxis(item.y, g_backend.y_min, g_backend.y_max, g_backend.screen_height),
                       kind == FrameKind::kDown ? NextTrackingId() : 0, down, ids, physical, actual,
                       frames, error))
            return Failure(error);
    }
    if (!frames.empty() && !SubmitFramesLocked(frames, error))
        return Failure(error);
    g_slot_down = down;
    g_tracking = ids;
    g_physical = physical;
    g_slots_down = std::count(down.begin(), down.end(), true);
    return Success("action=batch count=" + std::to_string(items.size()) + "\n");
}

} // namespace skpdb
