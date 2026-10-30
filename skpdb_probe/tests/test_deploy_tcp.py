#!/usr/bin/env python3
"""Host-side test for deploy.py over the wireless (tcp) transport.

Runs the real command line against mock_bridge.FakeDevice, so the upload
chunking, SHA-256 verification, the detached installer and the
reconnect-after-restart path are all exercised without a device.

    python skpdb_probe\\tests\\test_deploy_tcp.py
"""

import hashlib
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROBE = HERE.parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(PROBE))

import mock_bridge  # noqa: E402

DEPLOY = PROBE / "deploy.py"
KEY = "00112233445566778899aabbccddeeff"

failures = []


def check(condition, message):
    if condition:
        print("  ok   %s" % message)
    else:
        print("  FAIL %s" % message)
        failures.append(message)


def run(port, *arguments, stdin=None, knock_port=None):
    command = [sys.executable, str(DEPLOY), "--host", "127.0.0.1",
               "--tcp-port", str(port), "--timeout", "10"]
    if knock_port is not None:
        command.extend(["--knock-port", str(knock_port)])
    # Every connection is challenged, so a key has to be supplied each time.
    if not any(argument in ("--key-stdin", "--key-file") for argument in arguments):
        command.append("--key-stdin")
        if stdin is None:
            stdin = KEY + "\n"
    command.extend(arguments)
    completed = subprocess.run(command, input=stdin, capture_output=True,
                               text=True, timeout=120, cwd=str(PROBE.parent))
    return completed.returncode, completed.stdout + completed.stderr


def make_archive(directory, name, size):
    path = Path(directory) / name
    path.write_bytes(os.urandom(size))
    return path


def received_knock(device):
    import hashlib
    import hmac as hmac_mod
    for packet in getattr(device, "knocks", []):
        if len(packet) != 60 or packet[:8] != b"SKPDBK01":
            continue
        want = hmac_mod.new(KEY.encode(), packet[:28], hashlib.sha256).digest()
        if hmac_mod.compare_digest(want, packet[28:]):
            return True
    return False


def deploy_remote(name):
    import deploy
    return getattr(deploy, name)


def test_upload_and_install():
    print("upload + install over tcp")
    device = mock_bridge.FakeDevice()
    port, knock, stop = mock_bridge.serve(device)
    try:
        with tempfile.TemporaryDirectory() as temp:
            wireless = make_archive(temp, "wireless.zip", 12 * 1024 + 7)

            code, output = run(port, "upload", "--wireless", str(wireless), knock_port=knock)
            check(code == 0, "upload exits 0")
            check("target: tcp 127.0.0.1:%d" % port in output, "reports the tcp target")
            check(hashlib.sha256(wireless.read_bytes()).hexdigest() in output,
                  "wireless checksum matches locally")
            check(device.files.get(deploy_remote("WIRELESS_REMOTE")) == wireless.read_bytes(),
                  "wireless archive landed byte-exact on the device")

            code, output = run(port, "install", "--key-stdin", stdin=KEY + "\n", knock_port=knock)
            check(code == 0, "install exits 0")
            check("installing wireless" in output, "installs the wireless module")
            check("exit=0" in output, "reads the installer rc file back")
            check(device.installs == [(deploy_remote("WIRELESS_REMOTE"), "skpdb_install_once")],
                  "installer invoked once with the wireless archive")
            check(device.key_seen == KEY, "root key reached the installer verbatim")

            code, output = run(port, "check", knock_port=knock)
            check(code == 0, "check exits 0")
            check("5.10.101-mock" in output, "check prints the kernel version")
            check("shell: PING" in output, "check round-trips a shell command")
            check(not device.unsupported, "no command fell through to the mock's 127 path")
            check(received_knock(device), "each connection was preceded by a signed knock")
    finally:
        stop()


def test_reinstall_survives_session_drop():
    print("reinstall over the serving module (session drop + reconnect)")
    device = mock_bridge.FakeDevice(drop_on_install=True)
    port, knock, stop = mock_bridge.serve(device)
    try:
        code, output = run(port, "install", "--key-stdin", stdin=KEY + "\n", knock_port=knock)
        check(code == 0, "install exits 0")
        check("session closed while starting" in output, "reports the dropped session")
        check("exit=0" in output, "reconnects and still reads the rc file")
        check(device.installs and device.installs[0][0] == deploy_remote("WIRELESS_REMOTE"),
              "installer invoked with the wireless archive")
        check(not device.unsupported, "no command fell through to the mock's 127 path")
        check(received_knock(device), "each connection was preceded by a signed knock")
    finally:
        stop()


def main():
    test_upload_and_install()
    test_reinstall_survives_session_drop()
    print()
    if failures:
        print("%d check(s) failed" % len(failures))
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
