#!/usr/bin/env python3
"""Deploy the SKPDB wireless module over its own TCP bridge.

The module exposes a root shell, so the flow is: upload the module archive into
/data/local/tmp, verify its SHA-256, and call the SKRoot installer that is
already on the device. Reinstalling the module drops the session it is serving;
the tool reconnects once the new module is listening again.

The root key is read from the terminal (getpass), stdin or a file, never from
the command line, so it does not end up in process listings.

Examples (from the repository root):

    python skpdb_probe\\deploy.py --host 192.0.2.1 upload `
        --wireless skpdb_probe\\skpdb_dev_network.zip

    :: reinstall the network module itself (session drops, then reconnects)
    python skpdb_probe\\deploy.py --host 192.0.2.1 install

    python skpdb_probe\\deploy.py --host 192.0.2.1 check
"""

import argparse
import base64
import getpass
import hashlib
import os
import struct
import sys
import time
from pathlib import Path
from types import SimpleNamespace

import pc_client

WIRELESS_REMOTE = "/data/local/tmp/skpdb_dev_network.zip"
LOADER = "/data/local/tmp/skpdb_loader"
INSTALLER = "/data/local/tmp/libskpdb_install.so"
UPLOAD_CHUNK = 12 * 1024
DEFAULT_TCP_PORT = 39876
DEFAULT_KNOCK_PORT = pc_client.DEFAULT_KNOCK_PORT

# Set from --key-file / --key-stdin / SKPDB_KEY so no command needs a
# terminal; connect() hands it to pc_client instead of prompting.
SERVICE_KEY = None

# Only for the single upgrade that replaces a pre-0.9.0 module, which still
# trusts a stored marker instead of challenging every connection.
LEGACY_TRUST = False


class Target:
    """A reachable wireless bridge."""

    def __init__(self, endpoint, port=DEFAULT_TCP_PORT, knock_port=DEFAULT_KNOCK_PORT):
        self.endpoint = endpoint
        self.port = port
        self.knock_port = knock_port

    def __str__(self):
        return "tcp %s:%d (knock %d)" % (self.endpoint, self.port, self.knock_port)


def resolve_target(args):
    if not args.host:
        raise SystemExit("tcp transport needs --host <address>")
    return Target(args.host, args.tcp_port, args.knock_port)


def connect(target, timeout=60.0, attempts=5, delay=1.0):
    last_error = None
    for _ in range(attempts):
        try:
            probe = SimpleNamespace(
                host=target.endpoint,
                port=target.port,
                knock_port=target.knock_port,
                no_knock=False,
                legacy_trust=LEGACY_TRUST,
                timeout=timeout,
                key=SERVICE_KEY,
            )
            return pc_client.connect(probe)
        except (ConnectionError, OSError, ValueError) as error:
            last_error = error
            time.sleep(delay)
    raise ConnectionError("cannot open %s: %s" % (target, last_error))


def execute(channel, command):
    body = pc_client.request(channel, b"E" + command.encode(), b"E")
    exit_code, size = struct.unpack("!II", body[:8])
    return exit_code, body[8:8 + size].decode(errors="replace")


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def upload(channel, local, remote):
    execute(channel, "rm -f '%s' '%s.b64'" % (remote, remote))
    with open(local, "rb") as source:
        while True:
            block = source.read(UPLOAD_CHUNK)
            if not block:
                break
            text = base64.b64encode(block).decode("ascii")
            code, output = execute(channel, "printf '%%s' '%s' >> '%s.b64'" % (text, remote))
            if code != 0:
                raise RuntimeError("chunk upload failed: %s" % output.strip())
    code, output = execute(
        channel,
        "base64 -d '%s.b64' > '%s' && rm -f '%s.b64' && chmod 700 '%s'"
        % (remote, remote, remote, remote),
    )
    if code != 0:
        raise RuntimeError("decode failed: %s" % output.strip())
    code, output = execute(channel, "sha256sum '%s'" % remote)
    remote_hash = output.split()[0] if output.split() else ""
    local_hash = sha256_of(local)
    if remote_hash != local_hash:
        raise RuntimeError("checksum mismatch: local=%s remote=%s" % (local_hash, remote_hash))
    return local_hash


def tools_ready(channel):
    code, output = execute(
        channel,
        "test -x %s && test -f %s && echo TOOLS_OK" % (LOADER, INSTALLER),
    )
    return code == 0 and "TOOLS_OK" in output


def read_key(args):
    if SERVICE_KEY is not None:
        return SERVICE_KEY
    if args.key_file:
        return Path(args.key_file).read_text(encoding="utf-8").strip()
    if args.key_stdin:
        return sys.stdin.readline().strip()
    if os.environ.get("SKPDB_KEY"):
        return os.environ["SKPDB_KEY"]
    return getpass.getpass("SKP root key: ")


def early_key(argv):
    """Reads the key flags straight from argv, before argparse sees them."""
    if "--key-stdin" in argv:
        return sys.stdin.readline().strip()
    if "--key-file" in argv:
        index = argv.index("--key-file")
        if index + 1 < len(argv):
            try:
                return Path(argv[index + 1]).read_text(encoding="utf-8").strip()
            except OSError as error:
                raise SystemExit("cannot read the key file: %s" % error)
    return None


def install_detached(channel, remote_zip, key, tag):
    """Starts the installer in the background with output on the device.

    Reinstalling a module stops the process that is serving this session,
    which closes the transport underneath us; redirecting to files keeps the
    installer from dying with SIGPIPE when the shell's stdout disappears.
    """
    quoted = key.replace("'", "'\\''")
    log = "/data/local/tmp/skpdb_install_%s.log" % tag
    rc = "/data/local/tmp/skpdb_install_%s.rc" % tag
    command = (
        "rm -f '%s' '%s'; "
        "(printf '%%s\\n' '%s' | %s %s '%s' skpdb_install_once > '%s' 2>&1; "
        "echo $? > '%s') & echo STARTED"
        % (log, rc, quoted, LOADER, INSTALLER, remote_zip, log, rc)
    )
    return execute(channel, command)


def read_install_result(target, tag, timeout=90):
    """Waits for the installer rc file, reconnecting across a reinstall."""
    rc = "/data/local/tmp/skpdb_install_%s.rc" % tag
    log = "/data/local/tmp/skpdb_install_%s.log" % tag
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            channel = connect(target, timeout=10.0, attempts=1)
        except Exception:
            time.sleep(2)
            continue
        try:
            code, output = execute(channel, "cat '%s' 2>/dev/null; echo RC_END" % rc)
            if "RC_END" in output:
                body = output.split("RC_END")[0].strip()
                if body:
                    code, log_text = execute(channel, "cat '%s' 2>/dev/null" % log)
                    return body, log_text
        except (ConnectionError, OSError):
            pass
        finally:
            try:
                pc_client.close_session(channel)
            except Exception:
                pass
        time.sleep(2)
    return None, None


def cmd_upload(args):
    if not args.wireless:
        raise SystemExit("nothing to upload: pass --wireless")
    target = resolve_target(args)
    print("target:", target)
    channel = connect(target)
    try:
        if not tools_ready(channel):
            raise SystemExit("loader/installer missing in /data/local/tmp")
        digest = upload(channel, args.wireless, WIRELESS_REMOTE)
        print("wireless -> %s  sha256=%s" % (WIRELESS_REMOTE, digest))
    finally:
        pc_client.close_session(channel)


def cmd_install(args):
    global SERVICE_KEY
    target = resolve_target(args)
    key = read_key(args)
    if not key:
        raise SystemExit("empty root key")
    SERVICE_KEY = key  # the transport handshake uses the same key
    print("note: the session runs on the wireless module; "
          "reinstalling it drops the connection until the new one listens")

    print("installing wireless from %s via %s" % (WIRELESS_REMOTE, target))
    try:
        channel = connect(target)
        if not tools_ready(channel):
            raise SystemExit("loader/installer missing in /data/local/tmp")
        code, output = install_detached(channel, WIRELESS_REMOTE, key, "wireless")
        print("  start:", output.strip())
        pc_client.close_session(channel)
    except (ConnectionError, OSError) as error:
        print("  session closed while starting (%s)" % error)

    rc, log = read_install_result(target, "wireless", timeout=args.timeout)
    if rc is None:
        print("  result: no rc file yet; check %s later" % WIRELESS_REMOTE)
    else:
        print("  exit=%s" % rc)
        if log:
            for line in log.strip().splitlines():
                print("  | " + line)


def cmd_check(args):
    target = resolve_target(args)
    print("target:", target)
    channel = connect(target)
    try:
        command = "cat /proc/version; echo CHECK_END"
        code, output = execute(channel, command)
        print(output.strip())
        code, body = execute(channel, "echo PING")
        print("shell:", body.strip())
    finally:
        pc_client.close_session(channel)


def main():
    global SERVICE_KEY
    parser = argparse.ArgumentParser(
        description="SKPDB deployment helper (wireless tcp)")
    parser.add_argument("--host", help="address of the wireless module, e.g. 192.0.2.1")
    parser.add_argument("--tcp-port", type=int, default=DEFAULT_TCP_PORT,
                        help="TCP port of the wireless module (default %d)" % DEFAULT_TCP_PORT)
    parser.add_argument("--knock-port", type=int, default=DEFAULT_KNOCK_PORT,
                        help="UDP knock port (default %d)" % DEFAULT_KNOCK_PORT)
    parser.add_argument("--timeout", type=int, default=90, help="install wait, seconds")
    parser.add_argument("--legacy-trust", action="store_true",
                        help="accept the pre-0.9.0 trust-marker preamble (only needed "
                             "for the one upgrade that installs 0.9.0 or later)")
    parser.add_argument("--key-file", help="read the SKP key from a file (all commands)")
    parser.add_argument("--key-stdin", action="store_true",
                        help="read the SKP key from stdin (all commands)")
    commands = parser.add_subparsers(dest="action", required=True)
    # Accepted both before and after the subcommand; argparse only applies a
    # default when the attribute is still unset, so the two positions agree.
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--key-file", help=argparse.SUPPRESS)
    common.add_argument("--key-stdin", action="store_true", help=argparse.SUPPRESS)
    common.add_argument("--legacy-trust", action="store_true", help=argparse.SUPPRESS)

    upload = commands.add_parser("upload", parents=[common], help="upload the module archive")
    upload.add_argument("--wireless")
    upload.set_defaults(func=cmd_upload)

    install = commands.add_parser("install", parents=[common], help="install the uploaded archive")
    install.set_defaults(func=cmd_install)

    check = commands.add_parser("check", parents=[common], help="probe the bridge")
    check.set_defaults(func=cmd_check)

    args = parser.parse_args()
    global LEGACY_TRUST
    LEGACY_TRUST = "--legacy-trust" in sys.argv[1:]
    # argparse lets a subparser's defaults overwrite the shared key flags, so
    # the position of --key-stdin/--key-file would otherwise change behaviour.
    # Reading them from the raw command line keeps both positions identical.
    if SERVICE_KEY is None:
        SERVICE_KEY = early_key(sys.argv[1:])
    try:
        if SERVICE_KEY is None and args.key_file:
            SERVICE_KEY = Path(args.key_file).read_text(encoding="utf-8").strip()
        elif SERVICE_KEY is None and args.key_stdin:
            SERVICE_KEY = sys.stdin.readline().strip()
        args.func(args)
    except (ConnectionError, OSError, RuntimeError, ValueError) as error:
        print("deploy: %s" % error, file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
