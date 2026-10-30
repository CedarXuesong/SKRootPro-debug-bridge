"""Run the already-built WinUI harness against the local protocol fixture."""
import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import time


def wait_for(process, log, success, failure, seconds=55):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        content = log.read_text(encoding="utf-8-sig", errors="replace") if log.exists() else ""
        if failure and failure in content:
            raise RuntimeError(content)
        match = re.search(success, content)
        if match:
            return match, content
        if process.poll() is not None:
            raise RuntimeError(f"Process exited with {process.returncode}: {content}")
        time.sleep(0.1)
    raise TimeoutError(f"Timed out waiting for {success}; see {log}")


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--isolate-overlays", action="store_true", help="Restrict native image loading in the test host only")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    scratch = root / ".scratch"
    scratch.mkdir(exist_ok=True)
    report_path = args.report or scratch / f"desktop-ui-{stamp}.json"
    env = dict(os.environ, DOTNET_ROOT=str(root / ".tools/dotnet"))
    peer = host = None
    report = {"created": datetime.now().astimezone().isoformat(), "passed": False,
              "overlayIsolation": args.isolate_overlays, "suites": []}
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    try:
        peer_log = scratch / f"audit-peer-{stamp}.log"
        report["peerLog"] = str(peer_log)
        with peer_log.open("w", encoding="utf-8") as output:
            peer = subprocess.Popen([
                str(root / ".tools/dotnet/dotnet.exe"),
                str(root / "desktop/tests/LookHarness/bin/Debug/net8.0/LookHarness.dll"),
                "--snapshot-ui-peer"], cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT, creationflags=flags)
        match, _ = wait_for(peer, peer_log, r"SNAPSHOT_UI_PEER host=127\.0\.0\.1 port=(\d+)", None, 10)
        port = match.group(1)
        harness = root / "desktop/tests/DesktopInputHarness/bin/Debug/net8.0-windows10.0.19041.0/win-x64/DesktopInputHarness.exe"
        startup = subprocess.STARTUPINFO() if os.name == "nt" else None
        if startup is not None:
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = 0
        for suite, marker in [("input-ui-test", "INPUT_UI"), ("snapshot-ui-test", "SNAPSHOT_UI")]:
            log = scratch / f"audit-{suite}-{stamp}.log"
            with log.open("w", encoding="utf-8") as output:
                command = [str(harness), "127.0.0.1", f"--{suite}", f"--port={port}"]
                if args.isolate_overlays:
                    command.append("--isolate-overlays")
                host = subprocess.Popen(command,
                                        cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT, startupinfo=startup)
            _, content = wait_for(host, log, marker + "_PASS", marker + "_FAIL")
            report["suites"].append({"name": suite, "passed": True, "log": str(log),
                                     "nativeInputSkipped": "SKIP native page input" in content})
            print(content, flush=True)
            stop(host)
            host = None
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        stop(host)
        stop(peer)
        report["fixtureProcessesStopped"] = True
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"UI report: {report_path}", flush=True)


if __name__ == "__main__":
    main()
