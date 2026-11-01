"""Exercise the production HAL backend without installing a boot module."""
import json
from pathlib import Path
import secrets
import socket
import statistics
import threading
import time

ROOT = Path(__file__).resolve().parents[2]


def fields(text):
    return dict(line.split("=", 1) for line in text.splitlines() if "=" in line)


def phone_samples(lab, page=None, previous=0, settle=0):
    with lab.browser_lock:
        report = dict(lab.browser)
    if report.get("layout") != "android-xyz-v2":
        raise RuntimeError("请刷新手机网页，加载已修正的 Android 三轴映射")
    rows = [row for row in report["samples"] if row[0] > previous]
    if (page is not None and report["page"] != page) or not report["visible"] or \
            not report["secure"] or not report.get("running") or \
            time.time() - report["received"] > 2 or len(rows) < 30:
        raise RuntimeError("手机网页没有持续回传前台传感器事件")
    if settle:
        rows = [row for row in rows if row[1] >= rows[0][1] + settle]
        if len(rows) < 15:
            raise RuntimeError("手机网页过渡后有效样本不足")
    return report, rows


def run_global(lab, mode):
    directory = "/data/local/tmp/skpdb-gyro-hal-" + secrets.token_hex(6)
    finish = threading.Event()
    rate_lock = threading.Lock()
    rate = [0., 0., 0.]
    errors = []
    writer = network = sender = None
    created = launched = restored = False
    page = None
    result = lab.result
    try:
        if mode == "browser":
            report, rows = phone_samples(lab)
            page = report["page"]
            phone_bias = [statistics.fmean(row[j+2] for row in rows[-60:]) for j in range(3)]
            if max(map(abs, phone_bias)) > .15:
                raise RuntimeError("手机正在物理转动，请平放手机后重新开始")
        if lab.gyro(0)["installed"] != "0":
            raise RuntimeError("请先停止现有陀螺仪操控会话")
        output = ROOT / ".scratch/gyro-lab"
        files = [(output / "gyro_hal_device.so", "test.so"), (output / "gyro_probe", "gyro_probe"),
                 (ROOT / "skpdb_probe/libs-canary/arm64-v8a/skpdb_loader", "loader")]
        if any(not local.is_file() for local, _ in files):
            raise RuntimeError("请先编译 Canary 模块及 tools/gyro_lab/build_hal_test.ps1")
        result["phase"] = "准备全局 HAL 诊断"
        lab.execute(f"mkdir {directory}; chmod 700 {directory}")
        created = True
        for local, name in files:
            lab.upload(local, directory + "/" + name)
        lab.execute(f"chmod 700 {directory}/gyro_probe {directory}/loader")
        port = 40000 + secrets.randbelow(20000)
        if lab.execute(f"ss -H -ltn | grep -E ':{port}[[:space:]]' || true").strip():
            raise RuntimeError("临时诊断端口已占用，请重试")
        lab.execute(f"nohup toybox nc -s {lab.host} -l -p {port} -W 20 {directory}/loader {directory}/test.so "
                    f"{directory} __skroot_module_main_18fdf393885363712349ef3de5411bd5 {directory}/result "
                    f">{directory}/test.log 2>&1 </dev/null &")
        deadline = time.monotonic() + 8
        while True:
            try:
                network = socket.create_connection((lab.host, port), timeout=2)
                network.sendall(lab.key + b"\n")
                launched = True
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(.1)
        deadline = time.monotonic() + 25
        while True:
            state = fields(lab.execute(f"cat {directory}/status 2>/dev/null || true"))
            if state.get("installed") == "1" and state.get("phase") in ("armed", "running"):
                break
            log = lab.execute(f"cat {directory}/test.log 2>/dev/null || true")
            if "HAL_FAIL" in log or "loader dlopen:" in log or "loader dlsym:" in log or time.monotonic() > deadline:
                raise RuntimeError(log or "HAL 安装确认超时")
            if lab.cancel.wait(.1):
                raise RuntimeError("测试已停止")
        lab.log("HAL 全局处理点已确认；没有按应用 UID 定向")
        result["scope"] = "device-hal"
        result["layer"] = "vendor-hal"

        def samples(name):
            rows = []
            text = lab.execute(f"cat {directory}/{name}.txt")
            if "ERROR" in text:
                raise RuntimeError(name + ": " + text.strip())
            for line in text.splitlines():
                parts = line.split()
                if len(parts) == 8 and parts[0] in ("4", "16", "11", "15"):
                    rows.append((int(parts[1]), int(parts[0]), *(float(x) for x in parts[2:])))
            return rows

        baseline = {label: samples(label) for label in ("root", "shell")}
        bias = {label: {kind: [statistics.fmean(row[j+2] for row in rows if row[1] == kind)
                              for j in range(3)] for kind in (4, 16)} for label, rows in baseline.items()}
        plot_origin = time.monotonic()
        # Log/status downloads must not hold up the 200 ms control lease.
        sender = lab.independent()

        def send():
            sequence = 0
            try:
                while not finish.is_set() and not lab.cancel.is_set():
                    sequence += 1
                    with rate_lock:
                        text = str(sequence) + " " + " ".join(f"{x:.6f}" for x in rate)
                    sender.execute(f"printf '{text}\\n' >{directory}/command.tmp; mv {directory}/command.tmp {directory}/command")
                    finish.wait(.035)
            except Exception as error:
                errors.append(str(error).replace(lab.key.decode(), "<redacted>"))
                finish.set()
        writer = threading.Thread(target=send, daemon=True)
        writer.start()
        phases = [("X 正向", (.75, 0, 0)), ("X 反向", (-.75, 0, 0)),
                  ("Y 正向", (0, .75, 0)), ("Y 反向", (0, -.75, 0)),
                  ("Z 正向", (0, 0, .5)), ("停止输入归零", (0, 0, 0))]
        for name, vector in phases:
            result["phase"] = name
            before = {label: len(samples(label)) for label in baseline}
            with lab.browser_lock:
                old = dict(lab.browser)
            previous = old["samples"][-1][0] if old["samples"] else 0
            with rate_lock:
                rate[:] = vector
            if lab.cancel.wait(2):
                raise RuntimeError("测试已停止")
            if errors:
                raise RuntimeError(errors[0])
            readings = {}
            passed = True
            for label in baseline:
                rows = samples(label)[before[label]:]
                if not rows:
                    raise RuntimeError(label + " 没有实际传感器事件")
                rows = [row for row in rows if row[0] >= rows[0][0] + 500_000_000]
                readings[label] = {}
                for kind in (4, 16):
                    selected = [row for row in rows if row[1] == kind]
                    if len(selected) < 20:
                        raise RuntimeError(f"{label}/type {kind} 实际样本不足")
                    estimate = [statistics.fmean(row[j+2] for row in selected)-bias[label][kind][j] for j in range(3)]
                    readings[label][kind] = estimate
                    passed &= max(abs(estimate[j]-vector[j]) for j in range(3)) < .12
            phone = None
            if mode == "browser":
                report, rows = phone_samples(lab, page, previous, 500)
                phone = [statistics.fmean(row[j+2] for row in rows)-phone_bias[j] for j in range(3)]
                passed &= max(abs(phone[j]-vector[j]) for j in range(3)) < .12
                axis = next((j for j, x in enumerate(vector) if x), 0)
                now = time.monotonic() - plot_origin
                last = rows[-1][1]
                for row in rows:
                    result["samples"].append({"time": now+(row[1]-last)/1000, "axis": "xyz"[axis],
                        "requested": vector[axis], "target": row[axis+2]-phone_bias[axis]})
            summary = lambda values: " / ".join(f"{x:+.3f}" for x in values)
            result["checks"].append({"name": name,
                "target": summary(phone) if phone else "仅原生预检，未验证网页",
                "control": "root " + summary(readings["root"][4]) + " · shell " + summary(readings["shell"][4]),
                "passed": passed, "requested": vector, "native": readings, "browser": phone})
            lab.log(name + ("：通过" if passed else "：失败"))
            state = fields(lab.execute(f"cat {directory}/status"))
            result["hits"], result["updates"] = int(state["hits"]), int(state["updates"])
        with rate_lock:
            rate[:] = (0, 0, .5)
        if lab.cancel.wait(.7):
            raise RuntimeError("测试已停止")
        before = {label: len(samples(label)) for label in baseline}
        with lab.browser_lock:
            old = dict(lab.browser)
        previous = old["samples"][-1][0] if old["samples"] else 0
        finish.set()
        writer.join(timeout=15)
        result["phase"] = "验证 200 ms 租约后归零"
        if lab.cancel.wait(1):
            raise RuntimeError("测试已停止")
        passed = True
        for label in baseline:
            rows = samples(label)[before[label]:]
            if not rows:
                raise RuntimeError("租约归零没有实际采样")
            rows = [row for row in rows if row[0] >= rows[0][0] + 400_000_000 and row[1] == 4]
            if len(rows) < 15:
                raise RuntimeError("租约归零样本不足")
            passed &= max(abs(statistics.fmean(row[j+2] for row in rows)-bias[label][4][j])
                          for j in range(3)) < .12
        if mode == "browser":
            report, rows = phone_samples(lab, page, previous, 400)
            passed &= max(abs(statistics.fmean(row[j+2] for row in rows)-phone_bias[j]) for j in range(3)) < .12
        result["checks"].append({"name": "200 ms 租约后归零", "target": "停止发送后实际采样",
                                 "control": "400 ms 过渡后验证持续归零", "passed": passed})
        result["phase"] = "正在恢复 HAL"
    except Exception as error:
        result["phase"] = "正在停止" if lab.cancel.is_set() else "测试失败"
        lab.log(str(error).replace(lab.key.decode(), "<redacted>"))
    finally:
        finish.set()
        if writer:
            writer.join(timeout=15)
        if sender:
            sender.close()
        if launched:
            try:
                lab.execute(f"printf '0\\n' >{directory}/command")
                deadline = time.monotonic() + 130
                while True:
                    completed = lab.execute(f"cat {directory}/result 2>/dev/null || true")
                    log = lab.execute(f"cat {directory}/test.log 2>/dev/null || true")
                    if completed.strip() or "loader dlopen:" in log or "loader dlsym:" in log:
                        break
                    if time.monotonic() > deadline:
                        raise RuntimeError("临时诊断程序未确认退出")
                    time.sleep(.2)
                state = fields(lab.execute(f"cat {directory}/status 2>/dev/null || true"))
                restored = completed.strip() == "result=0" and state.get("phase") == "restored" and \
                    state.get("entry_read") == "1" and state.get("entry_word") == state.get("original")
                result["checks"].append({"name": "HAL 入口恢复回读", "target": state.get("entry_word", "未读到"),
                    "control": state.get("original", "未读到"), "passed": restored})
                lab.log(log.replace(lab.key.decode(), "<redacted>").strip())
                if not restored:
                    result["phase"] = "恢复未确认" if state.get("installed") == "1" else "测试失败"
            except Exception as error:
                result["phase"] = "恢复未确认"
                lab.log(str(error).replace(lab.key.decode(), "<redacted>"))
        if network:
            network.close()
        if created and (restored or not launched):
            try:
                lab.execute(f"rm -f {directory}/test.so {directory}/gyro_probe {directory}/loader {directory}/command "
                    f"{directory}/command.tmp {directory}/status {directory}/status.tmp {directory}/root.txt "
                    f"{directory}/shell.txt {directory}/test.log {directory}/result; rmdir {directory}")
            except Exception as error:
                result["phase"] = "清理未确认"
                lab.log(str(error))
        if restored and result["phase"] == "正在恢复 HAL":
            result["phase"] = ("网页闭环通过" if mode == "browser" else "原生预检通过 · 网页未验证") \
                if all(row["passed"] for row in result["checks"]) else "发现故障"
        elif restored and result["phase"] == "正在停止":
            result["phase"] = "已停止并恢复"
        result["running"] = False
        output = ROOT / ".scratch/gyro-lab"
        output.mkdir(parents=True, exist_ok=True)
        (output / ("global-" + time.strftime("%Y%m%d-%H%M%S") + ".json")).write_text(
            json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
