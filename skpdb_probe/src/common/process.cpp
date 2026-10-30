#include "common/process.h"

#include <fcntl.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <unistd.h>

namespace skpdb {

pid_t ForkDetachedChild() {
    pid_t child = fork();
    if (child < 0) {
        return -1;
    }
    if (child > 0) {
        return child;
    }

    // New session: the child has no controlling terminal and survives its
    // parent. Dumpable is cleared so the process cannot be ptraced by peers
    // with the same uid, and the umask keeps private files owner-only.
    setsid();
    prctl(PR_SET_DUMPABLE, 0);
    umask(077);

    int null_fd = open("/dev/null", O_RDWR);
    if (null_fd >= 0) {
        dup2(null_fd, STDIN_FILENO);
        dup2(null_fd, STDOUT_FILENO);
        dup2(null_fd, STDERR_FILENO);
        if (null_fd > STDERR_FILENO) {
            close(null_fd);
        }
    }
    return 0;
}

}  // namespace skpdb
