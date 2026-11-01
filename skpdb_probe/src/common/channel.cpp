#include "common/channel.h"

#include <dirent.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <thread>

#include "common/bytes.h"
#include "common/frame.h"
#include "display/cursor.h"
#include "input/gyro.h"
#include "input/touch.h"
#include "memory/memread.h"
#include "memory/symtab.h"
#include "common/stream.h"

namespace skpdb {
namespace {

std::vector<uint8_t> ErrorResponse(uint8_t opcode, const std::string& message) {
    std::vector<uint8_t> response{opcode, 1};
    size_t size = std::min(message.size(), kMaxFrame - 6);
    PutU32(response, static_cast<uint32_t>(size));
    response.insert(response.end(), message.begin(), message.begin() + size);
    return response;
}

// request: [E][command...]
// response: [E][0][u32 exit_code][u32 size][output...]
std::vector<uint8_t> ExecuteCommand(const std::vector<uint8_t>& request, const Stream& stream) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kExec, "empty command");
    }
    std::string command(reinterpret_cast<const char*>(request.data() + 1), request.size() - 1);

    int pipe_fds[2];
    if (pipe(pipe_fds) != 0) {
        return ErrorResponse(opcode::kExec, std::strerror(errno));
    }
    pid_t child = fork();
    if (child < 0) {
        int saved_errno = errno;
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        return ErrorResponse(opcode::kExec, std::strerror(saved_errno));
    }
    if (child == 0) {
        // Put the shell in its own process group so the whole command tree can
        // be signalled when the transport disappears.
        setpgid(0, 0);
        close(pipe_fds[0]);
        if (dup2(pipe_fds[1], STDOUT_FILENO) < 0 || dup2(pipe_fds[1], STDERR_FILENO) < 0) {
            _exit(126);
        }
        close(pipe_fds[1]);
        execl("/system/bin/sh", "sh", "-c", command.c_str(), static_cast<char*>(nullptr));
        _exit(127);
    }

    close(pipe_fds[1]);
    setpgid(child, child);
    fcntl(pipe_fds[0], F_SETFL, fcntl(pipe_fds[0], F_GETFL) | O_NONBLOCK);

    std::vector<uint8_t> output;
    output.reserve(4096);
    uint8_t buffer[8192];
    for (;;) {
        // A transport that can detect a dead link reports it here; the poll
        // below keeps the loop responsive while the child is running.
        if (!stream.Alive()) {
            kill(-child, SIGTERM);
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            kill(-child, SIGKILL);
            break;
        }
        struct pollfd descriptor{pipe_fds[0], POLLIN, 0};
        int ready = poll(&descriptor, 1, 100);
        if (ready < 0 && errno == EINTR) {
            continue;
        }
        if (ready < 0) {
            break;
        }
        if (ready == 0) {
            continue;
        }

        ssize_t count = read(pipe_fds[0], buffer, sizeof(buffer));
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            continue;
        }
        if (count <= 0) {
            break;
        }

        // Stop reading at the cap. The read end is closed below and the child
        // sees a broken pipe instead of filling memory.
        size_t take =
            std::min(static_cast<size_t>(count), kMaxCommandOutput - output.size());
        output.insert(output.end(), buffer, buffer + take);
        if (output.size() >= kMaxCommandOutput) {
            break;
        }
    }
    close(pipe_fds[0]);

    int status = 0;
    if (waitpid(child, &status, 0) < 0) {
        return ErrorResponse(opcode::kExec, std::strerror(errno));
    }
    int exit_code = 255;
    if (WIFEXITED(status)) {
        exit_code = WEXITSTATUS(status);
    } else if (WIFSIGNALED(status)) {
        exit_code = 128 + WTERMSIG(status);
    }

    size_t size = std::min(output.size(), kMaxFrame - 10);
    std::vector<uint8_t> response{opcode::kExec, 0};
    PutU32(response, static_cast<uint32_t>(exit_code));
    PutU32(response, static_cast<uint32_t>(size));
    response.insert(response.end(), output.begin(), output.begin() + size);
    return response;
}

// request: [R][u64 offset][u32 amount][path...]
// response: [R][0][u32 size][data...]
std::vector<uint8_t> ReadFile(const std::vector<uint8_t>& request) {
    if (request.size() < 13) {
        return ErrorResponse(opcode::kRead, "invalid read request");
    }
    uint64_t offset = GetU64(request.data() + 1);
    uint32_t amount = std::min(GetU32(request.data() + 9), static_cast<uint32_t>(kMaxFrame - 7));
    std::string path(reinterpret_cast<const char*>(request.data() + 13), request.size() - 13);

    int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return ErrorResponse(opcode::kRead, std::strerror(errno));
    }
    std::vector<uint8_t> data(amount);
    ssize_t count = pread(fd, data.data(), data.size(), static_cast<off_t>(offset));
    int saved_errno = errno;
    close(fd);
    if (count < 0) {
        return ErrorResponse(opcode::kRead, std::strerror(saved_errno));
    }
    data.resize(static_cast<size_t>(count));

    std::vector<uint8_t> response{opcode::kRead, 0};
    PutU32(response, static_cast<uint32_t>(data.size()));
    response.insert(response.end(), data.begin(), data.end());
    return response;
}

// request: [W][u64 offset][u8 flags][path\0][data...]
// flags bit 0 truncates the destination before writing.
// response: [W][0][u32 written]
std::vector<uint8_t> WriteFile(const std::vector<uint8_t>& request) {
    if (request.size() < 11) {
        return ErrorResponse(opcode::kWrite, "invalid write request");
    }
    uint64_t offset = GetU64(request.data() + 1);
    uint8_t flags = request[9];
    auto path_end = std::find(request.begin() + 10, request.end(), 0);
    if (path_end == request.end()) {
        return ErrorResponse(opcode::kWrite, "missing path");
    }
    std::string path(reinterpret_cast<const char*>(request.data() + 10),
                     static_cast<size_t>(path_end - (request.begin() + 10)));
    const uint8_t* data = request.data() + 10 + path.size() + 1;
    size_t size = static_cast<size_t>(request.data() + request.size() - data);

    int open_flags = O_WRONLY | O_CREAT | O_CLOEXEC;
    if (flags & 1) {
        open_flags |= O_TRUNC;
    }
    int fd = open(path.c_str(), open_flags, 0600);
    if (fd < 0) {
        return ErrorResponse(opcode::kWrite, std::strerror(errno));
    }
    ssize_t count = pwrite(fd, data, size, static_cast<off_t>(offset));
    int saved_errno = errno;
    close(fd);
    if (count < 0) {
        return ErrorResponse(opcode::kWrite, std::strerror(saved_errno));
    }

    std::vector<uint8_t> response{opcode::kWrite, 0};
    PutU32(response, static_cast<uint32_t>(count));
    return response;
}

// request: [L][path...]
// response: [L][0][u32 size][type TAB name LF ...]
std::vector<uint8_t> ListDirectory(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kList, "missing path");
    }
    std::string path(reinterpret_cast<const char*>(request.data() + 1), request.size() - 1);
    DIR* directory = opendir(path.c_str());
    if (!directory) {
        return ErrorResponse(opcode::kList, std::strerror(errno));
    }
    std::string listing;
    while (dirent* entry = readdir(directory)) {
        if (!std::strcmp(entry->d_name, ".") || !std::strcmp(entry->d_name, "..")) {
            continue;
        }
        listing += entry->d_type == DT_DIR ? "d\t" : entry->d_type == DT_LNK ? "l\t" : "f\t";
        listing += entry->d_name;
        listing.push_back('\n');
        if (listing.size() >= kMaxFrame - 7) {
            break;
        }
    }
    closedir(directory);

    std::vector<uint8_t> response{opcode::kList, 0};
    PutU32(response, static_cast<uint32_t>(listing.size()));
    response.insert(response.end(), listing.begin(), listing.end());
    return response;
}

// request: [T][path...]
// response: [T][0][u64 size][u32 mode][u32 uid][u32 gid][u64 mtime]
std::vector<uint8_t> StatPath(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kStat, "missing path");
    }
    std::string path(reinterpret_cast<const char*>(request.data() + 1), request.size() - 1);
    struct stat info{};
    if (stat(path.c_str(), &info) != 0) {
        return ErrorResponse(opcode::kStat, std::strerror(errno));
    }
    std::vector<uint8_t> response{opcode::kStat, 0};
    PutU64(response, static_cast<uint64_t>(info.st_size));
    PutU32(response, static_cast<uint32_t>(info.st_mode));
    PutU32(response, static_cast<uint32_t>(info.st_uid));
    PutU32(response, static_cast<uint32_t>(info.st_gid));
    PutU64(response, static_cast<uint64_t>(info.st_mtime));
    return response;
}

// request: [M][u32 pid][u64 address][u32 length][u8 mode]
// response: [M][0][u8 backend][bytes...]
std::vector<uint8_t> ReadProcessMemoryRange(const std::vector<uint8_t>& request) {
    if (request.size() != 18) {
        return ErrorResponse(opcode::kMemRead, "expected 18 bytes");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint64_t address = GetU64(request.data() + 5);
    const uint32_t length = GetU32(request.data() + 13);
    const uint8_t mode = request[17];
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemRead, "unknown mode");
    }
    if (length == 0 || length > kMaxMemRead) {
        return ErrorResponse(opcode::kMemRead,
                             "length must be 1.." + std::to_string(kMaxMemRead));
    }
    MemReadResult read = ReadProcessMemory(pid, address, length, mode);
    if (read.data.empty()) {
        return ErrorResponse(opcode::kMemRead, read.error.empty() ? "read failed" : read.error);
    }
    // A short answer is normal at the end of a mapping, so the byte count in the
    // frame is the length; backend tells the caller which path produced it.
    std::vector<uint8_t> response{opcode::kMemRead, 0, read.backend};
    response.insert(response.end(), read.data.begin(), read.data.end());
    return response;
}

// request: [D][u32 pid][u64 start][u64 end][u64 cursor][u32 max_bytes][u8 mode]
// response: [D][0][u64 data_start][u64 next_cursor][u32 data bytes][data]
// The caller writes `data` at file offset (data_start - start) and continues at
// next_cursor, so a dump is a sparse file with holes where memory was not
// resident.
std::vector<uint8_t> DumpRange(const std::vector<uint8_t>& request) {
    constexpr size_t kRequestBytes = 34;
    if (request.size() != kRequestBytes) {
        return ErrorResponse(opcode::kMemDump, "dump request must be 34 bytes");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint64_t start = GetU64(request.data() + 5);
    const uint64_t end = GetU64(request.data() + 13);
    const uint64_t cursor = GetU64(request.data() + 21);
    const uint32_t max_bytes = GetU32(request.data() + 29);
    const uint8_t mode = request[33];
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemDump, "unknown mode");
    }
    if (max_bytes == 0 || max_bytes > kMaxMemRead) {
        return ErrorResponse(opcode::kMemDump,
                             "max_bytes must be 1.." + std::to_string(kMaxMemRead));
    }
    if (end <= start || cursor < start || cursor > end) {
        return ErrorResponse(opcode::kMemDump, "cursor must be inside [start, end)");
    }
    MemDumpChunk chunk = DumpResidentRun(pid, cursor, end, max_bytes, mode);
    std::vector<uint8_t> response{opcode::kMemDump, 0};
    PutU64(response, chunk.data_start);
    PutU64(response, chunk.next_cursor);
    PutU32(response, static_cast<uint32_t>(chunk.data.size()));
    response.insert(response.end(), chunk.data.begin(), chunk.data.end());
    return response;
}

// request: [P][u32 pid][u64 start][u32 pages][u8 mode]
// response: [P][0][u32 pages][bitmap]
std::vector<uint8_t> ResidentPages(const std::vector<uint8_t>& request) {
    constexpr size_t kRequestBytes = 18;
    if (request.size() != kRequestBytes) {
        return ErrorResponse(opcode::kMemResident, "resident request must be 18 bytes");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint64_t start = GetU64(request.data() + 5);
    const uint32_t pages = GetU32(request.data() + 13);
    const uint8_t mode = request[17];
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemResident, "unknown mode");
    }
    if (pages == 0) {
        return ErrorResponse(opcode::kMemResident, "pages must be at least 1");
    }
    if (static_cast<uint64_t>(pages) * kMemPageBytes > kMaxMemRead * 64ull) {
        return ErrorResponse(opcode::kMemResident, "too many pages for one request");
    }
    MemResidentMap map = ProbeResidentPages(pid, start, pages, mode);
    if (!map.error.empty()) {
        return ErrorResponse(opcode::kMemResident, map.error);
    }
    std::vector<uint8_t> response{opcode::kMemResident, 0};
    PutU32(response, map.pages);
    response.insert(response.end(), map.bitmap.begin(), map.bitmap.end());
    return response;
}


// request: [X][u32 pid][u64 address][u32 length][u8 mode][u8 flags][bytes]
// response: [X][0][u8 backend][u32 written][u8 shared]
std::vector<uint8_t> WriteRange(const std::vector<uint8_t>& request) {
    constexpr size_t kHeaderBytes = 19;
    if (request.size() < kHeaderBytes) {
        return ErrorResponse(opcode::kMemWrite, "write request is too short");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint64_t address = GetU64(request.data() + 5);
    const uint32_t length = GetU32(request.data() + 13);
    const uint8_t mode = request[17];
    const uint8_t flags = request[18];
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemWrite, "unknown mode");
    }
    if (length == 0 || length > kMaxMemWrite) {
        return ErrorResponse(opcode::kMemWrite,
                             "length must be 1.." + std::to_string(kMaxMemWrite));
    }
    if (request.size() != kHeaderBytes + length) {
        return ErrorResponse(opcode::kMemWrite, "length does not match the carried bytes");
    }
    const std::vector<uint8_t> data(request.begin() + kHeaderBytes, request.end());
    MemWriteResult result = WriteProcessMemory(pid, address, data, mode, flags);
    if (result.written == 0 && !result.error.empty()) {
        return ErrorResponse(opcode::kMemWrite, result.error);
    }
    std::vector<uint8_t> response{opcode::kMemWrite, 0, result.backend};
    PutU32(response, result.written);
    response.push_back(result.shared ? 1 : 0);
    // A partial write keeps the count and carries the reason, so the caller can
    // report exactly how much of the buffer changed.
    PutU32(response, static_cast<uint32_t>(result.error.size()));
    response.insert(response.end(), result.error.begin(), result.error.end());
    return response;
}

// request: [F][u32 pid][u8 flags][module\0][name] where flags bit 0 = list all
// response: [F][0][text]
std::vector<uint8_t> Symbols(const std::vector<uint8_t>& request) {
    if (request.size() < 6) {
        return ErrorResponse(opcode::kMemSymbols, "symbol request is too short");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint8_t flags = request[5];
    const char* payload = reinterpret_cast<const char*>(request.data() + 6);
    const size_t payload_bytes = request.size() - 6;
    const size_t module_len = strnlen(payload, payload_bytes);
    if (module_len == payload_bytes) {
        return ErrorResponse(opcode::kMemSymbols, "module name is not terminated");
    }
    const std::string module(payload, module_len);
    const std::string name(payload + module_len + 1, payload_bytes - module_len - 1);
    if (module.empty()) {
        return ErrorResponse(opcode::kMemSymbols, "module substring is required");
    }
    const bool list = (flags & 0x01) != 0;
    const bool include_locals = (flags & 0x02) != 0;
    SymbolResult result =
        ResolveSymbols(pid, module, list ? std::string() : name, include_locals, 4096);
    if (!result.error.empty()) {
        return ErrorResponse(opcode::kMemSymbols, result.error);
    }
    std::vector<uint8_t> response{opcode::kMemSymbols, 0};
    response.insert(response.end(), result.text.begin(), result.text.end());
    return response;
}

// request: [Y][u32 pid][u64 start][u64 end][u32 max_hits][u16 pattern][u8 mode]
//          [u8 flags][pattern] ( [mask] when flags bit 0 is set )
// response: [Y][0][u32 hits][u64 scanned][u64 skipped][u64 address * hits]
std::vector<uint8_t> ScanRange(const std::vector<uint8_t>& request) {
    constexpr size_t kHeaderBytes = 29;
    if (request.size() < kHeaderBytes) {
        return ErrorResponse(opcode::kMemScan, "scan request is too short");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint64_t start = GetU64(request.data() + 5);
    const uint64_t end = GetU64(request.data() + 13);
    const uint32_t max_hits = GetU32(request.data() + 21);
    const uint16_t pattern_bytes = static_cast<uint16_t>(
        (static_cast<uint16_t>(request[25]) << 8) | request[26]);
    const uint8_t mode = request[27];
    const uint8_t flags = request[28];
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemScan, "unknown mode");
    }
    if (pattern_bytes == 0 || pattern_bytes > kMemScanMaxPattern) {
        return ErrorResponse(opcode::kMemScan,
                             "pattern must be 1.." + std::to_string(kMemScanMaxPattern) +
                                 " bytes");
    }
    const bool has_mask = (flags & 0x01) != 0;
    const size_t expected = kHeaderBytes + pattern_bytes * (has_mask ? 2u : 1u);
    if (request.size() != expected) {
        return ErrorResponse(opcode::kMemScan, "pattern length does not match the carried bytes");
    }
    const std::vector<uint8_t> pattern(request.begin() + kHeaderBytes,
                                      request.begin() + kHeaderBytes + pattern_bytes);
    std::vector<uint8_t> mask;
    if (has_mask) {
        mask.assign(request.begin() + kHeaderBytes + pattern_bytes, request.end());
    }
    MemScanResult result = ScanProcessMemory(pid, start, end, pattern, mask, max_hits, mode);
    if (!result.error.empty() && result.hits.empty()) {
        return ErrorResponse(opcode::kMemScan, result.error);
    }
    std::vector<uint8_t> response{opcode::kMemScan, 0};
    PutU32(response, static_cast<uint32_t>(result.hits.size()));
    PutU64(response, result.scanned);
    PutU64(response, result.skipped);
    for (uint64_t hit : result.hits) {
        PutU64(response, hit);
    }
    return response;
}

// request: [S][u32 pid][u8 mode][u32 count][(u64 address, u32 length) * count]
// response: [S][0][u32 count] then per range:
//   [u8 status][u8 backend][u32 data bytes][u32 error bytes][error][data]
// A range that cannot be read does not fail the request: strict mode is meant
// to hit holes, and a caller walking a region wants the readable parts.
std::vector<uint8_t> ScatterRead(const std::vector<uint8_t>& request) {
    constexpr size_t kHeaderBytes = 10;  // opcode + pid + mode + count
    constexpr uint32_t kMaxRanges = 64;
    if (request.size() < kHeaderBytes) {
        return ErrorResponse(opcode::kMemScatter, "scatter request is too short");
    }
    const uint32_t pid = GetU32(request.data() + 1);
    const uint8_t mode = request[5];
    const uint32_t count = GetU32(request.data() + 6);
    if (mode > kMemModeFault) {
        return ErrorResponse(opcode::kMemScatter, "unknown mode");
    }
    if (count == 0 || count > kMaxRanges) {
        return ErrorResponse(opcode::kMemScatter, "count must be 1.." + std::to_string(kMaxRanges));
    }
    if (request.size() != kHeaderBytes + static_cast<size_t>(count) * 12) {
        return ErrorResponse(opcode::kMemScatter, "scatter request is malformed");
    }
    uint64_t total = 0;
    for (uint32_t index = 0; index < count; ++index) {
        const size_t at = kHeaderBytes + index * 12;
        const uint32_t length = GetU32(request.data() + at + 8);
        if (length == 0 || length > kMaxMemRead) {
            return ErrorResponse(opcode::kMemScatter,
                                 "range " + std::to_string(index) + " length must be 1.." +
                                     std::to_string(kMaxMemRead));
        }
        total += length;
        if (total > kMaxMemRead) {
            return ErrorResponse(opcode::kMemScatter, "ranges do not fit in one frame");
        }
    }

    std::vector<uint8_t> response{opcode::kMemScatter, 0};
    PutU32(response, count);
    response.reserve(request.size() * 2);
    for (uint32_t index = 0; index < count; ++index) {
        const size_t at = kHeaderBytes + index * 12;
        const uint64_t address = GetU64(request.data() + at);
        const uint32_t length = GetU32(request.data() + at + 8);
        MemReadResult read = ReadProcessMemory(pid, address, length, mode);
        response.push_back(read.data.empty() ? 1 : 0);
        response.push_back(read.backend);
        PutU32(response, static_cast<uint32_t>(read.data.size()));
        PutU32(response, static_cast<uint32_t>(read.error.size()));
        response.insert(response.end(), read.error.begin(), read.error.end());
        response.insert(response.end(), read.data.begin(), read.data.end());
    }
    return response;
}

// request: [A][u32 pid]
// response: [A][0][maps text]
std::vector<uint8_t> ListProcessMappings(const std::vector<uint8_t>& request) {
    if (request.size() != 5) {
        return ErrorResponse(opcode::kMemMaps, "expected 5 bytes");
    }
    MemMapsResult maps = ListProcessMaps(GetU32(request.data() + 1));
    if (!maps.error.empty()) {
        return ErrorResponse(opcode::kMemMaps, maps.error);
    }
    std::vector<uint8_t> response{opcode::kMemMaps, 0};
    response.insert(response.end(), maps.text.begin(), maps.text.end());
    return response;
}

// request: [G]
// response: [G][0][report text]
std::vector<uint8_t> MemoryInfo() {
    const std::string report = MemoryDiagnostics();
    std::vector<uint8_t> response{opcode::kMemInfo, 0};
    response.insert(response.end(), report.begin(), report.end());
    return response;
}

// request: [I][u8 action][u8 flags][u16 slot][u32 x][u32 y][u32 x2][u32 y2]
//          [u32 duration_ms]
// response: [I][0][report text]
std::vector<uint8_t> TouchRequest(const std::vector<uint8_t>& request) {
    if (request.size() != kTouchRequestBytes) {
        return ErrorResponse(opcode::kTouch,
                             "touch request must be " + std::to_string(kTouchRequestBytes) +
                                 " bytes");
    }
    const uint8_t action = request[1];
    const uint16_t slot = static_cast<uint16_t>((static_cast<uint16_t>(request[3]) << 8) |
                                                request[4]);
    const uint32_t x = GetU32(request.data() + 5);
    const uint32_t y = GetU32(request.data() + 9);
    const uint32_t x2 = GetU32(request.data() + 13);
    const uint32_t y2 = GetU32(request.data() + 17);
    const uint32_t duration = GetU32(request.data() + 21);

    TouchResult result;
    switch (action) {
        case kTouchInfo:
            result = TouchDiagnostics();
            break;
        case kTouchDown:
            result = TouchDown(x, y, slot);
            break;
        case kTouchMove:
            result = TouchMove(x, y, slot);
            break;
        case kTouchUp:
            result = TouchUp(slot);
            break;
        case kTouchTap:
            result = TouchTap(x, y, duration, slot);
            break;
        case kTouchSwipe:
            result = TouchSwipe(x, y, x2, y2, duration, slot);
            break;
        default:
            return ErrorResponse(opcode::kTouch, "unknown touch action");
    }
    if (!result.ok) {
        std::string error = result.error.empty() ? "touch failed" : result.error;
        if (action == kTouchInfo && !result.report.empty())
            error += "\n" + result.report;
        return ErrorResponse(opcode::kTouch, error);
    }
    std::vector<uint8_t> response{opcode::kTouch, 0};
    response.insert(response.end(), result.report.begin(), result.report.end());
    return response;
}

// request: [J][u8 count] then count * [action u8][flags u8][slot u16][x u32][y u32]
// response: [J][0][report text]
std::vector<uint8_t> TouchBatchRequest(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kTouchBatch, "touch batch request is too short");
    }
    const uint8_t count = request[1];
    if (count == 0 || count > 32) {
        return ErrorResponse(opcode::kTouchBatch, "touch batch count must be 1..32");
    }
    constexpr size_t kRecordBytes = 12;
    if (request.size() != 2 + static_cast<size_t>(count) * kRecordBytes) {
        return ErrorResponse(opcode::kTouchBatch, "touch batch length mismatch");
    }
    std::vector<TouchBatchItem> items;
    items.reserve(count);
    for (uint8_t i = 0; i < count; ++i) {
        const uint8_t* record = request.data() + 2 + static_cast<size_t>(i) * kRecordBytes;
        TouchBatchItem item;
        item.action = record[0];
        item.slot = static_cast<uint16_t>((static_cast<uint16_t>(record[2]) << 8) | record[3]);
        item.x = GetU32(record + 4);
        item.y = GetU32(record + 8);
        items.push_back(item);
    }
    const TouchResult result = TouchBatch(items);
    if (!result.ok) {
        return ErrorResponse(opcode::kTouchBatch,
                             result.error.empty() ? "touch batch failed" : result.error);
    }
    std::vector<uint8_t> response{opcode::kTouchBatch, 0};
    response.insert(response.end(), result.report.begin(), result.report.end());
    return response;
}

// request: [Z][action] for status/install/remove; [Z][3][rx f32][ry f32][rz f32]
// sets the injected rate (big-endian IEEE). The answer is a key=value report.
std::vector<uint8_t> GyroRequest(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kGyro, "gyro request is too short");
    }
    const uint8_t action = request[1];
    std::string error;
    GyroStatus status;
    if (action == 0) {
        status = GyroQuery(error);
    } else if (action == 1) {
        if (request.size() != 2 && request.size() != 6)
            return ErrorResponse(opcode::kGyro, "invalid global HAL install request");
        status = GyroInstall(error, request.size() == 6 ? GetU32(request.data() + 2) : 0);
    } else if (action == 2) {
        status = GyroRemove(error);
    } else if (action == 7) {
        const std::string hits = GyroGatewayReport();
        std::vector<uint8_t> response{opcode::kGyro, 0};
        response.insert(response.end(), hits.begin(), hits.end());
        return response;
    } else if (action == 6) {
        char ping[64];
        std::snprintf(ping, sizeof(ping), "gateway_ping=%ld\n", GyroGatewayPing());
        std::vector<uint8_t> response{opcode::kGyro, 0};
        response.insert(response.end(), ping, ping + std::strlen(ping));
        return response;
    } else if (action == 5) {
        return ErrorResponse(opcode::kGyro, "live rate benchmark removed; use isolated sensor tests");
    } else if (action == 8) {
        if ((request.size() != 2 && request.size() != 6) || !GyroDisarm(error,request.size()==6?GetU32(request.data()+2):0)) return ErrorResponse(opcode::kGyro, error);
        status = GyroQuery(error);
    } else if (action == 3 || action == 4) {
        if (request.size() != 14) {
            return ErrorResponse(opcode::kGyro, "gyro rate needs three floats");
        }
        auto read_float = [](const uint8_t* data) {
            const uint32_t bits = GetU32(data);
            float value = 0.0f;
            std::memcpy(&value, &bits, sizeof(value));
            return value;
        };
        const float rx = read_float(request.data() + 2);
        const float ry = read_float(request.data() + 6);
        const float rz = read_float(request.data() + 10);
        timespec now{};
        clock_gettime(CLOCK_BOOTTIME, &now);
        const uint64_t now_ns = static_cast<uint64_t>(now.tv_sec) * 1000000000ull +
                                static_cast<uint64_t>(now.tv_nsec);
        std::string rate_error;
        if (!GyroApplyRate(rx, ry, rz, now_ns, action == 4, rate_error)) {
            return ErrorResponse(opcode::kGyro,
                                 rate_error.empty() ? "gyro rate update failed" : rate_error);
        }
        status = GyroQuery(error);
    } else {
        return ErrorResponse(opcode::kGyro, "unknown gyro action");
    }
    if (!error.empty()) {
        return ErrorResponse(opcode::kGyro, error);
    }
    char text[512];
    std::snprintf(text, sizeof(text),
                  "installed=%d\npid=%u\nbase=0x%llx\nentry=0x%llx\npatch=0x%llx\n"
                  "cave=0x%llx\nstate=0x%llx\nhits=%u\nupdates=%llu\ngateway=%d\ngateway_addr=0x%llx\ngateway_symbol=%s\n",
                  status.installed ? 1 : 0, status.pid,
                  static_cast<unsigned long long>(status.base),
                  static_cast<unsigned long long>(status.entry),
                  static_cast<unsigned long long>(status.patch),
                  static_cast<unsigned long long>(status.cave),
                  static_cast<unsigned long long>(status.state), status.hits,
                  static_cast<unsigned long long>(status.updates), status.gateway ? 1 : 0, static_cast<unsigned long long>(status.gateway_addr), status.gateway_symbol.c_str());
    std::vector<uint8_t> response{opcode::kGyro, 0};
    response.insert(response.end(), text, text + std::strlen(text));
    const std::string capabilities = "protocol=2\ntarget_uid=" + std::to_string(status.uid) +
        "\nstream=" + std::to_string(status.stream) + "\nenabled=" + (status.enabled ? "1" : "0") +
        "\nwriter=" + status.writer + "\nsensor_types=4,16,11,15\nscope=device-hal\nlayer=vendor-hal\nbackend=qsh-post-events\ndirect_channel=unsupported\n";
    response.insert(response.end(), capabilities.begin(), capabilities.end());
    if (!status.error.empty()) {
        response.push_back('\n');
        response.insert(response.end(), status.error.begin(), status.error.end());
    }
    return response;
}

// request: [C][action] for probe; [C][1][x u16][y u16][visible u8] for set;
// [C][2] hides cursor only. [C][3] + 18 bytes controls the independent HUD.
// All integers are big-endian; the renderer acknowledges posted HUD buffers.
std::vector<uint8_t> CursorRequest(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse(opcode::kCursor, "cursor request is too short");
    }
    CursorResult result;
    switch (request[1]) {
        case kCursorProbe:
            result = CursorProbe();
            break;
        case kCursorSet:
            if (request.size() != 7) {
                return ErrorResponse(opcode::kCursor, "cursor set requires x, y and visible");
            } else {
                const uint16_t x = static_cast<uint16_t>((request[2] << 8) | request[3]);
                const uint16_t y = static_cast<uint16_t>((request[4] << 8) | request[5]);
                result = CursorSet(x, y, request[6] != 0);
            }
            break;
        case kCursorDestroy:
            result = CursorDestroy();
            break;
        case kControlOverlay:
            if (request.size() != 20 || request[2] > 1 || request[3] > 3 ||
                request[18] > 2 || request[19] > 1) {
                return ErrorResponse(opcode::kCursor, "invalid control overlay fields");
            } else {
                auto word = [&](size_t i) { return static_cast<uint16_t>((request[i] << 8) | request[i + 1]); };
                ControlOverlayState state;
                state.enabled = request[2] != 0; state.rotation = request[3];
                state.width = word(4); state.height = word(6);
                state.active = word(8); state.capacity = word(10); state.pending = word(12);
                state.sampling_tenths = word(14); state.rtt_tenths = word(16);
                state.mode = request[18]; state.input_active = request[19] != 0;
                if (!state.width || !state.height || !state.capacity || state.active > state.capacity)
                    return ErrorResponse(opcode::kCursor, "invalid control overlay geometry or contacts");
                result = ControlOverlaySet(state);
            }
            break;
        default:
            return ErrorResponse(opcode::kCursor, "unknown cursor action");
    }
    if (!result.ok) {
        return ErrorResponse(opcode::kCursor,
                             result.error.empty() ? "cursor operation failed" : result.error);
    }
    std::vector<uint8_t> response{opcode::kCursor, 0};
    response.insert(response.end(), result.report.begin(), result.report.end());
    return response;
}

}  // namespace

std::vector<uint8_t> ProcessRequest(const std::vector<uint8_t>& request, const Stream& stream,
                                    const std::string& capabilities,
                                    const AdminRequestHandler& admin) {
    if (request.empty()) {
        return ErrorResponse('?', "empty request");
    }
    switch (request[0]) {
        case opcode::kExec:
            return ExecuteCommand(request, stream);
        case opcode::kRead:
            return ReadFile(request);
        case opcode::kWrite:
            return WriteFile(request);
        case opcode::kList:
            return ListDirectory(request);
        case opcode::kStat:
            return StatPath(request);
        case opcode::kMemRead:
            return ReadProcessMemoryRange(request);
        case opcode::kMemMaps:
            return ListProcessMappings(request);
        case opcode::kMemInfo:
            return MemoryInfo();
        case opcode::kMemScatter:
            return ScatterRead(request);
        case opcode::kMemDump:
            return DumpRange(request);
        case opcode::kMemWrite:
            return WriteRange(request);
        case opcode::kMemSymbols:
            return Symbols(request);
        case opcode::kMemScan:
            return ScanRange(request);
        case opcode::kMemResident:
            return ResidentPages(request);
        case opcode::kTouch:
            return TouchRequest(request);
        case opcode::kTouchBatch:
            return TouchBatchRequest(request);
        case opcode::kCursor:
            return CursorRequest(request);
        case opcode::kGyro:
            return GyroRequest(request);
        case opcode::kAdmin:
            if (!admin) {
                return ErrorResponse(opcode::kAdmin, "administration is not available here");
            }
            return admin(request);
        case opcode::kPing:
            return {opcode::kPing, 0, 'P', 'O', 'N', 'G'};
        case opcode::kVersion: {
            std::vector<uint8_t> response{opcode::kVersion, 0};
            PutU32(response, static_cast<uint32_t>(capabilities.size()));
            response.insert(response.end(), capabilities.begin(), capabilities.end());
            return response;
        }
        case opcode::kClose:
            return {opcode::kClose, 0};
        default:
            return ErrorResponse(request[0], "unknown opcode");
    }
}

}  // namespace skpdb
