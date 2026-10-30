import argparse
import getpass
import hashlib
import hmac
import os
import re
import secrets
import shlex
import socket
import gzip
import struct
import sys
import time
import urllib.parse
from pathlib import Path

MAX_FRAME = 1024 * 1024
CHUNK = 900 * 1024

# The device keeps its TCP port closed until a signed UDP knock arrives, so no
# port scan can see it. The datagram also proves the sender holds the root key.
# Pre-0.9.0 modules answered a stored trust marker with this preamble; the
# constant only survives so an upgrade can recognise such a device.
LEGACY_TRUST = b"SKPT"
KNOCK_MAGIC = b"SKPDBK01"
KNOCK_SIZE = 60
DEFAULT_KNOCK_PORT = 39877

# Administration frames: 'B' + action byte + optional body -> text report.
ADMIN = b"B"
ADMIN_INFO = 1
ADMIN_CONFIG = 2
ADMIN_WEBUI_START = 3
ADMIN_WEBUI_STOP = 4

# Memory access opcodes: 'M' reads another process without ptrace, 'A' returns
# its /proc/<pid>/maps, 'G' reports which read backends work on this kernel.
MEM_READ = b"M"
MEM_MAPS = b"A"
MEM_SCATTER = b"S"
MEM_DUMP = b"D"
MEM_RESIDENT = b"P"
MEM_WRITE = b"X"
MEM_SYMBOLS = b"F"
MEM_SCAN = b"Y"
MAX_WRITE = 512 * 1024 - 32
SCAN_MAX_PATTERN = 64
SCAN_FLAG_MASK = 0x01
SYM_FLAG_LIST = 0x01
SYM_FLAG_ALL = 0x02
SCAN_MAX_HITS = 256
WRITE_ALLOW_FAULT = 0x10
WRITE_ALLOW_SHARED = 0x20
MEM_PAGE = 4096
MAX_SCATTER_RANGES = 64
MEM_INFO = b"G"
MEM_BACKENDS = {0: "none", 1: "kernel", 2: "vm_readv", 3: "proc_mem"}
MEM_MODES = {"auto": 0, "kernel": 1, "userspace": 2, "fault": 3}
# One request moves at most kMaxFrame minus the opcode, status and backend
# bytes; anything longer is split by the client at the transport chunk size.
MAX_MEM_READ = 1024 * 1024 - 16



def parse_pattern(text):
    """Returns (pattern, mask); mask is None unless the pattern has ?? wildcards."""
    if text.startswith("text:") or "??" not in text:
        return parse_bytes(text), None
    digits = text[2:] if text.startswith("0x") else text
    digits = "".join(character for character in digits if character != " ")
    if not digits or len(digits) % 2:
        raise SystemExit("wildcard pattern needs two hex digits or ?? per byte")
    pattern = bytearray()
    mask = bytearray()
    for index in range(0, len(digits), 2):
        pair = digits[index:index + 2]
        if pair == "??":
            pattern.append(0)
            mask.append(0x00)
        else:
            pattern.append(int(pair, 16))
            mask.append(0xff)
    return bytes(pattern), bytes(mask)


def parse_bytes(text):
    """Hex digits (with optional 0x/separators) or text: followed by literals."""
    if text.startswith("text:"):
        return text[5:].encode()
    digits = text[2:] if text.startswith("0x") else text
    digits = "".join(character for character in digits if character not in " :-_")
    if not digits or len(digits) % 2:
        raise SystemExit("byte string needs an even number of hex digits")
    return bytes.fromhex(digits)



def resident_runs(channel, pid, start, length, mode):
    """Page-aligned runs of resident pages, found with one P request."""
    pages = (length + MEM_PAGE - 1) // MEM_PAGE
    body = request(channel, MEM_RESIDENT + struct.pack("!IQIB", pid, start, pages, mode),
                   MEM_RESIDENT)
    count = struct.unpack_from("!I", body, 0)[0]
    bitmap = body[4:4 + (count + 7) // 8]
    runs = []
    page = 0
    while page < count:
        if not (bitmap[page // 8] & (1 << (page % 8))):
            page += 1
            continue
        first = page
        while page < count and (bitmap[page // 8] & (1 << (page % 8))):
            page += 1
        runs.append((start + first * MEM_PAGE, start + page * MEM_PAGE))
    return runs


def readable_ranges(channel, pid):
    """The readable mappings of a process, straight from /proc/<pid>/maps."""
    body = request(channel, MEM_MAPS + struct.pack("!I", pid), MEM_MAPS)
    ranges = []
    for line in body.decode(errors="replace").splitlines():
        parts = line.split()
        if len(parts) < 2 or not parts[1].startswith("r"):
            continue
        try:
            start, end = (int(value, 16) for value in parts[0].split("-"))
        except ValueError:
            continue
        ranges.append((start, end))
    return ranges


def scatter_read(channel, pid, ranges, mode):
    """One S request for up to 64 ranges; yields (address, data, backend)."""
    payload = MEM_SCATTER + struct.pack("!IBI", pid, mode, len(ranges))
    for address, length in ranges:
        payload += struct.pack("!QI", address, length)
    body = request(channel, payload, MEM_SCATTER)
    count = struct.unpack_from("!I", body, 0)[0]
    offset = 4
    out = []
    for index in range(count):
        status, backend = body[offset], body[offset + 1]
        size, error_bytes = struct.unpack_from("!II", body, offset + 2)
        offset += 10
        error = body[offset:offset + error_bytes].decode(errors="replace")
        offset += error_bytes
        data = body[offset:offset + size]
        offset += size
        out.append((ranges[index][0], data, backend, status, error))
    return out


def write_gzip(path, chunks):
    with gzip.open(path, "wb") as output:
        for chunk in chunks:
            output.write(chunk)


def hexdump(data, base):
    for offset in range(0, len(data), 16):
        row = data[offset:offset + 16]
        cells = " ".join(f"{byte:02x}" for byte in row)
        if len(row) > 8:
            head, tail = cells[:23], cells[23:]
            cells = f"{head}  {tail}"
        text = "".join(chr(byte) if 32 <= byte < 127 else "." for byte in row)
        print(f"{base + offset:016x}  {cells:<49} |{text}|")


def recv_exact(channel, size):
    data = bytearray()
    while len(data) < size:
        part = channel.recv(size - len(data))
        if not part:
            raise ConnectionError("peer closed the connection")
        data.extend(part)
    return bytes(data)


def recv_frame(channel):
    size = struct.unpack("!I", recv_exact(channel, 4))[0]
    if size > MAX_FRAME:
        raise ValueError(f"frame exceeds {MAX_FRAME} bytes")
    return recv_exact(channel, size)


def send_frame(channel, payload):
    if len(payload) > MAX_FRAME:
        raise ValueError(f"frame exceeds {MAX_FRAME} bytes")
    channel.sendall(struct.pack("!I", len(payload)) + payload)


def resolve_key(args):
    """Key for the HMAC challenge: argument, environment, then prompt.

    Automation (CI, agents, scripts) must not block on a terminal prompt, so
    the key can come from `args.key` or SKPDB_KEY. The prompt stays the
    default for interactive use.
    """
    key = getattr(args, "key", None)
    if key is None:
        key = os.environ.get("SKPDB_KEY")
    if key is None:
        key = getpass.getpass("SKP key: ")
    return key.encode() if isinstance(key, str) else key


def build_knock(key, timestamp=None, nonce=None):
    timestamp = int(time.time()) if timestamp is None else timestamp
    nonce = secrets.randbits(64) if nonce is None else nonce
    body = KNOCK_MAGIC + bytes([1, 0]) + b"\x00\x00"
    body += struct.pack("!Q", timestamp) + struct.pack("!Q", nonce)
    return body + hmac.new(key, body, hashlib.sha256).digest()


def send_knock(host, port, key, attempts=5):
    """Send the signed datagram; UDP gives no confirmation."""
    packet = build_knock(key)
    # All copies are identical, so the device's nonce cache ignores the extras;
    # sending them back to back costs nothing and covers a single lost datagram.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as channel:
        for _ in range(attempts):
            channel.sendto(packet, (host, port))


def authenticate(channel, key, allow_legacy):
    """Reads the 32-byte nonce and proves the key, or explains why not."""
    nonce = recv_exact(channel, 32)
    if nonce[:4] == LEGACY_TRUST:
        # A pre-0.9.0 module answers a stored trust marker with this preamble
        # and never reads a proof. It is only reachable with --legacy-trust,
        # which exists to upgrade such a module in place; every newer build
        # challenges each connection.
        if not allow_legacy:
            channel.close()
            raise RuntimeError(
                "the device runs a pre-0.9.0 module that trusts a stored marker; "
                "pass --legacy-trust once to upgrade it")
        return channel
    channel.sendall(hmac.new(key, nonce, hashlib.sha256).digest())
    return channel


def connect(args):
    key = resolve_key(args)
    args.key = key
    no_knock = getattr(args, "no_knock", False)
    allow_legacy = getattr(args, "legacy_trust", False)

    def open_and_authenticate(timeout):
        channel = socket.create_connection((args.host, args.port), timeout=timeout)
        try:
            channel.settimeout(args.timeout)
            return authenticate(channel, key, allow_legacy)
        except (OSError, ConnectionError):
            channel.close()
            raise

    # The port stays bound for idle_timeout_seconds after a session ends, so a
    # command that follows another one needs no knock at all. Trying the socket
    # first keeps that case at one round trip.
    if not no_knock:
        try:
            return open_and_authenticate(0.6)
        except OSError:
            pass
    # A phone whose radio is asleep drops the whole burst: the radio has to wake
    # before anything is delivered, and UDP never retries. So the knock is
    # repeated with a fresh nonce (the device dedupes nonces, so resending the
    # same packet would be ignored) for as long as the connect keeps failing.
    # The cadence is deliberately slow -- a tight burst wakes nothing and only
    # looks like a flood to the target's traffic accounting.
    deadline = time.monotonic() + (0 if no_knock else 20.0)
    next_knock = time.monotonic()
    delay = 0.04
    while True:
        now = time.monotonic()
        if not no_knock and now >= next_knock:
            send_knock(args.host, args.knock_port, key, attempts=1)
            next_knock = now + 0.4
        try:
            return open_and_authenticate(4)
        except OSError as error:  # includes the ConnectionError of a closed gate
            if time.monotonic() >= deadline:
                if no_knock:
                    raise
                raise RuntimeError(
                    "the device did not accept the knock (no port opened on "
                    f"{args.host}:{args.port}); check the key and the knock port"
                ) from error
            time.sleep(delay)
            delay = min(delay + 0.03, 0.25)


def response(channel, opcode):
    packet = recv_frame(channel)
    if len(packet) < 2 or packet[0:1] != opcode:
        raise RuntimeError(f"unexpected response: {packet[:32]!r}")
    if packet[1] != 0:
        size = struct.unpack("!I", packet[2:6])[0]
        raise RuntimeError(packet[6:6 + size].decode(errors="replace"))
    return packet[2:]


def request(channel, payload, opcode):
    send_frame(channel, payload)
    return response(channel, opcode)


def close_session(channel):
    try:
        send_frame(channel, b"Q")
        recv_frame(channel)
    finally:
        channel.close()


def admin(channel, action, body=b""):
    payload = request(channel, ADMIN + bytes([action]) + body, ADMIN)
    return payload.decode(errors="replace")


def webui_link(host, port):
    """The page needs no credentials: starting the server already required the
    root key, and the page closes the server three seconds after it stops
    beating (background tab, closed window, lost link)."""
    return f"http://{host}:{port}/"


def main():
    parser = argparse.ArgumentParser(description="SKPDB development bridge client (TCP)")
    parser.add_argument("host", help="address of the wireless module, for example 192.0.2.1")
    parser.add_argument("--port", type=int, default=39876, help="TCP service port")
    parser.add_argument("--knock-port", type=int, default=DEFAULT_KNOCK_PORT,
                        help=f"UDP knock port (default {DEFAULT_KNOCK_PORT})")
    parser.add_argument("--no-knock", action="store_true",
                        help="connect without sending the knock first (the port must "
                             "already be open for this address)")
    parser.add_argument("--batch", action="store_true",
                        help="read commands from stdin, one per line, over one connection")
    parser.add_argument("--legacy-trust", action="store_true",
                        help="accept the pre-0.9.0 trust-marker preamble (only to "
                             "upgrade such a module; every newer build challenges each connection)")
    parser.add_argument("--timeout", type=float, default=90, help="I/O timeout in seconds")
    parser.add_argument("--chunk", type=int, default=CHUNK,
                        help=f"transfer chunk size in bytes (default {CHUNK})")
    commands = parser.add_subparsers(dest="action", required=False)
    run = commands.add_parser("run")
    run.add_argument("command")
    commands.add_parser("shell")
    get = commands.add_parser("get")
    get.add_argument("remote")
    get.add_argument("local")
    put = commands.add_parser("put")
    put.add_argument("local")
    put.add_argument("remote")
    ls = commands.add_parser("ls")
    ls.add_argument("path", nargs="?", default=".")
    stat = commands.add_parser("stat")
    stat.add_argument("path")
    commands.add_parser("meminfo")
    memmaps = commands.add_parser("memmaps")
    memmaps.add_argument("pid", type=lambda text: int(text, 0))
    memmaps.add_argument("output", nargs="?", help="write the listing to this file")
    mread = commands.add_parser("mread")
    mread.add_argument("pid", type=int)
    mread.add_argument("ranges", nargs="+", metavar="address:length",
                       help="one or more ranges read in a single request")
    mread.add_argument("--mode", choices=sorted(MEM_MODES), default="auto")
    mread.add_argument("--out")
    poke = commands.add_parser("poke")
    poke.add_argument("pid", type=int)
    poke.add_argument("address", type=lambda text: int(text, 0))
    poke.add_argument("value")
    poke.add_argument("--mode", choices=sorted(MEM_MODES), default="auto")
    poke.add_argument("--allow-fault", action="store_true")
    poke.add_argument("--allow-shared", action="store_true")
    memscan = commands.add_parser("memscan")
    memscan.add_argument("pid", type=int)
    memscan.add_argument("rest", nargs="+",
                         help="<start> <length> <pattern>, or --maps <pattern>")
    memscan.add_argument("--maps", action="store_true",
                         help="scan every readable mapping of the process")
    memscan.add_argument("--mode", choices=sorted(MEM_MODES), default="auto")
    memscan.add_argument("--max-hits", type=int, default=SCAN_MAX_HITS)
    sym = commands.add_parser("sym")
    sym.add_argument("pid", type=int)
    sym.add_argument("module")
    sym.add_argument("name", nargs="?", default="")
    sym.add_argument("--list", action="store_true")
    sym.add_argument("--all", action="store_true", help="include .symtab local symbols")
    memdump = commands.add_parser("memdump")
    memdump.add_argument("pid", type=int)
    memdump.add_argument("start", type=lambda text: int(text, 0))
    memdump.add_argument("length", type=lambda text: int(text, 0))
    memdump.add_argument("output")
    memdump.add_argument("--mode", choices=sorted(MEM_MODES), default="auto")
    memdump.add_argument("--gzip", action="store_true",
                         help="write a gzip stream (holes become implicit, see the summary)")
    memdump.add_argument("--scatter", action="store_true",
                         help="read resident runs through S instead of D (64 runs per round trip)")
    resident = commands.add_parser("resident")
    resident.add_argument("pid", type=int)
    resident.add_argument("start", type=lambda text: int(text, 0))
    resident.add_argument("length", type=lambda text: int(text, 0))
    resident.add_argument("--mode", choices=sorted(MEM_MODES), default="auto")
    modbase = commands.add_parser("modbase")
    modbase.add_argument("pid", type=int)
    modbase.add_argument("needle", help="substring of the mapping path")
    memread = commands.add_parser("memread")
    memread.add_argument("pid", type=lambda text: int(text, 0))
    memread.add_argument("address", type=lambda text: int(text, 0))
    memread.add_argument("length", type=lambda text: int(text, 0))
    memread.add_argument("--mode", choices=tuple(MEM_MODES), default="auto",
                         help="kernel uses the no-trace path only and fails instead of "
                              "falling back; userspace skips the kernel path")
    memread.add_argument("--out", help="write the raw bytes to this file")
    touch = commands.add_parser("touch", help="inject touch into the real touchscreen")
    touch_sub = touch.add_subparsers(dest="touch_action", required=True)
    touch_sub.add_parser("info", help="print the resolved touchscreen and its ranges")
    touch_tap = touch_sub.add_parser("tap")
    touch_tap.add_argument("x", type=int)
    touch_tap.add_argument("y", type=int)
    touch_tap.add_argument("hold_ms", nargs="?", type=int, default=60)
    touch_tap.add_argument("slot", nargs="?", type=int, default=9)
    touch_swipe = touch_sub.add_parser("swipe")
    touch_swipe.add_argument("x1", type=int)
    touch_swipe.add_argument("y1", type=int)
    touch_swipe.add_argument("x2", type=int)
    touch_swipe.add_argument("y2", type=int)
    touch_swipe.add_argument("duration_ms", nargs="?", type=int, default=300)
    touch_swipe.add_argument("slot", nargs="?", type=int, default=9)
    touch_down = touch_sub.add_parser("down")
    touch_down.add_argument("x", type=int)
    touch_down.add_argument("y", type=int)
    touch_down.add_argument("slot", nargs="?", type=int, default=9)
    touch_move = touch_sub.add_parser("move")
    touch_move.add_argument("x", type=int)
    touch_move.add_argument("y", type=int)
    touch_move.add_argument("slot", nargs="?", type=int, default=9)
    touch_up = touch_sub.add_parser("up")
    touch_up.add_argument("slot", nargs="?", type=int, default=9)
    cursor = commands.add_parser("cursor", help="probe or control the kernel overlay cursor")
    cursor.add_argument("cursor_action", nargs="?", choices=("probe", "set", "destroy"),
                        default="probe")
    cursor.add_argument("x", nargs="?", type=int, default=0)
    cursor.add_argument("y", nargs="?", type=int, default=0)
    cursor.add_argument("--hide", action="store_true")
    commands.add_parser("ping")
    commands.add_parser("version")
    commands.add_parser("knock", help="send the signed UDP knock and exit")
    commands.add_parser("info", help="print the module report")
    config = commands.add_parser("config", help="change settings")
    config.add_argument("assignments", nargs="+",
                        help="key=value, e.g. service_port=39876 knock_port=39877")
    webui = commands.add_parser("webui", help="control the module WebUI server")
    webui.add_argument("webui_action", nargs="?", choices=("start", "stop", "link"),
                       default="start", metavar="start|stop|link",
                       help="start the server and print a self-unlocking link (default), "
                            "stop it, or just print the link for a running server")
    args = parser.parse_args()
    if not args.action and not args.batch:
        parser.error("a command is required")
    if not 1 <= args.chunk <= MAX_FRAME - 16:
        parser.error(f"--chunk must be between 1 and {MAX_FRAME - 16}")

    if args.action == "knock":
        send_knock(args.host, args.knock_port, resolve_key(args))
        print(f"knock sent to {args.host}:{args.knock_port} "
              "(the port opens for this address only)")
        return 0
    channel = connect(args)
    key = args.key
    try:
        if args.batch:
            return run_batch(channel, args, parser)
        return run_action(channel, args, parser)
    finally:
        close_session(channel)


def run_action(channel, args, parser):
    if args.action in ("run", "shell"):
        if args.action == "shell":
            print("Authenticated. Type :quit to exit.")
            while True:
                try:
                    command = input("skp# ")
                except EOFError:
                    break
                if command == ":quit":
                    break
                body = request(channel, b"E" + command.encode(), b"E")
                exit_code, size = struct.unpack("!II", body[:8])
                sys.stdout.buffer.write(body[8:8 + size])
                if body[8:8 + size] and not body[8:8 + size].endswith(b"\n"):
                    sys.stdout.buffer.write(b"\n")
                sys.stdout.buffer.flush()
                if exit_code:
                    print(f"[exit {exit_code}]", file=sys.stderr)
        else:
            body = request(channel, b"E" + args.command.encode(), b"E")
            exit_code, size = struct.unpack("!II", body[:8])
            sys.stdout.buffer.write(body[8:8 + size])
            sys.stdout.buffer.flush()
            if exit_code:
                return exit_code
    elif args.action == "get":
        offset = 0
        with Path(args.local).open("wb") as output:
            while True:
                payload = b"R" + struct.pack("!QI", offset, args.chunk) + args.remote.encode()
                data = request(channel, payload, b"R")
                size = struct.unpack("!I", data[:4])[0]
                chunk = data[4:4 + size]
                output.write(chunk)
                offset += size
                if size < args.chunk:
                    break
        print(f"received {offset} bytes")
    elif args.action == "put":
        path = Path(args.local)
        offset = 0
        first = True
        with path.open("rb") as source:
            while True:
                chunk = source.read(args.chunk)
                payload = b"W" + struct.pack("!Q", offset) + bytes([1 if first else 0]) + args.remote.encode() + b"\0" + chunk
                body = request(channel, payload, b"W")
                written = struct.unpack("!I", body[:4])[0]
                offset += written
                if written != len(chunk):
                    raise IOError(f"short write: {written}/{len(chunk)}")
                first = False
                if not chunk:
                    break
        print(f"sent {offset} bytes")
    elif args.action == "ls":
        body = request(channel, b"L" + args.path.encode(), b"L")
        size = struct.unpack("!I", body[:4])[0]
        sys.stdout.write(body[4:4 + size].decode(errors="replace"))
    elif args.action == "stat":
        body = request(channel, b"T" + args.path.encode(), b"T")
        size, mode, uid, gid, mtime = struct.unpack("!QIIIQ", body[:28])
        print(f"size={size} mode={mode:o} uid={uid} gid={gid} mtime={mtime}")
    elif args.action == "meminfo":
        sys.stdout.write(request(channel, MEM_INFO, MEM_INFO).decode(errors="replace"))
    elif args.action == "memmaps":
        body = request(channel, MEM_MAPS + struct.pack("!I", args.pid), MEM_MAPS)
        if args.output:
            Path(args.output).write_bytes(body)
            print(f"wrote {len(body)} bytes to {args.output}", file=sys.stderr)
        else:
            sys.stdout.write(body.decode(errors="replace"))
    elif args.action == "mread":
        mode = MEM_MODES[args.mode]
        ranges = []
        total = 0
        for spec in args.ranges:
            address_text, _, length_text = spec.partition(":")
            if not _ or not length_text:
                parser.error(f"expected address:length, got {spec!r}")
            address = int(address_text, 0)
            length = int(length_text, 0)
            if length <= 0:
                parser.error("length must be at least 1")
            total += length
            ranges.append((address, length))
        if len(ranges) > MAX_SCATTER_RANGES:
            parser.error(f"at most {MAX_SCATTER_RANGES} ranges per request")
        if total > MAX_MEM_READ:
            parser.error(f"ranges must fit in one frame (max {MAX_MEM_READ} bytes)")
        payload = MEM_SCATTER + struct.pack("!IBI", args.pid, mode, len(ranges))
        for address, length in ranges:
            payload += struct.pack("!QI", address, length)
        body = request(channel, payload, MEM_SCATTER)
        # request() strips the opcode and status bytes already.
        count = struct.unpack_from("!I", body, 0)[0]
        at = 4
        collected = bytearray()
        for index in range(count):
            status, backend = body[at], body[at + 1]
            data_bytes, error_bytes = struct.unpack_from("!II", body, at + 2)
            at += 10
            error = body[at:at + error_bytes].decode(errors="replace")
            at += error_bytes
            data = body[at:at + data_bytes]
            at += data_bytes
            address, length = ranges[index]
            note = f' status=error "{error}"' if status else ""
            print(f"[{index}] 0x{address:x} +{length} "
                  f"backend={MEM_BACKENDS.get(backend, backend)} bytes={data_bytes}{note}",
                  file=sys.stderr)
            collected += data
            if not args.out and data:
                hexdump(data[:64], address)
        if args.out:
            Path(args.out).write_bytes(collected)
            print(f"wrote {len(collected)} bytes to {args.out}", file=sys.stderr)
    elif args.action == "poke":
        data = parse_bytes(args.value)
        if not data:
            raise SystemExit("poke needs at least one byte")
        if len(data) > MAX_WRITE:
            raise SystemExit(f"poke payload is larger than {MAX_WRITE} bytes")
        flags = ((WRITE_ALLOW_FAULT if args.allow_fault else 0) |
                 (WRITE_ALLOW_SHARED if args.allow_shared else 0))
        payload = (MEM_WRITE + struct.pack("!IQIBB", args.pid, args.address, len(data),
                                           MEM_MODES[args.mode], flags)) + data
        response = request(channel, payload, MEM_WRITE)
        backend, written, shared = struct.unpack_from("!BIB", response, 0)
        note = response[6:].decode()
        print(f"wrote={written} of {len(data)} backend={MEM_BACKENDS[backend]} "
              f"shared={'yes' if shared else 'no'}", file=sys.stderr)
        if note:
            raise SystemExit(note)
    elif args.action == "memscan":
        if args.maps:
            if len(args.rest) != 1:
                raise SystemExit("memscan --maps takes exactly one pattern")
            pattern, mask = parse_pattern(args.rest[0])
            # One request per 4 MiB keeps a huge mapping from turning into a
            # single long kernel walk; hits usually appear near the start.
            ranges = []
            for range_start, range_end in readable_ranges(channel, args.pid):
                cursor = range_start
                while cursor < range_end:
                    ranges.append((cursor, min(cursor + 4 * 1024 * 1024, range_end)))
                    cursor += 4 * 1024 * 1024
        else:
            if len(args.rest) != 3:
                raise SystemExit("memscan takes <address> <length> <pattern> "
                                 "(or --maps <pattern>)")
            pattern, mask = parse_pattern(args.rest[2])
            start = int(args.rest[0], 0)
            ranges = [(start, start + int(args.rest[1], 0))]
        if not 0 < len(pattern) <= SCAN_MAX_PATTERN:
            raise SystemExit(f"pattern must be 1..{SCAN_MAX_PATTERN} bytes")
        flags = SCAN_FLAG_MASK if mask else 0
        remaining = args.max_hits
        total = scanned_total = skipped_total = 0
        for range_start, range_end in ranges:
            if remaining <= 0:
                break
            payload = (MEM_SCAN + struct.pack("!IQQIHBB", args.pid, range_start, range_end,
                                              remaining, len(pattern), MEM_MODES[args.mode],
                                              flags) + pattern + (mask or b""))
            try:
                response = request(channel, payload, MEM_SCAN)
            except RuntimeError:
                # A mapping with nothing resident reports a hole; a wide
                # scan must walk past it instead of stopping there.
                if not args.maps:
                    raise
                continue
            hits, scanned, skipped = struct.unpack_from("!IQQ", response, 0)
            scanned_total += scanned
            skipped_total += skipped
            total += hits
            remaining -= hits
            for index in range(hits):
                print(f"0x{struct.unpack_from('!Q', response, 20 + index * 8)[0]:x}")
        print(f"hits={total} scanned={scanned_total} skipped={skipped_total} "
              f"ranges={len(ranges)}", file=sys.stderr)
    elif args.action == "sym":
        module = args.module.encode()
        name = b"" if args.list else (args.name or "").encode()
        symbol_flags = (SYM_FLAG_LIST if args.list else 0) | (SYM_FLAG_ALL if args.all else 0)
        payload = (MEM_SYMBOLS + struct.pack("!IB", args.pid, symbol_flags) +
                   module + b"\0" + name)
        response = request(channel, payload, MEM_SYMBOLS)
        sys.stdout.write(response.decode())
        if response and not response.endswith(b"\n"):
            sys.stdout.write("\n")
    elif args.action == "memdump":
        mode = MEM_MODES[args.mode]
        start = args.start
        end = start + args.length
        written = 0
        runs = 0
        gaps = 0
        gap_bytes = 0
        if args.scatter:
            # One residency probe, then S requests carrying up to 64 runs
            # each: the same bytes for far fewer round trips than the D loop.
            resident = resident_runs(channel, args.pid, start, args.length, mode)
            pieces = []
            for run_start, run_end in resident:
                cursor = run_start
                while cursor < run_end:
                    take = min(64 * 1024, run_end - cursor)
                    pieces.append((cursor, take))
                    cursor += take
            with (gzip.open(args.output, "wb") if args.gzip
                  else Path(args.output).open("wb")) as output:
                for base in range(0, len(pieces), 64):
                    batch = pieces[base:base + 64]
                    for address, data, backend, status, error in scatter_read(
                            channel, args.pid, batch, mode):
                        if data:
                            runs += 1
                            written += len(data)
                            if args.gzip:
                                output.write(data)
                            else:
                                output.seek(address - start)
                                output.write(data)
                        elif status:
                            gaps += 1
                            gap_bytes += len(data)
                    if args.gzip:
                        continue
            covered = sum(length for _, length in pieces)
            gap_bytes = max(gap_bytes, args.length - covered)
        else:
            max_bytes = min(args.chunk, MAX_MEM_READ)
            with (gzip.open(args.output, "wb") if args.gzip
                  else Path(args.output).open("wb")) as output:
                cursor = start
                while cursor < end:
                    payload = (MEM_DUMP + struct.pack("!IQQQIB", args.pid, start, end,
                                                      cursor, max_bytes, mode))
                    body = request(channel, payload, MEM_DUMP)
                    data_start, next_cursor, size = struct.unpack_from("!QQI", body, 0)
                    data = body[20:20 + size]
                    if data_start > cursor:
                        gaps += 1
                        gap_bytes += data_start - cursor
                    if data:
                        runs += 1
                        written += len(data)
                        if args.gzip:
                            output.write(data)
                        else:
                            output.seek(data_start - start)
                            output.write(data)
                    if next_cursor > data_start + size:
                        gaps += 1
                        gap_bytes += next_cursor - (data_start + size)
                    if next_cursor <= cursor:
                        break
                    cursor = next_cursor
        print(f"dump={args.output} pid={args.pid} range=0x{start:x}..0x{end:x} "
              f"bytes={written} runs={runs} gaps={gaps} gap_bytes={gap_bytes}"
              f"{' gzip=yes' if args.gzip else ''}"
              f"{' scatter=yes' if args.scatter else ''}", file=sys.stderr)
    elif args.action == "resident":
        mode = MEM_MODES[args.mode]
        pages = (args.length + MEM_PAGE - 1) // MEM_PAGE
        payload = MEM_RESIDENT + struct.pack("!IQIB", args.pid, args.start, pages, mode)
        body = request(channel, payload, MEM_RESIDENT)
        count = struct.unpack_from("!I", body, 0)[0]
        bitmap = body[4:4 + (count + 7) // 8]
        resident_count = sum(1 for page in range(count)
                             if bitmap[page // 8] & (1 << (page % 8)))
        print(f"pages={count} page_bytes={MEM_PAGE} resident={resident_count} "
              f"holes={count - resident_count}")
        shown = 0
        page = 0
        while page < count and shown < 16:
            if bitmap[page // 8] & (1 << (page % 8)):
                page += 1
                continue
            first = page
            while page < count and not (bitmap[page // 8] & (1 << (page % 8))):
                page += 1
            print(f"hole 0x{args.start + first * MEM_PAGE:x}..0x{args.start + page * MEM_PAGE:x} "
                  f"({page - first} pages)")
            shown += 1
        if shown == 16:
            print("... more holes not shown")
    elif args.action == "modbase":
        body = request(channel, MEM_MAPS + struct.pack("!I", args.pid), MEM_MAPS)
        import re as _re
        for line in body.decode(errors="replace").splitlines():
            if args.needle not in line:
                continue
            match = _re.match(r"([0-9a-f]+)-([0-9a-f]+) (\S+) ([0-9a-f]+)", line)
            if not match:
                continue
            start = int(match.group(1), 16)
            offset = int(match.group(4), 16)
            print(f"path={line}")
            print(f"mapping=0x{start:x}-0x{int(match.group(2), 16):x} perms={match.group(3)} "
                  f"offset=0x{offset:x}")
            print(f"base=0x{start - offset:x}")
            break
        else:
            print(f'no mapping matches "{args.needle}"', file=sys.stderr)
            sys.exit(1)
    elif args.action == "memread":
        if not 1 <= args.length:
            parser.error("length must be at least 1")
        mode = MEM_MODES[args.mode]
        chunk = min(args.chunk, MAX_MEM_READ)
        data = bytearray()
        backend = 0
        while len(data) < args.length:
            want = min(args.length - len(data), chunk)
            payload = (MEM_READ
                       + struct.pack("!IQIB", args.pid, args.address + len(data), want, mode))
            body = request(channel, payload, MEM_READ)
            backend = body[0]
            got = body[1:]
            data += got
            if len(got) < want:
                break
        name = MEM_BACKENDS.get(backend, f"unknown({backend})")
        short = "" if len(data) == args.length else " (short read)"
        print(f"backend={name} bytes={len(data)} of {args.length}{short}", file=sys.stderr)
        if args.out:
            Path(args.out).write_bytes(data)
            print(f"wrote {len(data)} bytes to {args.out}", file=sys.stderr)
        else:
            hexdump(data, args.address)
    elif args.action == "touch":
        actions = {"info": 0, "down": 1, "move": 2, "up": 3, "tap": 4, "swipe": 5}
        action = actions[args.touch_action]
        slot = getattr(args, "slot", 0) or 0
        if args.touch_action == "swipe":
            x, y = args.x1, args.y1
            x2, y2 = args.x2, args.y2
            duration = args.duration_ms
        elif args.touch_action == "tap":
            x, y = args.x, args.y
            x2 = y2 = 0
            duration = args.hold_ms
        elif args.touch_action in ("down", "move"):
            x, y = args.x, args.y
            x2 = y2 = 0
            duration = 0
        else:
            x = y = x2 = y2 = 0
            duration = 0
        payload = (b"I" + struct.pack("!BBHIIIII", action, 0, slot, x, y, x2, y2, duration))
        body = request(channel, payload, b"I")
        print(body.decode(errors="replace"), end="")
    elif args.action == "cursor":
        if args.cursor_action == "probe":
            payload = b"C\x00"
        elif args.cursor_action == "destroy":
            payload = b"C\x02"
        else:
            payload = (b"C\x01" + struct.pack("!HHB", args.x, args.y, not args.hide))
        body = request(channel, payload, b"C")
        print(body.decode(errors="replace"), end="")
    elif args.action == "ping":
        print(request(channel, b"K", b"K").decode())
    elif args.action == "version":
        body = request(channel, b"V", b"V")
        size = struct.unpack("!I", body[:4])[0]
        print(body[4:4 + size].decode(errors="replace"))
    elif args.action == "info":
        print(admin(channel, ADMIN_INFO), end="")
    elif args.action == "config":
        print(admin(channel, ADMIN_CONFIG, ";".join(args.assignments).encode()), end="")
    elif args.action == "webui":
        requested = args.webui_action or "start"
        if requested in ("start", "link"):
            if requested == "start":
                print(admin(channel, ADMIN_WEBUI_START), end="")
            port_text = ""
            info_text = admin(channel, ADMIN_INFO)
            for line in info_text.splitlines():
                if line.startswith("webui_port="):
                    port_text = line.split("=", 1)[1].strip()
            if port_text and port_text != "0":
                print(f"open: {webui_link(args.host, int(port_text))}")
            else:
                print("the web ui is not running; start it with: webui start", file=sys.stderr)
        else:
            print(admin(channel, ADMIN_WEBUI_STOP), end="")


def run_batch(channel, args, parser):
    """Lines from stdin, one command each, all over the open connection."""
    worst = 0
    for line in sys.stdin:
        text = line.strip()
        if not text or text.startswith("#"):
            continue
        if text in ("quit", "exit"):
            break
        try:
            tokens = shlex.split(text)
        except ValueError as error:
            print(f"skpdb: {error}", file=sys.stderr)
            worst = 2
            continue
        try:
            one = parser.parse_args([args.host] + tokens)
        except SystemExit:
            worst = 2
            continue
        for name in ("port", "knock_port", "timeout", "chunk", "window", "progress", "key",
                     "mem_mode", "no_knock", "legacy_trust"):
            if hasattr(args, name):
                setattr(one, name, getattr(args, name))
        try:
            run_action(channel, one, parser)
        except SystemExit as exit_request:
            worst = exit_request.code if isinstance(exit_request.code, int) else 1
        except (RuntimeError, OSError) as error:
            print(f"skpdb: {error}", file=sys.stderr)
            worst = 1
    return worst




if __name__ == "__main__":
    try:
        main()
    except (ConnectionError, OSError, RuntimeError, ValueError) as error:
        print(f"skpdb: {error}", file=sys.stderr)
        raise SystemExit(1)

