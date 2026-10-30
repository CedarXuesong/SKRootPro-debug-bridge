# Current release: module 0.12.14

See [the repository README](../README.md), [build instructions](../docs/BUILDING.md) and
[connection/touch troubleshooting](../docs/TROUBLESHOOTING.md). Source is available under
the restrictive [SKPDB license](../LICENSE); this is not an OSI open-source license.

Touch injection remains on the existing physical touchscreen. The leased software-release
guard is experimental, disabled by default, and pending device revalidation; no uinput or additional InputDevice is created. Hardware events
remain authoritative. Direct sensor channels are still unsupported. The detailed protocol and memory
reference below includes historical design context; the current troubleshooting/build documents take precedence.

# SKPDB Debug Bridge

SKPDB is an SKRoot module built on a transport-independent protocol core. It
exposes a root shell, file transfer, package install and untraced memory reads
over TCP, and is intended for development, installation and diagnostics.

## Source layout

| Path | Contents |
| --- | --- |
| `src/common/` | Transport-independent core: big-endian framing (`frame`, `bytes`), SHA-256/HMAC (`crypto`), the per-connection challenge (`auth`), the session loop (`session`), the request dispatcher (`channel`), the `Stream` abstraction, and small file/process helpers. |
| `src/memory/` | Everything that touches another process: `kernel_channel` holds the asmjit shellcode, the staging buffer, the per-pid kernel entry and the self-test; `memread` the public reads, the sparse dump and the residency bitmap; `memwrite` the guarded write; `memscan` the pattern search; `symtab` the ELF symbol resolver. |
| `src/input/` | Touch injection: `touch` resolves the real touchscreen `struct input_dev` and drives it with MT protocol B frames from the kernel input core. |
| `src/service/` | The control plane around the bridge: `config` (persisted ports and timeouts), `knock` (signed UDP gate that opens the TCP port), `admin` (unix-socket administration channel). |
| `src/wireless/` | The TCP module: `server` (gate, listener, admin loop, config apply), `webui` (SDK web UI handler), `socket_stream`, and `entry` with the module descriptor and version. |
| `src/wireless/webui/` | Chinese responsive console assets (`index.html`, `app.css`, `app.js`), embedded by `tools/build_webui.ps1`. |
| `jni/` | `Android.mk` that builds `libskpdb_core.a` plus the shared module; `Application.mk`. |
| `tools/` | Device-side helper programs that are not part of the module: `loader` (dlopen wrapper), `installer` and `uninstaller` (SKRoot one-shot .so), `lister` (prints the installed module table). See `tools/README.md`. |
| `tests/` | `mock_bridge.py` (in-process fake module with a fake address space), `test_deploy_tcp.py` (upload/install/check + reconnect) and `test_lab.py` (typed views, pointer chains, scans). |
| `pc_client.py` | PC client over TCP. |
| `lab.py` | Typed memory workbench: maps, views, pointer chains, value scans and snapshot refinement. |
| `deploy.py` | Uploads the module archive and calls the on-device SKRoot installer. |
| `../host/` | Native Windows client (`skpdb.exe`). |
| `../dist/skpdb.exe` | Built native client; see “Native PC client” below. |

The Windows client mirrors this split: `src/cli.h` holds the option struct and
the connect helpers, `src/commands.h` declares one function per command, and the
bodies live in `src/commands_session.cpp`, `src/commands_memory.cpp` and
`src/commands_admin.cpp`. `src/main.cpp` only parses options and dispatches.

Module identity: `SKPDB Development Network Bridge` 0.12.14, authored by
`雪松/CedarXuesong` (`36c8b2dc7a8b4bb6a2b0e0ce54e05f31`). The `id32` never
changes; only the version string moves (0.4.0 published the TCP bridge, 0.8.0
added untraced memory reads and dropped the wired transport, 0.9.0 replaced the
trust marker with a per-session challenge and added the knock-gated port and the
WebUI, 0.9.1 made never-faulting reads the default and put the WebUI on a page
heartbeat, 0.10.0 added touch injection into the real touchscreen, 0.11.2 made it
multi-slot safe, 0.11.3 enabled TCP_NODELAY, 0.11.4 added batched touch
frames plus the reduced move frame, 0.11.5 added the trusted UDP move path,
0.11.6 bounds bridge sessions and throttles the TCP move fallback, and 0.12.11
refreshes the descriptor and the WebUI text). The
install record is rewritten on reinstall, so `skpdb_list_modules` reports the
version in the archive that was installed; only a reboot swaps the running
code, which is why a fresh install can answer with the old behaviour until the
device restarts. `meminfo` and `info` are the authoritative checks of what is
actually running.

## Build

The gyro backend in 0.12.14 covers ordinary event queues for sensor types 4,
16, 11 and 15. It transforms private copies for an explicit UID, uses verified
ELF padding and owned patch records, and confirms seqlock state publication.
The global syscall gateway has been removed. Direct-report channels are **not
intercepted**; API status explicitly reports `direct_channel=unsupported`.
See `REVIEW-0.12.14.md` for tests and deployment scope.

CLI: `gyro install UID`, `gyro status`, `gyro rate RX RY RZ`, `gyro stop`,
`gyro remove`. Native UDP uses SKG2 (40 bytes): magic, 16-byte fast token,
stream u32, sequence u32, three big-endian floats. Installation returns a fresh
stream ID; stale streams/sequences, non-finite or out-of-range rates are rejected.
The 200 ms lease uses CLOCK_BOOTTIME. `gyro hold` and the old live benchmark
are rejected. The build embeds the PIC transform from `tools/build_gyro.ps1`.

Requires Android NDK r29 and the SKRoot SDK. Provide the SDK path using `-Sdk` or `SKROOT_SDK_ROOT` and can be overridden with `-Sdk` or the `SKROOT_SDK` make
variable.

Build the module and package it:

```powershell
.\skpdb_probe\build.ps1
```

Unsigned scripts are blocked by the default execution policy, so either
allow the script or run it through a bypass:

```powershell
powershell -ExecutionPolicy Bypass -File .\skpdb_probe\build.ps1
```

The script performs one `ndk-build` call equivalent to:

```powershell
& (Join-Path $env:ANDROID_NDK_ROOT 'ndk-build.cmd') `
  -C 'skpdb_probe\jni' NDK_PROJECT_PATH=. APP_BUILD_SCRIPT=Android.mk `
  NDK_APPLICATION_MK=Application.mk NDK_OUT=../obj/default NDK_LIBS_OUT=../libs `
  SKROOT_SDK=$env:SKROOT_SDK_ROOT
```

Outputs:

| Artifact | Contents |
| --- | --- |
| `libs/arm64-v8a/libskpdb_probe.so` | the module |
| `skpdb_dev_network.zip` | the `.so` only |

The archive contains one `.so`; filename and ELF SONAME follow the SKRoot
module packaging convention.

## Deploying to a device

`deploy.py` copies the archive into `/data/local/tmp` and invokes the SKRoot
installer that is already on the device, over the running module's TCP bridge.
The root key is read with `getpass` (or from `--key-file` / `--key-stdin` for
automation) and never appears on the command line.

```powershell
python skpdb_probe\deploy.py --host 192.0.2.1 upload `
    --wireless skpdb_probe\skpdb_dev_network.zip

:: reinstall the network module itself (session drops, then reconnects)
python skpdb_probe\deploy.py --host 192.0.2.1 install

python skpdb_probe\deploy.py --host 192.0.2.1 check
```

`deploy.py` knocks first, so it reaches the module even while no port is open.
`--tcp-port` defaults to 39876 and `--knock-port` to 39877; both must match the
device's `skpdb.conf`.

A module older than 0.9.0 has no knock port and answers a connection with the
old `SKPT` trust marker. The client refuses that answer instead of talking to
it, so upgrading such a device needs the one-shot escape hatch:

```powershell
python skpdb_probe\deploy.py --host 192.0.2.1 --legacy-trust install
```

After the reboot the device speaks the per-session challenge and the flag is
never needed again (and never accepted by the module).

The module cannot reinstall itself over its own session: the listener dies with
the old process. The helper therefore starts the installer detached (`>` log
file, `rc` file, no SIGPIPE), then reconnects every two seconds until the new
module answers and the `rc` file is readable.

The installer only registers a module; SKRoot starts installed modules at
boot, so a reinstall takes effect after a reboot. Installing is a registration
step — the running instance keeps serving with the old code until then, so
`install` followed by `check` can still show old behaviour; reboot and re-run
`meminfo`. The host can also replay the same two steps with the native client
(`skpdb.exe --host <address> put ...` then `run ...`), but the helper is the
supported path because it verifies the remote SHA-256 before calling the
installer and checks the exit status.

Do not try to drive the installer from `adb shell su`: it runs as uid 0 but
keeps the `u:r:shell:s0` SELinux context, and the SKRoot kernel channel is only
usable from the module's own context. Every call made that way — install,
uninstall, even `skpdb_list_modules` — returns `ERR_MODULE_NOT_KADDR`. Run the
installer through the module's TCP session (what `deploy.py install` does, or
`skpdb.exe run` with the loader), where it returns `err=OK`.

## Module

The module never keeps a TCP port open. It listens for one thing only: a
60-byte UDP knock on the knock port (default 39877), signed with the root key.
A valid knock adds that source address to an allowlist and opens the service
port (default 39876) right inside the knock handler, so the client's first
connect succeeds without waiting for another poll round (the receiver itself
polls every 20 ms); the port closes again after `idle_timeout_seconds` with
no session; the timer restarts when a session *ends*, not when it starts, so
finishing one command never leaves the next one knocking at a closed port.
To a port scan the machine looks like it has no such port at all — nothing is
bound before a knock, and `ss`/`netstat` show no entry either.

That model assumes the knock can reach the device. A TCP-only port forwarder
(frp, `ssh -L`, a reverse tunnel) cannot carry the UDP knock, so the port never
opens and the module looks dead from outside. For those setups set
`always_listen=1` (WebUI or `config always_listen=1`): the service port then
stays open, which costs the "no such port" property on the device's own network
but keeps the per-connection key challenge. The module rewrites
`skpdb.health` next to itself every ten seconds (uptime, knock ticks, whether
the receiver is running, how often it was restarted, listening state), so the
state before a failure can still be read after a restart. A supervisor restarts
the knock receiver when its tick counter stops advancing.

Settings live in `skpdb.conf` next to the module (written with tmp+rename,
mode 0600) and are editable from the WebUI or over the admin channel:

| key | default | meaning |
| --- | --- | --- |
| `service_port` | 39876 | TCP port opened after a knock |
| `knock_port` | 39877 | UDP port the knock is sent to |
| `idle_timeout_seconds` | 30 | close the service port after this much idle time |
| `auth_window_seconds` | 30 | accepted clock skew for signed requests |
| `always_listen` | 0 | keep TCP listening for TCP-only tunnels; HMAC authentication still applies |

Every TCP session starts with the same challenge: the module sends a fresh
32-byte nonce and expects `HMAC-SHA256(root key, nonce)`. There is no trust
marker and no remembered state — a session without the key is closed before any
frame is read, and each session pays the full challenge again.

The module also serves the SKRoot SDK WebUI. It is not running by default; the
`webui` admin action starts the SDK loader, which picks a random port in
20000–60000. Open `http://<device>:<webui port>/` after the existing module
manager/client opening authorization. The console opens directly and does not
ask for the Root key again. Each WebUI process generates a page request token
for same-origin API calls; it is unrelated to the Root key and is never persisted.

Version 0.12.10 provides four pages:

- Overview: live module/system information, IPv4 addresses, SKF2/240 Hz touch
  statistics, cursor traffic, and passive HUD state (including its transparent background).
- Diagnostics: explicit system/BTF, touch binding, memory backend and strict-read
  checks. It does not inject touches or start/reset overlays. Copy/download
  reports omit transport tokens, module-private paths and memory probe addresses.
- Basic tools: root command execution with a bounded wait, directory
  browsing and read-only previews of the first 64 KiB of a file. Output is rendered
  as text. Full keyboard/mouse sampling and layout editing remain desktop features.
- Settings: all five persisted connection settings, including `always_listen`;
  background polling does not overwrite unsaved input.

Static files and APIs use no-store responses. The opening authorization remains
the SDK/module manager's existing responsibility; the page request token prevents
cross-site calls and is not a second login. HTTP is the existing SDK transport.

The opened page holds a heartbeat. While it is in the foreground it posts to
`/api/alive` once a second, and each beat stamps `skpdb.webui.beat` next to the
module. The bridge daemon watches that file and terminates the WebUI loader as
soon as the beats stop — backgrounded tab, closed window, lost link — so the
port exists only while somebody is actually looking at the page. A freshly
started server that nobody has opened yet keeps a two-minute grace so the link
can be opened at leisure.

```
skpdb.exe --host <device> webui start     # prints: open: http://<device>:<port>/
```

WebUI API: `GET /api/info`, `POST /api/diagnostics`, `POST /api/config`,
`POST /api/exec`, `GET /api/list?path=...`, `GET /api/file?path=...`,
`POST /api/alive`, and `POST /api/webui/stop`.
All API calls carry the per-process `X-SKPDB-CSRF` page header and reject
cross-origin requests. There is no WebUI login/auth endpoint or key input field.
Backend admin action B/6 returns the diagnostic report over existing authenticated channels.

## Protocol

Frames are a 4-byte big-endian length followed by one opcode byte and its
payload; the maximum frame is 1 MiB. Requests use `E` (exec), `R` (read),
`W` (write), `L` (list), `T` (stat), `M` (memory read), `A` (memory maps),
`G` (memory info), `I` (touch inject), `B` (administration), `K` (ping), `V`
(version) and `Q` (close). `version` returns a compact capability record; PTY
and forwarding remain explicitly `none`.

The memory opcodes carry no paths or text on the request side:

| Request | Payload | Response |
| --- | --- | --- |
| `M` | `u32 pid`, `u64 address`, `u32 length`, `u8 mode` (18 bytes) | `u8 backend`, then the bytes actually read |
| `A` | `u32 pid` (5 bytes) | the `/proc/<pid>/maps` text |
| `G` | none | a `key=value` diagnostics report |

`mode` is 0 (`auto`, kernel path first then userspace fallbacks), 1
(`kernel` only, fail instead of falling back) or 2 (`userspace`, skip the
kernel path). `backend` is 1 (kernel `access_process_vm`), 2
(`process_vm_readv`) or 3 (`/proc/<pid>/mem`). A response shorter than `length`
is a normal end-of-mapping short read, not an error. One request carries at
most 1 MiB - 16 bytes so the answer fits a single frame, and both clients split
a longer read at the transport chunk size (900 KiB).

The administration opcode `B` carries an action byte and an optional body:
`1` returns the module report (`key=value` lines), `2` applies settings
(`service_port=...;knock_port=...`, ports restart the listener immediately),
`3` starts the WebUI server and answers `webui_port=<n>`, `4` stops it.
Clients normally use `info`, `config` and `webui` instead of the raw opcode.

The knock datagram is 60 bytes: `SKPDBK01`, version `1`, flags, reserved, the
Unix timestamp, a random 64-bit nonce, then `HMAC-SHA256(root key, first 28
bytes)`. A knock is accepted only inside `auth_window_seconds` of the module's
clock and only once per nonce, so a captured datagram cannot be replayed.

The first session uses an HMAC-SHA256 challenge over a 32-byte nonce, raw
bytes on the socket, and every later session repeats it. No key is stored in
this repository.

## Untraced memory reads

`M` (one range), `S` (several ranges), `A` (maps) and `G` (diagnostics) read
another process without ever attaching to it: no
`ptrace(PTRACE_ATTACH)`, no `TracerPid` change, no stop signal, and no
`process_vm_readv` syscall unless the kernel path is unusable. The primary
path runs inside the SKRoot kernel channel (`src/common/memread.cpp`):

1. `find_get_pid(pid)` returns a `struct pid *`.
2. `pid_task(PIDTYPE_PID)` resolves the thread-group leader's `task_struct`.
3. `access_process_vm(task, addr, stage, len, FOLL_FORCE)` copies to a kernel
   staging buffer (64 KiB, allocated with `alloc_kernel_mem`).
4. `put_pid()` drops the reference and `read_kernel_mem()` copies the bytes
   back to userspace.

All four steps are one generated aarch64 routine executed through
`execute_kernel_asm_func`, so no `task_struct` pointer ever outlives a single
kernel entry — a target that exits mid-read cannot leave a stale pointer
behind, and the whole sequence is serialized by a process-wide mutex so
concurrent sessions cannot interleave shellcode. `FOLL_FORCE` is what makes
COW and private pages readable, exactly as `fs/proc/base.c` does for a read.
The flags were plain `#define`s until 6.3, when they became an enum in
`include/linux/mm_types.h` and every bit moved, so the values are picked by
kernel version:

| kernel | `FOLL_FORCE` | `FOLL_NOFAULT` |
| --- | --- | --- |
| < 5.6 | `0x10` | absent |
| 5.6 – 6.2 | `0x10` | `0x80` |
| >= 6.3 | `0x08` | `0x20` |

`FOLL_NOWAIT` is never set (`0x20` in 5.6–6.2, `0x10` from 6.3 on): a strict
read wants the opposite of a background fault. The older pre-4.9 signature
(`int write = 0`) is selected with `is_kernel_version_less("4.9.0")`.

From 5.6 on the flags add `FOLL_NOFAULT`, and that is what keeps a
read invisible to the target. Without it a read of a non-resident page goes
through `handle_mm_fault`, which shows up as a minor fault and as rss growth —
counters the target can read about itself in `/proc/self/stat` and
`/proc/self/statm`. With it a hole is reported instead of filled:

```
address is not readable without faulting it in (strict mode: non-resident or PROT_NONE page)
```

The default modes therefore never fall back to the userspace backends, because
`process_vm_readv` and `/proc/<pid>/mem` reach pages through the fault path.

| `--mode` | wire | behaviour |
| --- | --- | --- |
| `auto` (default) | 0 | kernel path with `FOLL_NOFAULT`; a hole is an error, no fallback |
| `kernel` | 1 | identical to `auto`, kept for old clients |
| `userspace` | 2 | skips the kernel path; can fault pages in |
| `fault` | 3 | kernel path with faults allowed, then `process_vm_readv`, then `/proc/<pid>/mem` |

Use `--mode fault` only when the data matters more than the trace: it is the
pre-0.9.1 behaviour and it can pull pages into the target.

The first kernel read runs a self-test: it reads a 32-byte pattern out of this
very process through the same path and compares it byte for byte. A mismatch
latches the kernel backend as failed for the rest of the process instead of
silently returning garbage. `process_vm_readv` and `/proc/<pid>/mem` stay
available for `--mode fault` and `--mode userspace`; they do not change
`TracerPid` either, but they are visible to seccomp/LSM policy and to a
`syscall`-level audit, which is exactly why the kernel path is preferred.

The staging buffer is allocated once per process and reused, so a long read
session does not leave a trail of slab allocations in the kernel.

Capability record: `mem=read,maps,info,scatter,dump,resident,write,scan,sym;mem_ptrace=none;mem_backends=kernel,vm_readv,proc_mem;touch=inject,info,batch;touch_device=real-touchscreen;touch_ptrace=none`.
Clients should check for the `mem=` and `touch=` tokens before using the opcodes.

```powershell
dist\skpdb.exe --host 192.0.2.1 meminfo
dist\skpdb.exe --host 192.0.2.1 memmaps 12345 maps.txt
dist\skpdb.exe --host 192.0.2.1 memread 12345 0x7f0000123456 64
dist\skpdb.exe --host 192.0.2.1 memread 12345 0x7f0000123456 4096 --mode kernel --out page.bin
:: several ranges in one request: one kernel entry, one mmap_lock, one round trip
dist\skpdb.exe --host 192.0.2.1 mread 12345 0x7f0000123456:8 0x7f0000123500:8 --out chain.bin
:: where is libc mapped, and what does its symbol offset resolve against
dist\skpdb.exe --host 192.0.2.1 modbase 12345 libc.so
:: which pages are even readable, then save exactly those into a sparse image
dist\skpdb.exe --host 192.0.2.1 resident 12345 0x76ede97000 0x40000
dist\skpdb.exe --host 192.0.2.1 memdump 12345 0x76ede97000 0x40000 libc.bin
:: symbol to address, then confirm the bytes at that address
dist\skpdb.exe --host 192.0.2.1 sym 12345 libc.so strlen
dist\skpdb.exe --host 192.0.2.1 memread 12345 0x7cd4d4c610 16 --mode kernel
:: find a pattern without faulting anything in
dist\skpdb.exe --host 192.0.2.1 memscan 12345 0x6d6c5de000 0x4000 text:HIT
:: wildcard byte (??) and a scan over every readable mapping
dist\skpdb.exe --host 192.0.2.1 memscan 12345 0x6d6c5de000 0x4000 534b504442??50
dist\skpdb.exe --host 192.0.2.1 memscan --maps 12345 7f454c46
:: local symbols as well, when the library is not stripped
dist\skpdb.exe --host 192.0.2.1 sym 12345 libc.so strlen --all
:: dump the same range with one round trip per 64 runs, or as a gzip stream
python skpdb_probe\pc_client.py 192.0.2.1 memdump 12345 0x6d6c5de000 0x4000 out.bin --scatter
python skpdb_probe\pc_client.py 192.0.2.1 memdump 12345 0x6d6c5de000 0x4000 out.gz --scatter --gzip
:: patch bytes (refused outside a writable mapping, and on non-resident pages)
dist\skpdb.exe --host 192.0.2.1 poke 12345 0x6d6c5e2160 90909090
dist\skpdb.exe --host 192.0.2.1 poke 12345 0x6d6c5e2160 text:NOP
```

Measured on the device: a `??` wildcard scan of the poked scratch page found
`SKPDB?P` at exactly the written address; `memscan --maps <system_server>
7f454c46` found the three ELF headers in the first 20 MiB of 6214 bounded
requests (`scanned=20168704 skipped=12288`); `memdump --scatter` produced the
same 4416 resident bytes as the `D` loop in one batch, and `--gzip` produced a
4416-byte stream that still contains the probe string. `sym ... --all` printed
the same address twice, once as `dyn` and once as `sym`. `sym <system_server>
libc.so strlen` resolved to
`0x7cd4d4c610` (file offset `0xea610`) and `memread` there returned the same 16
bytes as `dd if=libc.so bs=1 skip=0xea610 count=16`; `memscan` found the probe
string at exactly its address with `scanned=11248 skipped=9232`; `poke` wrote 11
bytes through the kernel backend and `memread` read them back; a `poke` into
libc's `r-xp` mapping was refused with `is in a mapping without write
permission` before anything changed.

`D` (dump) streams one readable run per request:
`[D][u32 pid][u64 start][u64 end][u64 cursor][u32 max_bytes][u8 mode]` is answered
with `[D][0][u64 data_start][u64 next_cursor][u32 bytes][data]`. The caller writes
`data` at file offset `data_start - start` and resumes at `next_cursor`, so a
hole becomes a hole in the output file and the summary reports how many bytes
were skipped. `P` (resident) answers a page bitmap for
`[P][u32 pid][u64 start][u32 pages][u8 mode]`, which lets a caller pick the
readable parts instead of discovering holes one request at a time. Both refuse
to fault pages in, exactly like `M` and `S`.

`X` (write) takes `[X][u32 pid][u64 address][u32 length][u8 mode][u8 flags][bytes]`.
It is the one operation whose *effect* the target can see by definition, so the
mechanism is what stays quiet: `access_process_vm(..., FOLL_FORCE|FOLL_WRITE)`
inside one kernel entry, no ptrace, no `TracerPid`, no `/proc/<pid>/mem`
descriptor. Two guards keep it from changing more than asked: the range must sit
inside a writable mapping, and a `MAP_SHARED` file-backed page is refused unless
`--allow-shared` is passed. Writes to pages that are not resident are refused by
default (the page would have to be faulted in, which moves the target's rss);
`--allow-fault` opts into that.

`Y` (scan) searches `[Y][u32 pid][u64 start][u64 end][u32 max_hits][u16 pattern]
[u8 mode][u8 flags][pattern]` (plus a mask of the same length when flags bit 0 is
set) and answers `[Y][0][u32 hits][u64 scanned][u64 skipped]` followed by one
`u64` address per hit. A mask byte of 0xff requires equality, 0x00 accepts
anything, so a client can spell `??` wildcards. The walk uses the same strict
read policy, so a scan never faults anything in, and `skipped` reports how many
bytes it had to step over. `--maps` walks every readable mapping instead of one
range, in 4 MiB requests, and steps over mappings that hold nothing resident.

`F` (symbols) resolves names in a mapped ELF: `[F][u32 pid][u8 flags][module\0]
[name]`, answered with `[F][0][text]`, one `0x<address> 0x<file-offset> <type>
<name> <module> <table>` line per hit, where the last field says whether the line
came from `dyn` or, with flags bit 1 (`--all`), from a `.symtab` the file still
carries. The ELF is parsed from the mapping's file on disk,
so a lookup does not read the target's memory at all -- it only reads
`/proc/<pid>/maps` for the load bias. That makes `sym` the cheapest way to go
from "strlen" to an address, and `memread` at that address confirms it.

Verified on a device against the file itself: dumping the resident prefix of
`libc.so` out of `system_server` produced 167936 bytes whose SHA-256 equalled
`dd if=.../libc.so bs=4096 count=41 | sha256sum`, with the remaining 94208 bytes
of the 256 KiB range reported as a gap because those pages were not resident.

`S` is the scatter form of `M`: `[S][u32 pid][u8 mode][u32 count][(u64 address, u32
length) ...]`, answered with `[S][0][u32 count]` followed by, per range, `[u8
status][u8 backend][u32 data bytes][u32 error bytes][error][data]`. Ranges are
independent — a hole reports its own error and the rest still come back — and the
whole batch must fit in one frame (1 MiB - 16). A pointer chain or a struct walk
therefore costs one round trip instead of one per field, and one `mmap_lock`
instead of one per field, which matters for both latency and for how long the
target's mapping lock is held.

`memread` prints `backend=... bytes=...` on stderr followed by a hex dump, or
writes the raw bytes with `--out`. Reading `/proc/<pid>/status` in the target
before and after a strict read shows `TracerPid: 0` in both cases.

The stronger property is that the target's own memory does not move. `meminfo`
proves it on hardware with a self-contained check: it maps 1 MiB of anonymous
memory in the module, touches the first page, and then reads two addresses
through the kernel path.

```
kernel=ok
strict_guard=ok
stage_bytes=65536 (persistent)
```

`strict_guard=ok` means all three steps held:

1. the touched page came back through the strict path (64 bytes),
2. the untouched page was refused instead of being faulted in (0 bytes),
3. `--mode fault` then read exactly that page, which is the trace strict mode
   exists to avoid.

A strict read that reports `... is not readable without faulting it in` means
the page is not resident (or is `PROT_NONE`) and was deliberately left alone.

Note what a strict read does *not* leave behind: minor faults are charged to
whoever triggers the fault, not to the target, so `minflt` in the target's
`/proc/<pid>/stat` never moves even in `--mode fault`. What the target can
observe is its own resident set: `--mode fault` maps the page into it, while
strict mode leaves `statm` untouched. Measured on a quiet sleeper process:
resident page read → `rss 1416 -> 1416`, hole refused → `1416 -> 1416`, and the
same hole only became readable to a later strict read after `--mode fault` had
brought it in.

The kernel self-test deliberately runs with faults *allowed*: it validates the
shellcode channel, not residency, and a strict self-test would latch the backend
as failed whenever the kernel happened to reclaim the probe page.

## Touch injection

`I` injects a gesture into the real touchscreen input device. Unlike
`/dev/uinput` it creates no device node and no process: the events enter the
kernel input core where the panel driver would put them, so evdev,
InputFlinger and the target app see the real device id, source and timestamp.
`touch info` prints the resolved device:

```
touch=ready
name=NVTCapacitiveTouchScreen
event=/dev/input/event7
input_dev=0xffffff8827214800
screen=1280x2772
abs_x=0..127999
abs_y=0..277199
slots=0..9
pressure=0..1000
btn_touch=1
```

Discovery is two-sided: `/proc/bus/input/devices` picks the touchscreen (an
`EV_ABS` device with `ABS_MT_POSITION_X/Y`, `INPUT_PROP_DIRECT` preferred), and
`input_dev_list` plus BTF (`input_dev.node`, `input_dev.name`) resolves its
kernel `struct input_dev` pointer. One interface keeps the panel's real
`input_event` symbol: a down frame calls `input_event()` for `ABS_MT_SLOT`,
`ABS_MT_TRACKING_ID`, `ABS_MT_POSITION_X/Y`, pressure and major,
`BTN_TOUCH` where the device advertises it, then `SYN_REPORT`. A move frame
only sends `ABS_MT_POSITION_X/Y` and `SYN_REPORT`, and omits `ABS_MT_SLOT` when
the slot is unchanged; this cuts the per-sample kernel entry cost. Coordinates
are screen pixels; the module maps them through the axis range reported by
`EVIOCGABS`, so the raw values follow the panel's real resolution.

Request: `[I][action][flags][slot u16][x u32][y u32][x2 u32][y2 u32][duration u32]`.
Actions: 0 info, 1 down, 2 move, 3 up, 4 tap, 5 swipe. The answer is a
`key=value` report.

`J` is the batched form used by the desktop gamepad: request
`[J][count u8]` followed by `count * [action u8][flags u8][slot u16][x u32][y u32]`.
All frames in a batch share one kernel entry. The desktop client sends at most
two frames per batch so a simultaneous move/look start does not pay one long
kernel call; steady-state movement and look send one frame per 8 ms tick.

`fast_port` (default `service_port + 3`) is a trusted-device UDP move channel.
It accepts only source addresses that completed the signed knock and a packet of
the form `[SKF1][token 16][count u8]` followed by `[action u8][flags u8][slot u16]
[x u32][y u32]` records. The token is disclosed only in the authenticated admin
`info` report; there is no per-packet response or handshake. Only move actions
are accepted; down/up stay on TCP so a lost datagram cannot leave a finger down.

There is no uinput node and no injected-event flag: `getevent -p` and
`InputManager.getInputDeviceIds()` see only the physical touchscreen. The
unavoidable residual is on the kernel side: the controller's IRQ count and bus
traffic do not move for an injected frame, and event rate, pressure and path
geometry can be compared by a root-level checker. From the app's perspective
the event is indistinguishable from the touchscreen's own.

```powershell
python skpdb_probe\pc_client.py 192.0.2.1 touch info
python skpdb_probe\pc_client.py 192.0.2.1 touch tap 640 1386
python skpdb_probe\pc_client.py 192.0.2.1 touch tap 640 1386 90 9
python skpdb_probe\pc_client.py 192.0.2.1 touch swipe 200 1600 1000 1600 400
python skpdb_probe\pc_client.py 192.0.2.1 touch down 640 1386
python skpdb_probe\pc_client.py 192.0.2.1 touch move 800 1386
python skpdb_probe\pc_client.py 192.0.2.1 touch up
```

## PC client

```powershell
python skpdb_probe\pc_client.py 192.0.2.1 ping
python skpdb_probe\pc_client.py 192.0.2.1 version
python skpdb_probe\pc_client.py 192.0.2.1 run 'id; getprop sys.usb.config'
python skpdb_probe\pc_client.py 192.0.2.1 info
python skpdb_probe\pc_client.py 192.0.2.1 config service_port=39876 knock_port=39877
python skpdb_probe\pc_client.py 192.0.2.1 webui start
```

Every command knocks before connecting, so nothing outside this client can even
find the port. `webui start` prints the page address described above; `webui
stop` shuts the WebUI server down, and `webui link` prints the address of a
server that is already running.

The memory commands and `--mode` / `--out` work the same way:

```powershell
python skpdb_probe\pc_client.py 192.0.2.1 meminfo
python skpdb_probe\pc_client.py 192.0.2.1 memmaps 12345
python skpdb_probe\pc_client.py 192.0.2.1 memread 12345 0x7f0000123456 256 --mode kernel
```

File transfers default to 900 KiB frames; `--chunk` overrides the size.

## Memory workbench (lab.py)

`pc_client.py` reads a known address. `lab.py` answers the other direction: a
value whose address is not known yet, a struct whose layout is a guess, a
pointer chain that has to be validated hop by hop. It speaks the same opcodes,
so the strict kernel path and its no-fault guarantee are unchanged.

```powershell
set SKPDB_KEY=...
python skpdb_probe\lab.py 192.0.2.1 maps 12345 --filter libUE4
python skpdb_probe\lab.py 192.0.2.1 modules 12345
python skpdb_probe\lab.py 192.0.2.1 view 12345 0x7a1c2d3e00 --type f32 --length 0x40
python skpdb_probe\lab.py 192.0.2.1 chain 12345 libUE4.so+0x1a2b3c4 0x10 0x28 --read 16 --read-type f32
python skpdb_probe\lab.py 192.0.2.1 ptrs 12345 0x7a1c2d3e00 --region anon
python skpdb_probe\lab.py 192.0.2.1 scan 12345 --type f32 --cmp eq --value 152.5
python skpdb_probe\lab.py 192.0.2.1 snap 12345 --type f32 --out heap.npz --min-value 0.001
python skpdb_probe\lab.py 192.0.2.1 filter 12345 heap.npz --cmp decreased --out narrowed.npz
python skpdb_probe\lab.py 192.0.2.1 watch 12345 0x7a1c2d3e00 --type f32 --count 50 --interval 0.1
```

| command | what it does |
| --- | --- |
| `maps` | every mapping with size, permissions and label; `--filter` is a regex |
| `modules` | file-backed images folded into one base/size row each |
| `view` | a typed slice (`f32`, `f64`, `i32`, `u64`, `ptr`, `ptr32`, ...) with a `module+0xoffset` label per address |
| `chain` | `base offset offset ...`; base is absolute or `module+0xoffset`, every hop prints the pointer and where it lands, a pointer outside every mapping is reported as a stale chain |
| `ptrs` | reverse lookup: every qword equal to an address, i.e. "who points at this struct" |
| `scan` | first pass with a known value (`eq`, `gt`, `lt`, `between`) |
| `snap` | first pass with an **unknown** value: keeps every plausible candidate and saves `address`/`value` arrays |
| `filter` | refine a snapshot by `changed`, `unchanged`, `increased`, `decreased`, `eq`, `gt`, `lt`, `between` |
| `watch` | poll one address and print a time series |

Scans default to `--region anon`, which selects writable anonymous mappings
(`[anon:...]`, `[heap]`) and skips `dalvik`, `art`, `jit`, `stack` and
profiler regions; `--region all` or `--region <regex>` widens or narrows that.
`scan`/`snap`/`filter` need numpy; the bundled interpreter has it:

```powershell
& "$env:USERPROFILE\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe" `
  skpdb_probe\lab.py 192.0.2.1 scan 12345 --type f32 --cmp eq --value 152.5
```

Two properties matter for a live target. The scan walks with `D`, so holes are
skipped on the device instead of costing one request per page, and a
non-resident page is skipped rather than faulted in, exactly like `memscan`.
Filtering happens on this side, which is why `snap` stores only plausible
candidates instead of all 4-byte slots: a 400 MiB heap holds 100 million of
them, and shipping that set over the wire on every pass would cost more than
the pass itself. `filter` merges the survivors into contiguous runs and reads
them back with `S`, so refining ten thousand candidates is a handful of
requests.

Worked example, three float coordinates in an otherwise unknown heap: `snap`
over `anon`, move the character, `filter --cmp changed`, stop moving,
`filter --cmp unchanged`, then `filter --cmp between --value 150 --value2 155`
leaves a handful of addresses; `ptrs` on one of them walks back to the object
that owns the struct.

## Native PC client (skpdb.exe)

`host/` builds a self-contained Windows client with no Python dependency:

```powershell
powershell -ExecutionPolicy Bypass -File .\host\build.ps1
```

The build uses the MSVC toolchain and static CRT, runs the protocol unit
tests, and writes `dist\skpdb.exe`. It links only `bcrypt` and `ws2_32`.

```powershell
dist\skpdb.exe --host 192.0.2.1 ping
dist\skpdb.exe --host 192.0.2.1 run 'id; getprop sys.usb.config'
dist\skpdb.exe --host 192.0.2.1 info
dist\skpdb.exe --host 192.0.2.1 config service_port=39876
dist\skpdb.exe --host 192.0.2.1 webui start
dist\skpdb.exe --host 192.0.2.1 get /data/local/tmp/t.bin t.bin --progress
dist\skpdb.exe --host 192.0.2.1 put t.bin /data/local/tmp/t.bin --progress
dist\skpdb.exe --host 192.0.2.1 install app.apk -r
dist\skpdb.exe --host 192.0.2.1 meminfo
dist\skpdb.exe --host 192.0.2.1 touch info
dist\skpdb.exe --host 192.0.2.1 touch tap 640 1386 60
dist\skpdb.exe --host 192.0.2.1 touch swipe 200 1600 1000 1600 400
```

Every command knocks first, which is what keeps the port invisible; on a LAN
that costs 20-40 ms. `--batch` reads commands from stdin and runs them all over
one connection:

```powershell
@'
ping
info
memread 1234 0x7f0000123456 32 --mode kernel
'@ | dist\skpdb.exe --host 192.0.2.1 --batch
```

In batch mode the root key comes from `SKPDB_KEY`, because stdin carries the
commands. Connect retries start at 40 ms, grow by 30 ms and give up after 2 s
with "the device did not accept the knock", so a wrong key or a deaf knock port
is reported instead of looking like a hang.

`install` behaves like `adb install`: it uploads the APK to
`/data/local/tmp`, runs `pm install` as the bridge's root uid, and accepts
`-r` (replace), `-d` (downgrade), `-g` (grant), `-t` (test packages), `-s`
(sdcard) and `--user <id>`. The install runs detached with a 300 s hard
timeout while the client polls a log file, so a package-manager confirmation
or verifier stall cannot block the bridge session. Exit status is 0 on
`Success` and 1 on failure, like adb.

`--host` is required; `--port` defaults to 39876 and `--knock-port` to 39877.
`--no-knock` connects without knocking (the port must already be open for this
address). `--window` controls request pipelining (default 8, the module
advertises `window=16`) and `--timeout` bounds each I/O operation. The client
sends a `Q` close frame before closing so the device returns to accepting the
next connection immediately.

## Tests

`host/tests/protocol_test.cpp` is run by `host/build.ps1` (ctest) and pins the
memory wire format: the 18-byte `M` request layout, the 5-byte `A` request,
and the `[backend][bytes]` response shape, so the host client and the device
channel cannot drift apart silently.

`skpdb_probe/tests/test_deploy_tcp.py` drives the real `deploy.py` command
line against `skpdb_probe/tests/mock_bridge.py`, an in-process fake of the
module that answers the knock, sends the 32-byte challenge, and speaks the
frame format and the `E` shell opcode. It covers chunked upload with the
SHA-256 round-trip, the detached installer and its `rc` file, the reconnect
that a reinstall forces (the mock drops the session mid-install), and that every
command is preceded by a knock whose HMAC verifies against the root key — the
mock records the raw datagrams, so a knock-less or unsigned connection fails
the run. It needs nothing but Python:

```powershell
python skpdb_probe\tests\test_deploy_tcp.py
```

`skpdb_probe/tests/test_lab.py` drives `lab.py` against the same mock, whose
`FakeMemory` holds a three-float struct, a pointer to it and a mapped module.
It pins the `M`, `A`, `D`, `P`, `Y` and `S` layouts the workbench relies on, and
covers the pointer chain, the typed view, both scan kinds and the
snapshot-refine path. It needs numpy for the scan checks:

```powershell
python skpdb_probe\tests\test_lab.py
```

The Android build itself is verified with `ndk-build`; the module behaviour on
a device is not covered by these checks. The kernel memory path in particular
cannot be exercised on the host: `meminfo` is the on-device check — its
`kernel=ok` line means the self-test read the probe pattern back through
`access_process_vm`, and `kernel=failed: ...` carries the exact failing step
(`find_get_pid`, `pid_task`, `access_process_vm`, `put_pid`,
`alloc_kernel_mem` or `read_kernel_mem`).
