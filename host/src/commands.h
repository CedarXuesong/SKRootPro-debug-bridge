#pragma once

// One declaration per client command. main.cpp parses options and dispatches;
// the bodies live in commands_session.cpp, commands_memory.cpp and
// commands_admin.cpp.

#include <string>
#include <vector>

#include "cli.h"
#include "protocol.h"
#include "transport.h"

namespace cli {

uint64_t NowMs();
void PrintProgress(size_t& last_percent, uint64_t total, uint64_t started_ms);
std::string ExecCapture(Transport& channel, const std::string& command, int timeout_seconds,
                        uint32_t& code);
int CommandRun(Transport& channel, const Options& options);
int CommandShell(Transport& channel, const Options& options);
std::string BaseName(const std::string& path);
int CommandGet(Transport& channel, const Options& options);
uint64_t UploadFile(Transport& channel, const std::string& local, const std::string& remote,
                    const Options& options, bool report);
int CommandPut(Transport& channel, const Options& options);
int CommandInstall(Transport& channel, const Options& options);
int CommandList(Transport& channel, const Options& options);
int CommandStat(Transport& channel, const Options& options);
uint64_t ParseQuantity(const std::string& text, const char* name);
uint32_t ParsePid(const std::string& text);
uint64_t ParseNumber(const std::string& text);
std::vector<uint8_t> ParseBytes(const std::string& text);
bool ParsePattern(const std::string& text, std::vector<uint8_t>& pattern,
                  std::vector<uint8_t>& mask);
uint8_t MemMode(const std::string& text);
void PrintHexDump(const std::vector<uint8_t>& data, uint64_t base);
bool ParseRangeSpec(const std::string& text, uint64_t& address, uint64_t& length,
                    std::string& error);
std::vector<std::pair<uint64_t, uint64_t>> ReadableRangesFromMaps(Transport& channel,
                                                                const Options& options,
                                                                uint32_t pid);
int CommandMemInfo(Transport& channel, const Options& options);
int CommandMemMaps(Transport& channel, const Options& options);
int CommandMemRead(Transport& channel, const Options& options);
int CommandMemScatter(Transport& channel, const Options& options);
int CommandModBase(Transport& channel, const Options& options);
int CommandMemDump(Transport& channel, const Options& options);
int CommandResident(Transport& channel, const Options& options);
int CommandSymbols(Transport& channel, const Options& options);
int CommandPoke(Transport& channel, const Options& options);
int CommandTouch(Transport& channel, const Options& options);
int CommandGyro(Transport& channel, const Options& options);
int CommandMemScan(Transport& channel, const Options& options);
std::string AdminCall(Transport& channel, uint8_t action, const std::string& body,
                      const Options& options);
int CommandInfo(Transport& channel, const Options& options);
int CommandConfig(Transport& channel, const Options& options);
int CommandWebUi(Transport& channel, const Options& options);
int CommandKnock(const Options& options);
int CommandPing(Transport& channel, const Options& options);
int CommandVersion(Transport& channel, const Options& options);

// Runs one already-parsed command against an open channel.
int Dispatch(Transport& channel, const Options& options);

}  // namespace cli
