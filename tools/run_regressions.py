"""Run the offline desktop suites; no phone, key or game is required."""
import argparse
import json
import os
import shutil
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--dotnet", default=shutil.which("dotnet") or str(root / ".tools/dotnet/dotnet.exe"))
parser.add_argument("--report", type=Path, default=root / ".scratch/regressions.json")
args = parser.parse_args()
env = dict(os.environ)
cache = root / ".tools"
for name, folder in {"TMP": "tmp", "TEMP": "tmp", "DOTNET_CLI_HOME": "dotnet_home", "NUGET_PACKAGES": "nuget", "APPDATA": "appdata"}.items():
    path = cache / folder
    path.mkdir(parents=True, exist_ok=True)
    env[name] = str(path)
build = subprocess.run([args.dotnet, "build", str(root / "desktop/tests/LookHarness"), "-v:q"], cwd=root, env=env)
if build.returncode:
    raise SystemExit(build.returncode)
suites = ["slots", "input", "profile", "transport", "client", "knock", "reconfigure", "joystick", "preview", "self", "control", "snapshot", "overlay", "micro", "gyro", "update"]
report = {"passed": False, "suites": []}
for suite in suites:
    result = subprocess.run([args.dotnet, str(root / "desktop/tests/LookHarness/bin/Debug/net8.0/LookHarness.dll"), f"--{suite}-test"], cwd=root, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
    report["suites"].append({"suite": suite, "passed": result.returncode == 0, "exitCode": result.returncode, "output": result.stdout + result.stderr})
    print(f"{suite}: {'PASS' if result.returncode == 0 else 'FAIL'}", flush=True)
report["passed"] = all(suite["passed"] for suite in report["suites"])
args.report.parent.mkdir(parents=True, exist_ok=True)
args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
raise SystemExit(0 if report["passed"] else 1)
