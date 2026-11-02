"""Check startup dialogs, window response, image policy, event logs and graceful close."""
import argparse
import ctypes
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import time
from ctypes import wintypes


def read_events(started, client):
    script = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$started = [datetime]::Parse($env:SKPDB_SMOKE_STARTED).ToLocalTime()
$suffix = $env:SKPDB_SMOKE_CLIENT.Substring(2)
$events = @()
$errors = @()
foreach ($spec in @(
    @{Log='Application'; Ids=@(1000,1026)},
    @{Log='Microsoft-Windows-CodeIntegrity/Operational'; Ids=@(3033,3077)}
)) {
    try {
        $found = @(Get-WinEvent -FilterHashtable @{LogName=$spec.Log;Id=$spec.Ids;StartTime=$started} -ErrorAction Stop)
        foreach ($event in $found) {
            if ($event.Message.IndexOf($suffix,[StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $events += @{log=$spec.Log;id=$event.Id;time=$event.TimeCreated.ToString('o');message=$event.Message}
            }
        }
    } catch {
        if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { $errors += $_.Exception.Message }
    }
}
@{events=$events;errors=$errors} | ConvertTo-Json -Depth 5 -Compress
"""
    run = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        env=dict(os.environ, SKPDB_SMOKE_STARTED=started, SKPDB_SMOKE_CLIENT=str(client)),
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
    )
    if run.returncode:
        raise RuntimeError(run.stderr)
    return json.loads(run.stdout.lstrip("\ufeff"))


def main():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, default=root / "dist/SKPDB/SKPDB.exe")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--expected-title", default="SKPDB",
                        help="Exact main window title, including version when present")
    parser.add_argument("--settle-seconds", type=float, default=10)
    parser.add_argument("--dotnet-root", type=Path, help="Optional runtime override; default uses the installed runtime")
    args = parser.parse_args()
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    log = root / ".scratch" / f"production-smoke-{stamp}.log"
    report_path = args.report or root / ".scratch" / f"production-smoke-{stamp}.json"
    result = {"created": datetime.now().astimezone().isoformat(), "exe": str(args.client.resolve()),
              "expectedTitle": args.expected_title, "log": str(log), "passed": False}
    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    user.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM,
                                       wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_size_t)]
    user.SendMessageTimeoutW.restype = wintypes.LPARAM
    user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    user.IsWindowEnabled.argtypes = [wintypes.HWND]
    user.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetProcessMitigationPolicy.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, ctypes.c_size_t]
    kernel.K32EnumProcessModulesEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                              ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]
    kernel.K32GetModuleFileNameExW.argtypes = [wintypes.HANDLE, wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.EnumChildWindows.argtypes = [wintypes.HWND, callback_type, wintypes.LPARAM]
    windows = []
    seen = {}
    log.parent.mkdir(exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ)
    if args.dotnet_root:
        environment["DOTNET_ROOT"] = str(args.dotnet_root.resolve())
    result["runtimeRootOverride"] = environment.get("DOTNET_ROOT")
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen([str(args.client.resolve())], cwd=args.client.resolve().parent,
                                   env=environment,
                                   stdout=output, stderr=subprocess.STDOUT)
    result["pid"] = process.pid
    try:
        @callback_type
        def capture(hwnd, _):
            pid = wintypes.DWORD()
            user.GetWindowThreadProcessId(ctypes.c_void_p(hwnd), ctypes.byref(pid))
            if pid.value == process.pid and user.IsWindowVisible(ctypes.c_void_p(hwnd)):
                title = ctypes.create_unicode_buffer(512)
                user.GetWindowTextW(ctypes.c_void_p(hwnd), title, 512)
                class_name = ctypes.create_unicode_buffer(256)
                user.GetClassNameW(hwnd, class_name, len(class_name))
                response = ctypes.c_size_t()
                responsive = bool(user.SendMessageTimeoutW(hwnd, 0, 0, 0, 2, 500, ctypes.byref(response)))
                entry = {"hwnd": int(hwnd), "title": title.value, "class": class_name.value,
                         "responsive": responsive, "enabled": bool(user.IsWindowEnabled(hwnd))}
                if class_name.value == "#32770":
                    texts = []

                    @callback_type
                    def child_text(child, _):
                        text = ctypes.create_unicode_buffer(2048)
                        user.GetWindowTextW(ctypes.c_void_p(child), text, len(text))
                        if text.value:
                            texts.append(text.value)
                        return True

                    user.EnumChildWindows(hwnd, child_text, 0)
                    entry["text"] = texts
                windows.append(entry)
                seen[(int(hwnd), title.value, class_name.value)] = entry
            return True

        deadline = time.monotonic() + max(2, args.settle_seconds)
        while time.monotonic() < deadline and process.poll() is None:
            windows.clear()
            user.EnumWindows(capture, 0)
            time.sleep(0.25)
        windows.clear()
        user.EnumWindows(capture, 0)
        result["observedWindows"] = list(seen.values())
        result["visibleWindows"] = windows
        result["runningAtCheck"] = process.poll() is None
        result["unexpectedWindows"] = [w for w in seen.values()
                                       if w["class"] == "#32770" or (w["title"] and w["title"] != args.expected_title)]
        if result["runningAtCheck"]:
            handle = kernel.OpenProcess(0x0410, False, process.pid)
            if not handle:
                raise ctypes.WinError(ctypes.get_last_error())
            try:
                policy = wintypes.DWORD()
                if not kernel.GetProcessMitigationPolicy(handle, 8, ctypes.byref(policy), ctypes.sizeof(policy)):
                    raise ctypes.WinError(ctypes.get_last_error())
                result["signaturePolicyFlags"] = policy.value
                modules = (wintypes.HMODULE * 2048)()
                needed = wintypes.DWORD()
                if not kernel.K32EnumProcessModulesEx(handle, modules, ctypes.sizeof(modules), ctypes.byref(needed), 3):
                    raise ctypes.WinError(ctypes.get_last_error())
                paths = []
                for module in modules[:min(len(modules), needed.value // ctypes.sizeof(wintypes.HMODULE))]:
                    path = ctypes.create_unicode_buffer(32768)
                    if kernel.K32GetModuleFileNameExW(handle, module, path, len(path)):
                        paths.append(path.value)
                result["loadedModules"] = paths
            finally:
                kernel.CloseHandle(handle)
        main_windows = [w for w in windows if w["title"] == args.expected_title and w["class"] != "#32770"]
        for window in main_windows:
            user.PostMessageW(window["hwnd"], 0x0010, 0, 0)
        try:
            process.wait(timeout=8)
            result["gracefulClose"] = process.returncode == 0
        except subprocess.TimeoutExpired:
            result["gracefulClose"] = False
        result["exitCode"] = process.poll()
        time.sleep(1)
        result["eventCheck"] = read_events(result["created"], args.client.resolve())
        result["passed"] = (result["runningAtCheck"] and len(main_windows) == 1
                            and all(w["responsive"] and w["enabled"] for w in main_windows)
                            and not result["unexpectedWindows"] and result["gracefulClose"]
                            and result.get("signaturePolicyFlags", 3) & 3 == 0
                            and not result["eventCheck"]["events"] and not result["eventCheck"]["errors"])
    except Exception as error:
        result["error"] = repr(error)
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)
        report_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "loadedModules"}, ensure_ascii=False), flush=True)
    if not result["passed"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
