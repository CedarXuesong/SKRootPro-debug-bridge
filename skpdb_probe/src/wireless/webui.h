#pragma once

#include <cstdint>
#include <string>

#include <module_web_ui_http_handler.h>

namespace skpdb::wireless {

// Serves the administration page. The server is started on demand from the
// admin socket on a random high port. Access uses the SDK's existing opening
// authorization; the page does not ask for the key again. Its lifetime is
// decided by the foreground heartbeat:
// /api/alive beat is written to skpdb.webui.beat, and the bridge daemon kills
// the loader once the beats stop (see module_info.h).
class WebUiHandler : public kernel_module::WebUIHttpHandler {
public:
    WebUiHandler() = default;

    void onPrepareCreate(const char* root_key, const char* module_private_dir,
                         uint32_t port) override;
    bool handleGet(CivetServer* server, mg_connection* conn, const std::string& path,
                   const std::string& query) override;
    bool handlePost(CivetServer* server, mg_connection* conn, const std::string& path,
                    const std::string& body) override;
    ServerExitAction onBeforeServerExit() override;

private:
    // Sends the request to the bridge daemon over its filesystem socket.
    bool Admin(uint8_t action, const std::string& body, std::string& reply, std::string& error);
    bool Exchange(const std::vector<uint8_t>& request, std::vector<uint8_t>& reply, std::string& error);
    bool Authorize(mg_connection* conn);
    void Reply(mg_connection* conn, int status, const std::string& text, const char* type = "text/plain; charset=utf-8");
    // Stamps skpdb.webui.beat so the daemon knows the page is still there.
    void Beat();

    std::string root_key_;
    std::string socket_path_;
    std::string beat_path_;
    std::string request_token_;
};

}  // namespace skpdb::wireless
