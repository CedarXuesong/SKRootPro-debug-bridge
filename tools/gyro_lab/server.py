"""Loopback-only real-device gyro diagnostics; root credentials stay in memory."""
import argparse
import getpass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
from pathlib import Path
import secrets
import struct
import subprocess
import sys
import threading
import time
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
ASSETS = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "skpdb_probe"))
import pc_client as pc


def fields(text):
    return dict(line.split("=", 1) for line in text.splitlines() if "=" in line)


class Lab:
    def __init__(self, host, key):
        self.host, self.key = host, key
        self.lock = threading.Lock()
        self.connection = None
        self.cancel = threading.Event()
        self.worker = None
        self.result = {"running": False, "phase": "尚未测试", "events": [], "samples": [], "checks": []}
        self.last_status = {"version": "尚未连接", "gyro": {}}
        self.status_error = None
        self.next_status_retry = 0
        self.browser_lock = threading.Lock()
        self.browser = {"page": None, "source": None, "secure": False, "visible": False,
                        "running": False, "error": None, "samples": [], "received": 0}

    def redact(self, value):
        return str(value).replace(self.key.decode(), "<redacted>") if self.key else str(value)

    def independent(self):
        return Lab(self.host, self.key)

    def request(self, payload, opcode):
        with self.lock:
            if self.connection is None:
                self.connection = pc.connect(SimpleNamespace(
                    host=self.host, port=39876, knock_port=39877, key=self.key, timeout=15,
                    no_knock=False, legacy_trust=False))
            try:
                return pc.request(self.connection, payload, opcode)
            except Exception:
                self.connection.close()
                self.connection = None
                raise

    def status(self):
        if time.monotonic() >= self.next_status_retry:
            try:
                info = fields(self.request(b"B\x01", b"B").decode())
                gyro = self.gyro(0)
                self.last_status = {"version": info.get("version", "未知版本"), "gyro": gyro}
                self.status_error = None
            except Exception as error:
                self.status_error = self.redact(error)
                self.next_status_retry = time.monotonic() + 5
        with self.browser_lock:
            browser = {key: value for key, value in self.browser.items() if key != "samples"}
        return {"host": self.host, **self.last_status, "connected": self.status_error is None,
                "error": self.status_error, "result": self.result, "browser": browser}

    def browser_report(self, report):
        samples = report.get("samples")
        if not isinstance(samples, list) or len(samples) > 240:
            raise ValueError("Invalid browser sample count")
        cleaned = []
        previous = -1
        for row in samples:
            if not isinstance(row, list) or len(row) != 5 or not all(
                    isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x) for x in row):
                raise ValueError("Invalid browser sensor sample")
            if row[0] <= previous or row[1] < 0:
                raise ValueError("Browser sample order is invalid")
            previous = row[0]
            cleaned.append(row)
        with self.browser_lock:
            self.browser = {"page": str(report.get("page", ""))[:64],
                "source": str(report.get("source", ""))[:40],
                "layout": str(report.get("layout", ""))[:40],
                "secure": report.get("secure") is True, "visible": report.get("visible") is True,
                "running": report.get("running") is True,
                "error": str(report.get("error") or "")[:512],
                "samples": cleaned, "received": time.time()}
        return {"phase": self.result["phase"]}

    def execute(self, command):
        data = self.request(b"E" + command.encode(), b"E")
        code, size = struct.unpack_from("!II", data)
        output = data[8:8 + size].decode(errors="replace")
        if code:
            raise RuntimeError(output.strip() or f"Phone command failed ({code})")
        return output

    def upload(self, local, remote):
        offset = 0
        with local.open("rb") as source:
            while True:
                block = source.read(128 * 1024)
                payload = b"W" + struct.pack("!QB", offset, int(offset == 0)) + remote.encode() + b"\0" + block
                if struct.unpack("!I", self.request(payload, b"W"))[0] != len(block):
                    raise RuntimeError("Short probe upload")
                offset += len(block)
                if not block:
                    return

    def gyro(self, action, data=b""):
        return fields(self.request(b"Z" + bytes([action]) + data, b"Z").decode())

    def start(self, mode):
        if mode not in ("browser", "native"):
            raise ValueError("仅支持手机网页闭环和整机 HAL 原生预检")
        with self.lock:
            if self.worker and self.worker.is_alive():
                raise RuntimeError("测试正在进行")
            self.cancel.clear()
            self.result = {"running": True, "phase": "准备全局 HAL 验证", "events": [],
                           "samples": [], "checks": [], "hits": 0, "updates": 0, "mode": mode}
            from device_hal import run_global
            self.worker = threading.Thread(target=run_global, args=(self, mode), daemon=True)
            self.worker.start()

    def log(self, text):
        self.result["events"].append(time.strftime("%H:%M:%S") + " " + self.redact(text))

    def close(self):
        try:
            with self.lock:
                if self.connection is not None:
                    try:
                        pc.close_session(self.connection)
                    finally:
                        self.connection = None
        finally:
            self.key = b""


def serve(lab, port):
    token = secrets.token_hex(32)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def reply(self, status, body, content_type="application/json; charset=utf-8"):
            if not isinstance(body, bytes):
                body = (body if isinstance(body, str) else json.dumps(body, ensure_ascii=False)).encode()
            self.send_response(status)
            for key, value in {"Content-Type": content_type, "Content-Length": str(len(body)),
                "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
                "Content-Security-Policy": "default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'",
                "Referrer-Policy": "no-referrer"}.items():
                self.send_header(key, value)
            self.end_headers()
            self.wfile.write(body)

        def authorized(self):
            if self.headers.get("Host") != f"127.0.0.1:{self.server.server_port}":
                self.reply(403, {"error": "Invalid host"})
                return False
            origin = self.headers.get("Origin")
            if origin and origin != f"http://127.0.0.1:{self.server.server_port}":
                self.reply(403, {"error": "Invalid origin"})
                return False
            return True

        def page_token(self):
            if secrets.compare_digest(self.headers.get("X-Lab-Token", ""), token):
                return True
            self.reply(403, {"error": "Page expired"})
            return False

        def do_GET(self):
            if not self.authorized():
                return
            try:
                if self.path in ("/", "/index.html", "/phone"):
                    file = "phone.html" if self.path == "/phone" else "index.html"
                    self.reply(200, (ASSETS / file).read_text(encoding="utf-8").replace("__TOKEN__", token),
                               "text/html; charset=utf-8")
                elif self.path in ("/app.js", "/app.css", "/phone.js", "/phone.css"):
                    kind = "text/javascript" if self.path.endswith(".js") else "text/css"
                    self.reply(200, (ASSETS / self.path[1:]).read_bytes(), kind + "; charset=utf-8")
                elif self.path == "/api/status":
                    if self.page_token():
                        self.reply(200, lab.status())
                elif self.path == "/api/browser":
                    if self.page_token():
                        with lab.browser_lock:
                            self.reply(200, lab.browser)
                else:
                    self.reply(404, {"error": "Not found"})
            except Exception as error:
                self.reply(502, {"error": str(error).replace(lab.key.decode(), "<redacted>")})

        def do_POST(self):
            if not self.authorized() or not self.page_token():
                return
            try:
                if self.path in ("/api/run", "/api/browser"):
                    length = int(self.headers.get("Content-Length", "0"))
                    if length < 1 or length > (65536 if self.path == "/api/browser" else 1024):
                        raise ValueError("Invalid request size")
                    body = json.loads(self.rfile.read(length))
                    if not isinstance(body, dict):
                        raise ValueError("Expected a JSON object")
                    if self.path == "/api/browser":
                        self.reply(200, lab.browser_report(body))
                    else:
                        lab.start(body.get("mode", "browser"))
                        self.reply(202, {"started": True})
                elif self.path == "/api/stop":
                    lab.cancel.set()
                    self.reply(200, {"stopping": True})
                elif self.path == "/api/shutdown":
                    lab.cancel.set()
                    self.reply(200, {"stopping": True})
                    threading.Thread(target=server.shutdown, daemon=True).start()
                else:
                    self.reply(404, {"error": "Not found"})
            except Exception as error:
                self.reply(409, {"error": str(error).replace(lab.key.decode(), "<redacted>")})

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"GYRO_LAB_URL=http://127.0.0.1:{server.server_port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        lab.cancel.set()
        if lab.worker:
            lab.worker.join()
        server.server_close()
        lab.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, default=8767)
    parser.add_argument("--background", action="store_true")
    parser.add_argument("--key-stdin", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    key = sys.stdin.buffer.readline().rstrip(b"\r\n") if args.key_stdin else getpass.getpass("Phone key (not saved): ").encode()
    if not key:
        raise SystemExit("Missing phone key")
    if args.background:
        options = {"creationflags": subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP} \
            if sys.platform == "win32" else {"start_new_session": True}
        child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--host", args.host,
            "--port", str(args.port), "--key-stdin"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, **options)
        child.stdin.write(key + b"\n")
        child.stdin.close()
        ready = False
        for raw in child.stdout:
            line = raw.decode("utf-8", errors="replace").strip()
            if line.startswith("GYRO_LAB_URL="):
                print(line, flush=True)
                print(f"GYRO_LAB_PID={child.pid}", flush=True)
                ready = True
                break
            if "Error" in line or "Exception" in line:
                print(line.replace(key.decode(), "<redacted>"), flush=True)
        child.stdout.close()
        if not ready:
            raise SystemExit("Background server did not start")
    else:
        serve(Lab(args.host, key), args.port)
    key = b""
