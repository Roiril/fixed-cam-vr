"""デジタルツインの見る面のサーバ（Range 対応。動画のシークに要る）。py -3.11 tools/twin-game/serve.py [port]

- /            … tools/twin-game/ の静的ファイル
- /game/...    … Drive の renders/twin-cycles/game/（固定カメラの動画）
- /walk/...    … Drive の renders/virtualwalk/（本物の Director を通した HMD の画 walk.mp4）
"""
from __future__ import annotations

import mimetypes
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlparse

HERE = Path(__file__).resolve().parent
GAME = Path(os.environ.get("TWIN_GAME", r"G:\マイドライブ\shubie\renders\twin-cycles\game"))
WALK = Path(r"G:\マイドライブ\shubie\renders\virtualwalk")
mimetypes.add_type("video/mp4", ".mp4")
mimetypes.add_type("text/javascript", ".mjs")
mimetypes.add_type("text/javascript", ".js")


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _resolve(self):
        path = unquote(urlparse(self.path).path)
        if path == "/":
            path = "/index.html"
        for prefix, root in (("/game/", GAME), ("/walk/", WALK)):
            if path.startswith(prefix):
                p = (root / path[len(prefix):]).resolve()
                return p if str(p).startswith(str(root.resolve())) else None
        p = (HERE / path.lstrip("/")).resolve()
        return p if str(p).startswith(str(HERE)) else None

    def do_GET(self):
        p = self._resolve()
        if p is None or not p.is_file():
            self.send_error(404)
            return
        size = p.stat().st_size
        ctype = mimetypes.guess_type(p.name)[0] or "application/octet-stream"
        rng = self.headers.get("Range")
        start, end = 0, size - 1
        code = 200
        m = re.match(r"bytes=(\d*)-(\d*)", rng or "")
        if m and size:
            if m.group(1):
                start = int(m.group(1))
                end = int(m.group(2)) if m.group(2) else end
            else:
                start = max(0, size - int(m.group(2)))
            end = min(end, size - 1)
            code = 206
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Cache-Control", "no-store")
        if code == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        try:
            with p.open("rb") as f:
                f.seek(start)
                left = end - start + 1
                while left > 0:
                    chunk = f.read(min(256 * 1024, left))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    left -= len(chunk)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8123
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()


if __name__ == "__main__":
    main()
