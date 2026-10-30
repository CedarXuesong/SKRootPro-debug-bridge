#include "display/cursor_surface.h"

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cerrno>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>

#include "display/cursor_dex.h"

namespace skpdb {
namespace {

std::mutex g_mutex;
pid_t g_child = -1;
int g_stdin = -1;
int g_stdout = -1;
bool g_started = false;
uint64_t g_sequence = 0;
bool g_cursor_visible = false;
ControlOverlayState g_overlay;
std::chrono::steady_clock::time_point g_overlay_updated;

constexpr const char* kDexPath = "/data/local/tmp/skpdb_cursor.dex";

bool WriteAll(int fd, const void* data, size_t size) {
    const char* bytes = static_cast<const char*>(data);
    while (size > 0) {
        const ssize_t written = write(fd, bytes, size);
        if (written <= 0) {
            if (errno == EINTR) {
                continue;
            }
            return false;
        }
        bytes += written;
        size -= static_cast<size_t>(written);
    }
    return true;
}

bool Send(const std::string& line) {
    if (g_stdin < 0) {
        return false;
    }
    return WriteAll(g_stdin, line.data(), line.size());
}

void Reset() {
    if (g_stdin >= 0) close(g_stdin);
    if (g_stdout >= 0) close(g_stdout);
    g_stdin = g_stdout = -1;
    if (g_child > 0) { kill(g_child, SIGKILL); (void)waitpid(g_child, nullptr, 0); }
    g_child = -1; g_started = false;
    g_cursor_visible = false; g_overlay.enabled = false;
}

bool Await(const std::string& expected, int timeout_ms, std::string& error) {
    using Clock = std::chrono::steady_clock;
    const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms);
    std::string line;
    while (Clock::now() < deadline) {
        const int remaining = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count());
        pollfd fd{g_stdout, POLLIN, 0};
        const int ready = poll(&fd, 1, remaining > 0 ? remaining : 1);
        if (ready < 0 && errno == EINTR) continue;
        if (ready <= 0) break;
        char byte;
        if (read(g_stdout, &byte, 1) != 1) break;
        if (byte == '\n') {
            if (line == expected) return true;
            if (line.rfind("ERROR ", 0) == 0) { error = line; Reset(); return false; }
            line.clear();
        } else if (line.size() < 256) line += byte;
        else break;
    }
    error = "overlay renderer did not acknowledge " + expected;
    Reset();
    return false;
}

}  // namespace

bool CursorSurfaceStart(std::string& error) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (g_started) {
        int status = 0;
        const auto child = waitpid(g_child, &status, WNOHANG);
        if (child == 0) return true;
        g_child = -1;
        Reset();
    }
    int dex = open(kDexPath, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (dex < 0) {
        error = std::string("open cursor dex: ") + std::strerror(errno);
        return false;
    }
    if (!WriteAll(dex, kCursorDex, kCursorDexSize)) {
        error = std::string("write cursor dex: ") + std::strerror(errno);
        close(dex);
        return false;
    }
    close(dex);

    int pipe_fds[2] = {-1, -1};
    if (pipe2(pipe_fds, O_CLOEXEC) != 0) {
        error = std::string("pipe: ") + std::strerror(errno);
        return false;
    }
    int output_fds[2] = {-1, -1};
    if (pipe2(output_fds, O_CLOEXEC) != 0) {
        error = std::string("overlay output pipe: ") + std::strerror(errno);
        close(pipe_fds[0]); close(pipe_fds[1]); return false;
    }
    const pid_t child = fork();
    if (child < 0) {
        error = std::string("fork: ") + std::strerror(errno);
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        close(output_fds[0]); close(output_fds[1]);
        return false;
    }
    if (child == 0) {
        if (dup2(pipe_fds[0], STDIN_FILENO) < 0 || dup2(output_fds[1], STDOUT_FILENO) < 0) {
            _exit(127);
        }
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        close(output_fds[0]); close(output_fds[1]);
        execl("/system/bin/app_process", "app_process",
              "-Djava.class.path=/data/local/tmp/skpdb_cursor.dex",
              "/system/bin", "skpdb.cursor.Main", static_cast<char*>(nullptr));
        _exit(127);
    }
    close(pipe_fds[0]);
    close(output_fds[1]);
    g_child = child;
    g_stdin = pipe_fds[1];
    // A stuck JVM must not block the input server indefinitely on a full pipe.
    if (fcntl(g_stdin, F_SETFL, O_NONBLOCK) < 0) {
        close(output_fds[0]); error = "overlay pipe nonblocking setup failed"; Reset(); return false;
    }
    g_stdout = output_fds[0];
    g_started = true;
    return Await("READY", 4000, error);
}

bool CursorSurfaceStarted() {
    std::lock_guard<std::mutex> guard(g_mutex);
    return g_started;
}

std::string CursorSurfaceReport() {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (g_started && waitpid(g_child, nullptr, WNOHANG) == g_child) { g_child = -1; Reset(); }
    const auto age = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - g_overlay_updated).count();
    return std::string("renderer=") + (g_started ? "ready\n" : "stopped\n") +
        "cursor_visible=" + (g_started && g_cursor_visible ? "1\n" : "0\n") +
        "hud_visible=" + (g_started && g_overlay.enabled && age < 6000 ? "1\n" : "0\n") +
        "hud_width=" + std::to_string(g_overlay.width) + "\nhud_height=" + std::to_string(g_overlay.height) +
        "\nhud_rotation=" + std::to_string(g_overlay.rotation) +
        "\nhud_background=transparent\nhud_lease_ms=6000\nrender_mode=cpu\n";
}

void CursorSurfaceMove(int x, int y) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (!g_started) {
        return;
    }
    if (!Send("move " + std::to_string(x) + " " + std::to_string(y) + "\n")) Reset();
}

void CursorSurfaceSetVisible(bool visible) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (!g_started) {
        return;
    }
    if (!Send(std::string("visible ") + (visible ? "1" : "0") + "\n")) Reset();
    else g_cursor_visible = visible;
}

bool CursorSurfaceSetOverlay(const ControlOverlayState& s, std::string& error) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (!g_started) {
        if (!s.enabled) return true;
        error = "overlay renderer is not running"; return false;
    }
    const auto sequence = ++g_sequence;
    const auto word = [](uint64_t n) { return std::to_string(n) + " "; };
    const std::string command = "hud " + word(sequence) + word(s.enabled) + word(s.rotation) +
        word(s.width) + word(s.height) + word(s.active) + word(s.capacity) + word(s.pending) +
        word(s.sampling_tenths) + word(s.rtt_tenths) + word(s.mode) + word(s.input_active) + "\n";
    if (!Send(command)) { error = "overlay pipe write failed"; Reset(); return false; }
    if (!Await("HUD " + std::to_string(sequence), 2000, error)) return false;
    g_overlay = s; g_overlay_updated = std::chrono::steady_clock::now();
    return true;
}

void CursorSurfaceStop() {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (!g_started) {
        return;
    }
    Send("destroy\n");
    if (g_stdin >= 0) {
        close(g_stdin);
        g_stdin = -1;
    }
    if (g_stdout >= 0) { close(g_stdout); g_stdout = -1; }
    if (g_child > 0) {
        for (int i = 0; i < 100; ++i) {
            int status = 0;
            if (waitpid(g_child, &status, WNOHANG) == g_child) {
                g_child = -1;
                break;
            }
            usleep(20 * 1000);
        }
        if (g_child > 0) {
            kill(g_child, SIGKILL);
            (void)waitpid(g_child, nullptr, 0);
            g_child = -1;
        }
    }
    g_started = false;
    g_cursor_visible = false; g_overlay.enabled = false;
}

}  // namespace skpdb
