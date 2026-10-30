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

std::string AdminCall(Transport& channel, uint8_t action, const std::string& body,
                      const Options& options) {
    std::vector<uint8_t> request{skpdb::opcode::kAdmin, action};
    request.insert(request.end(), body.begin(), body.end());
    skpdb::Response response =
        skpdb::Request(channel, request, skpdb::opcode::kAdmin, options.timeout * 1000);
    return std::string(response.payload.begin(), response.payload.end());
}

int CommandInfo(Transport& channel, const Options& options) {
    std::cout << AdminCall(channel, skpdb::kAdminInfo, "", options);
    return 0;
}

int CommandConfig(Transport& channel, const Options& options) {
    if (options.args.empty()) {
        std::cerr << "skpdb: config needs at least one key=value pair" << std::endl;
        return 2;
    }
    std::string body;
    for (const std::string& item : options.args) {
        if (!body.empty()) {
            body.push_back(';');
        }
        body += item;
    }
    std::cout << AdminCall(channel, skpdb::kAdminConfig, body, options);
    return 0;
}

int CommandWebUi(Transport& channel, const Options& options) {
    const std::string action = options.args.empty() ? "start" : options.args[0];
    if (action == "stop") {
        std::cout << AdminCall(channel, skpdb::kAdminWebUiStop, "", options);
        return 0;
    }
    if (action != "start" && action != "link") {
        std::cerr << "skpdb: webui takes start, stop or link" << std::endl;
        return 2;
    }
    if (action == "start") {
        std::cout << AdminCall(channel, skpdb::kAdminWebUiStart, "", options);
    }

    // The start reply may say "already running" instead of the port, so the
    // port is always taken from the module report.
    const std::string info = AdminCall(channel, skpdb::kAdminInfo, "", options);
    const std::string marker = "webui_port=";
    const size_t at = info.find(marker);
    std::string port_text;
    if (at != std::string::npos) {
        size_t end = at + marker.size();
        const size_t start = end;
        while (end < info.size() && info[end] >= '0' && info[end] <= '9') {
            ++end;
        }
        port_text = info.substr(start, end - start);
    }
    if (port_text.empty() || port_text == "0") {
        std::cerr << "skpdb: the web ui is not running; start it with: webui start"
                  << std::endl;
        return 1;
    }
    // The page needs no credentials: starting the server already required the
    // key, and the page closes the server three seconds after it stops
    // beating (background tab, closed window, lost link).
    std::cout << "open: http://" << options.host << ":" << port_text << "/" << std::endl;
    return 0;
}

int CommandKnock(const Options& options) {
    const std::string key = PromptKey();
    if (key.empty()) {
        std::cerr << "skpdb: a root key is required for this device" << std::endl;
        return 1;
    }
    std::string error;
    if (!skpdb::SendKnock(options.host, static_cast<uint16_t>(options.knock_port), key, 3,
                          error)) {
        std::cerr << "skpdb: " << error << std::endl;
        return 1;
    }
    std::cout << "knock sent to " << options.host << ":" << options.knock_port
              << " (the port opens for this address only)" << std::endl;
    return 0;
}

int CommandPing(Transport& channel, const Options& options) {    auto response =
        skpdb::Request(channel, {skpdb::opcode::kPing}, skpdb::opcode::kPing,
                       options.timeout * 1000);
    std::string text(response.payload.begin(), response.payload.end());
    std::cout << text << std::endl;
    return 0;
}

int CommandVersion(Transport& channel, const Options& options) {
    auto response =
        skpdb::Request(channel, {skpdb::opcode::kVersion}, skpdb::opcode::kVersion,
                       options.timeout * 1000);
    if (response.payload.size() < 4) {
        throw std::runtime_error("short version response");
    }
    uint32_t size = skpdb::GetU32(response.payload.data());
    if (response.payload.size() < 4 + size) {
        throw std::runtime_error("short version payload");
    }
    std::cout.write(reinterpret_cast<const char*>(response.payload.data() + 4), size);
    std::cout << std::endl;
    return 0;
}


}  // namespace cli
