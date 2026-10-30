#include <linux/input.h>
#include <fcntl.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <cstdio>
#include <cstdint>
#include <vector>

// Read-only kernel state, independent of the bridge's bookkeeping.
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    int fd = open(argv[1], O_RDONLY | O_CLOEXEC);
    if (fd < 0) { perror("open"); return 1; }
    input_absinfo slots{};
    if (ioctl(fd, EVIOCGABS(ABS_MT_SLOT), &slots) < 0) { perror("EVIOCGABS"); return 1; }
    unsigned char keys[(KEY_MAX + 8) / 8]{};
    if (ioctl(fd, EVIOCGKEY(sizeof(keys)), keys) < 0) { perror("EVIOCGKEY"); return 1; }
    printf("slot=%d btn_touch=%d btn_tool_finger=%d\n", slots.value,
           (keys[BTN_TOUCH/8] >> (BTN_TOUCH%8)) & 1,
           (keys[BTN_TOOL_FINGER/8] >> (BTN_TOOL_FINGER%8)) & 1);
    for (int axis : {ABS_MT_TRACKING_ID, ABS_MT_POSITION_X, ABS_MT_POSITION_Y,
                     ABS_MT_PRESSURE, ABS_MT_TOUCH_MAJOR}) {
        std::vector<int32_t> values(slots.maximum + 2);
        values[0] = axis;
        if (ioctl(fd, EVIOCGMTSLOTS(values.size() * sizeof(int32_t)), values.data()) < 0) {
            perror("EVIOCGMTSLOTS"); return 1;
        }
        printf("axis=%02x", axis);
        for (size_t i = 1; i < values.size(); ++i) printf(" %d", values[i]);
        puts("");
    }
    close(fd);
}
