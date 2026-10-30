#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace skpdb {

class Stream;

// Request and response payloads start with one opcode byte. Responses echo
// the opcode; the second byte is 0 on success and non-zero on failure, in
// which case a length-prefixed error message follows.
namespace opcode {

constexpr uint8_t kExec = 'E';
constexpr uint8_t kRead = 'R';
constexpr uint8_t kWrite = 'W';
constexpr uint8_t kList = 'L';
constexpr uint8_t kStat = 'T';
constexpr uint8_t kPing = 'K';
constexpr uint8_t kVersion = 'V';
constexpr uint8_t kClose = 'Q';
// Memory access. 'M' reads another process' address space without ptrace,
// 'A' returns its /proc/<pid>/maps listing and 'G' reports backend health.
constexpr uint8_t kMemRead = 'M';
constexpr uint8_t kMemMaps = 'A';
constexpr uint8_t kMemInfo = 'G';
// 'S' reads several ranges in one request. One kernel entry, one mmap_lock and
// one round trip instead of N: a pointer chain or a struct walk stops costing
// an RTT per field. Ranges are independent, so a hole is reported per range.
constexpr uint8_t kMemScatter = 'S';
// A write, a symbol lookup and a pattern scan. Each is answered from a handler
// below; none of them introduces a second way to reach into the target.
constexpr uint8_t kMemWrite = 'X';
constexpr uint8_t kMemSymbols = 'F';
constexpr uint8_t kMemScan = 'Y';
// 'D' streams the readable runs of a range, 'P' reports which pages are
// resident. Both refuse to fault pages in, like 'M' and 'S'.
constexpr uint8_t kMemDump = 'D';
constexpr uint8_t kMemResident = 'P';
// Bridge administration. Requests are `[B][action][arguments]`; the handler is
// supplied by the transport because it needs the listener state.
constexpr uint8_t kAdmin = 'B';
// Touch injection into the real touchscreen input device. Requests are
// `[I][action][flags][slot u16][x u32][y u32][x2 u32][y2 u32][duration u32]`;
// the answer is a key=value report, or an error.
constexpr uint8_t kTouch = 'I';
// Batched touch frames: `[J][count u8]` followed by count records of
// `[action u8][flags u8][slot u16][x u32][y u32]`. All frames are emitted in
// one kernel entry, which amortizes the shellcode switch across a sample tick.
constexpr uint8_t kTouchBatch = 'J';
// Trusted CPU screen overlays. C/0 probes, C/1 sets x/y/visible, C/2 hides
// the cursor without stopping the independent HUD. C/3 is a 20-byte request:
// C,3,enabled,rotation,width:u16,height:u16,active:u16,capacity:u16,pending:u16,
// samplingHz*10:u16,RTTms*10:u16,mode:u8,inputActive:u8 (big-endian).
// Modes 0/1/2 = mouse/view/held-look. HUD ACK follows renderer submission;
// heartbeat loss hides both panels after six seconds.
constexpr uint8_t kCursor = 'C';
// Framework gyro injection. Requests are `[Z][action]`, plus three big-endian
// IEEE floats for the rate action. The hook itself lives in system_server and is
// installed on demand; see input/gyro.h.
constexpr uint8_t kGyro = 'Z';

}  // namespace opcode

// Handles an administration request. Returns the complete response payload.
using AdminRequestHandler = std::function<std::vector<uint8_t>(const std::vector<uint8_t>&)>;

// Command output is collected in memory before it is framed, so the capture
// is bounded independently of kMaxFrame.
constexpr size_t kMaxCommandOutput = 8 * 1024 * 1024;

// Handles one request. `stream` is used only to observe liveness while a
// child process is running. `admin` may be empty on transports without
// administration support, in which case `[B]` is rejected.
std::vector<uint8_t> ProcessRequest(const std::vector<uint8_t>& request, const Stream& stream,
                                    const std::string& capabilities,
                                    const AdminRequestHandler& admin = AdminRequestHandler());

}  // namespace skpdb
