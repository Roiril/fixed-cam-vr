"""Romi WebUI の机上検証用の代役サーバ。ESP32 と同じ API（/run /stop /state /res /cal /info /health）を返す。
webui.h から HTML を取り出して配る（毎回読み直すので画面を直したらリロードするだけ）。
使い方: py -3.11 tools/romi/webui-stub.py [port]"""
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
from pathlib import Path


def load_html():
    src = (Path(__file__).parent / "diag" / "webui.h").read_text(encoding="utf-8")
    return re.search(r'R"HTML\((.*)\)HTML"', src, re.S).group(1)


CAL = {"sv": 0.75, "sd": 8.0, "st": 0.0, "rv": 4.5, "rd": 10.0, "rt": 0.0, "ver": 0}
state = {"id": 0, "t0": 0.0, "ms": 0, "active": False, "lastId": 0, "lastEl": 0}


def plan(m, duty, tgt):
    r = CAL["sv"] * (duty - CAL["sd"]) if m == "s" else CAL["rv"] * (duty - CAL["rd"])
    if r < 0.5:
        return None, "weak"
    if tgt < 1 or tgt > (150 if m == "s" else 720):
        return None, "range"
    ms = tgt / r * 1000 + (CAL["st"] if m == "s" else CAL["rt"])
    if ms < 50 or ms > 8000:
        return None, "range"
    return int(ms + 0.5), r


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

    def _tick(self, now):
        if state["active"] and (now - state["t0"]) * 1000 >= state["ms"]:
            state.update(active=False, lastId=state["id"], lastEl=state["ms"])

    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        now = time.time()
        self._tick(now)
        if u.path == "/":
            self._send(load_html(), "text/html")
        elif u.path == "/run":
            if state["active"]:
                self._send(json.dumps({"ok": 0, "err": "busy"}))
                return
            m, duty = q.get("m", "s"), int(q.get("duty", 40))
            if q.get("ms"):
                ms, rate = int(q["ms"]), 0
                if not (50 <= ms <= 8000):
                    self._send(json.dumps({"ok": 0, "err": "range"}))
                    return
            else:
                ms, rate = plan(m, duty, float(q.get("tgt", 0)))
                if ms is None:
                    self._send(json.dumps({"ok": 0, "err": rate}))
                    return
            state.update(id=state["id"] + 1, t0=now, ms=ms, active=True)
            print("[stub] RUN", q, "-> ms", ms, flush=True)
            self._send(json.dumps({"ok": 1, "id": state["id"], "ms": ms, "rate": round(rate, 2) if rate else 0}))
        elif u.path == "/joy":
            self._send('{"ok":1}')
        elif u.path == "/stop":
            state["active"] = False
            self._send('{"ok":1}')
        elif u.path == "/state":
            el = int((now - state["t0"]) * 1000) if state["active"] else 0
            self._send(json.dumps({"busy": 1 if state["active"] else 0, "id": state["id"], "el": el, "ms": state["ms"],
                                   "lastId": state["lastId"], "lastEl": state["lastEl"]}))
        elif u.path == "/cal":
            changed = False
            for k in ("sv", "sd", "st", "rv", "rd", "rt"):
                if k in q:
                    CAL[k] = float(q[k]); changed = True
            if changed:
                CAL["ver"] += 1
            self._send(json.dumps(CAL))
        elif u.path == "/res":
            print("[stub] RES", q, flush=True)
            self._send('{"ok":1}')
        elif u.path == "/info":
            self._send(json.dumps({"role": "cart", "id": "R", "version": "stub", "calVer": CAL["ver"]}))
        elif u.path == "/health":
            self._send('{"ok":1}')
        else:
            self.send_error(404)


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8123
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
