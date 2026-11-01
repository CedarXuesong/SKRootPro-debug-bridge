"""Bounded, reversible real-device HAL test; the root key is only sent on stdin."""
import argparse
import getpass
from html.parser import HTMLParser
import json
import math
from pathlib import Path
import queue
import secrets
import statistics
import subprocess
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


class TokenParser(HTMLParser):
    token = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "meta" and attrs.get("name") == "lab-token":
            self.token = attrs["content"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--port", type=int, default=8767)
    parser.add_argument("--native-only", action="store_true", help="Preflight only; does not establish browser delivery")
    args = parser.parse_args()
    adb = [args.adb, "-s", args.serial]

    def command(*arguments, timeout=15):
        result = subprocess.run([*adb, *arguments], capture_output=True, text=True, encoding="utf-8",
                                errors="replace", timeout=timeout)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or result.stdout.strip() or f"ADB exited {result.returncode}")
        return result.stdout.strip()

    base = f"http://127.0.0.1:{args.port}"
    token_parser = TokenParser()
    with urllib.request.urlopen(base, timeout=5) as response:
        token_parser.feed(response.read().decode())
    token = token_parser.token
    if not token:
        raise RuntimeError("Local diagnostic page token is missing")

    def browser():
        with urllib.request.urlopen(urllib.request.Request(base + "/api/browser",
                headers={"X-Lab-Token": token}), timeout=5) as response:
            return json.load(response)

    if not args.native_only:
        print(f"Waiting for real phone sensor events: {base}/phone", flush=True)
        deadline = time.monotonic() + 60
        while True:
            report = browser()
            if report.get("layout") == "android-xyz-v2" and report["visible"] and \
                    report["secure"] and report.get("running") and len(report["samples"]) >= 30 and \
                    time.time() - report["received"] < 2 and not report["error"]:
                break
            if time.monotonic() >= deadline:
                raise RuntimeError("The phone page has no fresh, visible gyroscope events; no hook was installed")
            time.sleep(.5)

    key = getpass.getpass("Phone key (not saved): ")
    directory = "/data/local/tmp/skpdb-gyro-hal-" + secrets.token_hex(6)
    output = ROOT / ".scratch/gyro-lab"
    result = {"scope": "device-hal", "layer": "vendor-hal", "direct_channel": "unsupported",
              "browser_required": not args.native_only, "checks": [], "events": [], "passed": False}
    child = None
    writer = None
    finish = threading.Event()
    writer_error = []
    rate_lock = threading.Lock()
    rate = [0.0, 0.0, 0.0]
    captured = []
    created = False
    try:
        command("shell", f"mkdir {directory} && chmod 700 {directory}")
        created = True
        for local, name in (
            (output / "gyro_hal_device.so", "test.so"),
            (output / "gyro_probe", "gyro_probe"),
            (ROOT / "skpdb_probe/libs-canary/arm64-v8a/skpdb_loader", "loader"),
        ):
            command("push", str(local), directory + "/" + name)
        command("shell", f"chmod 700 {directory}/loader {directory}/gyro_probe")
        child = subprocess.Popen([*adb, "shell", "-T", directory + "/loader",
            directory + "/test.so", directory, "__skroot_module_main_18fdf393885363712349ef3de5411bd5",
            directory + "/result"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        child.stdin.write(key + "\n")
        child.stdin.close()
        lines = queue.Queue()

        def collect():
            for line in child.stdout:
                line = line.rstrip().replace(key, "<redacted>")
                captured.append(line)
                lines.put(line)
        reader = threading.Thread(target=collect, daemon=True)
        reader.start()
        deadline = time.monotonic() + 30
        while True:
            state = dict(line.split("=", 1) for line in command("shell",
                f"cat {directory}/status 2>/dev/null || true").splitlines() if "=" in line)
            if state.get("installed") == "1" and state.get("phase") in ("armed", "running"):
                print("HAL_READY " + json.dumps(state), flush=True)
                break
            if child.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("HAL preflight did not confirm installation")
            time.sleep(.1)

        def samples(name):
            records = []
            for line in command("shell", f"cat {directory}/{name}.txt").splitlines():
                values = line.split()
                if len(values) == 8 and values[0] in ("4", "16", "11", "15"):
                    records.append((int(values[1]), int(values[0]), *(float(x) for x in values[2:])))
            return records

        baseline = {name: samples(name) for name in ("root", "shell")}
        bias = {name: {kind: [statistics.fmean(row[j+2] for row in baseline[name] if row[1] == kind)
                             for j in range(3)] for kind in (4, 16)} for name in baseline}
        phone_baseline = browser()
        phone_bias = [statistics.fmean(row[j+2] for row in phone_baseline["samples"][-60:])
                      for j in range(3)] if not args.native_only and phone_baseline["samples"] else None
        if phone_bias and max(map(abs, phone_bias)) > .15:
            raise RuntimeError("Keep the phone still for the baseline; the page is measuring physical movement")

        def send():
            sequence = 0
            try:
                while not finish.is_set():
                    sequence += 1
                    with rate_lock:
                        vector = tuple(rate)
                    body = f"{sequence} " + " ".join(f"{x:.7f}" for x in vector)
                    command("shell", f"printf '{body}\\n' > {directory}/command.tmp && mv {directory}/command.tmp {directory}/command")
                    finish.wait(.035)
            except Exception as error:
                writer_error.append(str(error).replace(key, "<redacted>"))
                finish.set()
        writer = threading.Thread(target=send, daemon=True)
        writer.start()

        phases = [("X+", (.75, 0, 0)), ("X-", (-.75, 0, 0)), ("Y+", (0, .75, 0)),
                  ("Y-", (0, -.75, 0)), ("Z+", (0, 0, .5)), ("zero", (0, 0, 0))]
        for name, vector in phases:
            before = {label: len(samples(label)) for label in baseline}
            old_phone = browser()
            start_sequence = old_phone["samples"][-1][0] if old_phone["samples"] else 0
            with rate_lock:
                rate[:] = vector
            time.sleep(2)
            if writer_error:
                raise RuntimeError(writer_error[0])
            check = {"phase": name, "requested": vector, "receivers": {}, "passed": True}
            for label in baseline:
                fresh = samples(label)[before[label]:]
                if not fresh:
                    raise RuntimeError("No native sensor events: " + label)
                fresh = [row for row in fresh if row[0] >= fresh[0][0] + 500_000_000]
                estimates = {}
                for kind in (4, 16):
                    selected = [row for row in fresh if row[1] == kind]
                    if len(selected) < 20:
                        raise RuntimeError(f"{label} type {kind} has insufficient actual samples")
                    estimates[kind] = [statistics.fmean(row[j+2] for row in selected) - bias[label][kind][j]
                                       for j in range(3)]
                check["receivers"][label] = estimates
                check["passed"] &= all(max(abs(values[j] - vector[j]) for j in range(3)) < .12
                                       for values in estimates.values())
            if not args.native_only:
                report = browser()
                fresh = [row for row in report["samples"] if row[0] > start_sequence]
                if report.get("layout") != "android-xyz-v2" or report["page"] != phone_baseline["page"] or \
                        not report["secure"] or not report["visible"] or not report.get("running") or \
                        time.time() - report["received"] > 2 or len(fresh) < 30:
                    raise RuntimeError("Phone page stopped delivering fresh foreground events")
                fresh = [row for row in fresh if row[1] >= fresh[0][1] + 500]
                estimates = [statistics.fmean(row[j+2] for row in fresh) - phone_bias[j] for j in range(3)]
                check["browser"] = {"source": report["source"], "rate": estimates, "count": len(fresh),
                                    "samples": fresh}
                check["passed"] &= max(abs(estimates[j] - vector[j]) for j in range(3)) < .12
            result["checks"].append(check)
            print(json.dumps({key: value for key, value in check.items() if key != "browser"},
                             ensure_ascii=False), flush=True)
        finish.set()
        writer.join(timeout=15)
        time.sleep(.5)
        command("shell", f"printf '0\\n' > {directory}/command")
        child.wait(timeout=15)
        reader.join(timeout=3)
        result["events"] = captured
        restored = dict(line.split("=", 1) for line in command("shell",
            f"cat {directory}/status").splitlines() if "=" in line)
        result["passed"] = child.returncode == 0 and restored.get("phase") == "restored" and \
            restored.get("entry_read") == "1" and restored.get("entry_word") == restored.get("original") and \
            all(check["passed"] for check in result["checks"])
    except Exception as error:
        result["error"] = str(error).replace(key, "<redacted>")
    finally:
        finish.set()
        if writer:
            writer.join(timeout=15)
        if child and child.poll() is None:
            command("shell", f"printf '0\\n' > {directory}/command")
            child.wait(timeout=20)
        for name in ("status", "root.txt", "shell.txt", "test.log", "result"):
            try:
                result[name] = command("shell", f"cat {directory}/{name}")
            except Exception:
                pass
        result["events"] = captured
        output.mkdir(parents=True, exist_ok=True)
        path = output / ("hal-result-" + time.strftime("%Y%m%d-%H%M%S") + ".json")
        path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"passed": result["passed"], "error": result.get("error"), "report": str(path)},
                         ensure_ascii=False), flush=True)
        # The directory was created by this run; no other paths are removed.
        if created:
            try:
                command("shell", f"rm -f {directory}/test.so {directory}/gyro_probe {directory}/loader {directory}/command "
                        f"{directory}/command.tmp {directory}/status {directory}/status.tmp {directory}/root.txt "
                        f"{directory}/shell.txt {directory}/test.log {directory}/result; rmdir {directory}")
            except Exception as error:
                print("Temporary files need cleanup: " + str(error).replace(key, "<redacted>"), flush=True)
        key = ""
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
