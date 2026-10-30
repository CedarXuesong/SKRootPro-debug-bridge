#!/usr/bin/env python3
"""In-process fake of the SKPDB module, for host-side tests.

Speaks just enough of the real protocol to drive deploy.py without a
device: the 32-byte challenge answered with 32 bytes of HMAC, length-prefixed
frames, the "E"
shell opcode and "Q". The fake filesystem lives in a dict and only the shell
commands the deploy helper emits are understood; anything else answers
exit=127, so a wrong command shows up in the test instead of passing silently.

The detached install is emulated by writing the log/rc files immediately.
With drop_on_install the first successful install command kills the session
instead of replying, which is what reinstalling a module really does.
"""

import base64
import hashlib
import os
import re
import socket
import struct
import threading

# Every connection is challenged: the client answers 32 bytes of HMAC
# before the first frame, so there is no carry-over trust.
CHALLENGE = os.urandom(32)
MAX_FRAME = 16 << 20

LOADER = "/data/local/tmp/skpdb_loader"
INSTALLER = "/data/local/tmp/libskpdb_install.so"
PAGE = 4096


class FakeMemory:
    """A tiny address space for the memory opcodes.

    Segments are plain bytearrays, so a test can place a struct, a pointer to it
    and a gap that reports as a hole, and then drive the real client code over
    the real wire format without a device.
    """

    def __init__(self):
        self.segments = []

    def add(self, start, data, perms="rw-p", path="[anon:test]"):
        self.segments.append({"start": start, "data": bytearray(data),
                              "perms": perms, "path": path})
        self.segments.sort(key=lambda entry: entry["start"])
        return start

    def _segment(self, address):
        for entry in self.segments:
            if entry["start"] <= address < entry["start"] + len(entry["data"]):
                return entry
        return None

    def read(self, address, length):
        entry = self._segment(address)
        if entry is None:
            return b"", None
        offset = address - entry["start"]
        return bytes(entry["data"][offset:offset + length]), entry

    def write(self, address, data):
        entry = self._segment(address)
        if entry is None or "w" not in entry["perms"]:
            return 0
        offset = address - entry["start"]
        room = min(len(data), len(entry["data"]) - offset)
        entry["data"][offset:offset + room] = data[:room]
        return room

    def first_run(self, cursor, end, cap):
        for entry in self.segments:
            start = max(cursor, entry["start"])
            stop = min(end, entry["start"] + len(entry["data"]))
            if start >= stop:
                continue
            chunk = bytes(entry["data"][start - entry["start"]:stop - entry["start"]])
            return start, chunk[:cap], min(stop, start + cap)
        return 0, b"", end

    def scan(self, start, end, pattern, mask, max_hits):
        hits = []
        scanned = skipped = 0
        for entry in self.segments:
            begin = max(start, entry["start"])
            stop = min(end, entry["start"] + len(entry["data"]))
            if begin >= stop:
                continue
            skipped += begin - start
            start = begin
            data = bytes(entry["data"][begin - entry["start"]:stop - entry["start"]])
            scanned += len(data)
            for offset in range(0, len(data) - len(pattern) + 1):
                if len(hits) >= max_hits:
                    break
                if all((data[offset + index] & mask[index]) ==
                       (pattern[index] & mask[index]) for index in range(len(pattern))):
                    hits.append(begin + offset)
            start = stop
        return hits, scanned, skipped

    def resident_bitmap(self, start, pages):
        bitmap = bytearray((pages + 7) // 8)
        for page in range(pages):
            address = start + page * PAGE
            entry = self._segment(address)
            if entry is not None:
                bitmap[page // 8] |= 1 << (page % 8)
        return bytes(bitmap)

    def maps_text(self):
        lines = []
        for entry in self.segments:
            end = entry["start"] + len(entry["data"])
            lines.append(f"{entry['start']:x}-{end:x} {entry['perms']} "
                         f"00000000 00:00 0 {entry['path']}")
        return "\n".join(lines) + "\n"


def _u32(payload, offset):
    return struct.unpack_from("!I", payload, offset)[0]


def _u64(payload, offset):
    return struct.unpack_from("!Q", payload, offset)[0]



class FakeDevice:
    def __init__(self, drop_on_install=False, install_exit="0"):
        self.files = {LOADER: b"\x7fELF", INSTALLER: b"\x7fELF"}
        self.drop_on_install = drop_on_install
        self.install_exit = install_exit
        self.installs = []
        self.key_seen = None
        self.commands = []
        self.unsupported = []
        self.memory = FakeMemory()

    # -- memory ----------------------------------------------------------
    def memory_request(self, opcode, payload):
        """Answers M/A/D/P/Y against the fake address space; None if unknown."""
        memory = self.memory
        if opcode == b"A":
            return memory.maps_text().encode()
        if opcode == b"M":
            _pid, address, length, _mode = struct.unpack_from("!IQIB", payload, 0)
            data, entry = memory.read(address, length)
            return (b"\x01" if entry else b"\x00") + data
        if opcode == b"D":
            _pid, _start, end, cursor, cap, _mode = struct.unpack_from("!IQQQIB",
                                                                      payload, 0)
            data_start, data, next_cursor = memory.first_run(cursor, end, cap)
            return struct.pack("!QQI", data_start, next_cursor, len(data)) + data
        if opcode == b"P":
            _pid, start, pages, _mode = struct.unpack_from("!IQIB", payload, 0)
            return struct.pack("!I", pages) + memory.resident_bitmap(start, pages)
        if opcode == b"S":
            _pid, _mode, count = struct.unpack_from("!IBI", payload, 0)
            offset = 9
            body = struct.pack("!I", count)
            for _ in range(count):
                address, length = struct.unpack_from("!QI", payload, offset)
                offset += 12
                data, entry = memory.read(address, length)
                body += bytes([0, 1 if entry else 0])
                body += struct.pack("!II", len(data), 0)
                body += data
            return body
        if opcode == b"Y":
            start, end = _u64(payload, 4), _u64(payload, 12)
            max_hits = _u32(payload, 20)
            length = struct.unpack_from("!H", payload, 24)[0]
            flags = payload[27]
            pattern = payload[28:28 + length]
            mask = (payload[28 + length:28 + 2 * length] if flags & 1
                    else b"\xff" * length)
            hits, scanned, skipped = memory.scan(start, end, pattern, mask, max_hits)
            body = struct.pack("!IQQ", len(hits), scanned, skipped)
            for hit in hits:
                body += struct.pack("!Q", hit)
            return body
        return None

    # -- shell -----------------------------------------------------------
    def run(self, command):
        self.commands.append(command)
        try:
            return 0, self._run(command)
        except NotImplementedError:
            self.unsupported.append(command)
            return 127, "mock: unsupported command"

    def _run(self, command):
        match = re.match(r"^rm -f ([^;]+)$", command)
        if match:
            for token in re.findall(r"'([^']*)'", match.group(1)):
                self.files.pop(token, None)
            return ""

        match = re.match(r"^printf '%s' '([^']*)' >> '([^']+)'$", command)
        if match:
            path = match.group(2)
            self.files[path] = self.files.get(path, b"") + match.group(1).encode()
            return ""

        match = re.match(
            r"^base64 -d '([^']+)' > '([^']+)' && rm -f '([^']+)' && chmod 700 '([^']+)'$",
            command)
        if match:
            source, target, temp, _mode_target = match.groups()
            self.files[target] = base64.b64decode(self.files.get(source, b""))
            self.files.pop(temp, None)
            return ""

        match = re.match(r"^sha256sum '([^']+)'$", command)
        if match:
            data = self.files.get(match.group(1))
            if data is None:
                return ""
            return hashlib.sha256(data).hexdigest() + "  " + match.group(1)

        if command == "test -x %s && test -f %s && echo TOOLS_OK" % (LOADER, INSTALLER):
            return "TOOLS_OK"

        match = re.match(
            r"^rm -f '([^']+)' '([^']+)'; \(printf '%s\\n' '(.+)' \| (\S+) (\S+) '([^']+)'"
            r" (\S+) > '([^']+)' 2>&1; echo \$\? > '([^']+)'\) & echo STARTED$",
            command)
        if match:
            key = match.group(3)
            archive = match.group(6)
            mode = match.group(7)
            log_target = match.group(8)
            rc_target = match.group(9)
            self.key_seen = key
            self.installs.append((archive, mode))
            self.files[log_target] = b"mock install OK\n"
            self.files[rc_target] = (self.install_exit + "\n").encode()
            return "STARTED"

        match = re.match(r"^cat '([^']+)' 2>/dev/null; echo RC_END$", command)
        if match:
            return self.files.get(match.group(1), b"").decode(errors="replace") + "RC_END"

        match = re.match(r"^cat '([^']+)' 2>/dev/null$", command)
        if match:
            return self.files.get(match.group(1), b"").decode(errors="replace")

        if command == "echo PING":
            return "PING"

        if command.startswith("cat /proc/version"):
            extra = "/dev/ttyGS0\n" if "ttyGS" in command else ""
            return "Linux version 5.10.101-mock #1 SMP\n" + extra

        raise NotImplementedError(command)


def serve(device, listen_port=0, knock_port=0):
    """Starts the fake bridge on 127.0.0.1; returns (port, knock port, stop).

    Knocks are recorded on ``device.knocks`` so a test can prove the client
    sent a signed datagram before it connected."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", listen_port))
    server.listen(4)
    server.settimeout(0.5)
    knocker = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    knocker.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    knocker.bind(("127.0.0.1", knock_port))
    knocker.settimeout(0.5)
    state = {"running": True, "dropped": False, "armed": False}
    device.knocks = []

    def handle(connection):
        try:
            connection.settimeout(30)
            connection.sendall(CHALLENGE)
            if _recv_exact(connection, 32) is None:
                return
            while True:
                payload = _read_frame(connection)
                if payload is None:
                    return
                opcode = payload[0:1]
                if opcode == b"Q":
                    _send(connection, b"Q\0")
                    return
                if opcode != b"E":
                    answer = device.memory_request(opcode, payload[1:])
                    if answer is None:
                        _send(connection, opcode + b"\x01" + _msg("mock: unsupported opcode"))
                    else:
                        _send(connection, opcode + b"\x00" + answer)
                    continue
                exit_code, output = device.run(payload[1:].decode(errors="replace"))
                if (device.drop_on_install and not state["dropped"] and output == "STARTED"):
                    state["dropped"] = True
                    return
                raw = output.encode()
                _send(connection, b"E\0" + struct.pack("!II", exit_code, len(raw)) + raw)
        except OSError:
            return
        finally:
            try:
                connection.close()
            except OSError:
                pass

    def loop():
        while state["running"]:
            try:
                connection, _ = server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            if not state["armed"]:
                # Mirrors the real gate: the port only answers after a knock,
                # and one knock buys exactly one session.
                connection.close()
                continue
            state["armed"] = False
            threading.Thread(target=handle, args=(connection,), daemon=True).start()

    threading.Thread(target=loop, daemon=True).start()

    def knock_loop():
        while state["running"]:
            try:
                data, _ = knocker.recvfrom(256)
            except socket.timeout:
                continue
            except OSError:
                return
            device.knocks.append(data)
            state["armed"] = True

    threading.Thread(target=knock_loop, daemon=True).start()

    def stop():
        state["running"] = False
        for handle in (server, knocker):
            try:
                handle.close()
            except OSError:
                pass

    return server.getsockname()[1], knocker.getsockname()[1], stop


def _read_frame(connection):
    header = _recv_exact(connection, 4)
    if header is None:
        return None
    size = struct.unpack("!I", header)[0]
    if size > MAX_FRAME:
        return None
    return _recv_exact(connection, size)


def _recv_exact(connection, size):
    data = bytearray()
    while len(data) < size:
        part = connection.recv(size - len(data))
        if not part:
            return None
        data.extend(part)
    return bytes(data)


def _send(connection, payload):
    connection.sendall(struct.pack("!I", len(payload)) + payload)


def _msg(text):
    raw = text.encode()
    return struct.pack("!I", len(raw)) + raw
