#include "input/gyro.h"
#include "input/gyro_payload.h"
#include "input/gyro_layout.h"
#include "input/gyro_protocol.h"
#include "input/gyro_payload_bytes.h"
#include "memory/memread.h"
#include <kernel_module_kit_umbrella.h>
#include <dirent.h>
#include <elf.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <mutex>
#include <sstream>
#include <vector>
using namespace asmjit::a64;
using namespace asmjit::a64::Predicate;
namespace skpdb {
namespace {
using namespace gyro_payload;
struct MapLine {
    uint64_t start = 0, end = 0, offset = 0;
    char perms[5] = {};
    std::string path;
};
struct Record {
    uint64_t magic = 0, process_start = 0, entry = 0, patch = 0, cave = 0, code_hash = 0;
    uint32_t schema = 0, uid_offset = 0, code_bytes = 0, original = 0, first = 0, reserved = 0;
};
struct Storage {
    State state;
    Record record;
};
constexpr uint64_t kRecordMagic = 0x334e574f52594753ull;
struct Installed {
    bool active = false;
    uint32_t pid = 0, uid = 0, stream = 0, sequence = 0, epoch = 0;
    uint64_t base = 0, state = 0, updates = 0, start = 0, armed = 0;
    Record record;
    int mem = -1;
    std::string writer, error;
};
std::mutex g_mutex;
Installed g_hook;
std::atomic<uint32_t> g_stream{0};
uint64_t Now() {
    timespec t{};
    clock_gettime(CLOCK_BOOTTIME, &t);
    return uint64_t(t.tv_sec) * 1000000000ull + t.tv_nsec;
}
uint64_t Identity(uint32_t pid) {
    auto file = std::fopen(("/proc/" + std::to_string(pid) + "/stat").c_str(), "r");
    if (!file)
        return 0;
    char b[4096] = {};
    auto n = std::fread(b, 1, sizeof(b) - 1, file);
    std::fclose(file);
    std::string text(b, n);
    auto at = text.rfind(')');
    if (at == std::string::npos)
        return 0;
    std::istringstream fields(text.substr(at + 2));
    std::string field;
    for (int i = 3; i <= 22; i++) {
        if (!(fields >> field))
            return 0;
        if (i == 22)
            return std::strtoull(field.c_str(), nullptr, 10);
    }
    return 0;
}
uint64_t Hash(const std::vector<uint8_t> &b) {
    uint64_t h = 1469598103934665603ull;
    for (auto x : b) {
        h ^= x;
        h *= 1099511628211ull;
    }
    return h;
}
bool Read(uint32_t pid, uint64_t at, size_t n, std::vector<uint8_t> &out, std::string &error) {
    auto r = ReadProcessMemory(pid, at, uint32_t(n), kMemModeFault);
    if (r.data.size() != n) {
        error = r.error.empty() ? "short remote read" : r.error;
        return false;
    }
    out = std::move(r.data);
    return true;
}
bool Write(uint32_t pid, uint64_t at, const void *data, size_t n, bool code, std::string &error) {
    const auto *p = static_cast<const uint8_t *>(data);
    auto r = WriteProcessMemory(pid, at, std::vector<uint8_t>(p, p + n), kMemModeKernelOnly,
                                code ? kMemWriteAllowCode : kMemWriteAllowFault);
    if (r.written != n) {
        error = r.error.empty() ? "short remote write" : r.error;
        return false;
    }
    return true;
}
uint32_t FindProcess(const char *name) {
    DIR *proc = opendir("/proc");
    if (proc == nullptr) {
        return 0;
    }
    uint32_t found = 0;
    const size_t name_length = std::strlen(name);
    while (dirent *entry = readdir(proc)) {
        if (entry->d_name[0] < '0' || entry->d_name[0] > '9') {
            continue;
        }
        const std::string path = std::string("/proc/") + entry->d_name + "/cmdline";
        const int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
        if (fd < 0) {
            continue;
        }
        char buffer[256] = {0};
        const ssize_t got = ::read(fd, buffer, sizeof(buffer) - 1);
        ::close(fd);
        if (got <= 0) {
            continue;
        }
        if (std::strncmp(buffer, name, name_length) == 0 &&
            (buffer[name_length] == '\0' || buffer[name_length] == ' ')) {
            found = static_cast<uint32_t>(std::strtoul(entry->d_name, nullptr, 10));
            break;
        }
    }
    closedir(proc);
    return found;
}

bool ReadMaps(uint32_t pid, std::vector<MapLine> &maps, std::string &error) {
    const std::string path = "/proc/" + std::to_string(pid) + "/maps";
    FILE *file = std::fopen(path.c_str(), "re");
    if (file == nullptr) {
        error = "cannot open " + path;
        return false;
    }
    char line[4096];
    while (std::fgets(line, sizeof(line), file) != nullptr) {
        unsigned long long start = 0;
        unsigned long long end = 0;
        unsigned long long offset = 0;
        char perms[8] = {0};
        int consumed = 0;
        if (std::sscanf(line, "%llx-%llx %7s %llx %*s %*s %n", &start, &end, perms, &offset, &consumed) < 4) {
            continue;
        }
        MapLine map;
        map.start = start;
        map.end = end;
        map.offset = offset;
        std::memcpy(map.perms, perms, 4);
        if (consumed > 0 && static_cast<size_t>(consumed) < std::strlen(line)) {
            map.path = line + consumed;
            while (!map.path.empty() && (map.path.back() == '\n' || map.path.back() == ' ')) {
                map.path.pop_back();
            }
        }
        maps.push_back(std::move(map));
    }
    std::fclose(file);
    return true;
}

bool ResolveSymbol(const std::string &path, const std::string &prefix, uint64_t &value, std::string &error) {
    const int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        error = "cannot open " + path;
        return false;
    }
    auto read_at = [fd](uint64_t offset, void *data, size_t size) {
        return ::pread(fd, data, size, static_cast<off_t>(offset)) == static_cast<ssize_t>(size);
    };
    Elf64_Ehdr header{};
    struct stat file_info{};
    if (fstat(fd, &file_info) != 0) {
        ::close(fd);
        error = "cannot inspect sensor ELF";
        return false;
    }
    if (!read_at(0, &header, sizeof(header)) || std::memcmp(header.e_ident, ELFMAG, SELFMAG) != 0 ||
        header.e_ident[EI_CLASS] != ELFCLASS64 || header.e_shoff == 0 || header.e_shnum == 0 ||
        header.e_shnum > 4096 || header.e_shentsize != sizeof(Elf64_Shdr) ||
        header.e_shoff > uint64_t(file_info.st_size) ||
        uint64_t(header.e_shnum) * sizeof(Elf64_Shdr) > uint64_t(file_info.st_size) - header.e_shoff) {
        ::close(fd);
        error = path + " is not a usable ELF64 file";
        return false;
    }
    std::vector<Elf64_Shdr> sections(header.e_shnum);
    if (!read_at(header.e_shoff, sections.data(), sections.size() * sizeof(Elf64_Shdr))) {
        ::close(fd);
        error = "cannot read sections of " + path;
        return false;
    }
    bool found = false;
    for (const Elf64_Shdr &table : sections) {
        if (table.sh_type != SHT_DYNSYM || table.sh_entsize != sizeof(Elf64_Sym) ||
            table.sh_link >= sections.size()) {
            continue;
        }
        const Elf64_Shdr &strings = sections[table.sh_link];
        if (strings.sh_size > 16 * 1024 * 1024 || table.sh_size > 16 * 1024 * 1024 ||
            strings.sh_offset > uint64_t(file_info.st_size) ||
            strings.sh_size > uint64_t(file_info.st_size) - strings.sh_offset ||
            table.sh_offset > uint64_t(file_info.st_size) ||
            table.sh_size > uint64_t(file_info.st_size) - table.sh_offset)
            continue;
        std::vector<char> names(strings.sh_size);
        std::vector<Elf64_Sym> symbols(table.sh_size / sizeof(Elf64_Sym));
        if (!read_at(strings.sh_offset, names.data(), names.size()) ||
            !read_at(table.sh_offset, symbols.data(), symbols.size() * sizeof(Elf64_Sym))) {
            continue;
        }
        for (const Elf64_Sym &symbol : symbols) {
            if (symbol.st_shndx == SHN_UNDEF || symbol.st_name == 0 || symbol.st_name >= names.size()) {
                continue;
            }
            const char *name = names.data() + symbol.st_name;
            if (names.size() - symbol.st_name >= prefix.size() &&
                std::strncmp(name, prefix.c_str(), prefix.size()) == 0) {
                if (found && value != symbol.st_value) {
                    ::close(fd);
                    error = "ambiguous sensor symbol overload";
                    return false;
                }
                value = symbol.st_value;
                found = true;
            }
        }
    }
    ::close(fd);
    if (!found) {
        error = "symbol prefix not found in " + path + ": " + prefix;
    }
    return found;
}

bool Image(const std::string &path, std::vector<Elf64_Phdr> &phdr, std::string &error) {
    FILE *f = std::fopen(path.c_str(), "rb");
    if (!f) {
        error = "cannot open sensor library";
        return false;
    }
    Elf64_Ehdr h{};
    bool ok = std::fread(&h, 1, sizeof(h), f) == sizeof(h) && std::memcmp(h.e_ident, ELFMAG, 4) == 0 &&
              h.e_machine == EM_AARCH64 && h.e_ident[EI_CLASS] == ELFCLASS64 &&
              h.e_ident[EI_DATA] == ELFDATA2LSB && h.e_phentsize == sizeof(Elf64_Phdr) && h.e_phnum < 128;
    if (ok) {
        phdr.resize(h.e_phnum);
        ok = std::fseek(f, long(h.e_phoff), SEEK_SET) == 0 &&
             std::fread(phdr.data(), sizeof(Elf64_Phdr), phdr.size(), f) == phdr.size();
    }
    std::fclose(f);
    if (!ok)
        error = "unsupported sensor ELF layout";
    return ok;
}
bool Padding(const std::vector<MapLine> &maps, const std::vector<Elf64_Phdr> &phdr, uint64_t base, bool code,
             size_t size, uint64_t &address) {
    const uint64_t page = uint64_t(sysconf(_SC_PAGESIZE));
    for (auto it = phdr.rbegin(); it != phdr.rend(); ++it) {
        const auto &p = *it;
        if (p.p_type != PT_LOAD || bool(p.p_flags & PF_X) != code || (!code && !(p.p_flags & PF_W)))
            continue;
        const uint64_t end = base + p.p_vaddr + p.p_memsz, limit = (end + page - 1) & ~(page - 1);
        if (limit < end || limit - end < size)
            continue;
        const uint64_t at = (limit - size) & ~15ull;
        if (at < end)
            continue;
        for (const auto &m : maps)
            if (at >= m.start && limit <= m.end && (code ? m.perms[2] == 'x' : m.perms[1] == 'w')) {
                address = at;
                return true;
            }
    }
    return false;
}
bool UidMember(const std::string &file, uint32_t pid, uint64_t base, uint32_t &offset, std::string &error) {
    uint64_t symbol = 0;
    if (!ResolveSymbol(file, "_ZN7android13SensorService21SensorEventConnectionC1E", symbol, error))
        return false;
    std::vector<uint8_t> code;
    if (!Read(pid, base + symbol, 1024, code, error))
        return false;
    if (!gyro_layout::UidOffset(code, offset)) {
        error = "cannot verify SensorEventConnection UID member; refusing guessed offsets";
        return false;
    }
    return true;
}
std::vector<uint8_t> Wrapper(uint64_t cave, uint64_t state, uint64_t resume, uint32_t uidOffset,
                             uint32_t original, uint32_t first, size_t payloadOffset, std::string &error) {
    auto ctx = init_aarch64_asm();
    auto *a = ctx.assembler();
    auto pass = a->newLabel(), copy = a->newLabel(), stub = a->newLabel(), invoke = a->newLabel();
    auto raw = [&](uint32_t word) { a->embed(&word, 4); };
    aarch64_asm_mov_x(a, x9, state);
    a->ldr(w10, ptr(x9, offsetof(State, control) + offsetof(Control, enabled)));
    a->cbz(w10, pass);
    a->ldr(w10, ptr(x9, offsetof(State, control) + offsetof(Control, uid)));
    a->ldr(w11, ptr(x0, uidOffset));
    a->cmp(w10, w11);
    a->b(CondCode::kNE, pass);
    a->cbz(x2, pass);
    a->cmp(x2, kMaxEvents);
    a->b(CondCode::kHI, pass);
    constexpr uint64_t bytes = kMaxEvents * sizeof(Event) + 96;
    a->sub(sp, sp, bytes & ~4095ull);
    a->sub(sp, sp, bytes & 4095);
    a->stp(x0, x1, ptr(sp));
    a->stp(x2, x3, ptr(sp, 16));
    a->stp(x4, x30, ptr(sp, 32));
    a->mov(x10, x1);
    a->add(x11, sp, 96);
    a->mov(x12, sizeof(Event));
    a->mul(x12, x2, x12);
    a->bind(copy);
    a->ldr(x13, ptr(x10));
    a->str(x13, ptr(x11));
    a->add(x10, x10, 8);
    a->add(x11, x11, 8);
    a->subs(x12, x12, 8);
    a->b(CondCode::kNE, copy);
    // Linux clock_gettime(BOOTTIME), private stack storage. No target timestamp is used as wall time.
    a->mov(x0, 7);
    a->add(x1, sp, 64);
    a->mov(x8, 113);
    a->svc(0);
    a->cbnz(x0, invoke);
    a->ldp(x3, x4, ptr(sp, 64));
    aarch64_asm_mov_x(a, x12, 1000000000ull);
    a->mul(x3, x3, x12);
    a->add(x4, x3, x4);
    aarch64_asm_mov_x(a, x0, state);
    a->ldr(x9, ptr(sp));
    a->ldr(w1, ptr(x9, uidOffset));
    a->add(x2, sp, 96);
    a->ldr(x3, ptr(sp, 16));
    // The helper is PIC and starts with a valid BTI landing instruction.
    aarch64_asm_mov_x(a, x16, cave + payloadOffset);
    a->blr(x16);
    a->bind(invoke);
    a->ldr(x0, ptr(sp));
    a->add(x1, sp, 96);
    a->ldp(x2, x3, ptr(sp, 16));
    a->ldr(x4, ptr(sp, 32));
    a->bl(stub);
    a->ldr(x30, ptr(sp, 40));
    a->add(sp, sp, bytes & ~4095ull);
    a->add(sp, sp, bytes & 4095);
    if (first == 0xd503233f)
        raw(0xd50323bf); // autiasp for the original caller's signed LR
    a->ret(x30);
    a->bind(stub);
    if (first == 0xd503233f)
        raw(0xd503233f);
    raw(original);
    a->nop();
    a->bind(pass);
    raw(original);
    a->nop();
    auto b = aarch64_asm_to_bytes(a);
    if (b.empty()) {
        error = "wrapper assembly failed";
        return {};
    }
    // Both tail NOPs resume the actual displaced instruction's successor.
    for (size_t i = b.size() - 12; i < b.size(); i += 8) {
        uint32_t branch = 0;
        if (!gyro_layout::Branch(cave + i, resume, branch)) {
            error = "wrapper resume out of range";
            return {};
        }
        std::memcpy(b.data() + i, &branch, 4);
    }
    return b;
}
GyroStatus Status(std::string &error) {
    GyroStatus s;
    s.installed = g_hook.active;
    s.pid = g_hook.pid;
    s.base = g_hook.base;
    s.entry = g_hook.record.entry;
    s.patch = g_hook.record.patch;
    s.cave = g_hook.record.cave;
    s.state = g_hook.state;
    s.uid = g_hook.uid;
    s.stream = g_hook.stream;
    s.enabled = g_hook.stream != 0;
    s.writer = g_hook.writer;
    s.updates = g_hook.updates;
    if (g_hook.active && Identity(g_hook.pid) != g_hook.start) {
        g_hook.active = false;
        g_hook.stream = 0;
        s.installed = false;
        s.enabled = false;
        error = "system_server identity changed; reinstall required";
    }
    if (s.installed) {
        std::vector<uint8_t> b(sizeof(State));
        bool read = g_hook.mem >= 0
                        ? pread(g_hook.mem, b.data(), b.size(), off_t(g_hook.state)) == ssize_t(b.size())
                        : Read(g_hook.pid, g_hook.state, sizeof(State), b, error);
        if (read) {
            State r{};
            std::memcpy(&r, b.data(), sizeof(r));
            s.hits = r.hits;
            s.enabled = r.control.enabled == 1 && !(r.control.sequence & 1) && Now() >= r.control.updated &&
                        Now() - r.control.updated <= kLeaseNs;
            if (r.magic != kMagic || r.version != kVersion) {
                s.installed = false;
                error = "gyro state ownership changed";
            }
        } else if (error.empty())
            error = "cannot confirm gyro runtime state";
    }
    if (!s.installed)
        g_stream.store(0);
    s.error = error;
    return s;
}
bool Publish(Control c, std::string &error) {
    if (!g_hook.active || Identity(g_hook.pid) != g_hook.start) {
        error = "gyro target process changed";
        return false;
    }
    const uint64_t at = g_hook.state + offsetof(State, control);
    const uint32_t odd = g_hook.sequence + 1, even = g_hook.sequence + 2;
    c.sequence = even;
    // Reserve this version even if publication/readback fails. Reusing an even
    // version could hide a later write cycle from a concurrent reader.
    g_hook.sequence = even;
    auto transfer = [&](uint64_t dest, const void *data, size_t size) {
        if (g_hook.mem >= 0) {
            auto n = pwrite(g_hook.mem, data, size, off_t(dest));
            if (n != ssize_t(size)) {
                error = "remote state write failed";
                return false;
            }
            return true;
        }
        return Write(g_hook.pid, dest, data, size, false, error);
    };
    if (!transfer(at, &odd, 4) ||
        !transfer(at + 4, reinterpret_cast<const uint8_t *>(&c) + 4, sizeof(c) - 4) ||
        !transfer(at, &even, 4))
        return false;
    uint32_t observed = 0;
    if (g_hook.mem >= 0) {
        if (pread(g_hook.mem, &observed, 4, off_t(at)) != 4) {
            error = "cannot confirm gyro state write";
            return false;
        }
    } else {
        std::vector<uint8_t> b;
        if (!Read(g_hook.pid, at, 4, b, error))
            return false;
        std::memcpy(&observed, b.data(), 4);
    }
    if (observed != even) {
        error = "gyro state acknowledgement mismatch";
        return false;
    }
    g_hook.sequence = even;
    ++g_hook.updates;
    return true;
}
uint32_t NewStream() {
    uint32_t n = 0;
    int f = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (f >= 0) {
        if (read(f, &n, 4) != 4)
            n = 0;
        close(f);
    }
    return n;
}
} // namespace

GyroStatus GyroInstall(std::string &error, uint32_t uid) {
    std::lock_guard<std::mutex> guard(g_mutex);
    error.clear();
    if (uid < 1000) {
        error = "an explicit target application UID is required";
        return {};
    }
    if (g_hook.active && Identity(g_hook.pid) == g_hook.start) {
        g_hook.uid = uid;
        g_hook.stream = NewStream();
        ++g_hook.epoch;
        if (!g_hook.stream) {
            error = "cannot create gyro stream";
            return {};
        }
        g_hook.armed = Now();
        Control c{};
        c.enabled = 1;
        c.uid = uid;
        c.stream = g_hook.stream;
        c.updated = g_hook.armed;
        c.valid_since = g_hook.armed;
        c.epoch = g_hook.epoch;
        if (!Publish(c, error)) {
            g_hook.stream = 0;
            g_stream.store(0);
            return Status(error);
        }
        g_stream.store(g_hook.stream);
        return Status(error);
    }
    g_stream.store(0);
    if (g_hook.mem >= 0)
        close(g_hook.mem);
    g_hook = Installed{};
    uint32_t pid = FindProcess("system_server");
    uint64_t identity = Identity(pid);
    if (!pid || !identity) {
        error = "system_server unavailable";
        return {};
    }
    std::vector<MapLine> maps;
    if (!ReadMaps(pid, maps, error))
        return {};
    std::string path;
    uint64_t base = 0;
    for (const auto &m : maps)
        if (m.path.size() >= 19 && m.path.substr(m.path.size() - 19) == "libsensorservice.so") {
            path = m.path;
            base = m.start - m.offset;
            break;
        }
    if (path.empty()) {
        error = "sensor library not mapped";
        return {};
    }
    std::vector<Elf64_Phdr> phdr;
    if (!Image(path, phdr, error))
        return {};
    uint64_t symbol = 0;
    if (!ResolveSymbol(path, "_ZN7android13SensorService21SensorEventConnection10sendEventsE", symbol, error))
        return {};
    uint64_t entry = base + symbol, patch = entry + 4;
    std::vector<uint8_t> head;
    if (!Read(pid, entry, 8, head, error))
        return {};
    uint32_t first = 0, displaced = 0;
    std::memcpy(&first, head.data(), 4);
    std::memcpy(&displaced, head.data() + 4, 4);
    uint64_t storageAt = 0, cave = 0;
    constexpr size_t reserve = 2304;
    if (!Padding(maps, phdr, base, false, sizeof(Storage), storageAt) ||
        !Padding(maps, phdr, base, true, reserve, cave)) {
        error = "verified ELF page padding is insufficient; no arbitrary zero scan is permitted";
        return {};
    }
    std::vector<uint8_t> raw;
    if (!Read(pid, storageAt, sizeof(Storage), raw, error))
        return {};
    Storage storage{};
    std::memcpy(&storage, raw.data(), sizeof(storage));
    bool owned = storage.record.magic == kRecordMagic && storage.record.schema == 3 &&
                 storage.record.process_start == identity && storage.record.entry == entry &&
                 storage.record.cave == cave;
    if (!owned && !std::all_of(raw.begin(), raw.end(), [](uint8_t x) { return x == 0; })) {
        error = "state padding belongs to another user; refusing overwrite";
        return {};
    }
    uint32_t uidOffset = 0;
    if (!UidMember(path, pid, base, uidOffset, error))
        return {};
    uint32_t original = owned ? storage.record.original : displaced;
    if (!gyro_layout::Prologue(first, original)) {
        error = "unsupported verified function prologue";
        return {};
    }
    uint32_t patchWord = 0;
    if (!gyro_layout::Branch(patch, cave, patchWord)) {
        error = "gyro cave outside branch range";
        return {};
    }
    if (displaced != original && (!owned || displaced != patchWord)) {
        error = "unowned entry patch detected; remove it using its original owner";
        return {};
    }
    constexpr size_t helperAt = 512;
    auto code = Wrapper(cave, storageAt, entry + 8, uidOffset, original, first, helperAt, error);
    if (code.empty() || code.size() > helperAt) {
        if (error.empty())
            error = "wrapper exceeds reserved layout";
        return {};
    }
    code.resize(helperAt, 0);
    code.insert(code.end(), std::begin(kGyroPayload), std::end(kGyroPayload));
    if (code.size() > reserve) {
        error = "gyro helper exceeds verified padding";
        return {};
    }
    std::vector<uint8_t> existing;
    if (!Read(pid, cave, code.size(), existing, error))
        return {};
    if (owned) {
        if (existing != code || storage.record.code_hash != Hash(code)) {
            error = "retired helper differs; restart of target service required before replacement";
            return {};
        }
    } else {
        if (!std::all_of(existing.begin(), existing.end(), [](uint8_t x) { return x == 0; })) {
            error = "executable padding occupied; refusing signature-based reuse";
            return {};
        }
        storage.state.magic = kMagic;
        storage.state.version = kVersion;
        storage.record = {kRecordMagic,          identity, entry, patch, cave, Hash(code), 3, uidOffset,
                          uint32_t(code.size()), original, first, 0};
        if (!Write(pid, cave, code.data(), code.size(), true, error) ||
            !Write(pid, storageAt, &storage, sizeof(storage), false, error))
            return {};
    }
    if (!Write(pid, patch, &patchWord, 4, true, error))
        return {};
    std::vector<uint8_t> verify;
    if (!Read(pid, patch, 4, verify, error) || std::memcmp(verify.data(), &patchWord, 4) != 0) {
        error = "entry patch confirmation failed; owned state retained for recovery";
        return {};
    }
    g_hook.active = true;
    g_hook.pid = pid;
    g_hook.start = identity;
    g_hook.base = base;
    g_hook.state = storageAt;
    g_hook.record = storage.record;
    g_hook.uid = uid;
    g_hook.sequence = storage.state.control.sequence & ~1u;
    g_hook.epoch = storage.state.control.epoch + 1;
    g_hook.stream = NewStream();
    g_hook.mem = open(("/proc/" + std::to_string(pid) + "/mem").c_str(), O_RDWR | O_CLOEXEC);
    g_hook.writer = g_hook.mem >= 0 ? "proc-mem-confirmed" : "kernel-confirmed-degraded";
    if (!g_hook.stream) {
        error = "cannot create gyro stream";
        return Status(error);
    }
    g_hook.armed = Now();
    Control c{};
    c.enabled = 1;
    c.uid = uid;
    c.stream = g_hook.stream;
    c.updated = g_hook.armed;
    c.valid_since = g_hook.armed;
    c.epoch = g_hook.epoch;
    if (!Publish(c, error)) {
        if (g_hook.mem >= 0) {
            close(g_hook.mem);
            g_hook.mem = -1;
            g_hook.writer = "kernel-confirmed-degraded";
            error.clear();
            if (Publish(c, error)) {
                g_stream.store(g_hook.stream);
                return Status(error);
            }
        }
        g_hook.stream = 0;
        g_stream.store(0);
        return Status(error);
    }
    g_stream.store(g_hook.stream);
    return Status(error);
}
bool GyroDisarm(std::string &error, uint32_t expected_stream) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (expected_stream && expected_stream != g_hook.stream) {
        error = "gyro stream ownership changed";
        return false;
    }
    g_stream.store(0);
    if (!g_hook.active)
        return true;
    Control c{};
    c.uid = g_hook.uid;
    c.updated = Now();
    c.epoch = ++g_hook.epoch;
    bool ok = Publish(c, error);
    g_hook.stream = 0;
    return ok;
}
GyroStatus GyroRemove(std::string &error) {
    std::lock_guard<std::mutex> guard(g_mutex);
    g_stream.store(0);
    error.clear();
    if (!g_hook.active)
        return {};
    if (Identity(g_hook.pid) != g_hook.start) {
        g_hook.active = false;
        g_hook.stream = 0;
        return {};
    }
    Control c{};
    c.updated = Now();
    c.epoch = ++g_hook.epoch;
    if (!Publish(c, error))
        return Status(error);
    g_hook.stream = 0;
    uint32_t expected = 0;
    gyro_layout::Branch(g_hook.record.patch, g_hook.record.cave, expected);
    std::vector<uint8_t> current;
    if (!Read(g_hook.pid, g_hook.record.patch, 4, current, error))
        return Status(error);
    uint32_t word = 0;
    std::memcpy(&word, current.data(), 4);
    if (word != expected && word != g_hook.record.original) {
        error = "entry ownership changed; refusing to restore foreign patch";
        return Status(error);
    }
    if (word == expected && !Write(g_hook.pid, g_hook.record.patch, &g_hook.record.original, 4, true, error))
        return Status(error);
    g_hook.active = false;
    // Do not erase immutable helper code or state: a thread may still be inside
    // the retired wrapper. The ownership record permits exact-code reattachment.
    if (g_hook.mem >= 0) {
        close(g_hook.mem);
        g_hook.mem = -1;
    }
    return Status(error);
}
GyroStatus GyroQuery(std::string &error) {
    std::lock_guard<std::mutex> guard(g_mutex);
    error.clear();
    return Status(error);
}
bool GyroActive() {
    std::lock_guard<std::mutex> guard(g_mutex);
    return g_hook.active && g_hook.stream != 0;
}
uint32_t GyroStream() { return g_stream.load(); }
bool GyroApplyRate(float x, float y, float z, uint64_t timestamp, bool sticky, std::string &error) {
    std::lock_guard<std::mutex> guard(g_mutex);
    if (sticky) {
        error = "unbounded sticky rates are not supported";
        return false;
    }
    if (!g_hook.active || !g_hook.stream) {
        error = "gyro stream not armed";
        return false;
    }
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) || std::fabs(x) > 15 ||
        std::fabs(y) > 15 || std::fabs(z) > 15) {
        error = "gyro rate must be finite and within +/-15 rad/s";
        return false;
    }
    auto now = Now();
    if (timestamp > now || now - timestamp > kLeaseNs) {
        error = "gyro update lease expired";
        return false;
    }
    Control c{};
    c.enabled = 1;
    c.uid = g_hook.uid;
    c.stream = g_hook.stream;
    c.updated = timestamp;
    c.valid_since = g_hook.armed;
    c.rate[0] = x;
    c.rate[1] = y;
    c.rate[2] = z;
    c.epoch = g_hook.epoch;
    return Publish(c, error);
}
bool GyroApplyRate(float x, float y, float z) {
    std::string e;
    return GyroApplyRate(x, y, z, Now(), false, e);
}
bool GyroApplyPacket(uint32_t stream, uint32_t sequence, float x, float y, float z, uint64_t received,
                     std::string &error) {
    // Session validation and publication share one lock; disarm cannot race a queued packet.
    std::lock_guard<std::mutex> guard(g_mutex);
    static uint32_t lastStream = 0, lastSequence = 0;
    if (!g_hook.active || !stream || stream != g_hook.stream) {
        error = "stale gyro stream";
        return false;
    }
    if (stream == lastStream && int32_t(sequence - lastSequence) <= 0) {
        error = "out-of-order gyro packet";
        return false;
    }
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) || std::fabs(x) > 15 ||
        std::fabs(y) > 15 || std::fabs(z) > 15) {
        error = "invalid gyro packet rate";
        return false;
    }
    auto now = Now();
    if (received > now || now - received > kLeaseNs) {
        error = "expired queued gyro packet";
        return false;
    }
    Control c{};
    c.enabled = 1;
    c.uid = g_hook.uid;
    c.stream = stream;
    c.updated = received;
    c.valid_since = g_hook.armed;
    c.rate[0] = x;
    c.rate[1] = y;
    c.rate[2] = z;
    c.epoch = g_hook.epoch;
    if (!Publish(c, error))
        return false;
    lastStream = stream;
    lastSequence = sequence;
    return true;
}
long GyroGatewayPing() { return -1; }
uint64_t GyroGatewayHits() { return 0; }
std::string GyroGatewayReport() {
    std::lock_guard<std::mutex> guard(g_mutex);
    return "gateway=disabled\nwriter=" + g_hook.writer +
           "\nconfirmed_updates=" + std::to_string(g_hook.updates) + "\ndirect_channel=unsupported\n";
}
} // namespace skpdb
