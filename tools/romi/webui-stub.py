"""Romi WebUI の机上検証用の代役サーバ。ESP32 と同じ API（/run /stop /state /res）を返す。
webui.h から HTML を取り出して配る。使い方: py -3.11 tools/romi/webui-stub.py [port]"""
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
from pathlib import Path

def load_html():
    src = (Path(__file__).parent / "diag" / "webui.h").read_text(encoding="utf-8")
    return re.search(r'R"HTML\((.*)\)HTML"', src, re.S).group(1)


state = {"id": 0, "t0": 0.0, "ms": 0, "active": False}


class H(BaseHTTPRequestHandler):
    def log_message(self, fmt, *a):
        print("[stub]", self.path, flush=True)

    def _send(self, body, ctype="application/json"):
        b = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", ctype + "; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        now = time.time()
        if state["active"] and (now - state["t0"]) * 1000 >= state["ms"]:
            state["active"] = False
        if u.path == "/":
            self._send(load_html(), "text/html")
        elif u.path == "/run":
            if state["active"]:
                self._send(json.dumps({"ok": 0, "err": "busy"}))
                return
            ms = int(q.get("ms", 0))
            if not (50 <= ms <= 4000):
                self._send(json.dumps({"ok": 0, "err": "range"}))
                return
            state.update(id=state["id"] + 1, t0=now, ms=ms, active=True)
            self._send(json.dumps({"ok": 1, "id": state["id"]}))
        elif u.path == "/stop":
            state["active"] = False
            self._send('{"ok":1}')
        elif u.path == "/state":
            el = int((now - state["t0"]) * 1000) if state["active"] else 0
            self._send(json.dumps({"busy": 1 if state["active"] else 0, "id": state["id"], "el": el, "ms": state["ms"]}))
        elif u.path == "/res":
            print("[stub] RES", q, flush=True)
            self._send('{"ok":1}')
        else:
            self.send_error(404)


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8123
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
