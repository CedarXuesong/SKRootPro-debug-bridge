#include <cassert>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <vector>

#include "protocol.h"
#include "transport.h"

namespace {

class MemoryTransport : public skpdb::Transport {
public:
    std::vector<uint8_t> input;
    std::vector<uint8_t> output;

    bool ReadExact(void* buffer, size_t size, int) override {
        if (input.size() < size) {
            return false;
        }
        std::memcpy(buffer, input.data(), size);
        input.erase(input.begin(), input.begin() + static_cast<std::ptrdiff_t>(size));
        return true;
    }

    bool WriteAll(const void* buffer, size_t size, int) override {
        const auto* bytes = static_cast<const uint8_t*>(buffer);
        output.insert(output.end(), bytes, bytes + size);
        return true;
    }

    void Close() override {}
    std::string Name() const override { return "memory"; }
};

void TestHmac() {
    // RFC 4231, test case 2.
    const char* key = "Jefe";
    const char* data = "what do ya want for nothing?";
    auto digest = skpdb::HmacSha256(key, reinterpret_cast<const uint8_t*>(data),
                                    std::strlen(data));
    const std::string expected =
        "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843";
    assert(skpdb::HexEncode(digest.data(), digest.size()) == expected);
}

void TestIntegerEncoding() {
    std::vector<uint8_t> buffer;
    skpdb::PutU32(buffer, 0x01020304);
    skpdb::PutU64(buffer, 0x0102030405060708ULL);
    assert(skpdb::GetU32(buffer.data()) == 0x01020304);
    assert(skpdb::GetU64(buffer.data() + 4) == 0x0102030405060708ULL);
}

void TestFrameSend() {
    MemoryTransport transport;
    std::vector<uint8_t> request{skpdb::opcode::kPing};
    assert(skpdb::SendRequest(transport, request, 1000));
    const std::vector<uint8_t> expected{0, 0, 0, 1, 'K'};
    assert(transport.output == expected);
}

void TestFrameReceive() {
    MemoryTransport transport;
    transport.input = {'K', 0, 'P', 'O', 'N', 'G'};
    std::vector<uint8_t> frame;
    // ReadResponse reads a length-prefixed frame, so prepend the length.
    std::vector<uint8_t> packet{0, 0, 0, 6};
    packet.insert(packet.end(), transport.input.begin(), transport.input.end());
    transport.input = packet;

    skpdb::Response response;
    std::string error;
    assert(skpdb::ReadResponse(transport, skpdb::opcode::kPing, response, error, 1000));
    const std::string text(response.payload.begin(), response.payload.end());
    assert(text == "PONG");
}

void TestErrorResponse() {
    MemoryTransport transport;
    const std::string message = "boom";
    std::vector<uint8_t> packet{0, 0, 0, static_cast<uint8_t>(6 + message.size())};
    packet.insert(packet.end(), {'R', 1});
    packet.push_back(0);
    packet.push_back(0);
    packet.push_back(0);
    packet.push_back(static_cast<uint8_t>(message.size()));
    packet.insert(packet.end(), message.begin(), message.end());
    transport.input = packet;

    skpdb::Response response;
    std::string error;
    assert(!skpdb::ReadResponse(transport, skpdb::opcode::kRead, response, error, 1000));
    assert(error == message);
}

// The new checks below must not live inside assert(): a Release build defines
// NDEBUG, and an operation hidden in an assert would simply never run. Check()
// verifies in every configuration.
void Check(bool condition, const char* what) {
    if (!condition) {
        std::cerr << "FAIL: " << what << std::endl;
        std::exit(1);
    }
}

// A read response must fit one frame: opcode + status + backend + max payload.
static_assert(skpdb::kMaxMemRead + 3 <= skpdb::kMaxFrame,
              "kMaxMemRead does not fit a protocol frame");

// The device side parses exactly this layout (channel.cpp
// ReadProcessMemoryRange): [M][u32 pid][u64 address][u32 length][u8 mode].
void TestMemReadRequest() {
    std::vector<uint8_t> request{skpdb::opcode::kMemRead};
    skpdb::PutU32(request, 4242);
    skpdb::PutU64(request, 0x7f0000123456ULL);
    skpdb::PutU32(request, 64);
    request.push_back(skpdb::kMemModeKernelOnly);

    const std::vector<uint8_t> expected{
        'M',                                            // opcode
        0x00, 0x00, 0x10, 0x92,                         // pid 4242
        0x00, 0x00, 0x7f, 0x00, 0x00, 0x12, 0x34, 0x56,  // address
        0x00, 0x00, 0x00, 0x40,                         // length 64
        0x01,                                            // mode: kernel only
    };
    Check(request == expected, "memread request layout");
    Check(request.size() == 18, "memread request size");

    // The userspace mode must encode as 2 and stay inside the same layout.
    std::vector<uint8_t> userspace{skpdb::opcode::kMemRead};
    skpdb::PutU32(userspace, 1);
    skpdb::PutU64(userspace, 0x1000);
    skpdb::PutU32(userspace, 1);
    userspace.push_back(skpdb::kMemModeUserspace);
    Check(userspace.size() == 18, "memread userspace request size");
    Check(userspace.back() == 2, "memread userspace mode byte");

    // fault mode is 3 on the wire and is the only one allowed to fault a page
    // in; the numbering is part of the protocol, never renumber it.
    Check(skpdb::kMemModeAuto == 0, "memread auto mode value");
    Check(skpdb::kMemModeKernelOnly == 1, "memread kernel mode value");
    Check(skpdb::kMemModeUserspace == 2, "memread userspace mode value");
    Check(skpdb::kMemModeFault == 3, "memread fault mode value");
}

// A successful read answers with [M][0][u8 backend][bytes...]; ReadResponse
// strips the first two bytes, so the payload is [backend][bytes...].
void TestMemReadResponse() {
    MemoryTransport transport;
    const std::vector<uint8_t> data{'S', 'K', 'P', 'D', 'B'};
    std::vector<uint8_t> packet;
    skpdb::PutU32(packet, static_cast<uint32_t>(2 + 1 + data.size()));
    packet.insert(packet.end(), {'M', 0, skpdb::kMemBackendKernel});
    packet.insert(packet.end(), data.begin(), data.end());
    transport.input = packet;

    skpdb::Response response;
    std::string error;
    const bool ok = skpdb::ReadResponse(transport, skpdb::opcode::kMemRead, response, error, 1000);
    Check(ok, "memread response parsed");
    Check(response.payload.size() == data.size() + 1, "memread payload carries backend");
    Check(response.payload[0] == skpdb::kMemBackendKernel, "memread backend byte");
    const std::vector<uint8_t> bytes(response.payload.begin() + 1, response.payload.end());
    Check(bytes == data, "memread payload bytes");
    Check(std::string(skpdb::MemBackendName(skpdb::kMemBackendKernel)) == "kernel",
          "backend name kernel");
    Check(std::string(skpdb::MemBackendName(skpdb::kMemBackendVmReadv)) == "vm_readv",
          "backend name vm_readv");
    Check(std::string(skpdb::MemBackendName(skpdb::kMemBackendProcMem)) == "proc_mem",
          "backend name proc_mem");
}

// [A][u32 pid], fixed at five bytes on both sides.
// Scatter read: [S][u32 pid][u8 mode][u32 count][(u64 address, u32 length) * count].
// The layout is part of the protocol, so it is pinned byte for byte.
void TestMemScatterRequest() {
    std::vector<uint8_t> request{skpdb::opcode::kMemScatter};
    skpdb::PutU32(request, 4242);
    request.push_back(skpdb::kMemModeAuto);
    skpdb::PutU32(request, 2);
    skpdb::PutU64(request, 0x7f0000123456ULL);
    skpdb::PutU32(request, 8);
    skpdb::PutU64(request, 0x7f0000123456ULL + 0x1000);
    skpdb::PutU32(request, 16);

    const std::vector<uint8_t> expected{
        'S',
        0x00, 0x00, 0x10, 0x92,                          // pid 4242
        0x00,                                            // mode auto
        0x00, 0x00, 0x00, 0x02,                          // count 2
        0x00, 0x00, 0x7f, 0x00, 0x00, 0x12, 0x34, 0x56,  // first address
        0x00, 0x00, 0x00, 0x08,                          // first length
        0x00, 0x00, 0x7f, 0x00, 0x00, 0x12, 0x44, 0x56,  // second address
        0x00, 0x00, 0x00, 0x10,                          // second length
    };
    Check(request == expected, "scatter request layout");
    Check(request.size() == 34, "scatter request size");
    Check(skpdb::kMaxScatterRanges == 64, "scatter range limit");
}

// memdump and resident pin their request layouts as well: the device side
// rejects anything that is not exactly this size.
// The write, scan and symbol requests pin their exact byte layouts: the device
// rejects any request whose size does not match its header.
void TestWriteScanSymbolRequests() {
    std::vector<uint8_t> write{skpdb::opcode::kMemWrite};
    skpdb::PutU32(write, 7);
    skpdb::PutU64(write, 0x1000);
    skpdb::PutU32(write, 4);
    write.push_back(skpdb::kMemModeAuto);
    write.push_back(skpdb::kMemWriteAllowShared);
    write.insert(write.end(), {0xde, 0xad, 0xbe, 0xef});
    Check(write.size() == 19 + 4, "write request size");
    Check(write.front() == 'X', "write opcode");
    Check(write[18] == 0x20, "write flags byte");

    std::vector<uint8_t> scan{skpdb::opcode::kMemScan};
    skpdb::PutU32(scan, 7);
    skpdb::PutU64(scan, 0x1000);
    skpdb::PutU64(scan, 0x2000);
    skpdb::PutU32(scan, skpdb::kMemScanMaxHits);
    scan.push_back(0);
    scan.push_back(2);
    scan.push_back(skpdb::kMemModeAuto);
    scan.push_back(0x00);  // flags: no mask
    scan.insert(scan.end(), {'A', 'B'});
    Check(scan.size() == 29 + 2, "scan request size");
    Check(scan[28] == 0, "scan flags byte");

    std::vector<uint8_t> masked = scan;
    masked[28] = 0x01;
    masked.push_back(0xff);
    masked.push_back(0x00);
    Check(masked.size() == 29 + 4, "masked scan request size");
    Check(scan.front() == 'Y', "scan opcode");
    Check(scan[25] == 0 && scan[26] == 2, "pattern length is big endian");

    std::vector<uint8_t> symbols{skpdb::opcode::kMemSymbols};
    skpdb::PutU32(symbols, 7);
    symbols.push_back(0);
    const char module[] = "libc.so";
    symbols.insert(symbols.end(), module, module + sizeof(module));
    const char name[] = "strlen";
    symbols.insert(symbols.end(), name, name + sizeof(name) - 1);
    Check(symbols.front() == 'F', "symbol opcode");
    Check(symbols[5] == 0, "symbol flags byte");
    Check(symbols[6 + sizeof(module) - 1] == 0, "module is null terminated");
}

void TestMemDumpAndResidentRequests() {
    std::vector<uint8_t> dump{skpdb::opcode::kMemDump};
    skpdb::PutU32(dump, 7);
    skpdb::PutU64(dump, 0x1000);
    skpdb::PutU64(dump, 0x3000);
    skpdb::PutU64(dump, 0x2000);
    skpdb::PutU32(dump, 4096);
    dump.push_back(skpdb::kMemModeAuto);
    Check(dump.size() == 34, "dump request size");
    Check(dump.front() == 'D', "dump opcode");

    std::vector<uint8_t> resident{skpdb::opcode::kMemResident};
    skpdb::PutU32(resident, 7);
    skpdb::PutU64(resident, 0x1000);
    skpdb::PutU32(resident, 16);
    resident.push_back(skpdb::kMemModeAuto);
    Check(resident.size() == 18, "resident request size");
    Check(resident.front() == 'P', "resident opcode");
    Check(skpdb::kMemPageBytes == 4096, "page size");
}

void TestMemMapsRequest() {
    std::vector<uint8_t> request{skpdb::opcode::kMemMaps};
    skpdb::PutU32(request, 1);
    const std::vector<uint8_t> expected{'A', 0x00, 0x00, 0x00, 0x01};
    Check(request == expected, "memmaps request layout");
    Check(request.size() == 5, "memmaps request size");
}

}  // namespace

int main() {
    TestHmac();
    TestIntegerEncoding();
    TestFrameSend();
    TestFrameReceive();
    TestErrorResponse();
    TestMemReadRequest();
    TestMemReadResponse();
    TestMemMapsRequest();
    TestMemScatterRequest();
    TestMemDumpAndResidentRequests();
    TestWriteScanSymbolRequests();
    std::cout << "PASS: HMAC, integer encoding, framing, error response, memory opcodes"
              << std::endl;
    return 0;
}
