#include "service/gesture_lease.h"
#include "common/files.h"
#include <cstdio>
#include <cstdlib>
#include <stdexcept>
#include <unistd.h>
#include <sys/stat.h>
using namespace std::chrono_literals;
void require(bool ok, const char* error) { if (!ok) throw std::runtime_error(error); }
int main() {
    char directory[] = "/data/local/tmp/skpdb-gesture-test-XXXXXX";
    if (!mkdtemp(directory)) return 2;
    const std::string journal = std::string(directory) + "/skpdb.gesture.restore";
    try {
        std::string value = "1", user = "10", report, error; int writes = 0; bool writeFail = false;
        auto now = std::chrono::steady_clock::now();
        auto command = [&](const std::vector<std::string>& args, std::string& out) {
            if (args[0] == "cmd") { out = user; return true; }
            require(args.size() >= 6 && args[1] == "--user" && args[2] == "10" &&
                args[4] == "system" && args[5] == "enable_three_gesture", "unexpected command/user");
            if (args[3] == "get") { out = value; return true; }
            require(args[3] == "put" && args.size() == 7, "unexpected mutation");
            if (writeFail) return false; value = args[6]; ++writes; return true;
        };
        skpdb::GestureLease lease(directory, command, [&] { return now; });
        require(lease.Recover(), "clean startup");
        require(lease.Apply(1, true, report, error) && value == "0" && writes == 1, "acquire");
        require(skpdb::ReadFileFully(journal) == "10\n1", "durable exact-user journal");
        require(!lease.Apply(2, false, report, error), "foreign owner released lease");
        lease.Release(2); require(value == "0", "foreign EOF restored setting");
        now += 7s; require(lease.Apply(1, true, report, error, true) && writes == 1, "renew mutated setting");
        now += 7s; lease.Tick(); require(value == "0", "lease expired too early");
        now += 2s; lease.Tick(); require(value == "1" && access(journal.c_str(), F_OK) != 0, "timeout restoration");
        require(!lease.Apply(1, true, report, error, true), "expired renew reacquired lease");
        require(lease.Apply(2, true, report, error), "new owner after expiry");
        lease.Release(2); require(value == "1", "EOF restore");
        for (const auto& original : {"0", "null", "vendor-value"}) {
            value = original; const int before = writes;
            require(lease.Apply(3, true, report, error) && report.find("gesture_changed=0") != std::string::npos, "unsupported state");
            lease.Release(3); require(value == original && writes == before, "unnecessary setting changed");
        }
        value = "1"; require(lease.Apply(4, true, report, error), "restore failure setup");
        writeFail = true; lease.Release(4); require(value == "0" && access(journal.c_str(), F_OK) == 0, "lost restore journal");
        writeFail = false; now += 9s; lease.Tick(); require(value == "1", "restore retry");
        require(lease.Apply(5, true, report, error), "crash setup");
        skpdb::GestureLease restarted(directory, command, [&] { return now; });
        require(restarted.Recover() && value == "1", "restart recovery");
        value = "1"; require(restarted.Apply(6, true, report, error), "user edit setup");
        value = "null"; restarted.Release(6); require(value == "null", "user edit overwritten");
        user = "bad"; require(!restarted.Apply(7, true, report, error), "invalid Android user accepted");
        puts("PASS gesture lease: ownership, heartbeat, expiry, EOF, unchanged unsupported settings, failed restore retry, restart recovery and user edits");
        rmdir(directory); return 0;
    } catch (const std::exception& error) {
        fprintf(stderr, "FAIL %s\n", error.what()); unlink(journal.c_str()); rmdir(directory); return 1;
    }
}
