#pragma once

#include <string>

namespace skpdb {

// Reads a short text file and strips trailing CR/LF. Used for ConfigFS
// attributes and sysfs one-liners. Returns an empty string on any error.
std::string ReadTextFile(const std::string& path);

// Reads a file of arbitrary length. Intended for the small state files kept
// in the module private directory.
std::string ReadFileFully(const std::string& path);

// Atomically replaces `path` with `data`: writes a sibling ".tmp" file,
// fsyncs it, then renames. Creates files with mode 0600.
bool WritePrivateFile(const std::string& path, const std::string& data);

// Writes a ConfigFS attribute. An empty string is written as a single
// newline, which is how the gadget stack unbinds a controller.
bool WriteAttribute(const std::string& path, const char* text);

}  // namespace skpdb
