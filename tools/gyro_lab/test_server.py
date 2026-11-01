import http.client
from html.parser import HTMLParser
import json
import threading
import time
import unittest
from unittest.mock import patch

import server
import device_hal


class TokenParser(HTMLParser):
    token = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "meta" and attrs.get("name") == "lab-token":
            self.token = attrs["content"]


class StatusTests(unittest.TestCase):
    def test_phone_checks_reject_stale_background_or_old_axis_reports(self):
        lab = server.Lab("192.0.2.1", b"fixture-secret")
        report = {"page": "fixture", "source": "DeviceMotion", "layout": "android-xyz-v2",
                  "secure": True, "visible": True, "running": True,
                  "samples": [[n, n*17, .1, .2, .3] for n in range(1, 91)]}
        lab.browser_report(report)
        _, rows = device_hal.phone_samples(lab, "fixture", 1, 500)
        self.assertGreaterEqual(len(rows), 15)
        for update in ({"layout": ""}, {"visible": False}, {"running": False},
                       {"secure": False}, {"page": "reloaded"}):
            lab.browser_report({**report, **update})
            with self.assertRaises(RuntimeError):
                device_hal.phone_samples(lab, "fixture", 1, 400)
        lab.browser_report(report)
        lab.browser["received"] = time.time() - 3
        with self.assertRaises(RuntimeError):
            device_hal.phone_samples(lab, "fixture", 1, 400)

    def test_browser_samples_are_real_finite_ordered_reports(self):
        lab = server.Lab("192.0.2.1", b"fixture-secret")
        report = {"page": "fixture", "source": "DeviceMotion", "secure": True,
                  "visible": True, "running": True, "samples": [[1, 1000, .1, .2, .3]]}
        lab.browser_report(report)
        self.assertEqual(lab.browser["samples"], report["samples"])
        self.assertTrue(lab.browser["running"])
        for samples in ([[1, 1000, float("nan"), 0, 0]], [[2, 1000, 0, 0, 0], [1, 1001, 0, 0, 0]],
                        [[1, 1000, False, 0, 0]]):
            with self.assertRaises(ValueError):
                lab.browser_report({**report, "samples": samples})
        for mode in ("desktop", "module"):
            with self.assertRaises(ValueError):
                lab.start(mode)

    def test_offline_status_preserves_results_and_redacts_key(self):
        lab = server.Lab("192.0.2.1", b"fixture-secret")
        lab.result["phase"] = "尚未测试"
        with patch.object(lab, "request", side_effect=RuntimeError("rejected fixture-secret")) as request:
            result = lab.status()
            self.assertFalse(result["connected"])
            self.assertEqual(result["result"]["phase"], "尚未测试")
            self.assertNotIn("fixture-secret", json.dumps(result))
            self.assertIn("<redacted>", result["error"])
            lab.status()
            self.assertEqual(request.call_count, 1)

    def test_status_can_recover_without_restarting_server(self):
        lab = server.Lab("192.0.2.1", b"fixture-secret")
        lab.status_error = "offline"
        with patch.object(lab, "request", side_effect=[
                b"version=fixture-version\nfast_token=must-not-reach-page\n",
                b"installed=0\nhits=0\nupdates=0\n"]):
            result = lab.status()
        self.assertTrue(result["connected"])
        self.assertEqual(result["version"], "fixture-version")
        self.assertNotIn("must-not-reach-page", json.dumps(result))


class HttpTests(unittest.TestCase):
    def test_loopback_host_origin_and_page_token(self):
        class Fixture:
            key = b"fixture-secret"
            worker = None
            cancel = threading.Event()
            started = []
            closed = False

            def status(self):
                return {"fixture": True}

            def start(self, mode):
                self.started.append(mode)

            def close(self):
                self.closed = True

        lab = Fixture()
        ready = threading.Event()
        original = server.ThreadingHTTPServer
        servers = []

        def create(*args):
            instance = original(*args)
            servers.append(instance)
            ready.set()
            return instance

        with patch.object(server, "ThreadingHTTPServer", create):
            worker = threading.Thread(target=server.serve, args=(lab, 0), daemon=True)
            worker.start()
            self.assertTrue(ready.wait(3))
            instance = servers[0]
            port = instance.server_port

            def request(method, path, headers=None, body=None):
                client = http.client.HTTPConnection("127.0.0.1", port, timeout=3)
                try:
                    client.request(method, path, body=body, headers=headers or {})
                    response = client.getresponse()
                    return response.status, dict(response.getheaders()), response.read()
                finally:
                    client.close()

            try:
                code, headers, html = request("GET", "/")
                self.assertEqual(code, 200)
                self.assertEqual(headers["Cache-Control"], "no-store")
                self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
                self.assertNotIn(lab.key, html)
                parser = TokenParser()
                parser.feed(html.decode())
                self.assertTrue(parser.token)
                auth = {"X-Lab-Token": parser.token}
                self.assertEqual(request("GET", "/api/status")[0], 403)
                self.assertEqual(request("GET", "/", {"Host": f"attacker.invalid:{port}"})[0], 403)
                self.assertEqual(request("GET", "/api/status",
                    {**auth, "Origin": "https://attacker.invalid"})[0], 403)
                self.assertEqual(request("POST", "/api/run", body=b"{}")[0], 403)
                self.assertEqual(lab.started, [])
                self.assertEqual(request("GET", "/api/status", auth)[0], 200)
                self.assertEqual(request("POST", "/api/run", auth,
                    b'{"mode":"desktop"}')[0], 202)
                self.assertEqual(lab.started, ["desktop"])
                self.assertEqual(request("POST", "/api/shutdown", auth, b"{}")[0], 200)
            finally:
                instance.shutdown()
                worker.join(timeout=5)
            self.assertFalse(worker.is_alive())
            self.assertTrue(lab.closed)


if __name__ == "__main__":
    unittest.main()
