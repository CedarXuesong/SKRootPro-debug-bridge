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

std::string g_root_key;

void PrintUsage() {
    std::cerr
        << "SKPDB desktop client\n"
        << "\n"
        << "Usage: skpdb [options] <command> [args]\n"
        << "\n"
        << "Commands:\n"
        << "  ping                    check the link\n"
        << "  version                 print the device capability record\n"
        << "  run <command>           execute a shell command and print its output\n"
        << "  shell                   interactive command loop (:quit to exit)\n"
        << "  get <remote> [local]    download a file\n"
        << "  put <local> <remote>    upload a file\n"
        << "  install <apk> [flags]   install an APK like adb install\n"
        << "                          flags: -r replace, -d downgrade, -g grant,\n"
        << "                                 -t test packages, -s sdcard, --user <id>\n"
        << "  ls [path]               list a directory\n"
        << "  stat <path>             print file metadata\n"
        << "  meminfo                 report which memory read backends work\n"
        << "  memmaps <pid> [out]     print (or save) /proc/<pid>/maps of a process\n"
        << "  memread <pid> <addr> <len>  read another process' memory without ptrace\n"
    << "  mread <pid> <addr:len>...   read several ranges in one request (one lock, one RTT)\n"
    << "  modbase <pid> <substring>   print the base address of a mapped module\n"
    << "  memdump <pid> <addr> <len> <file>  save every resident byte of a range (sparse)\n"
    << "  resident <pid> <addr> <len>  report which pages of a range are resident\n"
    << "  memscan <pid> <addr> <len> <pattern>  find a byte pattern, resident pages only\n"
    << "  memscan --maps <pid> <pattern>   scan every readable mapping instead\n"
    << "  sym <pid> <module> [name]   resolve a symbol in a mapped module (--all: locals too)\n"
    << "  poke <pid> <addr> <hex|text:...>   write bytes into a process\n"
    << "  touch info                       report the resolved touchscreen and its ranges\n"
    << "  touch tap <x> <y> [ms] [slot]     tap the real touchscreen (default 60 ms, slot 9)\n"
    << "  touch swipe <x1> <y1> <x2> <y2> [ms] [slot]  swipe (default 300 ms)\n"
    << "  touch down|move <x> <y> [slot]    hold a finger down, or move it\n"
    << "  touch up [slot]                  lift the injected finger\n"
    << "  gyro status|install <uid>|remove|stop  scoped framework gyro control\n"
    << "  gyro rate <rx> <ry> <rz>         set the injected rate in rad/s\n"
        << "                          addr accepts 0x hex; with --out the raw bytes\n"
        << "                          are written instead of a hex dump\n"
        << "  knock                   send the signed knock and exit\n"
        << "  info                    print the module report (ports, peers, sessions)\n"
        << "  config <k=v> ...        change settings, e.g. service_port=39876\n"
        << "                          keys: service_port, knock_port,\n"
        << "                                idle_timeout_seconds, auth_window_seconds\n"
        << "  webui [start|stop|link] control the module WebUI server and print the\n"
        << "                          self-unlocking page link (link by default)\n"
        << "\n"
        << "Options:\n"
        << "  --host <address>               IPv4 address of the module (required)\n"
        << "  --port <n>                     TCP service port (default 39876)\n"
        << "  --knock-port <n>               UDP knock port (default 39877)\n"
        << "  --no-knock                     connect without knocking first\n"
        << "  --timeout <seconds>            I/O timeout (default 60)\n"
        << "  --chunk <bytes>                transfer chunk (default 900 KiB)\n"
        << "  --window <n>                   pipelined read requests (default 8)\n"
        << "  --progress                     print transfer progress\n"
        << "  --mode <auto|kernel|userspace|fault>  memread policy (default auto: never faults)\n"
    << "  --allow-fault                  let poke fault a page in\n"
    << "  --allow-shared                 let poke write a shared file-backed page\n"
    << "  --allow-code                   let poke patch a read-only/executable page\n"
    << "  --all                          sym: include .symtab (local symbols)\n"
    << "  --maps                         memscan: scan every readable mapping\n"
    << "  --batch                        read commands from stdin, one per line,\n"
    << "                                 over a single connection (blank/# lines skipped)\n"
        << "  --out <file>                   write memread bytes to a file\n";
}

bool ParseOptions(int argc, char** argv, Options& options, std::string& error) {
    for (int index = 1; index < argc; ++index) {
        std::string argument = argv[index];
        auto next = [&](const char* name) -> std::string {
            if (index + 1 >= argc) {
                throw std::runtime_error(std::string("missing value for ") + name);
            }
            return argv[++index];
        };
        try {
            if (argument == "--host") {
                options.host = next("--host");
            } else if (argument == "--port") {
                options.port = std::stoi(next("--port"));
            } else if (argument == "--knock-port") {
                options.knock_port = std::stoi(next("--knock-port"));
            } else if (argument == "--no-knock") {
                options.no_knock = true;
            } else if (argument == "--timeout") {
                options.timeout = std::stoi(next("--timeout"));
            } else if (argument == "--chunk") {
                options.chunk = static_cast<size_t>(std::stoull(next("--chunk")));
            } else if (argument == "--window") {
                options.window = static_cast<size_t>(std::stoull(next("--window")));
            } else if (argument == "--progress") {
                options.progress = true;
            } else if (argument == "--mode") {
                options.mem_mode = next("--mode");
            } else if (argument == "--out") {
                options.mem_out = next("--out");
            } else if (argument == "--allow-fault") {
            options.mem_allow_fault = true;
        } else if (argument == "--allow-code") {
            options.mem_allow_code = true;
        } else if (argument == "--allow-shared") {
            options.mem_allow_shared = true;
        } else if (argument == "--all") {
            options.sym_all = true;
        } else if (argument == "--maps") {
            options.mem_scan_maps = true;
        } else if (argument == "--batch") {
            options.batch = true;
        } else if (argument == "--help" || argument == "-h") {
                PrintUsage();
                std::exit(0);
            } else if (options.command == "install" &&
                       (argument == "-r" || argument == "-d" || argument == "-g" ||
                        argument == "-t" || argument == "-s")) {
                if (argument == "-r") {
                    options.install_replace = true;
                } else if (argument == "-d") {
                    options.install_downgrade = true;
                } else if (argument == "-g") {
                    options.install_grant = true;
                } else if (argument == "-t") {
                    options.install_test = true;
                } else {
                    options.install_sdcard = true;
                }
            } else if (options.command == "install" && argument == "--user") {
                options.install_user = next("--user");
            } else if (!argument.empty() && argument[0] == '-') {
                error = "unknown option: " + argument;
                return false;
            } else if (options.command.empty()) {
                options.command = argument;
            } else {
                options.args.push_back(argument);
            }
        } catch (const std::exception&) {
            error = "invalid value for " + argument;
            return false;
        }
    }
    if (options.command.empty() && !options.batch) {
        error = "missing command";
        return false;
    }
    if (options.host.empty() && options.command != "help") {
        error = "--host is required";
        return false;
    }
    if (options.timeout < 1 || options.timeout > 600) {
        error = "--timeout must be between 1 and 600 seconds";
        return false;
    }
    if (options.window < 1 || options.window > 64) {
        error = "--window must be between 1 and 64";
        return false;
    }
    if (options.chunk > skpdb::kMaxFrame - 16) {
        error = "--chunk is larger than the protocol frame limit";
        return false;
    }
    if (options.mem_mode != "auto" && options.mem_mode != "kernel" &&
        options.mem_mode != "userspace" && options.mem_mode != "fault") {
        error = "--mode must be auto, kernel, userspace or fault";
        return false;
    }
    return true;
}

std::string PromptKey() {
    // A key in the environment comes first: --batch reads commands from stdin,
    // so the key cannot be expected there, and scripts prefer it this way.
    const char* from_env = std::getenv("SKPDB_KEY");
    if (from_env != nullptr && *from_env != '\0') {
        g_root_key = from_env;
        return g_root_key;
    }
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode = 0;
    bool console = GetConsoleMode(input, &mode) != 0;
    if (console) {
        SetConsoleMode(input, mode & ~ENABLE_ECHO_INPUT);
    }
    std::cerr << "SKP key: " << std::flush;
    std::string key;
    std::getline(std::cin, key);
    if (console) {
        SetConsoleMode(input, mode);
        std::cerr << std::endl;
    }
    g_root_key = key;
    return key;
}

size_t ChunkSize(const Options& options) {
    if (options.chunk != 0) {
        return options.chunk;
    }
    return 900 * 1024;
}

std::unique_ptr<Transport> Connect(const Options& options, std::string& error) {
    const std::string key = PromptKey();
    if (key.empty()) {
        error = "a root key is required for this device";
        return nullptr;
    }
    bool knocked = options.no_knock;
    if (!options.no_knock) {
        std::string knock_error;
        // Five datagrams: a lost UDP knock otherwise looks like a device that
        // is not listening at all.
        knocked = skpdb::SendKnock(options.host, static_cast<uint16_t>(options.knock_port), key,
                                   5, knock_error);
        if (!knocked) {
            error = knock_error;
            return nullptr;
        }
    }

    auto provider = [&key] { return key; };
    std::unique_ptr<Transport> channel;
    // The device opens the port inside its knock handler now, so the first
    // connect usually succeeds; the retries only cover a slow scheduler.
    auto delay = std::chrono::milliseconds(40);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(2000);
    for (;;) {
        channel = skpdb::TcpTransport::Open(options.host, options.port, options.timeout, error);
        if (channel) {
            break;
        }
        if (options.no_knock || std::chrono::steady_clock::now() >= deadline) {
            if (!options.no_knock) {
                error = "the device did not accept the knock (no port opened on " +
                        options.host + ":" + std::to_string(options.port) +
                        "); check the key and the knock port";
            }
            return nullptr;
        }
        std::this_thread::sleep_for(delay);
        delay += std::chrono::milliseconds(30);
    }
    std::cerr << "connected: " << channel->Name() << std::endl;

    if (!skpdb::Authenticate(*channel, provider, options.timeout * 1000, error)) {
        channel->Close();
        return nullptr;
    }
    return channel;
}

// One command against an open channel. Shared by the single-command path and by
// --batch so both print and fail the same way.
int Dispatch(Transport& channel, const Options& options) {
    if (options.command == "ping") {
        return CommandPing(channel, options);
    }
    if (options.command == "version") {
        return CommandVersion(channel, options);
    }
    if (options.command == "run") {
        return CommandRun(channel, options);
    }
    if (options.command == "shell") {
        return CommandShell(channel, options);
    }
    if (options.command == "get") {
        return CommandGet(channel, options);
    }
    if (options.command == "put") {
        return CommandPut(channel, options);
    }
    if (options.command == "install") {
        return CommandInstall(channel, options);
    }
    if (options.command == "ls") {
        return CommandList(channel, options);
    }
    if (options.command == "stat") {
        return CommandStat(channel, options);
    }
    if (options.command == "meminfo") {
        return CommandMemInfo(channel, options);
    }
    if (options.command == "memmaps") {
        return CommandMemMaps(channel, options);
    }
    if (options.command == "memread") {
        return CommandMemRead(channel, options);
    }
    if (options.command == "mread") {
        return CommandMemScatter(channel, options);
    }
    if (options.command == "memdump") {
        return CommandMemDump(channel, options);
    }
    if (options.command == "resident") {
        return CommandResident(channel, options);
    }
    if (options.command == "modbase") {
        return CommandModBase(channel, options);
    }
    if (options.command == "memscan") {
        return CommandMemScan(channel, options);
    }
    if (options.command == "sym") {
        return CommandSymbols(channel, options);
    }
    if (options.command == "poke") {
        return CommandPoke(channel, options);
    }
    if (options.command == "touch") {
        return CommandTouch(channel, options);
    }
    if (options.command == "gyro") {
        return CommandGyro(channel, options);
    }
    if (options.command == "info") {
        return CommandInfo(channel, options);
    }
    if (options.command == "config") {
        return CommandConfig(channel, options);
    }
    if (options.command == "webui") {
        return CommandWebUi(channel, options);
    }
    std::cerr << "skpdb: unknown command: " << options.command << std::endl;
    PrintUsage();
    return 2;
}

// Lines from stdin, one command per line, all over the connection that is
// already open. Blank lines and lines starting with # are ignored.
int RunBatch(Transport& channel, const Options& base) {
    std::string line;
    int worst = 0;
    while (std::getline(std::cin, line)) {
        const size_t first = line.find_first_not_of(" \t\r");
        if (first == std::string::npos || line[first] == '#') {
            continue;
        }
        const size_t last = line.find_last_not_of(" \t\r");
        const std::string text = line.substr(first, last - first + 1);
        std::vector<std::string> words;
        size_t cursor = 0;
        while (cursor < text.size()) {
            const size_t space = text.find_first_of(" \t", cursor);
            if (space == std::string::npos) {
                words.push_back(text.substr(cursor));
                break;
            }
            if (space > cursor) {
                words.push_back(text.substr(cursor, space - cursor));
            }
            cursor = space + 1;
        }
        if (words.empty()) {
            continue;
        }
        if (words[0] == "quit" || words[0] == "exit") {
            break;
        }
        Options one = base;
        one.command = words[0];
        one.args.assign(words.begin() + 1, words.end());
        try {
            const int result = Dispatch(channel, one);
            if (result != 0) {
                worst = result;
            }
        } catch (const std::exception& exception) {
            std::cerr << "skpdb: " << exception.what() << std::endl;
            worst = 1;
        }
    }
    return worst;
}

}  // namespace cli

using namespace cli;

int main(int argc, char** argv) {
    Options options;
    std::string error;
    if (!ParseOptions(argc, argv, options, error)) {
        std::cerr << "skpdb: " << error << std::endl;
        PrintUsage();
        return 2;
    }

    try {
        if (options.command == "help") {
            PrintUsage();
            return 0;
        }
        if (options.command == "knock") {
            return CommandKnock(options);
        }

        std::unique_ptr<Transport> channel = Connect(options, error);
        if (!channel) {
            std::cerr << "skpdb: " << error << std::endl;
            return 1;
        }

        int result = 0;
        if (options.batch) {
            result = RunBatch(*channel, options);
        } else {
            result = Dispatch(*channel, options);
        }
        // End the session explicitly. Without this the device keeps reading
        // the closed channel and can consume the next process's preamble as
        // if it were a frame header.
        try {
            skpdb::Request(*channel, {skpdb::opcode::kClose}, skpdb::opcode::kClose, 1000);
        } catch (const std::exception&) {
            // The result is already reported; a missing close is harmless.
        }
        channel->Close();
        return result;
    } catch (const std::exception& exception) {
        std::cerr << "skpdb: " << exception.what() << std::endl;
        return 1;
    }
}
