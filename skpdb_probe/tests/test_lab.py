#!/usr/bin/env python3
"""Host-side test for lab.py over the wireless transport.

Runs the real command line against mock_bridge.FakeDevice, whose fake address
space holds a three-float struct, a pointer to it and a mapped module. That
exercises the wire layouts lab.py depends on (M, A, D, P, Y) and the pointer
chain, typed view, value scan and snapshot-refine paths without a device.

    python skpdb_probe\\tests\\test_lab.py
"""

import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROBE = HERE.parent
sys.path.insert(0, str(HERE))

import mock_bridge  # noqa: E402

LAB = PROBE / "lab.py"
KEY = "00112233445566778899aabbccddeeff"

STRUCT = 0x7000000000
HOLDER = 0x7000100000
MODULE = 0x7100000000

failures = []


def check(condition, message):
    if condition:
        print("  ok   %s" % message)
    else:
        print("  FAIL %s" % message)
        failures.append(message)


def build_device():
    device = mock_bridge.FakeDevice()
    page = bytearray(4096)
    page[0x100:0x10c] = struct.pack("<3f", 1.5, 2.5, 3.5)
    page[0x200:0x204] = struct.pack("<f", 12.5)
    device.memory.add(STRUCT, page)
    holder = bytearray(4096)
    holder[0x40:0x48] = STRUCT.to_bytes(8, "little")
    device.memory.add(HOLDER, holder)
    device.memory.add(MODULE, bytearray(8192), perms="r-xp",
                      path="/data/app/com.tencent.tmgp.dfm/lib/arm64/libUE4.so")
    return device


def run(port, knock_port, *arguments):
    command = [sys.executable, str(LAB), "127.0.0.1", "--port", str(port),
               "--knock-port", str(knock_port), "--timeout", "20"]
    command.extend(str(argument) for argument in arguments)
    completed = subprocess.run(command, capture_output=True, text=True, timeout=180,
                               env=dict(os.environ, SKPDB_KEY=KEY),
                               cwd=str(PROBE.parent))
    return completed.returncode, completed.stdout + completed.stderr


def main():
    device = build_device()
    port, knock_port, stop = mock_bridge.serve(device)
    try:
        print("== maps ==")
        code, text = run(port, knock_port, "maps", 4242)
        check(code == 0, "maps exits 0")
        check("libUE4.so" in text, "maps lists the module mapping")
        check("mappings=3" in text, "maps counts all three mappings")

        print("== modules ==")
        code, text = run(port, knock_port, "modules", 4242)
        check(code == 0, "modules exits 0")
        check("libUE4.so" in text, "modules resolves the mapped image")
        check(f"{MODULE:012x}" in text, "modules reports the load base")

        print("== view ==")
        code, text = run(port, knock_port, "view", 4242, hex(STRUCT + 0x100),
                         "--type", "f32", "--length", "12")
        check(code == 0, "view exits 0")
        for value in ("1.5", "2.5", "3.5"):
            check(value in text, f"view prints {value}")
        check(f"{STRUCT + 0x100:012x}" in text, "view prints the addresses")

        print("== chain ==")
        code, text = run(port, knock_port, "chain", 4242, hex(HOLDER + 0x40), "0x100",
                         "--read", "12", "--read-type", "f32")
        check(code == 0, "chain exits 0")
        check(f"{STRUCT:#x}" in text, "chain dereferences the pointer")
        check("1.5" in text and "3.5" in text, "chain reads the struct after the hop")
        code, text = run(port, knock_port, "chain", 4242, "libUE4.so+0x100")
        check(code == 0, "chain resolves a module-relative base")
        check(f"{MODULE + 0x100:#x}" in text, "chain adds the load base")

        print("== ptrs ==")
        code, text = run(port, knock_port, "ptrs", 4242, hex(STRUCT), "--region", "anon")
        check(code == 0, "ptrs exits 0")
        check(f"{HOLDER + 0x40:012x}" in text, "ptrs finds the qword that holds the address")

        print("== scan ==")
        code, text = run(port, knock_port, "scan", 4242, "--type", "f32", "--cmp", "eq",
                         "--value", "2.5", "--region", "anon")
        check(code == 0, "scan exits 0")
        check(f"{STRUCT + 0x104:012x}" in text, "scan finds the exact address")
        check("hits=1 " in text, "scan reports a single hit")

        print("== region filter ==")
        code, text = run(port, knock_port, "scan", 4242, "--type", "f32", "--cmp", "eq",
                         "--value", "12.5", "--region", "libUE4")
        check(code == 0, "scan over a named region exits 0")
        check("hits=0" in text, "a named region scan reads only that mapping")

        print("== snapshot and refine ==")
        with tempfile.TemporaryDirectory() as directory:
            snap = Path(directory) / "snap.npz"
            kept = Path(directory) / "kept.npz"
            code, text = run(port, knock_port, "snap", 4242, "--type", "f32",
                             "--region", "anon", "--min-value", "0.001",
                             "--limit", "100000", "--out", str(snap))
            check(code == 0, "snap exits 0")
            check("hits=4" in text, "snap keeps the four plausible floats")
            check(snap.exists(), "snap writes the snapshot")
            code, text = run(port, knock_port, "filter", 4242, str(snap),
                             "--cmp", "unchanged", "--out", str(kept))
            check(code == 0, "filter exits 0")
            check("survivors=4 of 4" in text, "nothing changed, so nothing is dropped")
            check(kept.exists(), "filter writes the refined snapshot")

        print("== watch ==")
        code, text = run(port, knock_port, "watch", 4242, hex(STRUCT + 0x100),
                         "--type", "f32", "--count", "3", "--interval", "0")
        check(code == 0, "watch exits 0")
        check(text.count("1.5") == 3, "watch prints one sample per poll")
    finally:
        stop()

    print()
    if failures:
        print("FAILED: %d" % len(failures))
        return 1
    print("all lab checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
