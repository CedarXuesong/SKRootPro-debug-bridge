#include "../src/input/gyro_payload.h"
#include "../src/input/gyro_layout.h"
#include "../src/input/gyro_protocol.h"
#include <cstdio>
#include <cmath>
#include <vector>
#include <cstring>
#include <cstdlib>
#include <fstream>
using namespace skpdb::gyro_payload;
static void check(bool x, const char *text) {
    if (!x) {
        std::fprintf(stderr, "FAIL %s\n", text);
        std::exit(1);
    }
}
int main(int argc, char **argv) {
    State s{};
    s.magic = kMagic;
    s.version = kVersion;
    s.control.sequence = 2;
    s.control.uid = 10312;
    s.control.enabled = 1;
    s.control.stream = 8;
    s.control.updated = 1000000000;
    s.control.rate[0] = 1;
    s.control.rate[1] = -.5f;
    s.control.epoch = 1;
    Event events[5]{};
    for (int i = 0; i < 5; i++) {
        events[i].version = 104;
        events[i].timestamp = 1010000000;
        events[i].type = i == 0 ? 4 : i == 1 ? 16 : i == 2 ? 11 : i == 3 ? 15 : 1;
        events[i].data[3] = 1;
    }
    auto original = std::vector<uint8_t>((uint8_t *)events, (uint8_t *)events + sizeof(events));
    check(Transform(&s, 10001, events, 5, 1010000000) == 0, "UID isolation");
    check(std::memcmp(original.data(), events, sizeof(events)) == 0, "unrelated UID modified");
    check(Transform(&s, 10312, events, 5, 1010000000) == 4, "all ordinary sensor types");
    check(events[0].data[0] == 1 && events[1].data[1] == -.5f && events[4].data[0] == 0,
          "rates and accelerometer isolation");
    check(events[1].data[3] == 1 && events[1].timestamp == 1010000000,
          "uncalibrated bias/timestamp preserved");
    for (int t = 1; t <= 100; t++) {
        s.control.updated = 1000000000 + uint64_t(t) * 10000000;
        for (int i = 2; i < 4; i++) {
            events[i].timestamp = s.control.updated;
            events[i].data[0] = events[i].data[1] = events[i].data[2] = 0;
            events[i].data[3] = 1;
        }
        Transform(&s, 10312, events + 2, 2, s.control.updated);
        for (int i = 2; i < 4; i++) {
            double norm = 0;
            for (int j = 0; j < 4; j++)
                norm += double(events[i].data[j]) * events[i].data[j];
            check(std::fabs(norm - 1) < 1e-5, "quaternion normalization");
        }
    }
    check(std::fabs(events[2].data[0]) > .4 && std::fabs(events[3].data[1]) > .2, "fusion integration");
    Event stale{};
    stale.version = 104;
    stale.type = 4;
    stale.timestamp = s.control.updated + 1;
    check(Transform(&s, 10312, &stale, 1, s.control.updated + kLeaseNs + 1) == 0, "real wall-clock lease");
    s.control.sequence |= 1;
    check(Transform(&s, 10312, &stale, 1, stale.timestamp) == 0, "torn state rejected");
    s.control.sequence++;
    s.control.enabled = 0;
    check(Transform(&s, 10312, &stale, 1, stale.timestamp) == 0, "disable respected");
    uint32_t b = 0;
    check(skpdb::gyro_layout::Branch(0x1000, 0x800, b) && !skpdb::gyro_layout::Branch(0, 1ull << 28, b),
          "signed branch bounds");
    check(skpdb::gyro_layout::Prologue(0xd503233f, 0xd10203ff), "variable displaced stack size");
    check(skpdb::gyro_protocol::Newer(0, 0xffffffff) && !skpdb::gyro_protocol::Newer(9, 10) &&
              !skpdb::gyro_protocol::Newer(10, 10),
          "sequence wrap/reorder");
    check(!skpdb::gyro_protocol::Rates(__builtin_nanf(""), 0, 0) && !skpdb::gyro_protocol::Rates(16, 0, 0),
          "packet finite/bounds");
    if (argc > 1) {
        std::ifstream f(argv[1], std::ios::binary);
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(f)), {});
        std::vector<uint8_t> constructor(bytes.begin() + 0x44e7c, bytes.begin() + 0x44e7c + 1024);
        uint32_t uid = 0;
        check(skpdb::gyro_layout::UidOffset(constructor, uid) && uid == 0x38,
              "actual sensor library UID member");
    }
    std::puts("PASS UID isolation, raw/uncalibrated rates, both fusion types, normalization, lease, seqlock "
              "and layout guards");
}
