#include "memory/symtab.h"

#include <elf.h>
#include <fcntl.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstring>
#include <iostream>
#include <vector>

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    std::vector<unsigned char> data(4096);
    Elf64_Ehdr header{};
    std::memcpy(header.e_ident, ELFMAG, SELFMAG);
    header.e_ident[EI_CLASS] = ELFCLASS64;
    header.e_shoff = sizeof(header);
    header.e_shnum = 3;
    header.e_shentsize = sizeof(Elf64_Shdr);
    std::memcpy(data.data(), &header, sizeof(header));
    Elf64_Shdr sections[3]{};
    sections[1].sh_type = SHT_DYNSYM;
    sections[1].sh_offset = 512;
    sections[1].sh_size = 2 * sizeof(Elf64_Sym);
    sections[1].sh_entsize = sizeof(Elf64_Sym);
    sections[1].sh_link = 2;
    sections[2].sh_type = SHT_STRTAB;
    sections[2].sh_offset = 1024;
    sections[2].sh_size = 2;
    std::memcpy(data.data() + header.e_shoff, sections, sizeof(sections));
    Elf64_Sym symbol{};
    symbol.st_name = 1;
    symbol.st_shndx = 1;
    symbol.st_info = STT_FUNC;
    symbol.st_value = 16;
    std::memcpy(data.data() + 512 + sizeof(symbol), &symbol, sizeof(symbol));
    data[1024] = 0;
    data[1025] = 'A';  // No NUL inside the advertised string table.
    const int fd = open(argv[1], O_RDWR | O_CREAT | O_EXCL, 0600);
    if (fd < 0 || write(fd, data.data(), data.size()) != static_cast<ssize_t>(data.size())) return 3;
    void* mapped = mmap(nullptr, data.size(), PROT_READ, MAP_PRIVATE, fd, 0);
    if (mapped == MAP_FAILED) return 4;
    const char* basename = std::strrchr(argv[1], '/');
    const auto result = skpdb::ResolveSymbols(static_cast<uint32_t>(getpid()),
        basename ? basename + 1 : argv[1], "", false, 16);
    std::cout << result.error << result.text;
    munmap(mapped, data.size());
    close(fd);
}
