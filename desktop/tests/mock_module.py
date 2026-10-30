#!/usr/bin/env python3
"""Fake SKPDB module for the WinUI desktop client.

Speaks the desktop wire protocol (signed UDP knock, HMAC challenge, length
prefixed frames) and pretends to be a phone: touch info, display rotation,
foreground package and touch batches. Used to exercise the gamepad page
without a device.

Usage:
    python desktop/tests/mock_module.py --port 39876 --knock-port 39877

Every touch batch is printed as one line per event, so a UI test can prove
that a mode switch releases the camera finger.
"""

import argparse
import hashlib
import hmac
import os
import socket
import struct
import sys
import threading
import time

FOREGROUND_PACKAGE = "com.demo.game"


def now():
    return time.strftime("%H:%M:%S")


def log(message):
    print(f"{now()} {message}", flush=True)


def recv_exact(conn, size):
    data = b""
    while len(data) < size:
        chunk = conn.recv(size - len(data))
        if not chunk:
            raise ConnectionError("peer closed")
        data += chunk
    return data


def send_frame(conn, opcode, payload=b""):
    body = opcode + b"\x00" + payload
    conn.sendall(struct.pack("!I", len(body)) + body)


def send_error(conn, opcode, message):
    encoded = message.encode("utf-8")
    body = opcode + b"\x01" + struct.pack("!I", len(encoded)) + encoded
    conn.sendall(struct.pack("!I", len(body)) + body)


INFO_TEXT = """name=SKPDB mock
version=0.12.3-mock
id32=mock0001
pid=4242
uptime_seconds=123
service_port={port}
knock_port={knock_port}
sessions=1
peers=1
sessions_total=1
memory_backends=mock
listening=yes
fast_port=0
fast_token=
"""

TOUCH_INFO = """touch=ready
name=mock_touch
event=/dev/input/event9
input_dev=mock
screen=1280x2772
slots=0..9
btn_touch=1
btn_tool_finger=1
"""

DISPLAY_TEXT = (
    "Display Devices:\n"
    "  mDisplayId=0\n"
    "  Viewport INTERNAL: orientation=0, logicalFrame=Rect(0, 0 - 1280, 2772), "
    "physicalFrame=Rect(0, 0 - 1280, 2772), deviceSize=1280x2772\n"
)

ACTIVITY_TEXT = (
    f"  mResumedActivity: ActivityRecord{{1111aaa u0 {FOREGROUND_PACKAGE}/"
    f".MainActivity t42}}\n"
)


def handle_client(conn, address, options):
    try:
        nonce = os.urandom(32)
        conn.sendall(nonce)
        proof = recv_exact(conn, 32)
        expected = hmac.new(options.key, nonce, hashlib.sha256).digest()
        if not hmac.compare_digest(proof, expected):
            log(f"auth failed from {address[0]}:{address[1]}")
            return
        log(f"authenticated {address[0]}:{address[1]}")

        while True:
            header = recv_exact(conn, 4)
            length = struct.unpack("!I", header)[0]
            if length == 0 or length > 1 << 20:
                log(f"invalid frame length {length}")
                return
            frame = recv_exact(conn, length)
            opcode = frame[0:1]
            body = frame[1:]

            if opcode == b"B":
                send_frame(conn, b"B", INFO_TEXT.format(
                    port=options.port, knock_port=options.knock_port).encode())
            elif opcode == b"I":
                action = body[0] if body else 0
                if action == 0:
                    send_frame(conn, b"I", TOUCH_INFO.encode())
                else:
                    log(f"touch action={action} body={body.hex()}")
                    send_frame(conn, b"I", b"ok")
            elif opcode == b"J":
                log_batch(body)
                send_frame(conn, b"J", b"ok")
            elif opcode == b"C":
                sub = body[0] if body else 0
                if sub == 1 and len(body) >= 6:
                    x, y, visible = struct.unpack("!HHB", body[1:6])
                    log(f"cursor set x={x} y={y} visible={visible}")
                elif sub == 0:
                    log("cursor probe")
                else:
                    log(f"cursor sub={sub}")
                send_frame(conn, b"C", b"ok")
            elif opcode == b"E":
                command = body.decode("utf-8", "replace")
                if "dumpsys display" in command:
                    output = DISPLAY_TEXT
                elif "dumpsys activity activities" in command:
                    output = ACTIVITY_TEXT
                elif "dumpsys window" in command:
                    output = (f"  mCurrentFocus=Window{{2222bbb u0 {FOREGROUND_PACKAGE}/"
                              f"{FOREGROUND_PACKAGE}.MainActivity}}\n")
                elif "echo MOCK" in command:
                    output = "MOCK\n"
                else:
                    output = ""
                payload = struct.pack("!II", 0, len(output.encode())) + output.encode()
                send_frame(conn, b"E", payload)
                if "dumpsys" not in command:
                    log(f"exec {command!r} -> {output.strip()!r}")
            elif opcode == b"K":
                send_frame(conn, b"K", b"pong")
            else:
                log(f"unsupported opcode {opcode!r} body={body[:32].hex()}")
                send_error(conn, opcode, "mock: unsupported opcode")
    except ConnectionError:
        log(f"closed {address[0]}:{address[1]}")
    except Exception as error:  # noqa: BLE001
        log(f"error {type(error).__name__}: {error}")
    finally:
        try:
            conn.close()
        except OSError:
            pass


def log_batch(body):
    if not body:
        return
    count = body[0]
    parts = [f"batch n={count}"]
    for index in range(count):
        offset = 1 + index * 12
        if offset + 12 > len(body):
            break
        action, _flags, slot, x, y = struct.unpack(
            "!BBHII", body[offset:offset + 12])
        names = {1: "down", 2: "move", 3: "up"}
        parts.append(f"{names.get(action, action)} slot={slot} x={x} y={y}")
    log(" | ".join(parts))


def knock_loop(sock, options):
    while True:
        try:
            data, address = sock.recvfrom(256)
            log(f"knock {len(data)} bytes from {address[0]}:{address[1]}")
        except OSError:
            return


def serve(options):
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind((options.bind, options.port))
    listener.listen(4)
    listener.settimeout(0.5)

    knocker = None
    try:
        knocker = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        knocker.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        knocker.bind((options.bind, options.knock_port))
        threading.Thread(target=knock_loop, args=(knocker, options), daemon=True).start()
    except OSError as error:
        log(f"knock port unavailable: {error}")
        if knocker is not None:
            knocker.close()
            knocker = None

    log(f"READY tcp={options.bind}:{options.port} knock={options.bind}:{options.knock_port}")
    try:
        while True:
            try:
                conn, address = listener.accept()
            except socket.timeout:
                continue
            conn.settimeout(30)
            threading.Thread(target=handle_client, args=(conn, address, options),
                             daemon=True).start()
    except KeyboardInterrupt:
        pass
    finally:
        listener.close()
        if knocker is not None:
            knocker.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=39876)
    parser.add_argument("--knock-port", type=int, default=39877)
    parser.add_argument("--key", default="test-key",
                        help="shared key the client must send (default: test-key)")
    options = parser.parse_args()
    options.key = options.key.encode()
    serve(options)


if __name__ == "__main__":
    main()
