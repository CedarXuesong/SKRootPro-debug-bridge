#include "wireless/webui.h"
#include "wireless/webui_assets.h"
#include "service/admin.h"
#include "wireless/module_info.h"
#include "common/bytes.h"
#include <fcntl.h>
#include <unistd.h>
#include <algorithm>
#include <array>
#include <ctime>
#include <sstream>

namespace skpdb::wireless {
namespace {
std::string Header(mg_connection* conn, const char* key) {
    const char* value = mg_get_header(conn, key); return value ? value : "";
}
std::string Hex(const uint8_t* data, size_t count) {
    static constexpr char digits[] = "0123456789abcdef";
    std::string text; text.reserve(count * 2);
    for (size_t i = 0; i < count; ++i) { text += digits[data[i] >> 4]; text += digits[data[i] & 15]; }
    return text;
}
std::string RandomToken() {
    std::array<uint8_t, 32> bytes{};
    int fd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return "";
    size_t done = 0;
    while (done < bytes.size()) {
        auto n = read(fd, bytes.data() + done, bytes.size() - done);
        if (n <= 0) { close(fd); return ""; }
        done += static_cast<size_t>(n);
    }
    close(fd); return Hex(bytes.data(), bytes.size());
}
bool Equal(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    uint8_t diff = 0; for (size_t i = 0; i < a.size(); ++i) diff |= a[i] ^ b[i];
    return diff == 0;
}
bool SameOrigin(mg_connection* conn) {
    const auto origin = Header(conn, "Origin");
    return origin.empty() || origin == "http://" + Header(conn, "Host") || origin == "https://" + Header(conn, "Host");
}
std::string SafeInfo(const std::string& report) {
    std::istringstream input(report); std::string line, result;
    while (std::getline(input, line)) {
        if (line.rfind("fast_token=", 0) == 0 || line.rfind("admin_socket=", 0) == 0) continue;
        result += line + "\n";
    }
    return result;
}
bool PathQuery(const std::string& query, std::string& path) {
    if (query.rfind("path=", 0) != 0 || query.size() > 4096 || query.find('&') != std::string::npos) return false;
    path.clear();
    for (size_t i = 5; i < query.size(); ++i) {
        unsigned char c = query[i];
        if (c == '%') {
            if (i + 2 >= query.size()) return false;
            auto digit = [](char ch) { if (ch >= '0' && ch <= '9') return ch - '0'; if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10; if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10; return -1; };
            int a = digit(query[i + 1]), b = digit(query[i + 2]);
            if (a < 0 || b < 0) return false;
            c = static_cast<unsigned char>(a * 16 + b); i += 2;
        } else if (c == '+') c = ' ';
        if (c < 32 || c == 127) return false;
        path += static_cast<char>(c);
    }
    return !path.empty() && path[0] == '/';
}
}

void WebUiHandler::onPrepareCreate(const char* root_key, const char* directory, uint32_t) {
    root_key_ = root_key ? root_key : "";
    socket_path_ = AdminSocketPath(directory ? directory : "");
    beat_path_ = WebUiBeatPath(directory ? directory : "");
    request_token_ = RandomToken();
}
void WebUiHandler::Reply(mg_connection* conn, int status, const std::string& text, const char* type) {
    kernel_module::webui::KvMap headers{
        {"Cache-Control", "no-store"}, {"X-Content-Type-Options", "nosniff"}, {"Referrer-Policy", "no-referrer"},
        {"Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"}};
    kernel_module::webui::send_bytes(conn, status, type, text.data(), text.size(), &headers);
}
void WebUiHandler::Beat() {
    int fd = open(beat_path_.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0) return;
    const auto stamp = std::to_string(static_cast<long long>(time(nullptr))) + "\n";
    (void)write(fd, stamp.data(), stamp.size()); close(fd);
}
bool WebUiHandler::Authorize(mg_connection* conn) {
    if (!SameOrigin(conn) || request_token_.empty() || !Equal(request_token_, Header(conn, "X-SKPDB-CSRF"))) {
        Reply(conn, 403, "页面请求已失效，请从模块管理器重新打开 WebUI"); return false;
    }
    return true;
}
bool WebUiHandler::Exchange(const std::vector<uint8_t>& request, std::vector<uint8_t>& reply, std::string& error) {
    AdminClient client;
    const bool ok = client.Open(socket_path_, root_key_, 12000, error) && client.Request(request, reply, error);
    auto pos = error.find(socket_path_);
    if (pos != std::string::npos) error.replace(pos, socket_path_.size(), "<模块管理通道>");
    return ok;
}
bool WebUiHandler::Admin(uint8_t action, const std::string& body, std::string& reply, std::string& error) {
    std::vector<uint8_t> request{'B', action}, result;
    request.insert(request.end(), body.begin(), body.end());
    if (!Exchange(request, result, error)) return false;
    reply.assign(result.begin(), result.end()); return true;
}
bool WebUiHandler::handleGet(CivetServer*, mg_connection* conn, const std::string& path, const std::string& query) {
    if (path == "/" || path == "/index.html") {
        if (request_token_.empty()) { Reply(conn, 500, "无法初始化页面请求"); return true; }
        std::string html = kIndexHtml;
        const auto badge = html.find("__SKPDB_CHANNEL_BADGE__");
        if (badge != std::string::npos)
            html.replace(badge, 23, SKPDB_CANARY ? "<span class=\"badge\">Canary 版</span>" : "");
        const auto marker = html.find("__SKPDB_CSRF__");
        if (marker == std::string::npos) { Reply(conn, 500, "页面资源不完整"); return true; }
        html.replace(marker, 14, request_token_);
        Reply(conn, 200, html, "text/html; charset=utf-8");
    }
    else if (path == "/app.js") Reply(conn, 200, kAppJs, "application/javascript; charset=utf-8");
    else if (path == "/app.css") Reply(conn, 200, kAppCss, "text/css; charset=utf-8");
    else {
        if (!Authorize(conn)) return true;
        std::string reply, error;
        if (path == "/api/info") {
            if (!Admin(kAdminInfo, "", reply, error)) Reply(conn, 502, error);
            else Reply(conn, 200, SafeInfo(reply));
        } else if (path == "/api/list" || path == "/api/file") {
            std::string file;
            if (!PathQuery(query, file)) { Reply(conn, 400, "请输入有效绝对路径"); return true; }
            std::vector<uint8_t> request{static_cast<uint8_t>(path == "/api/list" ? 'L' : 'R')}, result;
            if (path == "/api/file") { PutU64(request, 0); PutU32(request, 65536); }
            request.insert(request.end(), file.begin(), file.end());
            if (!Exchange(request, result, error)) Reply(conn, 400, error);
            else if (result.size() < 4 || GetU32(result.data()) != result.size() - 4) Reply(conn, 502, "文件响应不完整");
            else Reply(conn, 200, std::string(result.begin() + 4, result.end()));
        } else Reply(conn, 404, "接口不存在");
    }
    return true;
}
bool WebUiHandler::handlePost(CivetServer*, mg_connection* conn, const std::string& path, const std::string& body) {
    if (!Authorize(conn)) return true;
    if (path == "/api/alive") { Beat(); Reply(conn, 200, "ok\n"); return true; }
    std::string reply, error; uint8_t action = 0;
    if (path == "/api/config") action = kAdminConfig;
    else if (path == "/api/diagnostics") action = kAdminDiagnostics;
    else if (path == "/api/webui/stop") action = kAdminWebUiStop;
    else if (path == "/api/exec") {
        if (body.empty() || body.size() > 8192 || body.find('\0') != std::string::npos) { Reply(conn, 400, "命令为空或超过 8192 字节"); return true; }
        std::vector<uint8_t> request{'E'}, result;
        request.insert(request.end(), body.begin(), body.end());
        if (!Exchange(request, result, error)) Reply(conn, 502, error);
        else if (result.size() < 8 || GetU32(result.data() + 4) != result.size() - 8) Reply(conn, 502, "命令响应不完整");
        else Reply(conn, 200, "exit=" + std::to_string(GetU32(result.data())) + "\n" + std::string(result.begin() + 8, result.end()));
        return true;
    } else { Reply(conn, 404, "接口不存在"); return true; }
    if (body.size() > 4096 || (action != kAdminConfig && !body.empty())) { Reply(conn, 400, "无效请求内容"); return true; }
    if (!Admin(action, body, reply, error)) Reply(conn, 400, error);
    else Reply(conn, 200, reply);
    return true;
}
kernel_module::WebUIHttpHandler::ServerExitAction WebUiHandler::onBeforeServerExit() { return ServerExitAction::Exit; }
}
