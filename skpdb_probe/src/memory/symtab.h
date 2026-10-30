#pragma once

#include <cstdint>
#include <string>

namespace skpdb {

// Result of a symbol lookup against a mapped ELF file. The text is one entry per
// line, already formatted for the wire: "<address> <file-offset> <type> <name>".
struct SymbolResult {
    std::string text;
    std::string error;
};

// Resolves symbols in the module of `pid` whose path contains `module`. The ELF
// file is parsed from the filesystem (the mapping's file), not from the target's
// address space, so the lookup never touches the target at all -- no read of its
// memory, no fault, nothing a target could observe.
//
// `name` empty lists the dynamic symbol table (up to `limit` entries); a value
// selects the symbols with exactly that name.
// `include_locals` also walks .symtab when the file still has one (unstripped
// libraries, or a library you built yourself). Each line ends with the table it
// came from, dyn or sym, so a caller can tell an exported symbol from a local
// one.
SymbolResult ResolveSymbols(uint32_t pid, const std::string& module, const std::string& name,
                            bool include_locals, uint32_t limit);

}  // namespace skpdb
