#include <windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "cli.h"
#include "commands.h"
#include "knock.h"
#include "protocol.h"
#include "tcp.h"

namespace cli {

uint64_t NowMs() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
                                     std::chrono::steady_clock::now().time_since_epoch())
                                     .count());
}

void PrintProgress(size_t& last_percent, uint64_t total, uint64_t started_ms) {
    uint64_t elapsed = NowMs() - started_ms;
    if (elapsed == 0) {
        return;
    }
    double rate = static_cast<double>(total) / (static_cast<double>(elapsed) / 1000.0) / (1024 * 1024);
    std::fprintf(stderr, "\r%8.2f MB  %6.2f MB/s", static_cast<double>(total) / (1024 * 1024), rate);
    std::fflush(stderr);
    (void)last_percent;
}

std::string ExecCapture(Transport& channel, const std::string& command, int timeout_seconds,
                        uint32_t& code) {
    std::vector<uint8_t> request{skpdb::opcode::kExec};
    request.insert(request.end(), command.begin(), command.end());
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kExec, timeout_seconds * 1000);
    if (response.payload.size() < 8) {
        throw std::runtime_error("short exec response");
    }
    code = skpdb::GetU32(response.payload.data());
    uint32_t size = skpdb::GetU32(response.payload.data() + 4);
    if (response.payload.size() < 8 + size) {
        throw std::runtime_error("short exec payload");
    }
    return std::string(reinterpret_cast<const char*>(response.payload.data() + 8), size);
}

int CommandRun(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("run requires a command");
    }
    std::string command;
    for (size_t index = 0; index < options.args.size(); ++index) {
        if (index) {
            command += ' ';
        }
        command += options.args[index];
    }
    uint32_t code = 0;
    std::string output = ExecCapture(channel, command, options.timeout, code);
    std::fwrite(output.data(), 1, output.size(), stdout);
    std::fflush(stdout);
    if (code != 0) {
        std::fprintf(stderr, "[exit %u]\n", code);
    }
    return static_cast<int>(code);
}

int CommandShell(Transport& channel, const Options& options) {
    std::cerr << "authenticated; :quit to exit" << std::endl;
    std::string line;
    while (true) {
        std::cerr << "skp# " << std::flush;
        if (!std::getline(std::cin, line) || line == ":quit") {
            break;
        }
        std::vector<uint8_t> request{skpdb::opcode::kExec};
        request.insert(request.end(), line.begin(), line.end());
        auto response =
            skpdb::Request(channel, request, skpdb::opcode::kExec, options.timeout * 1000);
        if (response.payload.size() < 8) {
            continue;
        }
        uint32_t code = skpdb::GetU32(response.payload.data());
        uint32_t size = skpdb::GetU32(response.payload.data() + 4);
        std::fwrite(response.payload.data() + 8, 1, size, stdout);
        std::fflush(stdout);
        if (code != 0) {
            std::fprintf(stderr, "[exit %u]\n", code);
        }
    }
    return 0;
}

std::string BaseName(const std::string& path) {
    size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

int CommandGet(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("get requires a remote path");
    }
    const std::string& remote = options.args[0];
    std::string local = options.args.size() > 1 ? options.args[1] : BaseName(remote);
    std::ofstream output(local, std::ios::binary);
    if (!output) {
        throw std::runtime_error("cannot create " + local);
    }

    const size_t chunk = ChunkSize(options);
    const size_t window = options.window;
    uint64_t next_offset = 0;
    uint64_t total = 0;
    size_t in_flight = 0;
    bool eof = false;
    uint64_t started = NowMs();
    size_t last_percent = 0;

    auto send_read = [&]() {
        std::vector<uint8_t> request{skpdb::opcode::kRead};
        skpdb::PutU64(request, next_offset);
        skpdb::PutU32(request, static_cast<uint32_t>(chunk));
        request.insert(request.end(), remote.begin(), remote.end());
        if (!skpdb::SendRequest(channel, request, options.timeout * 1000)) {
            throw std::runtime_error("send failed");
        }
        next_offset += chunk;
        ++in_flight;
    };

    auto read_one = [&]() {
        skpdb::Response response;
        std::string error;
        if (!skpdb::ReadResponse(channel, skpdb::opcode::kRead, response, error,
                                 options.timeout * 1000)) {
            throw std::runtime_error(error);
        }
        if (response.payload.size() < 4) {
            throw std::runtime_error("short read response");
        }
        uint32_t size = skpdb::GetU32(response.payload.data());
        if (response.payload.size() < 4 + size) {
            throw std::runtime_error("short read payload");
        }
        output.write(reinterpret_cast<const char*>(response.payload.data() + 4), size);
        total += size;
        --in_flight;
        if (size < chunk) {
            eof = true;
        }
        if (options.progress) {
            PrintProgress(last_percent, total, started);
        }
    };

    while (!eof) {
        while (in_flight < window && !eof) {
            send_read();
        }
        read_one();
    }
    while (in_flight > 0) {
        read_one();
    }
    if (!output) {
        throw std::runtime_error("write failed: " + local);
    }
    if (options.progress) {
        std::fprintf(stderr, "\n");
    }
    uint64_t elapsed = NowMs() - started;
    double rate = elapsed ? static_cast<double>(total) / (elapsed / 1000.0) / (1024 * 1024) : 0;
    std::fprintf(stderr, "received %llu bytes in %.2f s (%.2f MB/s)\n",
                 static_cast<unsigned long long>(total), elapsed / 1000.0, rate);
    return 0;
}

uint64_t UploadFile(Transport& channel, const std::string& local, const std::string& remote,
                    const Options& options, bool report) {
    std::ifstream input(local, std::ios::binary);
    if (!input) {
        throw std::runtime_error("cannot open " + local);
    }

    const size_t chunk = ChunkSize(options);
    uint64_t offset = 0;
    bool first = true;
    uint64_t total = 0;
    uint64_t started = NowMs();
    size_t last_percent = 0;
    std::vector<uint8_t> data(chunk);

    while (true) {
        input.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(chunk));
        size_t got = static_cast<size_t>(input.gcount());
        std::vector<uint8_t> request{skpdb::opcode::kWrite};
        skpdb::PutU64(request, offset);
        request.push_back(first ? 1 : 0);
        request.insert(request.end(), remote.begin(), remote.end());
        request.push_back(0);
        request.insert(request.end(), data.begin(), data.begin() + got);
        auto response =
            skpdb::Request(channel, request, skpdb::opcode::kWrite, options.timeout * 1000);
        if (response.payload.size() < 4) {
            throw std::runtime_error("short write response");
        }
        uint32_t written = skpdb::GetU32(response.payload.data());
        if (written != got) {
            throw std::runtime_error("short write on the device");
        }
        offset += written;
        total += written;
        first = false;
        if (options.progress) {
            PrintProgress(last_percent, total, started);
        }
        if (got < chunk) {
            break;
        }
    }
    if (options.progress) {
        std::fprintf(stderr, "\n");
    }
    if (report) {
        uint64_t elapsed = NowMs() - started;
        double rate =
            elapsed ? static_cast<double>(total) / (elapsed / 1000.0) / (1024 * 1024) : 0;
        std::fprintf(stderr, "sent %llu bytes in %.2f s (%.2f MB/s)\n",
                     static_cast<unsigned long long>(total), elapsed / 1000.0, rate);
    }
    return total;
}

int CommandPut(Transport& channel, const Options& options) {
    if (options.args.size() < 2) {
        throw std::runtime_error("put requires <local> <remote>");
    }
    UploadFile(channel, options.args[0], options.args[1], options, true);
    return 0;
}

int CommandInstall(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("install requires an APK path");
    }
    if (options.args.size() > 1) {
        throw std::runtime_error("install accepts a single APK; split APKs are not supported yet");
    }
    const std::string& local = options.args[0];
    {
        std::ifstream probe(local, std::ios::binary);
        if (!probe) {
            throw std::runtime_error("cannot open " + local);
        }
    }

    const unsigned long pid = GetCurrentProcessId();
    const std::string base = "/data/local/tmp/.skpdb-install-" + std::to_string(pid);
    const std::string remote = base + ".apk";
    const std::string log = base + ".log";
    std::cerr << "pushing " << local << std::endl;
    UploadFile(channel, local, remote, options, true);

    std::string flags;
    if (options.install_replace) {
        flags += " -r";
    }
    if (options.install_downgrade) {
        flags += " -d";
    }
    if (options.install_grant) {
        flags += " -g";
    }
    if (options.install_test) {
        flags += " -t";
    }
    if (options.install_sdcard) {
        flags += " -s";
    }
    if (!options.install_user.empty()) {
        flags += " --user " + options.install_user;
    }

    // The install runs detached. A package-manager confirmation or verifier
    // stall must not block the bridge session, so the client polls a log file
    // instead of waiting on the session, and `timeout -s KILL` bounds a hung
    // `pm`.
    const int budget = 300;
    const std::string command =
        "rm -f '" + log + "'; ( TO=\"\"; command -v timeout >/dev/null 2>&1 && " +
        "TO=\"timeout -s KILL " + std::to_string(budget) + "\"; $TO pm install" + flags + " '" +
        remote + "' > '" + log + "' 2>&1; echo \"SKPDB_RC=$?\" >> '" + log + "' ) " +
        "</dev/null >/dev/null 2>&1 & echo started";

    uint32_t code = 0;
    ExecCapture(channel, command, 30, code);

    std::string output;
    int result = -1;
    const uint64_t deadline = NowMs() + static_cast<uint64_t>(budget + 60) * 1000;
    while (NowMs() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
        std::string text;
        try {
            uint32_t poll_code = 0;
            text = ExecCapture(channel, "cat '" + log + "' 2>/dev/null", 20, poll_code);
        } catch (const std::exception&) {
            continue;
        }
        size_t marker = text.rfind("SKPDB_RC=");
        if (marker != std::string::npos) {
            result = std::atoi(text.c_str() + marker + 9);
            text.erase(marker);
            output = text;
            break;
        }
        output = text;
    }

    try {
        uint32_t cleanup = 0;
        ExecCapture(channel, "rm -f '" + remote + "' '" + log + "'", 20, cleanup);
    } catch (const std::exception&) {
        // Best effort: the names are unique per client process.
    }

    std::fwrite(output.data(), 1, output.size(), stdout);
    std::fflush(stdout);

    if (result == 0) {
        std::cerr << "installed" << std::endl;
        return 0;
    }
    if (result < 0) {
        std::cerr << "skpdb: install did not report a result in time" << std::endl;
        return 1;
    }
    if (result == 124 || result == 137) {
        std::cerr << "skpdb: install timed out on the device" << std::endl;
        return 1;
    }
    if (output.find("INSTALL_FAILED_USER_RESTRICTED") != std::string::npos) {
        std::cerr << "skpdb: enable USB installation in developer options" << std::endl;
    }
    std::cerr << "skpdb: install failed (rc=" << result << ")" << std::endl;
    return 1;
}

int CommandList(Transport& channel, const Options& options) {
    std::string path = options.args.empty() ? "." : options.args[0];
    std::vector<uint8_t> request{skpdb::opcode::kList};
    request.insert(request.end(), path.begin(), path.end());
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kList, options.timeout * 1000);
    if (response.payload.size() < 4) {
        throw std::runtime_error("short list response");
    }
    uint32_t size = skpdb::GetU32(response.payload.data());
    if (response.payload.size() < 4 + size) {
        throw std::runtime_error("short list payload");
    }
    std::fwrite(response.payload.data() + 4, 1, size, stdout);
    return 0;
}

int CommandStat(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("stat requires a path");
    }
    std::vector<uint8_t> request{skpdb::opcode::kStat};
    request.insert(request.end(), options.args[0].begin(), options.args[0].end());
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kStat, options.timeout * 1000);
    if (response.payload.size() < 28) {
        throw std::runtime_error("short stat response");
    }
    uint64_t size = skpdb::GetU64(response.payload.data());
    uint32_t mode = skpdb::GetU32(response.payload.data() + 8);
    uint32_t uid = skpdb::GetU32(response.payload.data() + 12);
    uint32_t gid = skpdb::GetU32(response.payload.data() + 16);
    uint64_t mtime = skpdb::GetU64(response.payload.data() + 20);
    std::printf("size=%llu mode=%o uid=%u gid=%u mtime=%llu\n",
                static_cast<unsigned long long>(size), mode, uid, gid,
                static_cast<unsigned long long>(mtime));
    return 0;
}


}  // namespace cli
