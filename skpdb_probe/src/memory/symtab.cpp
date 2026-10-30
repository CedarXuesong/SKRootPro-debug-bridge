#include "memory/symtab.h"

#include <elf.h>
#include <fcntl.h>
#include <unistd.h>

#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

namespace skpdb {
namespace {

// One mapping line from /proc/<pid>/maps that points at a real file.
struct Mapping {
    uint64_t start = 0;
    uint64_t end = 0;
    uint64_t offset = 0;
    std::string path;
};

bool ReadMaps(uint32_t pid, std::vector<Mapping>& out, std::string& error) {
    std::ifstream maps("/proc/" + std::to_string(pid) + "/maps");
    if (!maps) {
        error = "cannot open /proc/" + std::to_string(pid) + "/maps";
        return false;
    }
    std::string line;
    while (std::getline(maps, line)) {
        unsigned long long start = 0;
        unsigned long long end = 0;
        unsigned long long offset = 0;
        char perms[8] = {0};
        int consumed = 0;
        if (std::sscanf(line.c_str(), "%llx-%llx %7s %llx %*s %*s %n", &start, &end, perms,
                        &offset, &consumed) < 4) {
            continue;
        }
        if (consumed <= 0 || static_cast<size_t>(consumed) >= line.size()) {
            continue;
        }
        Mapping mapping;
        mapping.start = start;
        mapping.end = end;
        mapping.offset = offset;
        mapping.path = line.substr(consumed);
        // Trimmed entries such as "[stack]" or "anon_inode:..." never name a
        // file that can be opened.
        if (mapping.path.find('/') == std::string::npos) {
            continue;
        }
        const size_t deleted = mapping.path.find(" (deleted)");
        if (deleted != std::string::npos) {
            mapping.path.erase(deleted);
        }
        out.push_back(std::move(mapping));
    }
    return true;
}

bool ReadAt(int fd, uint64_t offset, void* buffer, size_t size) {
    size_t done = 0;
    while (done < size) {
        const ssize_t got = ::pread(fd, static_cast<uint8_t*>(buffer) + done, size - done,
                                    static_cast<off_t>(offset + done));
        if (got <= 0) {
            return false;
        }
        done += static_cast<size_t>(got);
    }
    return true;
}

char TypeLetter(uint8_t type) {
    switch (type & 0xf) {
        case STT_OBJECT: return 'O';
        case STT_FUNC: return 'F';
        case STT_SECTION: return 'S';
        case STT_FILE: return 'f';
        case STT_COMMON: return 'C';
        case STT_TLS: return 'T';
        case STT_GNU_IFUNC: return 'I';
        default: return 'N';
    }
}

std::string FormatEntry(uint64_t address, uint64_t file_offset, const char* type,
                        const char* name, const char* table) {
    char line[512];
    std::snprintf(line, sizeof(line), "0x%llx 0x%llx %s %s %s",
                  static_cast<unsigned long long>(address),
                  static_cast<unsigned long long>(file_offset), type, name, table);
    return std::string(line);
}

// Parses the section headers of the ELF behind `path`. Section headers are what
// Android keeps even in stripped system libraries, and they index .dynsym
// directly, so no DT_GNU_HASH walk is needed.
bool LoadSymbols(const std::string& path, uint64_t load_bias, const std::string& wanted,
                 bool include_locals, uint32_t limit, const char* module, std::string& text,
                 std::string& error) {
    const int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        error = "cannot open " + path + ": " + std::strerror(errno);
        return false;
    }

    Elf64_Ehdr ehdr{};
    if (!ReadAt(fd, 0, &ehdr, sizeof(ehdr)) || std::memcmp(ehdr.e_ident, ELFMAG, SELFMAG) != 0 ||
        ehdr.e_ident[EI_CLASS] != ELFCLASS64) {
        ::close(fd);
        error = path + " is not an ELF64 file";
        return false;
    }
    if (ehdr.e_shoff == 0 || ehdr.e_shnum == 0 || ehdr.e_shentsize != sizeof(Elf64_Shdr)) {
        ::close(fd);
        error = path + " has no section headers (section header table stripped)";
        return false;
    }
    if (ehdr.e_shnum > 65535) {
        ::close(fd);
        error = path + " reports an unreasonable section count";
        return false;
    }

    std::vector<Elf64_Shdr> sections(ehdr.e_shnum);
    if (!ReadAt(fd, ehdr.e_shoff, sections.data(), sections.size() * sizeof(Elf64_Shdr))) {
        ::close(fd);
        error = "cannot read the section header table of " + path;
        return false;
    }

    std::vector<const Elf64_Shdr*> tables;
    for (const Elf64_Shdr& section : sections) {
        if (section.sh_type == SHT_DYNSYM && section.sh_entsize == sizeof(Elf64_Sym) &&
            section.sh_size > 0) {
            tables.push_back(&section);
            break;
        }
    }
    if (tables.empty()) {
        ::close(fd);
        error = path + " has no .dynsym section";
        return false;
    }
    if (include_locals) {
        for (const Elf64_Shdr& section : sections) {
            if (section.sh_type == SHT_SYMTAB && section.sh_entsize == sizeof(Elf64_Sym) &&
                section.sh_size > 0) {
                tables.push_back(&section);
                break;
            }
        }
    }

    uint32_t emitted = 0;
    bool any_table = false;
    for (const Elf64_Shdr* table : tables) {
        const bool local_table = table->sh_type == SHT_SYMTAB;
        if (table->sh_link >= sections.size()) {
            continue;
        }
        const Elf64_Shdr& strtab = sections[table->sh_link];
        if (strtab.sh_type != SHT_STRTAB || strtab.sh_size == 0 || strtab.sh_size > (1u << 24)) {
            continue;
        }
        std::vector<Elf64_Sym> symbols(table->sh_size / sizeof(Elf64_Sym));
        std::vector<char> strings(strtab.sh_size);
        if (!ReadAt(fd, table->sh_offset, symbols.data(), symbols.size() * sizeof(Elf64_Sym)) ||
            !ReadAt(fd, strtab.sh_offset, strings.data(), strings.size())) {
            continue;
        }
        any_table = true;
        for (const Elf64_Sym& symbol : symbols) {
            if (symbol.st_shndx == SHN_UNDEF || symbol.st_name == 0 ||
                symbol.st_name >= strings.size()) {
                continue;
            }
            const char* name = strings.data() + symbol.st_name;
            if (name[0] == '\0') {
                continue;
            }
            if (!wanted.empty() && wanted != name) {
                continue;
            }
            const char type[2] = {TypeLetter(symbol.st_info), '\0'};
            text += FormatEntry(load_bias + symbol.st_value, symbol.st_value, type, name,
                                local_table ? "sym" : "dyn");
            text += " ";
            text += module;
            text += "\n";
            if (wanted.empty() && ++emitted >= limit) {
                ::close(fd);
                return true;
            }
        }
    }
    ::close(fd);
    if (!any_table) {
        error = "cannot read the symbol tables of " + path;
        return false;
    }
    if (text.empty()) {
        error = wanted.empty()
                    ? "no symbols in " + path
                    : "\"" + wanted + "\" is not exported by " + path +
                          (include_locals ? "" : " (try --all for local symbols)");
        return false;
    }
    return true;
}

}  // namespace

SymbolResult ResolveSymbols(uint32_t pid, const std::string& module, const std::string& name,
                            bool include_locals, uint32_t limit) {
    SymbolResult result;
    if (pid == 0) {
        result.error = "pid 0 is not a process";
        return result;
    }
    if (limit == 0 || limit > 8192) {
        limit = 4096;
    }

    std::vector<Mapping> mappings;
    std::string maps_error;
    if (!ReadMaps(pid, mappings, maps_error)) {
        result.error = maps_error;
        return result;
    }

    // The first mapping of the module carries the file offset that the load bias
    // has to cancel out; later mappings of the same file are not needed.
    const Mapping* chosen = nullptr;
    for (const Mapping& mapping : mappings) {
        if (mapping.path.find(module) == std::string::npos) {
            continue;
        }
        if (chosen == nullptr || mapping.offset < chosen->offset) {
            chosen = &mapping;
        }
    }
    if (chosen == nullptr) {
        result.error = "pid " + std::to_string(pid) + " has no mapping matching \"" + module +
                       "\"";
        return result;
    }

    const uint64_t load_bias = chosen->start - chosen->offset;
    std::string error;
    if (!LoadSymbols(chosen->path, load_bias, name, include_locals, limit,
                     chosen->path.c_str(), result.text, error)) {
        result.error = error;
        return result;
    }
    return result;
}

}  // namespace skpdb
