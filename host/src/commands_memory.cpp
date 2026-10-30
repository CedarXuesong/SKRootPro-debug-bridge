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

uint8_t ModeByte(const Options& options);

uint64_t ParseQuantity(const std::string& text, const char* name) {
    try {
        size_t used = 0;
        unsigned long long value = std::stoull(text, &used, 0);
        if (used != text.size()) {
            throw std::runtime_error(std::string("invalid ") + name + ": " + text);
        }
        return value;
    } catch (const std::exception&) {
        throw std::runtime_error(std::string("invalid ") + name + ": " + text);
    }
}

uint32_t ParsePid(const std::string& text) {
    const uint64_t value = ParseQuantity(text, "pid");
    if (value == 0 || value > 0x7fffffff) {
        throw std::runtime_error("invalid pid: " + text);
    }
    return static_cast<uint32_t>(value);
}

uint64_t ParseNumber(const std::string& text) { return ParseQuantity(text, "value"); }

std::vector<uint8_t> ParseBytes(const std::string& text) {
    std::vector<uint8_t> bytes;
    if (text.rfind("text:", 0) == 0) {
        const std::string literal = text.substr(5);
        bytes.assign(literal.begin(), literal.end());
        return bytes;
    }
    std::string digits = text.rfind("0x", 0) == 0 ? text.substr(2) : text;
    std::string cleaned;
    for (char character : digits) {
        if (character == ' ' || character == ':' || character == '_' || character == '-') {
            continue;
        }
        cleaned.push_back(character);
    }
    if (cleaned.empty() || (cleaned.size() % 2) != 0) {
        throw std::runtime_error("hex byte string needs an even number of digits");
    }
    auto nibble = [](char character) -> int {
        if (character >= '0' && character <= '9') return character - '0';
        if (character >= 'a' && character <= 'f') return character - 'a' + 10;
        if (character >= 'A' && character <= 'F') return character - 'A' + 10;
        throw std::runtime_error(std::string("not a hex digit: ") + character);
    };
    for (size_t index = 0; index < cleaned.size(); index += 2) {
        bytes.push_back(static_cast<uint8_t>((nibble(cleaned[index]) << 4) |
                                            nibble(cleaned[index + 1])));
    }
    return bytes;
}

bool ParsePattern(const std::string& text, std::vector<uint8_t>& pattern,
                  std::vector<uint8_t>& mask) {
    if (text.find("??") == std::string::npos && text.rfind("text:", 0) != 0) {
        pattern = ParseBytes(text);
        return false;
    }
    if (text.rfind("text:", 0) == 0) {
        pattern = ParseBytes(text);
        return false;
    }
    std::string digits = text.rfind("0x", 0) == 0 ? text.substr(2) : text;
    std::string cleaned;
    for (char character : digits) {
        if (character != ' ') {
            cleaned.push_back(character);
        }
    }
    if (cleaned.empty() || cleaned.size() % 2 != 0) {
        throw std::runtime_error("wildcard pattern needs two hex digits or ?? per byte");
    }
    bool wildcard = false;
    for (size_t index = 0; index < cleaned.size(); index += 2) {
        const std::string pair = cleaned.substr(index, 2);
        if (pair == "??") {
            pattern.push_back(0);
            mask.push_back(0x00);
            wildcard = true;
            continue;
        }
        pattern.push_back(ParseBytes(pair)[0]);
        mask.push_back(0xff);
    }
    if (!wildcard) {
        mask.clear();
    }
    return wildcard;
}

uint8_t MemMode(const std::string& text) {
    if (text == "auto") return skpdb::kMemModeAuto;
    if (text == "kernel") return skpdb::kMemModeKernelOnly;
    if (text == "userspace") return skpdb::kMemModeUserspace;
    if (text == "fault") return skpdb::kMemModeFault;
    throw std::runtime_error("invalid mode: " + text);
}

void PrintHexDump(const std::vector<uint8_t>& data, uint64_t base) {
    for (size_t offset = 0; offset < data.size(); offset += 16) {
        const size_t count = std::min<size_t>(16, data.size() - offset);
        std::printf("%016llx  ", static_cast<unsigned long long>(base + offset));
        for (size_t index = 0; index < 16; ++index) {
            if (index < count) {
                std::printf("%02x ", data[offset + index]);
            } else {
                std::printf("   ");
            }
            if (index == 7) {
                std::printf(" ");
            }
        }
        std::printf(" |");
        for (size_t index = 0; index < count; ++index) {
            const uint8_t byte = data[offset + index];
            std::printf("%c", byte >= 0x20 && byte < 0x7f ? byte : '.');
        }
        std::printf("|\n");
    }
    std::fflush(stdout);
}

bool ParseRangeSpec(const std::string& text, uint64_t& address, uint64_t& length,
                    std::string& error) {
    const size_t separator = text.find(':');
    if (separator == std::string::npos || separator == 0 || separator + 1 >= text.size()) {
        error = "expected <address>:<length>, got \"" + text + "\"";
        return false;
    }
    try {
        address = ParseQuantity(text.substr(0, separator), "address");
        length = ParseQuantity(text.substr(separator + 1), "length");
    } catch (const std::exception& failure) {
        error = failure.what();
        return false;
    }
    if (length == 0) {
        error = "length must be at least 1";
        return false;
    }
    return true;
}

std::vector<std::pair<uint64_t, uint64_t>> ReadableRangesFromMaps(Transport& channel,
                                                                const Options& options,
                                                                uint32_t pid) {
    const std::vector<uint8_t> request{skpdb::opcode::kMemMaps, static_cast<uint8_t>(pid & 0xff),
                                       static_cast<uint8_t>((pid >> 8) & 0xff),
                                       static_cast<uint8_t>((pid >> 16) & 0xff),
                                       static_cast<uint8_t>((pid >> 24) & 0xff)};
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemMaps, options.timeout * 1000);
    const std::string text(response.payload.begin(), response.payload.end());
    std::vector<std::pair<uint64_t, uint64_t>> ranges;
    size_t cursor = 0;
    while (cursor < text.size()) {
        const size_t newline = text.find('\n', cursor);
        const std::string line =
            text.substr(cursor, newline == std::string::npos ? std::string::npos : newline - cursor);
        cursor = newline == std::string::npos ? text.size() : newline + 1;
        unsigned long long start = 0;
        unsigned long long end = 0;
        char perms[8] = {0};
        if (std::sscanf(line.c_str(), "%llx-%llx %7s", &start, &end, perms) < 3) {
            continue;
        }
        if (perms[0] != 'r') {
            continue;
        }
        ranges.emplace_back(start, end);
    }
    return ranges;
}

int CommandMemInfo(Transport& channel, const Options& options) {
    auto response =
        skpdb::Request(channel, {skpdb::opcode::kMemInfo}, skpdb::opcode::kMemInfo,
                       options.timeout * 1000);
    std::fwrite(response.payload.data(), 1, response.payload.size(), stdout);
    std::fflush(stdout);
    return 0;
}

int CommandMemMaps(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("memmaps requires <pid>");
    }
    uint64_t pid = ParseQuantity(options.args[0], "pid");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    std::vector<uint8_t> request{skpdb::opcode::kMemMaps};
    skpdb::PutU32(request, static_cast<uint32_t>(pid));
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemMaps, options.timeout * 1000);
    if (options.args.size() > 1) {
        std::ofstream output(options.args[1], std::ios::binary);
        if (!output) {
            throw std::runtime_error("cannot create " + options.args[1]);
        }
        output.write(reinterpret_cast<const char*>(response.payload.data()),
                     static_cast<std::streamsize>(response.payload.size()));
        std::cerr << "wrote " << response.payload.size() << " bytes to " << options.args[1]
                  << std::endl;
        return 0;
    }
    std::fwrite(response.payload.data(), 1, response.payload.size(), stdout);
    std::fflush(stdout);
    return 0;
}

int CommandMemRead(Transport& channel, const Options& options) {
    if (options.args.size() < 3) {
        throw std::runtime_error("memread requires <pid> <address> <length>");
    }
    const uint64_t pid = ParseQuantity(options.args[0], "pid");
    const uint64_t address = ParseQuantity(options.args[1], "address");
    const uint64_t length = ParseQuantity(options.args[2], "length");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    if (length == 0) {
        throw std::runtime_error("length must be at least 1");
    }
    if (address > 0xFFFFFFFFFFFFFFFFull - length) {
        throw std::runtime_error("range wraps the address space");
    }

    uint8_t mode = skpdb::kMemModeAuto;
    if (options.mem_mode == "kernel") {
        mode = skpdb::kMemModeKernelOnly;
    } else if (options.mem_mode == "userspace") {
        mode = skpdb::kMemModeUserspace;
    } else if (options.mem_mode == "fault") {
        mode = skpdb::kMemModeFault;
    }

    // The device answers one request with a single frame, so a large read is
    // split at the transport's chunk size; a short answer means the mapping
    // ended and the loop stops there.
    const size_t chunk = std::min<size_t>(ChunkSize(options), skpdb::kMaxMemRead);
    std::vector<uint8_t> data;
    uint64_t done = 0;
    uint8_t backend = skpdb::kMemBackendNone;
    while (done < length) {
        const uint32_t want = static_cast<uint32_t>(std::min<uint64_t>(length - done, chunk));
        std::vector<uint8_t> request{skpdb::opcode::kMemRead};
        skpdb::PutU32(request, static_cast<uint32_t>(pid));
        skpdb::PutU64(request, address + done);
        skpdb::PutU32(request, want);
        request.push_back(mode);

        auto response =
            skpdb::Request(channel, request, skpdb::opcode::kMemRead, options.timeout * 1000);
        if (response.payload.empty()) {
            throw std::runtime_error("empty memread response");
        }
        backend = response.payload[0];
        const size_t before = data.size();
        data.insert(data.end(), response.payload.begin() + 1, response.payload.end());
        const size_t got = data.size() - before;
        done += got;
        if (got < want) {
            break;
        }
    }

    std::fprintf(stderr, "backend=%s bytes=%llu of %llu%s\n", skpdb::MemBackendName(backend),
                 static_cast<unsigned long long>(data.size()), static_cast<unsigned long long>(length),
                 data.size() < length ? " (short read)" : "");

    if (!options.mem_out.empty()) {
        std::ofstream output(options.mem_out, std::ios::binary);
        if (!output) {
            throw std::runtime_error("cannot create " + options.mem_out);
        }
        output.write(reinterpret_cast<const char*>(data.data()),
                     static_cast<std::streamsize>(data.size()));
        if (!output) {
            throw std::runtime_error("write failed: " + options.mem_out);
        }
        std::cerr << "wrote " << data.size() << " bytes to " << options.mem_out << std::endl;
        return 0;
    }
    PrintHexDump(data, address);
    return 0;
}

int CommandMemScatter(Transport& channel, const Options& options) {
    if (options.args.size() < 2) {
        throw std::runtime_error("mread requires <pid> <address>:<length> [...]");
    }
    const uint64_t pid = ParseQuantity(options.args[0], "pid");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    if (options.args.size() - 1 > skpdb::kMaxScatterRanges) {
        throw std::runtime_error("at most " + std::to_string(skpdb::kMaxScatterRanges) +
                                 " ranges per request");
    }
    std::vector<std::pair<uint64_t, uint64_t>> ranges;
    uint64_t total = 0;
    for (size_t index = 1; index < options.args.size(); ++index) {
        uint64_t address = 0;
        uint64_t length = 0;
        std::string error;
        if (!ParseRangeSpec(options.args[index], address, length, error)) {
            throw std::runtime_error(error);
        }
        total += length;
        ranges.emplace_back(address, length);
    }
    if (total > skpdb::kMaxMemRead) {
        throw std::runtime_error("ranges must fit in one frame (max " +
                                 std::to_string(skpdb::kMaxMemRead) + " bytes)");
    }

    std::vector<uint8_t> request{skpdb::opcode::kMemScatter};
    skpdb::PutU32(request, static_cast<uint32_t>(pid));
    request.push_back(ModeByte(options));
    skpdb::PutU32(request, static_cast<uint32_t>(ranges.size()));
    for (const auto& range : ranges) {
        skpdb::PutU64(request, range.first);
        skpdb::PutU32(request, static_cast<uint32_t>(range.second));
    }

    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemScatter, options.timeout * 1000);
    const std::vector<uint8_t>& payload = response.payload;
    if (payload.size() < 4) {
        throw std::runtime_error("truncated scatter response");
    }
    // The transport strips the opcode and status bytes, so the payload starts
    // at the range count.
    const uint32_t count = skpdb::GetU32(payload.data());
    std::vector<uint8_t> collected;
    size_t at = 4;
    for (uint32_t index = 0; index < count; ++index) {
        if (at + 10 > payload.size()) {
            throw std::runtime_error("truncated scatter response");
        }
        const uint8_t status = payload[at];
        const uint8_t backend = payload[at + 1];
        const uint32_t data_bytes = skpdb::GetU32(payload.data() + at + 2);
        const uint32_t error_bytes = skpdb::GetU32(payload.data() + at + 6);
        at += 10;
        if (at + error_bytes + data_bytes > payload.size()) {
            throw std::runtime_error("truncated scatter response");
        }
        const std::string error(reinterpret_cast<const char*>(payload.data() + at), error_bytes);
        at += error_bytes;
        const uint8_t* data = payload.data() + at;
        at += data_bytes;

        const auto& range = ranges[index];
        std::fprintf(stderr, "[%u] 0x%llx +%llu backend=%s bytes=%u", index,
                     static_cast<unsigned long long>(range.first),
                     static_cast<unsigned long long>(range.second),
                     skpdb::MemBackendName(backend), data_bytes);
        if (status != 0) {
            std::fprintf(stderr, " status=error \"%s\"", error.c_str());
        }
        std::fprintf(stderr, "\n");
        collected.insert(collected.end(), data, data + data_bytes);
        if (options.mem_out.empty() && data_bytes > 0) {
            const size_t shown = std::min<size_t>(data_bytes, 64);
            PrintHexDump(std::vector<uint8_t>(data, data + shown), range.first);
        }
    }
    if (!options.mem_out.empty()) {
        std::ofstream output(options.mem_out, std::ios::binary);
        if (!output) {
            throw std::runtime_error("cannot create " + options.mem_out);
        }
        output.write(reinterpret_cast<const char*>(collected.data()),
                     static_cast<std::streamsize>(collected.size()));
        std::cerr << "wrote " << collected.size() << " bytes to " << options.mem_out << std::endl;
    }
    return 0;
}

int CommandModBase(Transport& channel, const Options& options) {
    if (options.args.size() < 2) {
        throw std::runtime_error("modbase requires <pid> <substring>");
    }
    const uint64_t pid = ParseQuantity(options.args[0], "pid");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    const std::string needle = options.args[1];
    std::vector<uint8_t> request{skpdb::opcode::kMemMaps};
    skpdb::PutU32(request, static_cast<uint32_t>(pid));
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemMaps, options.timeout * 1000);
    const std::string maps(response.payload.begin(), response.payload.end());
    std::istringstream lines(maps);
    std::string line;
    while (std::getline(lines, line)) {
        if (line.find(needle) == std::string::npos) {
            continue;
        }
        unsigned long long start = 0;
        unsigned long long end = 0;
        unsigned long long offset = 0;
        char perms[8] = {0};
        if (std::sscanf(line.c_str(), "%llx-%llx %7s %llx", &start, &end, perms, &offset) != 4) {
            continue;
        }
        std::printf("path=%s\nmapping=0x%llx-0x%llx perms=%s offset=0x%llx\nbase=0x%llx\n",
                    line.c_str(), start, end, perms, offset, start - offset);
        return 0;
    }
    throw std::runtime_error("no mapping matches \"" + needle + "\"");
}

int CommandMemDump(Transport& channel, const Options& options) {
    if (options.args.size() < 4) {
        throw std::runtime_error("memdump requires <pid> <address> <length> <file>");
    }
    const uint64_t pid = ParseQuantity(options.args[0], "pid");
    const uint64_t start = ParseQuantity(options.args[1], "address");
    const uint64_t length = ParseQuantity(options.args[2], "length");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    if (length == 0) {
        throw std::runtime_error("length must be at least 1");
    }
    const uint64_t end = start + length;
    if (end < start) {
        throw std::runtime_error("range wraps the address space");
    }
    const std::string path = options.args[3];
    std::ofstream output(path, std::ios::binary);
    if (!output) {
        throw std::runtime_error("cannot create " + path);
    }

    const uint32_t max_bytes = static_cast<uint32_t>(
        std::min<uint64_t>(std::min<size_t>(ChunkSize(options), skpdb::kMaxMemRead),
                           skpdb::kMaxMemRead));
    uint64_t cursor = start;
    uint64_t written = 0;
    uint64_t gap_bytes = 0;
    uint64_t gaps = 0;
    uint64_t runs = 0;
    while (cursor < end) {
        std::vector<uint8_t> request{skpdb::opcode::kMemDump};
        skpdb::PutU32(request, static_cast<uint32_t>(pid));
        skpdb::PutU64(request, start);
        skpdb::PutU64(request, end);
        skpdb::PutU64(request, cursor);
        skpdb::PutU32(request, max_bytes);
        request.push_back(ModeByte(options));
        auto response =
            skpdb::Request(channel, request, skpdb::opcode::kMemDump, options.timeout * 1000);
        const std::vector<uint8_t>& payload = response.payload;
        if (payload.size() < 20) {
            throw std::runtime_error("truncated dump response");
        }
        const uint64_t data_start = skpdb::GetU64(payload.data());
        const uint64_t next_cursor = skpdb::GetU64(payload.data() + 8);
        const uint32_t bytes = skpdb::GetU32(payload.data() + 16);
        if (payload.size() < 20 + bytes) {
            throw std::runtime_error("truncated dump response");
        }
        if (data_start > cursor) {
            ++gaps;
            gap_bytes += data_start - cursor;
        }
        if (bytes > 0) {
            ++runs;
            output.seekp(static_cast<std::streamoff>(data_start - start));
            output.write(reinterpret_cast<const char*>(payload.data() + 20), bytes);
            if (!output) {
                throw std::runtime_error("write failed: " + path);
            }
            written += bytes;
        }
        if (next_cursor > data_start + bytes) {
            ++gaps;
            gap_bytes += next_cursor - (data_start + bytes);
        }
        if (next_cursor <= cursor) {
            break;  // no forward progress: stop instead of spinning
        }
        cursor = next_cursor;
    }
    output.close();
    std::fprintf(stderr, "dump=%s pid=%llu range=0x%llx..0x%llx bytes=%llu runs=%llu gaps=%llu "
                         "gap_bytes=%llu\n",
                 path.c_str(), static_cast<unsigned long long>(pid),
                 static_cast<unsigned long long>(start), static_cast<unsigned long long>(end),
                 static_cast<unsigned long long>(written), static_cast<unsigned long long>(runs),
                 static_cast<unsigned long long>(gaps),
                 static_cast<unsigned long long>(gap_bytes));
    return 0;
}

int CommandResident(Transport& channel, const Options& options) {
    if (options.args.size() < 3) {
        throw std::runtime_error("resident requires <pid> <address> <length>");
    }
    const uint64_t pid = ParseQuantity(options.args[0], "pid");
    const uint64_t start = ParseQuantity(options.args[1], "address");
    const uint64_t length = ParseQuantity(options.args[2], "length");
    if (pid == 0 || pid > 0xFFFFFFFFull) {
        throw std::runtime_error("pid out of range");
    }
    if (length == 0) {
        throw std::runtime_error("length must be at least 1");
    }
    const uint64_t pages64 = (length + skpdb::kMemPageBytes - 1) / skpdb::kMemPageBytes;
    if (pages64 == 0 || pages64 > 0xFFFFFFFFull) {
        throw std::runtime_error("range is too large");
    }
    std::vector<uint8_t> request{skpdb::opcode::kMemResident};
    skpdb::PutU32(request, static_cast<uint32_t>(pid));
    skpdb::PutU64(request, start);
    skpdb::PutU32(request, static_cast<uint32_t>(pages64));
    request.push_back(ModeByte(options));
    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemResident, options.timeout * 1000);
    const std::vector<uint8_t>& payload = response.payload;
    if (payload.size() < 4) {
        throw std::runtime_error("truncated resident response");
    }
    const uint32_t pages = skpdb::GetU32(payload.data());
    const size_t needed = 4 + (pages + 7) / 8;
    if (payload.size() < needed) {
        throw std::runtime_error("truncated resident response");
    }
    const uint8_t* bitmap = payload.data() + 4;
    uint32_t resident = 0;
    for (uint32_t page = 0; page < pages; ++page) {
        if (bitmap[page / 8] & (1u << (page % 8))) {
            ++resident;
        }
    }
    std::printf("pages=%u page_bytes=%llu resident=%u holes=%u\n", pages,
                static_cast<unsigned long long>(skpdb::kMemPageBytes), resident,
                pages - resident);
    uint32_t shown = 0;
    uint32_t page = 0;
    while (page < pages && shown < 16) {
        if (bitmap[page / 8] & (1u << (page % 8))) {
            ++page;
            continue;
        }
        const uint32_t first = page;
        while (page < pages && !(bitmap[page / 8] & (1u << (page % 8)))) {
            ++page;
        }
        std::printf("hole 0x%llx..0x%llx (%u pages)\n",
                    static_cast<unsigned long long>(start + static_cast<uint64_t>(first) *
                                                                skpdb::kMemPageBytes),
                    static_cast<unsigned long long>(start + static_cast<uint64_t>(page) *
                                                                skpdb::kMemPageBytes),
                    page - first);
        ++shown;
    }
    if (shown == 16) {
        std::printf("... more holes not shown\n");
    }
    return 0;
}

int CommandSymbols(Transport& channel, const Options& options) {
    if (options.args.size() < 2) {
        throw std::runtime_error("sym requires <pid> <module> [name]");
    }
    const uint32_t pid = ParsePid(options.args[0]);
    const std::string module = options.args[1];
    const bool list = options.args.size() < 3;
    const std::string name = list ? std::string() : options.args[2];

    std::vector<uint8_t> request{skpdb::opcode::kMemSymbols};
    skpdb::PutU32(request, pid);
    request.push_back(static_cast<uint8_t>((list ? 0x01 : 0x00) |
                                           (options.sym_all ? 0x02 : 0x00)));
    request.insert(request.end(), module.begin(), module.end());
    request.push_back(0);
    request.insert(request.end(), name.begin(), name.end());

    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemSymbols, options.timeout * 1000);
    std::cout.write(reinterpret_cast<const char*>(response.payload.data()),
                    static_cast<std::streamsize>(response.payload.size()));
    if (!response.payload.empty() && response.payload.back() != '\n') {
        std::cout << '\n';
    }
    return 0;
}

int CommandPoke(Transport& channel, const Options& options) {
    if (options.args.size() < 3) {
        throw std::runtime_error("poke requires <pid> <address> <hex|text:...>");
    }
    const uint32_t pid = ParsePid(options.args[0]);
    const uint64_t address = ParseNumber(options.args[1]);
    std::vector<uint8_t> bytes = ParseBytes(options.args[2]);
    if (bytes.empty()) {
        throw std::runtime_error("poke needs at least one byte");
    }
    if (bytes.size() > skpdb::kMaxMemWrite) {
        throw std::runtime_error("poke payload is too large for one request");
    }
    uint8_t flags = 0;
    if (options.mem_allow_fault) {
        flags |= skpdb::kMemWriteAllowFault;
    }
    if (options.mem_allow_code) {
        flags |= skpdb::kMemWriteAllowCode;
    }
    if (options.mem_allow_shared) {
        flags |= skpdb::kMemWriteAllowShared;
    }

    std::vector<uint8_t> request{skpdb::opcode::kMemWrite};
    skpdb::PutU32(request, pid);
    skpdb::PutU64(request, address);
    skpdb::PutU32(request, static_cast<uint32_t>(bytes.size()));
    request.push_back(MemMode(options.mem_mode));
    request.push_back(flags);
    request.insert(request.end(), bytes.begin(), bytes.end());

    auto response =
        skpdb::Request(channel, request, skpdb::opcode::kMemWrite, options.timeout * 1000);
    if (response.payload.size() < 9) {
        throw std::runtime_error("truncated write response");
    }
    const uint8_t backend = response.payload[0];
    const uint32_t written = skpdb::GetU32(response.payload.data() + 1);
    const bool shared = response.payload[5] != 0;
    const uint32_t error_bytes = skpdb::GetU32(response.payload.data() + 6);
    const std::string note(response.payload.begin() + 10,
                           response.payload.begin() + 10 + error_bytes);
    std::fprintf(stderr, "wrote=%u of %zu backend=%s shared=%s\n", written, bytes.size(),
                 skpdb::MemBackendName(backend), shared ? "yes" : "no");
    if (!note.empty()) {
        throw std::runtime_error(note);
    }
    return 0;
}

int CommandMemScan(Transport& channel, const Options& options) {
    std::vector<uint8_t> pattern;
    std::vector<uint8_t> mask;
    std::vector<std::pair<uint64_t, uint64_t>> ranges;
    const uint32_t pid = ParsePid(options.args.at(0));
    if (options.mem_scan_maps) {
        if (options.args.size() < 2) {
            throw std::runtime_error("memscan --maps requires <pid> <pattern>");
        }
        ParsePattern(options.args[1], pattern, mask);
        // Bound each request: a 200 MiB mapping in one scan would run for
        // minutes inside a single kernel walk.
        for (const auto& range : ReadableRangesFromMaps(channel, options, pid)) {
            uint64_t cursor = range.first;
            while (cursor < range.second) {
                ranges.emplace_back(cursor, std::min<uint64_t>(cursor + 4 * 1024 * 1024,
                                                              range.second));
                cursor += 4 * 1024 * 1024;
            }
        }
    } else {
        if (options.args.size() < 4) {
            throw std::runtime_error("memscan requires <pid> <address> <length> <pattern>");
        }
        ParsePattern(options.args[3], pattern, mask);
        const uint64_t start = ParseNumber(options.args[1]);
        const uint64_t length = ParseNumber(options.args[2]);
        ranges.emplace_back(start, start + length);
    }
    if (pattern.empty() || pattern.size() > skpdb::kMemScanMaxPattern) {
        throw std::runtime_error("pattern must be 1.." + std::to_string(skpdb::kMemScanMaxPattern) +
                                 " bytes");
    }

    uint32_t remaining = skpdb::kMemScanMaxHits;
    uint32_t total_hits = 0;
    uint64_t total_scanned = 0;
    uint64_t total_skipped = 0;
    size_t empty_ranges = 0;
    for (const auto& range : ranges) {
        if (remaining == 0) {
            break;
        }
        std::vector<uint8_t> request{skpdb::opcode::kMemScan};
        skpdb::PutU32(request, pid);
        skpdb::PutU64(request, range.first);
        skpdb::PutU64(request, range.second);
        skpdb::PutU32(request, remaining);
        request.push_back(static_cast<uint8_t>(pattern.size() >> 8));
        request.push_back(static_cast<uint8_t>(pattern.size() & 0xff));
        request.push_back(MemMode(options.mem_mode));
        request.push_back(mask.empty() ? 0 : 0x01);
        request.insert(request.end(), pattern.begin(), pattern.end());
        request.insert(request.end(), mask.begin(), mask.end());

        skpdb::Response response;
        try {
            response =
                skpdb::Request(channel, request, skpdb::opcode::kMemScan, options.timeout * 1000);
        } catch (const std::exception&) {
            // A mapping with nothing resident answers with an error; a wide
            // scan has to step over it rather than stop at the first hole.
            if (!options.mem_scan_maps) {
                throw;
            }
            ++empty_ranges;
            continue;
        }
        if (response.payload.size() < 20) {
            throw std::runtime_error("truncated scan response");
        }
        const uint32_t hits = skpdb::GetU32(response.payload.data());
        total_scanned += skpdb::GetU64(response.payload.data() + 4);
        total_skipped += skpdb::GetU64(response.payload.data() + 12);
        for (uint32_t index = 0; index < hits; ++index) {
            const uint64_t address = skpdb::GetU64(response.payload.data() + 20 + index * 8);
            std::printf("0x%llx\n", static_cast<unsigned long long>(address));
        }
        total_hits += hits;
        remaining -= std::min(remaining, hits);
    }
    std::fprintf(stderr, "hits=%u scanned=%llu skipped=%llu ranges=%zu empty=%zu\n", total_hits,
                 static_cast<unsigned long long>(total_scanned),
                 static_cast<unsigned long long>(total_skipped), ranges.size(), empty_ranges);
    return total_hits == 0 ? 1 : 0;
}


// touch info|tap|swipe|down|move|up. The device answers with a key=value
// report; an error is carried as a non-zero status and thrown by Request.
int CommandTouch(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("touch needs info|tap|swipe|down|move|up");
    }
    const std::string& action = options.args[0];
    auto number = [&](size_t index, const char* name) -> uint32_t {
        if (index >= options.args.size()) {
            throw std::runtime_error(std::string("touch: missing ") + name);
        }
        const uint64_t value = ParseQuantity(options.args[index], name);
        if (value > 0xffffffffull) {
            throw std::runtime_error(std::string("touch: ") + name + " is out of range");
        }
        return static_cast<uint32_t>(value);
    };
    auto optional_slot = [&](size_t index) -> uint16_t {
        if (index >= options.args.size()) {
            return 9;
        }
        const uint64_t value = ParseQuantity(options.args[index], "slot");
        if (value > 0xffffull) {
            throw std::runtime_error("touch: slot is out of range");
        }
        return static_cast<uint16_t>(value);
    };

    uint8_t code = 0;
    uint32_t x = 0;
    uint32_t y = 0;
    uint32_t x2 = 0;
    uint32_t y2 = 0;
    uint32_t duration = 0;
    uint16_t slot = 9;

    if (action == "info") {
        code = 0;
    } else if (action == "down" || action == "move") {
        code = action == "down" ? 1 : 2;
        x = number(1, "x");
        y = number(2, "y");
        slot = optional_slot(3);
    } else if (action == "up") {
        code = 3;
        slot = optional_slot(1);
    } else if (action == "tap") {
        code = 4;
        x = number(1, "x");
        y = number(2, "y");
        duration = options.args.size() > 3 ? number(3, "hold_ms") : 60;
        slot = optional_slot(4);
    } else if (action == "swipe") {
        code = 5;
        x = number(1, "x1");
        y = number(2, "y1");
        x2 = number(3, "x2");
        y2 = number(4, "y2");
        duration = options.args.size() > 5 ? number(5, "duration_ms") : 300;
        slot = optional_slot(6);
    } else {
        throw std::runtime_error("touch: unknown action " + action);
    }

    std::vector<uint8_t> request{skpdb::opcode::kTouch, code, 0,
                                 static_cast<uint8_t>(slot >> 8),
                                 static_cast<uint8_t>(slot & 0xff)};
    skpdb::PutU32(request, x);
    skpdb::PutU32(request, y);
    skpdb::PutU32(request, x2);
    skpdb::PutU32(request, y2);
    skpdb::PutU32(request, duration);

    skpdb::Response response =
        skpdb::Request(channel, request, skpdb::opcode::kTouch, options.timeout * 1000);
    if (!response.payload.empty()) {
        std::fwrite(response.payload.data(), 1, response.payload.size(), stdout);
    }
    return 0;
}

// gyro status|install|remove|rate <rx> <ry> <rz>. The module owns the hook in
// system_server and answers with a key=value report.
int CommandGyro(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        throw std::runtime_error("gyro needs status|install|remove|rate");
    }
    const std::string& action = options.args[0];
    std::vector<uint8_t> request{skpdb::opcode::kGyro};
    if (action == "status") {
        request.push_back(0);
    } else if (action == "install") {
        if(options.args.size()!=2)throw std::runtime_error("gyro install requires target UID");
        if(options.args[1].empty()||options.args[1].find_first_not_of("0123456789")!=std::string::npos)throw std::runtime_error("invalid gyro UID");
        request.push_back(1);
        const auto uid=std::stoul(options.args[1]);if(uid<1000||uid>UINT32_MAX)throw std::runtime_error("invalid gyro UID");
        skpdb::PutU32(request,static_cast<uint32_t>(uid));
    } else if (action == "remove") {
        request.push_back(2);
    } else if (action == "stop") {
        request.push_back(8);
    } else if (action == "ping") {
        request.push_back(6);
    } else if (action == "hits") {
        request.push_back(7);
    } else if (action == "bench") {
        request.push_back(5);
    } else if (action == "rate" || action == "hold") {
        if (options.args.size() != 4) {
            throw std::runtime_error("gyro rate needs <rx> <ry> <rz> in rad/s");
        }
        request.push_back(action == "hold" ? 4 : 3);
        for (size_t index = 1; index <= 3; ++index) {
            const float value = std::stof(options.args[index]);
            uint32_t bits = 0;
            std::memcpy(&bits, &value, sizeof(bits));
            skpdb::PutU32(request, bits);
        }
    } else {
        throw std::runtime_error("gyro: unknown action " + action);
    }

    skpdb::Response response =
        skpdb::Request(channel, request, skpdb::opcode::kGyro, options.timeout * 1000);
    if (!response.payload.empty()) {
        std::fwrite(response.payload.data(), 1, response.payload.size(), stdout);
    }
    return 0;
}

uint8_t ModeByte(const Options& options) {
    if (options.mem_mode == "kernel") {
        return skpdb::kMemModeKernelOnly;
    }
    if (options.mem_mode == "userspace") {
        return skpdb::kMemModeUserspace;
    }
    if (options.mem_mode == "fault") {
        return skpdb::kMemModeFault;
    }
    return skpdb::kMemModeAuto;
}


}  // namespace cli
