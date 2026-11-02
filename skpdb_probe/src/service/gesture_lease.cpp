#include "service/gesture_lease.h"
#include "common/files.h"

#include <algorithm>
#include <cerrno>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <sys/wait.h>
#include <thread>
#include <unistd.h>

extern char** environ;
namespace skpdb {
namespace {
bool Number(const std::string& value) {
    return !value.empty() && value.size() <= 6 &&
        std::all_of(value.begin(), value.end(), [](char c) { return c >= '0' && c <= '9'; });
}
// Fixed executable/argv; no shell interpretation. Bound both output and child lifetime.
bool Run(const std::vector<std::string>& arguments, std::string& output) {
    int fds[2]; if (pipe2(fds, O_CLOEXEC) != 0) return false;
    posix_spawn_file_actions_t actions; posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_adddup2(&actions, fds[1], STDOUT_FILENO);
    posix_spawn_file_actions_addopen(&actions, STDERR_FILENO, "/dev/null", O_WRONLY, 0);
    posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0);
    posix_spawn_file_actions_addclose(&actions, fds[0]);
    std::vector<char*> argv;
    for (const auto& item : arguments) argv.push_back(const_cast<char*>(item.c_str()));
    argv.push_back(nullptr);
    pid_t child = -1;
    const auto program = std::string("/system/bin/") + arguments[0];
    const int rc = posix_spawn(&child, program.c_str(), &actions, nullptr, argv.data(), environ);
    posix_spawn_file_actions_destroy(&actions); close(fds[1]);
    if (rc != 0) { close(fds[0]); return false; }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    output.clear(); bool eof = false, reaped = false; int status = 0;
    while (std::chrono::steady_clock::now() < deadline) {
        if (!eof) {
            pollfd p{fds[0], POLLIN, 0};
            if (poll(&p, 1, 20) > 0 && (p.revents & (POLLIN | POLLHUP))) {
                char buf[256]; const auto n = read(fds[0], buf, sizeof(buf));
                if (n > 0) { output.append(buf, n); if (output.size() > 4096) break; }
                else if (n == 0) eof = true;
            }
        } else std::this_thread::sleep_for(std::chrono::milliseconds(10));
        if (!reaped) reaped = waitpid(child, &status, WNOHANG) == child;
        if (reaped && eof) break;
    }
    close(fds[0]);
    if (!reaped) { kill(child, SIGKILL); while (waitpid(child, &status, 0) < 0 && errno == EINTR) {} return false; }
    while (!output.empty() && (output.back() == '\n' || output.back() == '\r')) output.pop_back();
    return eof && WIFEXITED(status) && WEXITSTATUS(status) == 0;
}
}

GestureLease::GestureLease(std::string directory, Command command, Clock clock)
    : path_(std::move(directory) + "/skpdb.gesture.restore"),
      command_(command ? std::move(command) : Run),
      clock_(clock ? std::move(clock) : [] { return std::chrono::steady_clock::now(); }) {}

bool GestureLease::Setting(const char* operation, const std::string& user, std::string& result, const char* value) {
    std::vector<std::string> args{"settings", "--user", user, operation, "system", "enable_three_gesture"};
    if (value) args.emplace_back(value);
    return command_(args, result);
}

bool GestureLease::Restore() {
    if (changed_) {
        std::string current, ignored;
        if (!Setting("get", user_, current)) return false;
        // Respect an explicit change made by the user while control was active.
        if (current == "0" && !Setting("put", user_, ignored, "1")) return false;
        if (!Setting("get", user_, current) || current == "0") return false;
        if (unlink(path_.c_str()) != 0 && errno != ENOENT) return false;
    }
    owner_ = 0; changed_ = false; user_.clear(); return true;
}

bool GestureLease::Recover() {
    std::lock_guard<std::mutex> guard(mutex_);
    if (owner_) return false;
    const auto journal = ReadFileFully(path_);
    if (journal.empty()) return access(path_.c_str(), F_OK) != 0 && errno == ENOENT;
    if (journal.size() < 3 || journal.substr(journal.size() - 2) != "\n1") return false;
    user_ = journal.substr(0, journal.size() - 2);
    if (!Number(user_)) return false;
    changed_ = true; return Restore();
}

bool GestureLease::Apply(uint64_t owner, bool enabled, std::string& report, std::string& error, bool renew) {
    std::lock_guard<std::mutex> guard(mutex_);
    if (renew && (!owner_ || owner_ != owner || clock_() >= expires_)) {
        error = "system gesture lease expired"; return false;
    }
    if (!owner) { error = "gesture lease requires a control socket"; return false; }
    if (owner_ && owner_ != owner) { error = "another control socket owns the gesture lease"; return false; }
    if (!enabled) {
        if (!Restore()) { error = "could not restore system gesture setting"; return false; }
        report = "gesture_lease=0\ngesture_changed=0\n"; return true;
    }
    if (!owner_) {
        if (changed_ || access(path_.c_str(), F_OK) == 0) {
            error = "previous system gesture restoration is pending"; return false;
        }
        std::string value;
        if (!command_({"cmd", "activity", "get-current-user"}, user_) || !Number(user_) ||
            !Setting("get", user_, value)) { error = "could not read system gesture setting"; return false; }
        if (value == "1") {
            if (!WritePrivateFile(path_, user_ + "\n1")) { error = "could not journal system gesture setting"; return false; }
            const bool written = Setting("put", user_, value, "0"); changed_ = true;
            if (!written || !Setting("get", user_, value) || value != "0") {
                Restore(); error = "could not suspend conflicting system gesture"; return false;
            }
        }
        owner_ = owner;
    }
    expires_ = clock_() + std::chrono::seconds(8);
    report = "gesture_lease=1\ngesture_changed=" + std::string(changed_ ? "1\n" : "0\n");
    return true;
}

void GestureLease::Release(uint64_t owner) {
    std::lock_guard<std::mutex> guard(mutex_);
    if (owner_ == owner && owner) (void)Restore();
}
void GestureLease::Tick() {
    std::lock_guard<std::mutex> guard(mutex_);
    if ((owner_ && clock_() >= expires_) || (!owner_ && changed_)) (void)Restore();
}
}
