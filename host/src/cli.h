#pragma once

// Shared CLI state: the parsed options, the root key that every connection has
// to prove it knows, and the connection helpers every command uses.

#include <memory>
#include <string>
#include <vector>

#include "knock.h"
#include "protocol.h"
#include "transport.h"

namespace cli {

using skpdb::Transport;

struct Options {
    std::string host;
    int port = 39876;
    int knock_port = skpdb::kDefaultKnockPort;
    bool no_knock = false;
    int timeout = 60;
    size_t chunk = 0;
    size_t window = 8;
    bool progress = false;
    bool install_replace = false;
    bool install_downgrade = false;
    bool install_grant = false;
    bool install_test = false;
    bool install_sdcard = false;
    std::string install_user;
    std::string command;
    std::vector<std::string> args;
    std::string mem_mode = "auto";
    bool mem_allow_fault = false;
    bool mem_allow_shared = false;
    bool mem_allow_code = false;
    bool mem_scan_maps = false;
    bool sym_all = false;
    std::string mem_out;
    // Read commands from stdin and run them over one connection.
    bool batch = false;
};

extern std::string g_root_key;

void PrintUsage();
bool ParseOptions(int argc, char** argv, Options& options, std::string& error);
std::string PromptKey();
size_t ChunkSize(const Options& options);
std::unique_ptr<skpdb::Transport> Connect(const Options& options, std::string& error);

}  // namespace cli
