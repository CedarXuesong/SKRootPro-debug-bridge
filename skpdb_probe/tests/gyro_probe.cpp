#include <android/sensor.h>
#include <android/looper.h>
#include <unistd.h>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <initializer_list>
int main(int argc, char **argv) {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    int uid = argc > 1 ? std::atoi(argv[1]) : 0;
    int seconds = argc > 2 ? std::atoi(argv[2]) : 5;
    if (seconds < 1 || seconds > 120)
        return 1;
    if (uid && (setgid(uid) || setuid(uid))) {
        std::puts("ERROR setuid");
        return 2;
    }
    auto *m = ASensorManager_getInstanceForPackage(uid == 2000 ? "com.android.shell" : "skpdb.gyrotest");
    auto *l = ALooper_prepare(ALOOPER_PREPARE_ALLOW_NON_CALLBACKS);
    auto *q = m ? ASensorManager_createEventQueue(m, l, 1, nullptr, nullptr) : nullptr;
    if (!q) {
        std::puts("ERROR sensor queue");
        return 3;
    }
    unsigned registered = 0;
    for (int type : {4, 16, 11, 15}) {
        const auto *s = ASensorManager_getDefaultSensor(m, type);
        if (s) {
            int rc = ASensorEventQueue_registerSensor(q, s, 20000, 0);
            std::printf("# register uid=%d type=%d rc=%d\n", getuid(), type, rc);
            if (rc == 0)
                ++registered;
        }
    }
    if (!registered) {
        std::puts("ERROR no registered sensors");
        return 4;
    }
    timespec start{};
    clock_gettime(CLOCK_MONOTONIC, &start);
    while (true) {
        timespec now{};
        clock_gettime(CLOCK_MONOTONIC, &now);
        if (now.tv_sec - start.tv_sec >= seconds)
            break;
        ASensorEvent e{};
        while (ASensorEventQueue_getEvents(q, &e, 1) > 0)
            if (e.type == 4 || e.type == 16 || e.type == 11 || e.type == 15)
                std::printf("%d %lld %.8f %.8f %.8f %.8f %.8f %.8f\n", e.type, (long long)e.timestamp,
                            e.data[0], e.data[1], e.data[2], e.data[3], e.data[4], e.data[5]);
        usleep(2000);
    }
    ASensorManager_destroyEventQueue(m, q);
    return 0;
}
