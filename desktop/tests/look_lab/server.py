"""Local phone look test: adb reverse tcp:8765 tcp:8765, then open localhost:8765.

Only synthetic touch/angle telemetry is stored, in memory. The real desktop
engine is driven by ../LookHarness; no root key is accepted by this server.
"""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
LOCK = threading.Lock()
CONTROL = {"generation": 0, "run": "ready"}
STATE = {}
EVENTS = []
FRAMES = []


def summary():
    moves = [e for e in EVENTS if e["kind"] == "camera" and abs(e.get("dx", 0)) + abs(e.get("dy", 0)) > 0.01]
    gaps = [b["t"] - a["t"] for a, b in zip(moves, moves[1:])]
    gaps.sort()
    steps = [abs(e.get("dx", 0)) for e in moves]
    return {
        "run": STATE.get("run"), "events": len(EVENTS), "frames": len(FRAMES),
        "cameraMoves": len(moves), "dx": sum(e.get("dx", 0) for e in moves),
        "dy": sum(e.get("dy", 0) for e in moves),
        "maxStepPx": max(steps, default=0),
        "maxGapMs": max(gaps, default=0),
        "p95GapMs": gaps[int((len(gaps) - 1) * 0.95)] if gaps else 0,
        "longGaps80ms": sum(g > 80 for g in gaps),
        "downs": sum(e["kind"] == "down" for e in EVENTS),
        "ups": sum(e["kind"] == "up" for e in EVENTS),
        "maxTouches": max((e.get("n", 0) for e in EVENTS), default=0),
        "touchesAfterStop": STATE.get("n"),
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def reply(self, data, content_type="application/json"):
        if data is None:
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if not isinstance(data, bytes):
            data = json.dumps(data).encode()
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        route = self.path.split("?")[0]
        with LOCK:
            if route == "/control": return self.reply(CONTROL)
            if route == "/state": return self.reply(STATE)
            if route == "/results":
                return self.reply({"state": STATE, "summary": summary(), "events": EVENTS, "frames": FRAMES})
        if route in ("/", "/index.html"):
            return self.reply((ROOT / "index.html").read_bytes(), "text/html; charset=utf-8")
        self.send_error(404)

    def do_POST(self):
        global STATE
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        with LOCK:
            if self.path == "/reset":
                CONTROL.update(generation=CONTROL["generation"] + 1, run=data["run"])
                STATE = {}
                EVENTS.clear()
                FRAMES.clear()
            elif self.path == "/report" and data["generation"] == CONTROL["generation"]:
                STATE = data["state"]
                EVENTS.extend(data["events"])
                FRAMES.extend(data["frames"])
        self.reply(None)

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    print("Look Lab http://127.0.0.1:8765", flush=True)
    ThreadingHTTPServer(("127.0.0.1", 8765), Handler).serve_forever()
