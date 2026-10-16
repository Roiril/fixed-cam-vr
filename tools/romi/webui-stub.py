"""Romi WebUI の机上検証用の代役サーバ。ESP32 と同じ API（/run /stop /state /res /cal /info /health /ending）を返す。
終幕の自動走行（GET/POST /ending・POST /ending/go・POST /ending/cut）も ESP32 と同じ形で真似る（実際には動かない）。
博士タブレットの画面をブラウザで試すときのため、CORS を開けてある。
webui.h から HTML を取り出して配る（毎回読み直すので画面を直したらリロードするだけ）。
使い方: py -3.11 tools/romi/webui-stub.py [port]"""
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
from pathlib import Path


def load_html():
    src = (Path(__file__).parent / "diag" / "webui.h").read_text(encoding="utf-8")
    return re.search(r'R"HTML\((.*)\)HTML"', src, re.S).group(1)


# 実機の NVS に入っている較正（cal ver=5・2026-10-07 の実測・USB 給電・載せ物なし）
CAL = {"sv": 0.5667, "sd": 8.0, "st": 120.0, "rv": 4.565, "rd": 10.0, "rt": 115.0, "ver": 5}
state = {"id": 0, "t0": 0.0, "ms": 0, "active": False, "lastId": 0, "lastEl": 0}
# diag.ino の Ending と同じ既定・同じ範囲
ENDING = {"en": 1, "delayMs": 3500, "rotDir": 1, "rotDuty": 40, "rotMs": 1400, "fwdDir": 1, "fwdDuty": 40, "fwdMs": 3400,
          "stopAtCut": 0, "ver": 0}
ENDING_RANGE = {"en": (0, 1), "delayMs": (0, 20000), "rotDuty": (10, 68), "rotMs": (0, 8000), "fwdDuty": (10, 68),
                "fwdMs": (0, 8000), "stopAtCut": (0, 1)}
last = {"goAt": 0.0, "cutMs": -1, "dry": 0, "stopped": 0, "src": "", "count": 0, "endAt": 0.0}
GAP_MS = 250


def ending_json(now):
    def est(r, ms, t):
        return max(0.0, r * (ms - t) / 1000) if r > 0 and ms > t else 0.0
    st = "idle"
    if last["goAt"]:
        st = "dry" if last["dry"] else ("stopped" if last["stopped"] else ("running" if now < last["endAt"] else "done"))
    return dict(ENDING, gapMs=GAP_MS, dutyMax=68, msMax=8000,
                estDeg=round(est(CAL["rv"] * (ENDING["rotDuty"] - CAL["rd"]), ENDING["rotMs"], CAL["rt"])),
                estCm=round(est(CAL["sv"] * (ENDING["fwdDuty"] - CAL["sd"]), ENDING["fwdMs"], CAL["st"]), 1),
                calVer=CAL["ver"], cal=dict(CAL), busy=1 if (state["active"] or (st == "running")) else 0,
                last={"state": st, "src": last["src"], "agoMs": int((now - last["goAt"]) * 1000) if last["goAt"] else 0,
                      "cutMs": last["cutMs"], "count": last["count"]})


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
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(b)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def do_POST(self):
        u = urlparse(self.path)
        n = int(self.headers.get("Content-Length") or 0)
        try:
            body = json.loads(self.rfile.read(n) or b"{}")
        except ValueError:
            body = {}
        now = time.time()
        if u.path == "/ending":
            for k, (lo, hi) in ENDING_RANGE.items():
                if k in body:
                    ENDING[k] = min(hi, max(lo, int(round(float(body[k])))))
            for k in ("rotDir", "fwdDir"):
                if k in body:
                    ENDING[k] = -1 if float(body[k]) < 0 else 1
            for k in ("rotMs", "fwdMs"):
                if 0 < ENDING[k] < 50:
                    ENDING[k] = 50
            ENDING["ver"] += 1
            print("[stub] ENDING saved", ENDING, flush=True)
            self._send(json.dumps(ending_json(now)))
        elif u.path == "/ending/go":
            key = str(body.get("key", ""))
            if key and key == last.get("key") and now - last["goAt"] < 30:
                print("[stub] ENDING dup", key, flush=True)
                self._send(json.dumps({"ok": 1, "id": last["count"], "dry": last["dry"], "ver": ENDING["ver"], "dup": 1}))
                return
            if not ENDING["en"] and not body.get("force"):
                self._send('{"ok":0,"err":"disabled"}')
                return
            dry = 1 if body.get("dry") else 0
            total = ENDING["delayMs"] + ENDING["rotMs"] + (GAP_MS if ENDING["rotMs"] else 0) + ENDING["fwdMs"]
            last.update(key=key, goAt=now, cutMs=-1, dry=dry, stopped=0, src=str(body.get("src", "?")), count=last["count"] + 1,
                        endAt=now + total / 1000)
            print("[stub] ENDING go", body, flush=True)
            self._send(json.dumps({"ok": 1, "id": last["count"], "dry": dry, "ver": ENDING["ver"]}))
        elif u.path == "/ending/cut":
            if last["goAt"] and last["cutMs"] < 0:
                last["cutMs"] = int((now - last["goAt"]) * 1000)
                if ENDING["stopAtCut"] and now < last["endAt"] and not last["dry"]:
                    last["stopped"] = 1
            print("[stub] ENDING cut", last, flush=True)
            self._send(json.dumps({"ok": 1, "cutMs": last["cutMs"], "stopped": last["stopped"]}))
        else:
            self.send_error(404)

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
        elif u.path == "/ending":
            self._send(json.dumps(ending_json(now)))
        elif u.path == "/stop":
            state["active"] = False
            if last["goAt"] and now < last["endAt"]:
                last["stopped"] = 1
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
