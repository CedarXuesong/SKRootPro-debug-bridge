// Isolated process test. Never opens or patches system_server.
#include "../src/input/gyro.cpp"
#include <sys/mman.h>
struct TestReceiver {
    unsigned char pad[0x38];
    uint32_t uid;
    float result[3];
};
extern "C" void gyro_test_body(TestReceiver *target, const skpdb::gyro_payload::Event *e, size_t n, void *,
                               void *) {
    if (n) {
        target->result[0] = e[0].data[0];
        target->result[1] = e[0].data[1];
        target->result[2] = e[0].data[2];
    }
}
extern "C" void gyro_test_entry(TestReceiver *, const skpdb::gyro_payload::Event *, size_t, void *, void *);
asm(".text\n.p2align 4\n.global gyro_test_entry\n.type gyro_test_entry,%function\ngyro_test_entry:\n"
    "paciasp\nsub sp, sp, #0x80\nstp x29,x30,[sp,#0x60]\nbl gyro_test_body\nldp x29,x30,[sp,#0x60]\nadd "
    "sp,sp,#0x80\nautiasp\nret\n");
int main() {
    using namespace skpdb;
    using namespace skpdb::gyro_payload;
    auto page = size_t(sysconf(_SC_PAGESIZE));
    void *code = mmap(reinterpret_cast<void *>(
                          (reinterpret_cast<uint64_t>(gyro_test_entry) & ~(uint64_t(page) - 1)) + 0x100000),
                      page, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (code == MAP_FAILED)
        return 2;
    State state{};
    state.magic = kMagic;
    state.version = kVersion;
    state.control.sequence = 2;
    state.control.enabled = 1;
    state.control.uid = 2000;
    state.control.stream = 3;
    state.control.rate[0] = .75f;
    state.control.rate[1] = -.25f;
    state.control.updated = Now();
    state.control.epoch = 1;
    uint32_t head[2];
    std::memcpy(head, reinterpret_cast<void *>(gyro_test_entry), 8);
    std::string error;
    uint64_t cave = reinterpret_cast<uint64_t>(code), entry = reinterpret_cast<uint64_t>(gyro_test_entry);
    auto bytes =
        Wrapper(cave, reinterpret_cast<uint64_t>(&state), entry + 8, 0x38, head[1], head[0], 512, error);
    if (bytes.empty() || bytes.size() > 512) {
        std::printf("wrapper fail %s\n", error.c_str());
        return 3;
    }
    bytes.resize(512);
    bytes.insert(bytes.end(), std::begin(kGyroPayload), std::end(kGyroPayload));
    std::memcpy(code, bytes.data(), bytes.size());
    __builtin___clear_cache(static_cast<char *>(code), static_cast<char *>(code) + bytes.size());
    if (mprotect(code, page, PROT_READ | PROT_EXEC))
        return 4;
    auto originalPage = entry & ~(uint64_t(page) - 1);
    if (mprotect(reinterpret_cast<void *>(originalPage), page, PROT_READ | PROT_WRITE | PROT_EXEC))
        return 5;
    uint32_t branch = 0;
    if (!gyro_layout::Branch(entry + 4, cave, branch)) {
        std::puts("test code mapping out of range");
        return 77;
    }
    std::memcpy(reinterpret_cast<void *>(entry + 4), &branch, 4);
    __builtin___clear_cache(reinterpret_cast<char *>(entry), reinterpret_cast<char *>(entry + 8));
    TestReceiver target{};
    target.uid = 2000;
    state.control.updated = Now();
    Event e{};
    e.version = 104;
    e.type = 4;
    e.timestamp = Now();
    gyro_test_entry(&target, &e, 1, nullptr, nullptr);
    if (target.result[0] != .75f || target.result[1] != -.25f || e.data[0] != 0) {
        std::printf("bad transform %.4f %.4f source %.4f\n", target.result[0], target.result[1], e.data[0]);
        return 6;
    }
    target.uid = 2001;
    gyro_test_entry(&target, &e, 1, nullptr, nullptr);
    if (target.result[0] != 0)
        return 7;
    std::memcpy(reinterpret_cast<void *>(entry + 4), &head[1], 4);
    __builtin___clear_cache(reinterpret_cast<char *>(entry), reinterpret_cast<char *>(entry + 8));
    std::puts("PASS real AArch64 private-copy wrapper, actual displaced stack frame, PAC return and UID "
              "pass-through");
    return 0;
}
