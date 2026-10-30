#pragma once
#include <cstdint>
#include <string>
namespace skpdb {
bool TouchGuardInstall(uint64_t device, std::string &error);
bool TouchGuardRenew(std::string &error);
bool TouchGuardRemove(std::string &error);
std::string TouchGuardReport();
} // namespace skpdb
