#include "wireless/server.h"
#include "wireless/diagnostics.h"

#include <arpa/inet.h>
#include <dirent.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <sys/socket.h>
#include <sys/resource.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <ctime>
#include <mutex>
#include <set>
#include <string>
#include <sstream>
#include <thread>
#include <vector>

#include <module_err_def.h>
#include <skroot_env/features/skroot_module_webui_loader.h>

#include "service/admin.h"
#include "common/auth.h"
#include "common/bytes.h"
#include "common/channel.h"
#include "service/config.h"
#include "common/frame.h"
#include "service/knock.h"
#include "common/session.h"
#include "display/cursor_surface.h"
#include "input/gyro.h"
#include "input/gyro_protocol.h"
#include "input/touch.h"
#include "wireless/module_info.h"
#include "wireless/socket_stream.h"

namespace skpdb::wireless {
namespace {

// The transport record is fixed; the ports and timeouts are appended from the
// live configuration so a client can read them without a second round trip.
constexpr char kBaseCapabilities[] =
    "version=1;transport=tcp;auth=hmac-sha256;file=rw;shell=exec;pty=none;forward=none;"
    "mem=read,maps,info,scatter,dump,resident,write,scan,sym;mem_ptrace=none;mem_backends=kernel,vm_readv,proc_mem;"
    "touch=inject,info,batch;touch_device=real-touchscreen;touch_ptrace=none;"
    "knock=udp;admin=unix;webui=http;mem_policy=no-fault-default;fast=SKF2;hud=transparent;"
    "diagnostics=1;gyro=fw-hook;";

std::vector<uint8_t> ErrorResponse(const std::string& message) {
    std::vector<uint8_t> response{opcode::kAdmin, 1};
    PutU32(response, static_cast<uint32_t>(message.size()));
    response.insert(response.end(), message.begin(), message.end());
    return response;
}

std::vector<uint8_t> TextResponse(const std::string& text) {
    std::vector<uint8_t> response{opcode::kAdmin, 0};
    response.insert(response.end(), text.begin(), text.end());
    return response;
}

void FillFastToken(std::array<uint8_t, 16>& token) {
    const int fd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (fd >= 0) {
        ssize_t got = 0;
        while (got < static_cast<ssize_t>(token.size())) {
            const ssize_t n = read(fd, token.data() + got, token.size() - got);
            if (n <= 0) {
                break;
            }
            got += n;
        }
        close(fd);
        if (got == static_cast<ssize_t>(token.size())) {
            return;
        }
    }
    const uint64_t seed = static_cast<uint64_t>(time(nullptr)) ^
                          (static_cast<uint64_t>(getpid()) << 32);
    for (size_t i = 0; i < token.size(); ++i) {
        token[i] = static_cast<uint8_t>((seed >> ((i % 8) * 8)) ^ (i * 131u + 0x5a));
    }
}

std::string HexToken(const std::array<uint8_t, 16>& token) {
    static constexpr char kHex[] = "0123456789abcdef";
    std::string text;
    text.reserve(token.size() * 2);
    for (uint8_t byte : token) {
        text.push_back(kHex[byte >> 4]);
        text.push_back(kHex[byte & 0x0f]);
    }
    return text;
}

bool TokenEquals(const uint8_t* received, const std::array<uint8_t, 16>& token) {
    uint8_t diff = 0;
    for (size_t i = 0; i < token.size(); ++i) {
        diff |= static_cast<uint8_t>(received[i] ^ token[i]);
    }
    return diff == 0;
}

std::string JoinList(const std::set<uint32_t>& addresses) {    std::string text;
    for (uint32_t address : addresses) {
        in_addr value{};
        value.s_addr = address;
        char buffer[INET_ADDRSTRLEN] = {0};
        if (!inet_ntop(AF_INET, &value, buffer, sizeof(buffer))) {
            continue;
        }
        if (!text.empty()) {
            text.push_back(',');
        }
        text += buffer;
    }
    return text;
}

// True when something accepts a connection on the loopback port. The WebUI
// loader runs in its own domain, so a /proc inventory is not a reliable check;
// a connect on the port it announced is.
bool PortAccepting(int port) {
    if (port <= 0 || port > 65535) {
        return false;
    }
    const int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return false;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(static_cast<uint16_t>(port));
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    const int flags = fcntl(fd, F_GETFL, 0);
    fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    const int rc = connect(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address));
    bool alive = rc == 0;
    if (!alive && errno == EINPROGRESS) {
        pollfd entry{};
        entry.fd = fd;
        entry.events = POLLOUT;
        alive = poll(&entry, 1, 300) == 1;
    }
    close(fd);
    return alive;
}

bool StopWebUiLoader() {
    DIR* directory = opendir("/proc");
    if (!directory) {
        return false;
    }
    bool signalled = false;
    while (dirent* entry = readdir(directory)) {
        if (entry->d_name[0] < '1' || entry->d_name[0] > '9') {
            continue;
        }
        std::string path = std::string("/proc/") + entry->d_name + "/cmdline";
        FILE* file = fopen(path.c_str(), "rb");
        if (!file) {
            continue;
        }
        char buffer[4096];
        size_t size = fread(buffer, 1, sizeof(buffer) - 1, file);
        fclose(file);
        if (size == 0) {
            continue;
        }
        std::string command(buffer, size);
        if (command.find("webui_loader") == std::string::npos ||
            command.find(kModuleId32) == std::string::npos) {
            continue;
        }
        pid_t pid = static_cast<pid_t>(std::strtol(entry->d_name, nullptr, 10));
        if (pid > 1 && pid != getpid()) {
            kill(pid, SIGTERM);
            signalled = true;
        }
    }
    closedir(directory);
    return signalled;
}

class Bridge {
public:
    Bridge(std::string root_key, std::string module_dir)
        : root_key_(std::move(root_key)), module_dir_(std::move(module_dir)) {}

    void Run();

private:
    void GateLoop();
    void AdminLoop();
    void FastLoop();
    void FastWorkerLoop();
    void GyroWriterLoop();
    bool StartFast(uint16_t port);
    void StopFast();
    void ServeAdmin(int client);
    void ServeClient(int client);
    void OnKnock(uint32_t address);

    std::vector<uint8_t> OnAdmin(const std::vector<uint8_t>& request);
    std::vector<uint8_t> TouchStreamRequest(const std::vector<uint8_t>& request);
    std::string InfoReport();
    std::vector<uint8_t> ConfigUpdate(const std::string& body);
    std::vector<uint8_t> WebUiStart();
    std::vector<uint8_t> WebUiStop();
    // Kills the WebUI loader once the page stops beating (see module_info.h).
    void WebUiWatchLoop();
    long long WebUiBeatAge() const;
    // Writes skpdb.health and restarts the knock receiver when its thread stops
    // ticking; a deaf receiver is the one failure that is invisible from
    // outside until the module is rebooted.
    void HealthLoop();
    void WriteHealth();
    bool AlwaysListen();

    std::string Capabilities();
    uint32_t IdleTimeout();
    bool StartKnock(uint16_t port);
    void StopKnock();
    void RestartKnock(uint16_t port);
    bool PeerAllowed(uint32_t address);
    void ClearPeers();
    int OpenListener(uint16_t port);
    // Opens the service port right away when a peer knocks, so the listener is
    // there before the client's first connect instead of one poll later.
    bool ArmForPeer();

    std::string root_key_;
    std::string module_dir_;

    std::mutex config_mutex_;
    BridgeConfig config_;

    std::mutex knock_mutex_;
    KnockReceiver knock_;
    uint16_t knock_port_ = 0;

    std::mutex peer_mutex_;
    std::set<uint32_t> peers_;
    std::atomic<bool> armed_{false};

    std::atomic<bool> listening_{false};
    std::atomic<int> sessions_{0};
    std::atomic<uint64_t> sessions_total_{0};
    std::atomic<uint32_t> knock_restarts_{0};
    std::atomic<int> webui_port_{0};
    std::atomic<long long> webui_started_{0};
    std::atomic<uint32_t> generation_{0};
    // Last moment the gate was useful: a knock, an accept or the end of a
    // session. The idle timer restarts from here, so a session that ends does
    // not immediately close a port that was just used.
    std::atomic<long long> last_activity_{0};
    std::atomic<bool> stopping_{false};
    std::atomic<long long> started_{0};

    std::mutex listener_mutex_;
    int listener_ = -1;
    bool reopen_ = false;

    // Authenticated-but-connectionless touch fast path. The token is only
    // disclosed over the authenticated TCP channel; the source address must
    // also be one that completed a signed knock.
    std::atomic<int> fast_fd_{-1};
    std::atomic<uint16_t> fast_port_{0};
    std::atomic<uint64_t> fast_rx_{0};
    std::atomic<uint64_t> fast_ok_{0};
    std::atomic<uint64_t> fast_bad_{0};
    // Gyro fast path: SKG1 packets carry three floats per sample, so the rate
    // can be refreshed at the desktop sampling rate without a TCP round trip.
    std::atomic<uint64_t> gyro_rx_{0};
    std::atomic<uint64_t> gyro_ok_{0};
    std::atomic<uint64_t> gyro_bad_{0};
    uint32_t fast_gyro_sequence_ = 0;
    // The UDP thread only publishes the newest sample; a normal thread applies
    // it to system_server so a slow kernel write never runs on the realtime path.
    std::mutex gyro_rate_mutex_;
    float gyro_rate_[3] = {0.0f, 0.0f, 0.0f};
    uint64_t gyro_rate_seq_ = 0;
    uint32_t gyro_stream_ = 0, gyro_packet_sequence_ = 0;
    uint64_t gyro_received_ns_ = 0;
    std::condition_variable gyro_cv_;
    std::atomic<uint64_t> fast_stale_{0};
    std::atomic<uint64_t> fast_dropped_{0};
    std::atomic<bool> fast_realtime_{false};
    std::atomic<uint64_t> cursor_rx_{0};
    std::atomic<uint64_t> cursor_ok_{0};
    std::atomic<uint64_t> cursor_bad_{0};
    std::array<uint8_t, 16> fast_token_{};
    struct FastSlot {
        uint32_t x = 0;
        uint32_t y = 0;
        bool pending = false;
    };
    std::mutex fast_mutex_;
    // Serializes reliable generation changes with worker injection. The worker
    // rechecks its snapshot under this lock, including snapshots already taken
    // out of the receive queue before a handoff.
    std::mutex fast_apply_mutex_;
    uint32_t fast_stream_ = 0;
    uint32_t fast_generation_ = 0;
    uint32_t fast_sequence_ = 0;
    std::array<bool, 16> fast_held_{};
    std::array<FastSlot, 16> fast_slots_{};
    struct FastFrame {
        uint32_t stream;
        uint32_t generation;
        int64_t arrived_ns;
        std::vector<TouchBatchItem> items;
    };
    std::deque<FastFrame> fast_frames_;
};

std::string Bridge::Capabilities() {
    std::lock_guard<std::mutex> guard(config_mutex_);
    std::string text = kBaseCapabilities;
    text += "service_port=" + std::to_string(config_.service_port);
    text += ";knock_port=" + std::to_string(config_.knock_port);
    text += ";idle_timeout=" + std::to_string(config_.idle_timeout_seconds);
    text += ";window=16;chunk=917504";
    const uint16_t fast_port = fast_port_.load();
    if (fast_port != 0) {
        text += ";fast=udp;fast_port=" + std::to_string(fast_port);
    }
    return text;
}

uint32_t Bridge::IdleTimeout() {
    std::lock_guard<std::mutex> guard(config_mutex_);
    return config_.idle_timeout_seconds;
}

bool Bridge::StartKnock(uint16_t port) {
    std::lock_guard<std::mutex> guard(knock_mutex_);
    uint32_t window = 0;
    {
        std::lock_guard<std::mutex> config_guard(config_mutex_);
        window = config_.auth_window_seconds;
    }
    std::string error;
    if (!knock_.Start(port, root_key_, window,
                      [this](uint32_t address) { OnKnock(address); }, error)) {
        return false;
    }
    knock_port_ = port;
    return true;
}

void Bridge::StopKnock() {
    std::lock_guard<std::mutex> guard(knock_mutex_);
    knock_.Stop();
    knock_port_ = 0;
}

void Bridge::RestartKnock(uint16_t port) {
    StopKnock();
    StartKnock(port);
}

void Bridge::OnKnock(uint32_t address) {
    {
        std::lock_guard<std::mutex> guard(peer_mutex_);
        peers_.insert(address);
    }
    armed_.store(true);
    last_activity_.store(static_cast<long long>(std::time(nullptr)));
    // Bind here rather than waiting for GateLoop: with the 20 ms poll in the
    // knock receiver this is what turns the cold path into a few milliseconds.
    if (!ArmForPeer()) {
        // A failed bind is retried by GateLoop; the peer stays armed.
    }
}

bool Bridge::ArmForPeer() {
    std::lock_guard<std::mutex> guard(listener_mutex_);
    if (listener_ >= 0) {
        return true;
    }
    uint16_t port = 0;
    {
        std::lock_guard<std::mutex> config_guard(config_mutex_);
        port = config_.service_port;
    }
    const int opened = OpenListener(port);
    if (opened < 0) {
        return false;
    }
    listener_ = opened;
    listening_.store(true);
    last_activity_.store(static_cast<long long>(std::time(nullptr)));
    return true;
}

// True when the service port should stay open regardless of knocks. Read from
// the config on every call so a config update takes effect without a restart.
bool Bridge::AlwaysListen() {
    std::lock_guard<std::mutex> guard(config_mutex_);
    return config_.always_listen;
}

bool Bridge::PeerAllowed(uint32_t address) {
    // With always_listen the port is not a secret any more, so every peer is
    // accepted; the per-connection key challenge still decides who gets in.
    if (AlwaysListen()) {
        return true;
    }
    std::lock_guard<std::mutex> guard(peer_mutex_);
    return peers_.count(address) != 0;
}

void Bridge::ClearPeers() {
    std::lock_guard<std::mutex> guard(peer_mutex_);
    peers_.clear();
}

int Bridge::OpenListener(uint16_t port) {
    int listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (listener < 0) {
        return -1;
    }
    int reuse = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0 ||
        listen(listener, 8) != 0) {
        close(listener);
        return -1;
    }
    return listener;
}

void Bridge::GateLoop() {
    uint32_t seen_generation = 0;
    while (!stopping_.load()) {
        if (seen_generation != generation_.load()) {
            seen_generation = generation_.load();
            std::lock_guard<std::mutex> guard(listener_mutex_);
            if (listener_ >= 0) {
                close(listener_);
                listener_ = -1;
            }
            listening_.store(false);
            ClearPeers();
            armed_.store(false);
        }

        int listener = -1;
        {
            std::lock_guard<std::mutex> guard(listener_mutex_);
            listener = listener_;
        }

        const bool always = AlwaysListen();
        if (listener < 0) {
            if (!armed_.load() && !always) {
                usleep(50 * 1000);
                continue;
            }
            // The knock thread normally got here first; this covers the case
            // where its bind failed or always_listen is set.
            if (!ArmForPeer()) {
                usleep(200 * 1000);
            }
            continue;
        }

        pollfd entry{listener, POLLIN, 0};
        int ready = poll(&entry, 1, 500);
        if (ready > 0 && (entry.revents & POLLIN)) {
            sockaddr_in peer{};
            socklen_t length = sizeof(peer);
            int client = accept4(listener, reinterpret_cast<sockaddr*>(&peer), &length,
                                 SOCK_CLOEXEC);
            if (client >= 0) {
                if (!PeerAllowed(peer.sin_addr.s_addr) || sessions_.load() >= 16) {
                    close(client);
                } else {
                    last_activity_.store(static_cast<long long>(std::time(nullptr)));
                    sessions_.fetch_add(1);
                    sessions_total_.fetch_add(1);
                    std::thread([this, client] { ServeClient(client); }).detach();
                }
            }
        }

        const uint32_t idle = IdleTimeout();
        if (!always && idle > 0 && sessions_.load() == 0 &&
            static_cast<long long>(std::time(nullptr)) - last_activity_.load() >= idle) {
            std::lock_guard<std::mutex> guard(listener_mutex_);
            if (listener_ >= 0) {
                close(listener_);
                listener_ = -1;
            }
            listening_.store(false);
            ClearPeers();
            armed_.store(false);
        }
    }

    std::lock_guard<std::mutex> guard(listener_mutex_);
    if (listener_ >= 0) {
        close(listener_);
        listener_ = -1;
    }
}

// A single small file that survives a restart, so the state before a failure
// can still be read after the module comes back.
void Bridge::WriteHealth() {
    const std::string path = module_dir_ + "/skpdb.health";
    std::ostringstream text;
    text << "version=" << kModuleVersion << "\n"
         << "pid=" << getpid() << "\n"
         << "uptime_seconds=" << static_cast<long long>(std::time(nullptr)) - started_.load()
         << "\n"
         << "knock_port=" << knock_.port() << "\n"
         << "knock_running=" << (knock_.running() ? 1 : 0) << "\n"
         << "knock_ticks=" << knock_.ticks() << "\n"
         << "knock_restarts=" << knock_restarts_.load() << "\n"
         << "armed=" << (armed_.load() ? 1 : 0) << "\n"
         << "listening=" << (listening_.load() ? 1 : 0) << "\n"
         << "sessions=" << sessions_.load() << "\n"
         << "sessions_total=" << sessions_total_.load() << "\n"
         << "always_listen=" << (AlwaysListen() ? 1 : 0) << "\n";
    const int fd = open(path.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0) {
        return;
    }
    const std::string body = text.str();
    (void)write(fd, body.data(), body.size());
    close(fd);
}

void Bridge::HealthLoop() {
    uint64_t last_ticks = 0;
    int stagnant = 0;
    for (;;) {
        if (stopping_.load()) {
            return;
        }
        usleep(10 * 1000 * 1000);
        WriteHealth();
        const uint64_t ticks = knock_.ticks();
        if (ticks == last_ticks) {
            ++stagnant;
        } else {
            stagnant = 0;
        }
        last_ticks = ticks;
        // Two quiet checks mean the receiver thread is gone or wedged; start it
        // over instead of staying deaf until the next reboot.
        if (stagnant >= 2) {
            uint16_t port = 0;
            {
                std::lock_guard<std::mutex> guard(config_mutex_);
                port = config_.knock_port;
            }
            knock_restarts_.fetch_add(1);
            (void)StartKnock(port);
            stagnant = 0;
            last_ticks = knock_.ticks();
        }
    }
}

void Bridge::ServeClient(int client) {
    int no_delay = 1;
    setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &no_delay, sizeof(no_delay));
    timeval timeout{60, 0};
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));

    SessionConfig session;
    session.capabilities = Capabilities();
    session.admin = [this](const std::vector<uint8_t>& request) { return OnAdmin(request); };

    SocketStream stream(client);
    ServeSession(stream, root_key_, session, -1);
    if (sessions_.fetch_sub(1) == 1) {
        CursorSurfaceStop();
        std::string ignored;GyroDisarm(ignored);
        std::lock_guard<std::mutex> apply(fast_apply_mutex_);
        if (sessions_.load() == 0) {
            std::lock_guard<std::mutex> guard(fast_mutex_);
            fast_stream_=0;fast_held_.fill(false);fast_frames_.clear();
            for(auto& slot:fast_slots_)slot.pending=false;
            TouchShutdown();
        }
    }
    // Restart the idle timer from the end of the session, not from its start:
    // a client that just finished must not find the port already closed.
    last_activity_.store(static_cast<long long>(std::time(nullptr)));
}

void Bridge::ServeAdmin(int client) {
    timeval timeout{30, 0};
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));

    UnixStream stream(client);
    if (!Authenticate(stream, root_key_, 30000)) {
        return;
    }
    for (;;) {
        std::vector<uint8_t> request;
        if (!RecvFrame(stream, request, 30000)) {
            break;
        }
        if (request.empty()) {
            break;
        }
        std::vector<uint8_t> response = ProcessRequest(request, stream, Capabilities(),
                                                      [this](const std::vector<uint8_t>& item) {
                                                          return OnAdmin(item);
                                                      });
        if (!SendFrame(stream, response, 30000)) {
            break;
        }
        if (request[0] == opcode::kClose) {
            break;
        }
    }
}

void Bridge::AdminLoop() {
    const std::string path = AdminSocketPath(module_dir_);
    if (path.empty()) {
        return;
    }
    ::unlink(path.c_str());
    int server = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (server < 0) {
        return;
    }
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    if (path.size() >= sizeof(address.sun_path)) {
        close(server);
        return;
    }
    std::memcpy(address.sun_path, path.c_str(), path.size());
    if (bind(server, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0 ||
        listen(server, 4) != 0) {
        close(server);
        return;
    }
    ::chmod(path.c_str(), 0600);

    for (;;) {
        int client = accept4(server, nullptr, nullptr, SOCK_CLOEXEC);
        if (client < 0) {
            if (errno == EINTR) {
                continue;
            }
            break;
        }
        std::thread([this, client] { ServeAdmin(client); }).detach();
    }
    close(server);
    ::unlink(path.c_str());
}

std::string Bridge::InfoReport() {
    BridgeConfig config;
    {
        std::lock_guard<std::mutex> guard(config_mutex_);
        config = config_;
    }
    std::string peers;
    {
        std::lock_guard<std::mutex> guard(peer_mutex_);
        peers = JoinList(peers_);
    }
    const long long uptime =
        static_cast<long long>(std::time(nullptr)) - started_.load(std::memory_order_relaxed);
    const int webui = webui_port_.load();
    std::string text;
    text += "name=" + std::string(kModuleName) + "\n";
    text += "version=" + std::string(kModuleVersion) + "\n";
    text += "id32=" + std::string(kModuleId32) + "\n";
    text += "pid=" + std::to_string(getpid()) + "\n";
    text += "uptime_seconds=" + std::to_string(uptime) + "\n";
    text += "service_port=" + std::to_string(config.service_port) + "\n";
    text += "knock_port=" + std::to_string(config.knock_port) + "\n";
    text += "fast_port=" + std::to_string(fast_port_.load()) + "\n";
    text += "fast_protocol=2\n";
    text += "fast_rx=" + std::to_string(fast_rx_.load()) + "\n";
    text += "fast_ok=" + std::to_string(fast_ok_.load()) + "\n";
    text += "fast_bad=" + std::to_string(fast_bad_.load()) + "\n";
    text += "fast_stale=" + std::to_string(fast_stale_.load()) + "\n";
    text += "fast_rate_hz=240\nfast_buffer_samples=2\nfast_max_records=16\n";
    text += "fast_dropped=" + std::to_string(fast_dropped_.load()) + "\n";
    text += std::string("fast_realtime=") + (fast_realtime_.load() ? "1\n" : "0\n");
    text += "cursor_fast_rx=" + std::to_string(cursor_rx_.load()) + "\n";
    text += "cursor_fast_ok=" + std::to_string(cursor_ok_.load()) + "\n";
    text += "cursor_fast_bad=" + std::to_string(cursor_bad_.load()) + "\n";
    text += "gyro_rx=" + std::to_string(gyro_rx_.load()) + "\n";
    text += "gyro_ok=" + std::to_string(gyro_ok_.load()) + "\n";
    text += "gyro_bad=" + std::to_string(gyro_bad_.load()) + "\n";
    {
        std::string gyro_error;
        const GyroStatus gyro = GyroQuery(gyro_error);
        text += std::string("gyro_installed=") + (gyro.installed ? "1\n" : "0\n");
        text += "gyro_hits=" + std::to_string(gyro.hits) + "\n";
        text += "gyro_updates=" + std::to_string(gyro.updates) + "\n";
        text += "gyro_protocol=2\ngyro_sensor_types=4,16,11,15\ngyro_direct_channel=unsupported\n";
        text += "gyro_target_uid=" + std::to_string(gyro.uid) + "\ngyro_writer=" + gyro.writer + "\n";
        text += std::string("gyro_enabled=") + (gyro.enabled ? "1\n" : "0\n");
    }
    if (fast_port_.load() != 0) {
        text += "fast_token=" + HexToken(fast_token_) + "\n";
    }
    text += "idle_timeout_seconds=" + std::to_string(config.idle_timeout_seconds) + "\n";
    text += "auth_window_seconds=" + std::to_string(config.auth_window_seconds) + "\n";
    text += std::string("always_listen=") + (config.always_listen ? "1\n" : "0\n");
    text += std::string("listening=") + (listening_.load() ? "yes" : "no") + "\n";
    text += "peers=" + peers + "\n";
    text += "sessions=" + std::to_string(sessions_.load()) + "\n";
    text += "sessions_total=" + std::to_string(sessions_total_.load()) + "\n";
    text += "webui_port=" + std::to_string(webui > 0 && PortAccepting(webui) ? webui : 0) + "\n";
    text += "auth=key-each-connection\n";
    text += "admin_socket=" + AdminSocketPath(module_dir_) + "\n";
    text += "memory_backends=kernel,vm_readv,proc_mem\n";
    text += SystemReport();
    std::istringstream overlay(CursorSurfaceReport());
    std::string row;
    while (std::getline(overlay, row)) text += "overlay." + row + "\n";
    return text;
}

std::vector<uint8_t> Bridge::ConfigUpdate(const std::string& body) {
    BridgeConfig next;
    {
        std::lock_guard<std::mutex> guard(config_mutex_);
        next = config_;
    }
    std::string error;
    if (!ApplyConfigUpdate(next, body, error)) {
        return ErrorResponse(error);
    }
    bool ports_changed = false;
    {
        std::lock_guard<std::mutex> guard(config_mutex_);
        ports_changed = next.service_port != config_.service_port ||
                        next.knock_port != config_.knock_port;
    }
    if (!SaveBridgeConfig(module_dir_, next, error)) {
        return ErrorResponse(error);
    }
    {
        std::lock_guard<std::mutex> guard(config_mutex_);
        config_ = next;
    }
    if (ports_changed) {
        generation_.fetch_add(1);
        RestartKnock(next.knock_port);
    }
    std::string text;
    text += "service_port=" + std::to_string(next.service_port) + "\n";
    text += "knock_port=" + std::to_string(next.knock_port) + "\n";
    text += "idle_timeout_seconds=" + std::to_string(next.idle_timeout_seconds) + "\n";
    text += "auth_window_seconds=" + std::to_string(next.auth_window_seconds) + "\n";
    text += std::string("always_listen=") + (next.always_listen ? "1\n" : "0\n");
    text += std::string("applied=") + (ports_changed ? "ports-restarted" : "settings") + "\n";
    return TextResponse(text);
}

std::vector<uint8_t> Bridge::WebUiStart() {
    const int current = webui_port_.load();
    if (current > 0 && PortAccepting(current)) {
        return TextResponse("webui_port=" + std::to_string(current) + "\nnote=already running\n");
    }
    int port = 0;
    const KModErr result = skroot_env::features::web_ui::start_module_web_ui_server_async(
        root_key_.c_str(), kModuleId32, port);
    if (result != KModErr::OK) {
        return ErrorResponse("web ui start failed: err=" +
                             std::to_string(static_cast<long long>(result)));
    }
    remove(WebUiBeatPath(module_dir_).c_str());
    webui_started_.store(static_cast<long long>(std::time(nullptr)), std::memory_order_relaxed);
    webui_port_.store(port);
    return TextResponse("webui_port=" + std::to_string(port) + "\n");
}

std::vector<uint8_t> Bridge::WebUiStop() {
    const bool signalled = StopWebUiLoader();
    webui_port_.store(0);
    return TextResponse(signalled ? "webui=stopped\n" : "webui=not running\n");
}

// Seconds since the page last beat, or -1 when no beat file exists yet.
long long Bridge::WebUiBeatAge() const {
    struct stat info;
    if (stat(WebUiBeatPath(module_dir_).c_str(), &info) != 0) {
        return -1;
    }
    return static_cast<long long>(std::time(nullptr)) - static_cast<long long>(info.st_mtime);
}

// The page is a foreground tool. It writes skpdb.webui.beat once a second while
// it is visible and stops the moment the tab is hidden, so a beat age beyond
// kWebUiHeartbeatSeconds means nobody is looking at the page any more. Before
// the first beat the longer grace applies, otherwise the port would vanish
// while the operator is still reaching for the printed link.
void Bridge::WebUiWatchLoop() {
    while (!stopping_.load()) {
        usleep(250 * 1000);
        if (webui_port_.load() <= 0) {
            continue;
        }
        const long long age = WebUiBeatAge();
        const bool lapsed =
            age >= 0 ? age > kWebUiHeartbeatSeconds
                     : static_cast<long long>(std::time(nullptr)) -
                               webui_started_.load() > kWebUiFirstBeatGraceSeconds;
        if (lapsed) {
            StopWebUiLoader();
            webui_port_.store(0);
        }
    }
}

std::vector<uint8_t> Bridge::OnAdmin(const std::vector<uint8_t>& request) {
    if (request.size() < 2) {
        return ErrorResponse("admin request needs an action");
    }
    const std::string body(request.begin() + 2, request.end());
    switch (request[1]) {
        case kAdminInfo:
            return TextResponse(InfoReport());
        case kAdminConfig:
            return ConfigUpdate(body);
        case kAdminWebUiStart:
            return WebUiStart();
        case kAdminWebUiStop:
            return WebUiStop();
        case kAdminTouchStream:
            return TouchStreamRequest(request);
        case kAdminDiagnostics:
            if (!body.empty()) return ErrorResponse("diagnostics takes no arguments");
            return TextResponse(CapabilityDiagnostics());
        default:
            return ErrorResponse("unknown admin action");
    }
}

std::vector<uint8_t> Bridge::TouchStreamRequest(const std::vector<uint8_t>& request) {
    // [B][5][stream u32][generation u32][count u8][records 12 bytes each]
    if (request.size() < 11) return ErrorResponse("touch stream request is too short");
    const uint32_t stream = GetU32(request.data() + 2);
    const uint32_t generation = GetU32(request.data() + 6);
    const uint8_t count = request[10];
    if (!stream || !generation || !count || count > 32 || request.size() != 11u + count * 12u)
        return ErrorResponse("invalid touch stream frame");
    std::vector<TouchBatchItem> items;
    for (uint8_t i = 0; i < count; ++i) {
        const uint8_t* p = request.data() + 11 + i * 12;
        const uint16_t slot = static_cast<uint16_t>((p[2] << 8) | p[3]);
        if (slot >= fast_slots_.size() || p[0] < kTouchDown || p[0] > kTouchUp)
            return ErrorResponse("invalid touch stream record");
        items.push_back({p[0], slot, GetU32(p + 4), GetU32(p + 8)});
    }
    const auto queued = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> apply(fast_apply_mutex_);
    const auto entered = std::chrono::steady_clock::now();
    {
        std::lock_guard<std::mutex> guard(fast_mutex_);
        if (fast_stream_ == stream && static_cast<int32_t>(generation - fast_generation_) <= 0)
            return ErrorResponse("stale touch generation");
    }
    const TouchResult result = TouchBatch(items);
    const auto injected = std::chrono::steady_clock::now();
    if (!result.ok) return ErrorResponse(result.error);
    {
        std::lock_guard<std::mutex> guard(fast_mutex_);
        if (fast_stream_ != stream) fast_held_.fill(false);
        fast_stream_ = stream;
        fast_generation_ = generation;
        fast_sequence_ = 0;
        for (auto& slot : fast_slots_) slot.pending = false;
        fast_frames_.clear();
        for (const auto& item : items) {
            if (item.action == kTouchDown) fast_held_[item.slot] = true;
            if (item.action == kTouchUp) fast_held_[item.slot] = false;
        }
        // A stopped stream releases ownership, preserving SKF1 compatibility.
        if (std::none_of(fast_held_.begin(), fast_held_.end(), [](bool down) { return down; }))
            fast_stream_ = 0;
    }
    return TextResponse(result.report + "queue_us=" + std::to_string(
        std::chrono::duration_cast<std::chrono::microseconds>(entered - queued).count()) +
        "\ninject_us=" + std::to_string(
        std::chrono::duration_cast<std::chrono::microseconds>(injected - entered).count()) + "\n");
}

bool Bridge::StartFast(uint16_t port) {
    if (port == 0) {
        return false;
    }
    const int fd = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return false;
    }
    int one = 1;
    (void)setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    int buffer = 512 * 1024;
    (void)setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &buffer, sizeof(buffer));
    timeval timeout{0, 250000};
    (void)setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
        close(fd);
        return false;
    }
    fast_fd_.store(fd);
    fast_port_.store(port);
    std::thread([this] { FastLoop(); }).detach();
    std::thread([this] { FastWorkerLoop(); }).detach();
    std::thread([this] { GyroWriterLoop(); }).detach();
    return true;
}

void Bridge::GyroWriterLoop() {
    uint64_t applied = 0;
    while (!stopping_.load()) {
        float rate[3] = {0.0f, 0.0f, 0.0f};
        uint64_t sequence = 0;
        uint32_t stream = 0, packetSequence = 0; uint64_t received = 0;
        {
            std::lock_guard<std::mutex> guard(gyro_rate_mutex_);
            sequence = gyro_rate_seq_;
            rate[0] = gyro_rate_[0];
            rate[1] = gyro_rate_[1];
            rate[2] = gyro_rate_[2];
            stream = gyro_stream_; packetSequence = gyro_packet_sequence_; received = gyro_received_ns_;
        }
        if (sequence != applied) {
            std::string error;
            if (GyroApplyPacket(stream, packetSequence, rate[0], rate[1], rate[2], received, error)) {
                applied = sequence; gyro_ok_.fetch_add(1);
            } else {
                applied = sequence; gyro_bad_.fetch_add(1);
            }
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

void Bridge::FastLoop() {
    const int fd = fast_fd_.load();
    if (fd < 0) {
        return;
    }
    uint8_t packet[512];
    while (!stopping_.load()) {
        sockaddr_in source{};
        socklen_t source_len = sizeof(source);
        const ssize_t size = recvfrom(fd, packet, sizeof(packet), 0,
                                      reinterpret_cast<sockaddr*>(&source), &source_len);
        if (size < 0) {
            continue;
        }
        if (size >= 4 && std::memcmp(packet, "SKC1", 4) == 0) {
            if (size != 32 || !TokenEquals(packet + 4, fast_token_) ||
                !PeerAllowed(source.sin_addr.s_addr)) {
                cursor_bad_.fetch_add(1);
                continue;
            }
            cursor_rx_.fetch_add(1);
            const uint16_t x = static_cast<uint16_t>(
                (static_cast<uint16_t>(packet[24]) << 8) | packet[25]);
            const uint16_t y = static_cast<uint16_t>(
                (static_cast<uint16_t>(packet[26]) << 8) | packet[27]);
            if (CursorSurfaceStarted()) {
                CursorSurfaceMove(x, y);
                cursor_ok_.fetch_add(1);
            } else {
                cursor_bad_.fetch_add(1);
            }
            continue;
        }
        // [SKG1][token 16][sequence u32][rx f32][ry f32][rz f32]: one kernel
        // write per sample, no TCP round trip. A repeated sequence is dropped so
        // a duplicated datagram cannot double the injected rate.
        if (size >= 4 && std::memcmp(packet, "SKG2", 4) == 0) {
            if (size != 40 || !TokenEquals(packet + 4, fast_token_) ||
                !PeerAllowed(source.sin_addr.s_addr)) {
                gyro_bad_.fetch_add(1);
                continue;
            }
            gyro_rx_.fetch_add(1);
            const uint32_t stream = GetU32(packet + 20), sequence = GetU32(packet + 24);
            auto read_float = [](const uint8_t* data) {
                const uint32_t bits = GetU32(data);
                float value = 0.0f;
                std::memcpy(&value, &bits, sizeof(value));
                return value;
            };
            const float rx=read_float(packet+28),ry=read_float(packet+32),rz=read_float(packet+36);
            if(stream==0||stream!=GyroStream()||!gyro_protocol::Rates(rx,ry,rz)){gyro_bad_.fetch_add(1);continue;}
            {
                std::lock_guard<std::mutex> guard(gyro_rate_mutex_);
                if(stream==gyro_stream_&&!gyro_protocol::Newer(sequence,gyro_packet_sequence_)){gyro_bad_.fetch_add(1);continue;}
                gyro_rate_[0] = rx;
                gyro_rate_[1] = ry;
                gyro_rate_[2] = rz;
                gyro_stream_ = stream; gyro_packet_sequence_ = sequence;
                timespec received{};clock_gettime(CLOCK_BOOTTIME, &received);
                gyro_received_ns_ = uint64_t(received.tv_sec) * 1000000000ull + received.tv_nsec;
                ++gyro_rate_seq_;
            }
            continue;
        }
        const bool sequenced = size >= 4 && std::memcmp(packet, "SKF2", 4) == 0;
        const size_t header = sequenced ? 33 : 21;
        if (size < static_cast<ssize_t>(header) ||
            (!sequenced && std::memcmp(packet, "SKF1", 4) != 0) ||
            !TokenEquals(packet + 4, fast_token_) || !PeerAllowed(source.sin_addr.s_addr)) {
            fast_bad_.fetch_add(1);
            continue;
        }
        const uint8_t count = packet[header - 1];
        if (count == 0 || count > (sequenced ? 16 : 8) || size != static_cast<ssize_t>(header + count * 12)) {
            fast_bad_.fetch_add(1);
            continue;
        }
        std::vector<TouchBatchItem> items;
        bool valid = true;
        for (uint8_t i = 0; i < count; ++i) {
            const uint8_t* record = packet + header + static_cast<size_t>(i) * 12;
            const uint16_t slot = static_cast<uint16_t>((record[2] << 8) | record[3]);
            if (record[0] != kTouchMove || slot >= fast_slots_.size()) {
                valid = false;
                break;
            }
            items.push_back({kTouchMove, slot, GetU32(record + 4), GetU32(record + 8)});
        }
        if (!valid) { fast_bad_.fetch_add(1); continue; }
        fast_rx_.fetch_add(1);
        std::lock_guard<std::mutex> guard(fast_mutex_);
        if (sequenced) {
            const uint32_t stream = GetU32(packet + 20);
            const uint32_t generation = GetU32(packet + 24);
            const uint32_t sequence = GetU32(packet + 28);
            if (!stream || stream != fast_stream_ || generation != fast_generation_ ||
                static_cast<int32_t>(sequence - fast_sequence_) <= 0) {
                fast_stale_.fetch_add(1);
                continue;
            }
            fast_sequence_ = sequence;
        } else if (fast_stream_ != 0) {
            fast_stale_.fetch_add(1);
            continue;
        }
        if (sequenced) {
            items.erase(std::remove_if(items.begin(), items.end(), [this](const TouchBatchItem& item) {
                return !fast_held_[item.slot];
            }), items.end());
            if (!items.empty()) {
                timespec now{};
                clock_gettime(CLOCK_MONOTONIC, &now);
                if (fast_frames_.size() >= 8) {
                    fast_frames_.pop_front();
                    fast_dropped_.fetch_add(1);
                }
                fast_frames_.push_back({fast_stream_, fast_generation_,
                    now.tv_sec * 1000000000LL + now.tv_nsec, std::move(items)});
            }
        } else {
            for (const auto& item : items) {
                fast_slots_[item.slot].x = item.x;
                fast_slots_[item.slot].y = item.y;
                fast_slots_[item.slot].pending = true;
            }
        }
    }
}

void Bridge::FastWorkerLoop() {
    // Android launchers can pass down millisecond timer slack. This thread
    // owns the input cadence, so do not coalesce its absolute wakeups.
    (void)prctl(PR_SET_TIMERSLACK, 1UL, 0UL, 0UL, 0UL);
    (void)setpriority(PRIO_PROCESS, 0, -15);
    // Only this short, sleeping input worker gets the lowest FIFO priority;
    // network/shell/UI threads keep their normal scheduling policy.
    sched_param priority{};
    priority.sched_priority = 1;
    fast_realtime_.store(pthread_setschedparam(pthread_self(), SCHED_FIFO, &priority) == 0);
    const auto now_ns = [] {
        timespec t{};
        clock_gettime(CLOCK_MONOTONIC, &t);
        return t.tv_sec * 1000000000LL + t.tv_nsec;
    };
    int64_t epoch = now_ns();
    int64_t tick = 0;
    bool primed = false;
    uint32_t playing_stream = 0, playing_generation = 0;
    while (!stopping_.load()) {
        int64_t deadline = epoch + ++tick * 1000000000LL / 240;
        timespec due{deadline / 1000000000LL, deadline % 1000000000LL};
        int wait_result;
        do { wait_result = clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &due, nullptr); }
        while (wait_result == EINTR);
        if (wait_result != 0) {
            // A timer failure must never create a busy real-time thread.
            std::this_thread::sleep_for(std::chrono::milliseconds(4));
            epoch = now_ns();
            tick = 0;
            continue;
        }
        const int64_t now = now_ns();
        if (now - deadline > 1000000000LL / 240) {
            // Never replay missed output periods in an injection burst.
            epoch = now;
            tick = 0;
        }
        TouchHeartbeat();
        std::vector<TouchBatchItem> items;
        uint32_t stream, generation;
        {
            std::lock_guard<std::mutex> guard(fast_mutex_);
            stream = fast_stream_;
            generation = fast_generation_;
            if (stream != playing_stream || generation != playing_generation) {
                primed = false;
                playing_stream = stream;
                playing_generation = generation;
            }
            if (stream != 0) {
                if (fast_frames_.empty()) { primed = false; continue; }
                if (!primed && now - fast_frames_.front().arrived_ns < 2 * 1000000000LL / 240)
                    continue;
                primed = true;
                items = std::move(fast_frames_.front().items);
                fast_frames_.pop_front();
            } else {
                primed = false;
                for (size_t slot = 0; slot < fast_slots_.size(); ++slot) {
                    if (!fast_slots_[slot].pending) continue;
                    items.push_back({kTouchMove, static_cast<uint16_t>(slot),
                        fast_slots_[slot].x, fast_slots_[slot].y});
                    fast_slots_[slot].pending = false;
                }
            }
        }
        if (items.empty()) continue;
        std::lock_guard<std::mutex> apply(fast_apply_mutex_);
        {
            std::lock_guard<std::mutex> guard(fast_mutex_);
            if (stream != fast_stream_ || generation != fast_generation_) {
                fast_stale_.fetch_add(1);
                continue;
            }
        }
        const TouchResult result = TouchBatch(items);
        if (result.ok) fast_ok_.fetch_add(items.size());
        else fast_bad_.fetch_add(1);
    }
}

void Bridge::StopFast() {
    const int fd = fast_fd_.exchange(-1);
    if (fd >= 0) {
        close(fd);
    }
    fast_port_.store(0);
}

void Bridge::Run() {
    (void)setpriority(PRIO_PROCESS, 0, -10);
    signal(SIGPIPE, SIG_IGN);
    // A diagnostic loader may inherit SIGCHLD=SIG_IGN from its launcher.
    // ExecuteCommand owns waitpid() for its children and must be able to reap.
    signal(SIGCHLD, SIG_DFL);
    started_.store(static_cast<long long>(std::time(nullptr)), std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> guard(config_mutex_);
        config_ = LoadBridgeConfig(module_dir_);
    }
    const uint16_t knock_port = [this] {
        std::lock_guard<std::mutex> guard(config_mutex_);
        return config_.knock_port;
    }();
    const uint16_t fast_port = [this] {
        std::lock_guard<std::mutex> guard(config_mutex_);
        return static_cast<uint16_t>(config_.service_port + 3);
    }();
    FillFastToken(fast_token_);
    (void)StartFast(fast_port);

    std::thread([this] { AdminLoop(); }).detach();
    std::thread([this] { GateLoop(); }).detach();
    std::thread([this] { WebUiWatchLoop(); }).detach();
    std::thread([this] { HealthLoop(); }).detach();

    // The module starts before the network is up, so the raw socket is retried
    // until it binds.
    while (!stopping_.load()) {
        if (StartKnock(knock_port)) {
            break;
        }
        sleep(5);
    }
    while (!stopping_.load()) {
        sleep(30);
    }
}

}  // namespace

void RunBridge(const std::string& root_key, const std::string& module_dir) {
    Bridge bridge(root_key, module_dir);
    bridge.Run();
}

}  // namespace skpdb::wireless
