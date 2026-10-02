#!/usr/bin/env python3
"""Quest のタブレットの口（VisitorPortal :8090）の**代役**。面（visitor.html）を机上で確かめるためのもの。

Quest 無しで `Assets/Resources/Visitor/visitor.html` をブラウザで開き、送る → 待ち → 反映 の見え方を通す。
実機の VisitorPortalLogic と同じ GET / ・ GET /status ・ POST /set ・ POST /clear ・
POST /tablet/pulse を持ち、
「注意書きの段（Wait）で受けたら書く／本編（RUN）では次まで持つ／始めたら枠を空にする」を真似る。

    py -3.11 tools/visitor-portal-stub.py                # :8090・段は Wait
    py -3.11 tools/visitor-portal-stub.py --stage Done --phase RUN   # 本編中の見え方
    curl -X POST http://127.0.0.1:8090/_stage -d Wait    # 走らせたまま段を変える（代役だけの口）
    curl -X POST http://127.0.0.1:8090/_consume          # 体験者が A を押した（枠を空にする）

⚠ 実機の bind・スレッド・IL2CPP はここでは確かめられない。走行の `visitor=1/…` が唯一の証拠。
"""
import argparse
import json
import os
import re
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, "..", "Assets", "Resources", "Visitor", "visitor.html")

_lock = threading.Lock()
_st = {
    "lang": "ja", "relief": False, "titleStage": "Wait", "phase": "INTRO",
    "pending": None, "appliedSeq": 0, "applyCount": 0, "received": 0, "rejected": 0,
    "model": "Quest 3 (stub)", "ip": "127.0.0.1", "port": 8090, "seq": 0,
    "portalSessionId": uuid.uuid4().hex, "lastRequest": None,
}
_initial = dict(_st)
_faults = {}
_tablets = {}
_id_rx = r'"([A-Za-z0-9_-]{1,128})"'


def _apply_if_wait():
    # 実機の TitleScreen.Update と同じ縁: Wait の段で枠があれば書く
    p = _st["pending"]
    if p and _st["titleStage"] == "Wait" and _st["appliedSeq"] != p["seq"]:
        _st["lang"] = p["lang"]
        _st["relief"] = p["relief"]
        _st["appliedSeq"] = p["seq"]
        _st["applyCount"] += 1


def _status():
    d = {k: v for k, v in _st.items() if k != "seq"}
    now = time.monotonic()
    d["tablets"] = [{"tabletSessionId": tablet_id, "ip": ip, "ageSec": now - seen_at}
                    for tablet_id, (ip, seen_at) in _tablets.items()]
    d["ok"] = True
    return json.dumps(d)


class H(BaseHTTPRequestHandler):
    def log_message(self, fmt, *a):
        print("[stub]", fmt % a)

    def _send(self, code, ctype, body):
        b = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            self.wfile.write(b)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # A timed-out browser request deliberately closes the connection.

    def _fault(self, path):
        """Local QA only: delay or reject selected requests without changing Unity."""
        with _lock:
            fault = _faults.get(path)
            if not fault:
                return False
            fault = dict(fault)
            if fault["count"] > 0:
                _faults[path]["count"] -= 1
                if _faults[path]["count"] == 0:
                    del _faults[path]
        time.sleep(fault["delayMs"] / 1000)
        if fault["status"] != 200:
            self._send(fault["status"], "application/json", '{"ok":false,"error":"qa_fault"}')
            return True
        return False

    def _send_bytes(self, code, ctype, data, extra=None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Accept-Ranges", "bytes")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?")[0]
        if self._fault(path):
            return
        if path in ("/", "/visitor.html", "/index.html"):
            with open(PAGE, encoding="utf-8") as f:
                return self._send(200, "text/html; charset=utf-8", f.read())
        if path.startswith("/asset/"):
            # 実機と同じ置き場（Resources/Visitor/<name>.bytes）から配る。Range も実機と同じ形で返す
            name = path[7:]
            fp = os.path.join(os.path.dirname(PAGE), name + ".bytes")
            if not os.path.isfile(fp) or "/" in name or "\\" in name:
                return self._send(404, "application/json", '{"ok":false}')
            with open(fp, "rb") as f:
                data = f.read()
            ctype = {"json": "application/json; charset=utf-8",
                     "jpg": "image/jpeg", "jpeg": "image/jpeg", "png": "image/png", "webp": "image/webp", "mp4": "video/mp4",
                     "webm": "video/webm", "mp3": "audio/mpeg", "wav": "audio/wav"}.get(name.rsplit(".", 1)[-1].lower(),
                                                                                         "application/octet-stream")
            rng = self.headers.get("Range")
            if rng and rng.startswith("bytes="):
                a, _, b = rng[6:].partition("-")
                start = int(a) if a else 0
                end = int(b) if b else len(data) - 1
                end = min(end, len(data) - 1)
                return self._send_bytes(206, ctype, data[start:end + 1],
                                        {"Content-Range": f"bytes {start}-{end}/{len(data)}"})
            return self._send_bytes(200, ctype, data)
        if path == "/status":
            with _lock:
                _apply_if_wait()
                return self._send(200, "application/json; charset=utf-8", _status())
        return self._send(404, "application/json", '{"ok":false}')

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace") if n else ""
        path = self.path.split("?")[0]
        if self._fault(path):
            return
        with _lock:
            if path == "/_fault":
                try:
                    spec = json.loads(body)
                    target = spec["path"]
                    delay = spec.get("delayMs", 0)
                    status = spec.get("status", 200)
                    count = spec.get("count", 1)
                    if (target not in ("/status", "/set", "/clear", "/tablet/pulse",
                                       "/asset/briefing-v1.json")
                            or type(delay) is not int or not 0 <= delay <= 15000
                            or type(status) is not int or status not in (200, 400, 503)
                            or type(count) is not int or not -1 <= count <= 100):
                        raise ValueError("fault")
                except (ValueError, KeyError, TypeError):
                    return self._send(400, "application/json", '{"ok":false}')
                if count == 0:
                    _faults.pop(target, None)
                else:
                    _faults[target] = {"delayMs": delay, "status": status, "count": count}
                return self._send(200, "application/json", '{"ok":true}')
            if path == "/_reboot":
                port = _st["port"]
                _st.update(_initial)
                _st["port"] = port
                _st["portalSessionId"] = uuid.uuid4().hex
                _tablets.clear()
                _faults.clear()
                return self._send(200, "application/json", '{"ok":true}')
            if path == "/_begin":
                # TitleScreen.BeginTitle: reset the actual values, then reapply a reservation.
                _st.update(lang="ja", relief=False, appliedSeq=0, titleStage="Wait", phase="INTRO")
                _apply_if_wait()
                return self._send(200, "application/json", '{"ok":true}')
            if path == "/set":
                m = re.search(r'"lang"\s*:\s*"([A-Za-z]{2})"', body)
                if not m or m.group(1).lower() not in ("ja", "en", "fr"):
                    _st["rejected"] += 1
                    return self._send(400, "application/json", '{"ok":false,"error":"lang"}')
                tablet = re.search(r'"tabletSessionId"\s*:\s*' + _id_rx, body)
                portal = re.search(r'"portalSessionId"\s*:\s*' + _id_rx, body)
                if not tablet or not portal:
                    _st["rejected"] += 1
                    return self._send(400, "application/json", '{"ok":false,"error":"session ids required"}')
                if portal.group(1) != _st["portalSessionId"]:
                    return self._send(409, "application/json", '{"ok":false,"error":"portal session changed"}')
                relief = bool(re.search(r'"relief"\s*:\s*true', body))
                _st["seq"] += 1
                _st["received"] += 1
                _st["pending"] = {"lang": m.group(1).lower(), "relief": relief, "seq": _st["seq"]}
                _st["lastRequest"] = {"tabletSessionId": tablet.group(1) if tablet else "",
                                      "seq": _st["seq"], "lang": m.group(1).lower(), "relief": relief}
                _apply_if_wait()
                return self._send(200, "application/json", json.dumps({"ok": True, "seq": _st["seq"]}))
            if path == "/tablet/pulse":
                tablet = re.search(r'"tabletSessionId"\s*:\s*' + _id_rx, body)
                if not tablet:
                    return self._send(400, "application/json", '{"ok":false,"error":"tabletSessionId required"}')
                tablet_id = tablet.group(1)
                _tablets.pop(tablet_id, None)
                _tablets[tablet_id] = (self.client_address[0], time.monotonic())
                if len(_tablets) > 8:
                    _tablets.pop(next(iter(_tablets)))
                return self._send(200, "application/json", '{"ok":true}')
            if path == "/clear":
                tablet = re.search(r'"tabletSessionId"\s*:\s*' + _id_rx, body)
                portal = re.search(r'"portalSessionId"\s*:\s*' + _id_rx, body)
                if not tablet or not portal:
                    return self._send(400, "application/json", '{"ok":false,"error":"session ids required"}')
                if portal.group(1) != _st["portalSessionId"]:
                    return self._send(409, "application/json", '{"ok":false,"error":"portal session changed"}')
                _st["pending"] = None
                return self._send(200, "application/json", '{"ok":true}')
            # ---- 代役だけの口（実機には無い）----
            if path == "/_stage":
                _st["titleStage"] = body.strip() or "Wait"
                _apply_if_wait()
                return self._send(200, "text/plain", "ok")
            if path == "/_phase":
                _st["phase"] = body.strip() or "INTRO"
                return self._send(200, "text/plain", "ok")
            if path == "/_consume":
                _st["pending"] = None
                _st["titleStage"] = "In"
                return self._send(200, "text/plain", "ok")
        return self._send(404, "application/json", '{"ok":false}')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8090)
    ap.add_argument("--stage", default="Wait")
    ap.add_argument("--phase", default="INTRO")
    a = ap.parse_args()
    _st["titleStage"] = a.stage
    _st["phase"] = a.phase
    _st["port"] = a.port
    srv = ThreadingHTTPServer(("0.0.0.0", a.port), H)
    print(f"[stub] http://127.0.0.1:{a.port}/  stage={a.stage} phase={a.phase}")
    srv.serve_forever()


if __name__ == "__main__":
    main()
