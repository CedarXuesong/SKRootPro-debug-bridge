#include "service/config.h"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <sstream>
#include <vector>

namespace skpdb {
namespace {

uint64_t ParseNumber(const std::string& text, uint64_t fallback) {
    if (text.empty()) {
        return fallback;
    }
    char* end = nullptr;
    errno = 0;
    unsigned long long value = std::strtoull(text.c_str(), &end, 10);
    if (errno != 0 || end == text.c_str() || *end != '\0') {
        return fallback;
    }
    return value;
}

std::string Trim(const std::string& text) {
    size_t begin = text.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) {
        return std::string();
    }
    size_t end = text.find_last_not_of(" \t\r\n");
    return text.substr(begin, end - begin + 1);
}

bool WriteFully(int fd, const std::string& text) {
    const char* cursor = text.data();
    size_t remaining = text.size();
    while (remaining) {
        ssize_t count = write(fd, cursor, remaining);
        if (count <= 0) {
            return false;
        }
        cursor += count;
        remaining -= static_cast<size_t>(count);
    }
    return true;
}

}  // namespace

std::string BridgeConfigPath(const std::string& module_dir) {
    if (module_dir.empty()) {
        return std::string();
    }
    std::string path = module_dir;
    if (path.back() != '/') {
        path.push_back('/');
    }
    path += "skpdb.conf";
    return path;
}

BridgeConfig LoadBridgeConfig(const std::string& module_dir) {
    BridgeConfig config;
    std::string path = BridgeConfigPath(module_dir);
    if (path.empty()) {
        return config;
    }
    FILE* file = std::fopen(path.c_str(), "re");
    if (file == nullptr) {
        return config;
    }
    char line[256];
    while (std::fgets(line, sizeof(line), file) != nullptr) {
        std::string entry = Trim(line);
        size_t separator = entry.find('=');
        if (entry.empty() || entry[0] == '#' || separator == std::string::npos) {
            continue;
        }
        std::string key = Trim(entry.substr(0, separator));
        uint64_t value = ParseNumber(Trim(entry.substr(separator + 1)), 0);
        if (key == "service_port" && value >= 1024 && value <= 65535) {
            config.service_port = static_cast<uint16_t>(value);
        } else if (key == "knock_port" && value >= 1024 && value <= 65535) {
            config.knock_port = static_cast<uint16_t>(value);
        } else if (key == "idle_timeout_seconds" && value <= 3600) {
            config.idle_timeout_seconds = static_cast<uint32_t>(value);
        } else if (key == "auth_window_seconds" && value >= 5 && value <= 300) {
            config.auth_window_seconds = static_cast<uint32_t>(value);
        } else if (key == "always_listen" && value <= 1) {
            config.always_listen = value == 1;
        }
    }
    std::fclose(file);
    return config;
}

bool SaveBridgeConfig(const std::string& module_dir, const BridgeConfig& config,
                      std::string& error) {
    std::string path = BridgeConfigPath(module_dir);
    if (path.empty()) {
        error = "module directory is unknown";
        return false;
    }
    std::string temporary = path + ".tmp";
    int fd = open(temporary.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0) {
        error = std::string("open ") + temporary + ": " + std::strerror(errno);
        return false;
    }
    std::ostringstream text;
    text << "# SKPDB bridge settings\n"
         << "service_port=" << config.service_port << "\n"
         << "knock_port=" << config.knock_port << "\n"
         << "idle_timeout_seconds=" << config.idle_timeout_seconds << "\n"
         << "auth_window_seconds=" << config.auth_window_seconds << "\n"
         << "always_listen=" << (config.always_listen ? 1 : 0) << "\n";
    bool ok = WriteFully(fd, text.str());
    fsync(fd);
    close(fd);
    if (!ok) {
        error = std::string("write ") + temporary + ": " + std::strerror(errno);
        unlink(temporary.c_str());
        return false;
    }
    if (rename(temporary.c_str(), path.c_str()) != 0) {
        error = std::string("rename ") + path + ": " + std::strerror(errno);
        unlink(temporary.c_str());
        return false;
    }
    return true;
}

bool ApplyConfigUpdate(BridgeConfig& config, const std::string& update, std::string& error) {
    size_t cursor = 0;
    bool changed = false;
    while (cursor <= update.size()) {
        size_t end = update.find_first_of(";&\n", cursor);
        if (end == std::string::npos) {
            end = update.size();
        }
        std::string entry = Trim(update.substr(cursor, end - cursor));
        cursor = end + 1;
        if (entry.empty()) {
            continue;
        }
        size_t separator = entry.find('=');
        if (separator == std::string::npos) {
            error = "expected key=value, got \"" + entry + "\"";
            return false;
        }
        std::string key = Trim(entry.substr(0, separator));
        std::string text = Trim(entry.substr(separator + 1));
        uint64_t value = ParseNumber(text, UINT64_MAX);
        if (value == UINT64_MAX) {
            error = "not a number: \"" + text + "\"";
            return false;
        }
        if (key == "service_port" || key == "knock_port") {
            if (value < 1024 || value > 65535) {
                error = key + " must be 1024..65535";
                return false;
            }
            if (key == "service_port") {
                config.service_port = static_cast<uint16_t>(value);
            } else {
                config.knock_port = static_cast<uint16_t>(value);
            }
        } else if (key == "idle_timeout_seconds") {
            if (value > 3600) {
                error = "idle_timeout_seconds must be 0..3600";
                return false;
            }
            config.idle_timeout_seconds = static_cast<uint32_t>(value);
        } else if (key == "auth_window_seconds") {
            if (value < 5 || value > 300) {
                error = "auth_window_seconds must be 5..300";
                return false;
            }
            config.auth_window_seconds = static_cast<uint32_t>(value);
        } else if (key == "always_listen") {
            if (value > 1) {
                error = "always_listen must be 0 or 1";
                return false;
            }
            config.always_listen = value == 1;
        } else {
            error = "unknown setting \"" + key + "\"";
            return false;
        }
        changed = true;
    }
    if (!changed) {
        error = "nothing to update";
        return false;
    }
    return true;
}

}  // namespace skpdb
