"""Synthetic mouse-to-touch telemetry; no keys or personal data accepted."""
import json
import threading
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

LOCK = threading.Lock()
CONTROL = {"generation": 0, "run": "ready"}
STATE = {}
GESTURES = []
EVENTS = []


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def reply(self, data, mime="application/json"):
        body = data if isinstance(data, bytes) else json.dumps(data).encode()
        self.send_response(200)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        with LOCK:
            if self.path == "/control": return self.reply(CONTROL)
            if self.path == "/state": return self.reply(STATE)
            if self.path == "/results": return self.reply({"state": STATE, "gestures": GESTURES, "events": EVENTS})
        if self.path == "/":
            return self.reply(Path(__file__).with_name("index.html").read_bytes(), "text/html; charset=utf-8")
        self.send_error(404)

    def do_POST(self):
        global STATE
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        with LOCK:
            if self.path == "/reset":
                CONTROL.update(generation=CONTROL["generation"] + 1, run=data["run"])
                STATE = {}
                GESTURES.clear()
                EVENTS.clear()
            elif (self.path == "/report" and data["generation"] == CONTROL["generation"]
                  and data["state"].get("visible") is True):
                STATE = data["state"]
                GESTURES.extend(data["gestures"])
                EVENTS.extend(data.get("events", []))
                del EVENTS[:-12000]
        self.reply({})

    def log_message(self, *_): pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
    print(f"Mouse Lab :{port}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
