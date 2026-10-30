#pragma once

#include <sys/types.h>

namespace skpdb {

// Delay before the detached service starts. The module entry point runs while
// the framework is still bringing up the device; the bridge waits for the USB
// stack and property service to settle before its first ConfigFS access.
constexpr int kStartupDelaySeconds = 12;

// Forks a detached child process.
//
// Parent: returns the child pid (> 0).
// Child:  performs setsid(), marks itself non-dumpable, sets umask(077) and
//         redirects stdio to /dev/null, then returns 0.
// Error:  returns -1.
pid_t ForkDetachedChild();

}  // namespace skpdb
