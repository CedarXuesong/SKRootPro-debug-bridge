// Explicit real-device diagnostic. Only --clear writes release events to the
// supplied EXISTING evdev node; no virtual device is ever created.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <linux/input.h>
#include <sys/ioctl.h>
#include <unistd.h>
int main(int argc, char **argv) {
    if (argc < 3)
        return 64;
    bool clear = std::strcmp(argv[1], "--clear") == 0;
    int fd = open(argv[2], (clear ? O_WRONLY : O_RDONLY) | O_CLOEXEC);
    if (fd < 0) {
        perror("open");
        return 1;
    }
    if (std::strcmp(argv[1], "--grab") == 0) {
        if (argc != 4)
            return 64;
        int seconds = std::atoi(argv[3]);
        if (seconds < 1 || seconds > 45)
            return 64;
        if (ioctl(fd, EVIOCGRAB, 1)) {
            perror("grab");
            return 1;
        }
        std::puts("GRABBED");
        std::fflush(stdout);
        sleep(seconds);
        close(fd);
        return 0;
    }
    if (clear) {
        if (argc != 4)
            return 64;
        int slot = std::atoi(argv[3]);
        if (slot < 0 || slot > 15)
            return 64;
        input_event events[5]{};
        events[0].type = EV_ABS;
        events[0].code = ABS_MT_SLOT;
        events[0].value = slot;
        events[1].type = EV_ABS;
        events[1].code = ABS_MT_TRACKING_ID;
        events[1].value = -1;
        events[2].type = EV_KEY;
        events[2].code = BTN_TOUCH;
        events[3].type = EV_KEY;
        events[3].code = BTN_TOOL_FINGER;
        events[4].type = EV_SYN;
        events[4].code = SYN_REPORT;
        if (write(fd, events, sizeof(events)) != sizeof(events)) {
            perror("write");
            return 1;
        }
    } else {
        int32_t slots[17];
        for (auto &slot : slots)
            slot = -1;
        slots[0] = ABS_MT_TRACKING_ID;
        if (ioctl(fd, EVIOCGMTSLOTS(sizeof(slots)), slots)) {
            perror("slots");
            return 1;
        }
        unsigned char keys[(KEY_MAX + 8) / 8]{};
        if (ioctl(fd, EVIOCGKEY(sizeof(keys)), keys) < 0) {
            perror("keys");
            return 1;
        }
        for (int i = 0; i < 16; ++i)
            std::printf("%d%c", slots[i + 1], i == 15 ? '\n' : ' ');
        std::printf("touch=%u tool=%u\n", (keys[BTN_TOUCH / 8] >> (BTN_TOUCH % 8)) & 1,
                    (keys[BTN_TOOL_FINGER / 8] >> (BTN_TOOL_FINGER % 8)) & 1);
    }
    close(fd);
}
