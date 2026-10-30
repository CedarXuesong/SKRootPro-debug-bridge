#include "common/session.h"

#include <vector>

#include "common/auth.h"
#include "common/channel.h"
#include "common/frame.h"
#include "common/stream.h"

namespace skpdb {

void ServeSession(Stream& stream, const std::string& root_key, const SessionConfig& config,
                  int timeout_ms) {
    if (!Authenticate(stream, root_key, timeout_ms)) {
        return;
    }
    for (;;) {
        std::vector<uint8_t> request;
        if (!RecvFrame(stream, request, timeout_ms)) {
            break;
        }
        std::vector<uint8_t> response =
            ProcessRequest(request, stream, config.capabilities, config.admin);
        if (!SendFrame(stream, response, timeout_ms)) {
            break;
        }
        if (!request.empty() && request[0] == opcode::kClose) {
            break;
        }
    }
}

}  // namespace skpdb
