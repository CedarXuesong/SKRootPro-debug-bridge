// Bounded diagnostic of the production global HAL hook. Never install at boot.
#include "input/gyro.cpp"
#include <signal.h>
#include <stdexcept>
#include <sys/wait.h>

namespace {
volatile sig_atomic_t stopped = 0;
void stop(int) { stopped = 1; }
void require(bool ok, const std::string &error) {
    if (!ok)
        throw std::runtime_error(error);
}
void status(const std::string &directory, const char *phase) {
    std::string error;
    const auto value = skpdb::GyroQuery(error);
    std::vector<uint8_t> instruction;
    uint32_t observed = 0;
    const bool read = value.pid && value.patch && skpdb::Read(value.pid, value.patch, 4, instruction, error);
    if (read)
        std::memcpy(&observed, instruction.data(), 4);
    FILE *file = std::fopen((directory + "/status.tmp").c_str(), "w");
    if (!file)
        return;
    std::fprintf(file, "phase=%s\ninstalled=%u\npid=%u\npatch=0x%llx\noriginal=%08x\n"
                      "entry_read=%u\nentry_word=%08x\nhits=%u\nupdates=%llu\nstream=%u\nenabled=%u\nerror=%s\n",
        phase, unsigned(value.installed), value.pid,
        static_cast<unsigned long long>(value.patch), skpdb::g_hook.record.original,
        unsigned(read), observed, value.hits,
        static_cast<unsigned long long>(value.updates), value.stream, unsigned(value.enabled), error.c_str());
    std::fclose(file);
    rename((directory + "/status.tmp").c_str(), (directory + "/status").c_str());
}
}

int skroot_module_main(const char *key, const char *path) {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    const std::string directory = path ? path : "";
    if (directory.rfind("/data/local/tmp/skpdb-gyro-hal-", 0) != 0 ||
        directory.find("..") != std::string::npos || directory.find('\n') != std::string::npos)
        return 64;
    signal(SIGTERM, stop);
    signal(SIGINT, stop);
    int result = 1;
    bool installed = false;
    uint32_t pid = 0;
    uint64_t identity = 0;
    std::vector<pid_t> receivers;
    try {
        require(is_ok(skroot_env::get_root(key)), "Cannot acquire the authorized diagnostic root context");
        const int log = open((directory + "/test.log").c_str(), O_WRONLY | O_CREAT | O_APPEND, 0644);
        require(log >= 0, "Cannot open the diagnostic log");
        dup2(log, STDOUT_FILENO);
        dup2(log, STDERR_FILENO);
        close(log);
        clearerr(stdout);
        clearerr(stderr);
        signal(SIGCHLD, SIG_DFL);
        for (const auto uid : {0, 2000}) {
            const auto child = fork();
            require(child >= 0, "Cannot start the global-scope receiver");
            if (child == 0) {
                const auto file = directory + (uid ? "/shell.txt" : "/root.txt");
                const int fd = open(file.c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0644);
                if (fd < 0)
                    _exit(70);
                dup2(fd, STDOUT_FILENO);
                dup2(fd, STDERR_FILENO);
                close(fd);
                const auto executable = directory + "/gyro_probe";
                execl(executable.c_str(), "gyro_probe", uid ? "2000" : "0", "120", nullptr);
                _exit(71);
            }
            receivers.push_back(child);
        }
        std::puts("HAL_BASELINE");
        usleep(1500000);
        std::string error;
        const auto value = skpdb::GyroInstall(error);
        installed = value.installed;
        require(installed && error.empty(), error.empty() ? "Global HAL installation failed" : error);
        pid = value.pid;
        identity = skpdb::Identity(pid);
        status(directory, "armed");
        std::printf("HAL_READY pid=%u scope=device-hal patch=0x%llx original=%08x\n",
            pid, static_cast<unsigned long long>(value.patch), skpdb::g_hook.record.original);
        const auto deadline = skpdb::Now() + 120000000000ull;
        uint32_t previous = 0;
        unsigned ticks = 0;
        while (!stopped && skpdb::Now() < deadline) {
            FILE *file = std::fopen((directory + "/command").c_str(), "r");
            if (file) {
                uint32_t sequence = 0;
                float x = 0, y = 0, z = 0;
                const auto count = std::fscanf(file, "%u %f %f %f", &sequence, &x, &y, &z);
                std::fclose(file);
                if (count == 1 && sequence == 0)
                    break;
                if (count == 4 && sequence != previous) {
                    require(std::fabs(x) <= 1 && std::fabs(y) <= 1 && std::fabs(z) <= 1,
                            "Diagnostic rates exceed the bounded test range");
                    require(skpdb::GyroApplyPacket(value.stream, sequence, x, y, z, skpdb::Now(), error), error);
                    previous = sequence;
                }
            }
            if (++ticks % 10 == 0)
                status(directory, "running");
            usleep(10000);
        }
        require(skpdb::GyroDisarm(error, value.stream), error);
        const auto removed = skpdb::GyroRemove(error);
        require(error.empty() && !removed.installed, "HAL removal failed: " + error);
        installed = false;
        std::vector<uint8_t> restored;
        require(skpdb::Read(pid, value.patch, 4, restored, error), error);
        uint32_t word = 0;
        std::memcpy(&word, restored.data(), 4);
        require(word == skpdb::g_hook.record.original, "HAL entry instruction was not restored");
        require(skpdb::Identity(pid) == identity, "HAL process restarted during the diagnostic");
        status(directory, "restored");
        std::printf("HAL_RESTORED word=%08x pid_stable=1\n", word);
        result = 0;
    } catch (const std::exception &error) {
        std::printf("HAL_FAIL %s\n", error.what());
    }
    if (installed) {
        std::string error;
        skpdb::GyroDisarm(error);
        skpdb::GyroRemove(error);
        status(directory, "cleanup");
        if (!error.empty())
            std::printf("HAL_CLEANUP_FAIL %s\n", error.c_str());
    }
    for (const auto receiver : receivers) {
        kill(receiver, SIGTERM);
        int ignored = 0;
        while (waitpid(receiver, &ignored, 0) < 0 && errno == EINTR) {}
    }
    return result;
}

SKROOT_MODULE_NAME("SKPDB bounded global HAL diagnostic")
SKROOT_MODULE_VERSION("0.0.1")
SKROOT_MODULE_DESC("Temporary gyro HAL validation; never install as a boot module.")
SKROOT_MODULE_AUTHOR("SKPDB")
SKROOT_MODULE_ID32("c7d7209861944c94bac5b40f3a4a8cf2")
