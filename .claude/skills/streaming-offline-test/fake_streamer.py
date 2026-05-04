"""
fixed-cam-streamer をローカルで模擬する fake MJPEG server。

エンドポイント:
- /video: multipart/x-mixed-replace boundary=frame、約 30fps で JPEG (1080x1080) を吐く
- /info:  JSON, rotationDeg=90 / isPortrait=True / widthPx=heightPx=1080
- /health: JSON, fps / uptimeMs / totalFrames / totalBytes

使い方:
    python fake_streamer.py
    # localhost:8080 で配信開始

Unity 側 Phone01.asset / Phone02.asset の host を 127.0.0.1 に向けて Play する。
詳細は同階層 SKILL.md 参照。
"""
import http.server, socketserver, threading, time, io, json, sys
from PIL import Image, ImageDraw

PORT = 8080
WIDTH = 1080
HEIGHT = 1080
QUALITY = 40
FPS = 30

START = time.time()
FRAMES = [0]
BYTES = [0]


def make_frame(idx: int) -> bytes:
    img = Image.new("RGB", (WIDTH, HEIGHT), color=(20, 20, 30))
    d = ImageDraw.Draw(img)
    d.rectangle([60, 60, WIDTH - 60, HEIGHT - 60], outline=(80, 200, 255), width=8)
    d.line([60, 60, WIDTH - 60, HEIGHT - 60], fill=(255, 100, 100), width=4)
    d.line([WIDTH - 60, 60, 60, HEIGHT - 60], fill=(255, 100, 100), width=4)
    cx = WIDTH // 2 + int(300 * (idx % 60) / 60.0 - 150)
    cy = HEIGHT // 2
    d.ellipse([cx - 40, cy - 40, cx + 40, cy + 40], fill=(255, 255, 0))
    d.text((100, 100), f"FAKE STREAMER #{idx}", fill=(220, 220, 220))
    d.text((100, 200), f"t={time.time() - START:.1f}s", fill=(180, 180, 180))
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=QUALITY)
    return buf.getvalue()


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/info":
            body = json.dumps({
                "deviceName": "FAKE Pixel 7 Pro",
                "lensId": "標準",
                "lensFovDeg": 71.43,
                "widthPx": WIDTH,
                "heightPx": HEIGHT,
                "rotationDeg": 90,
                "isPortrait": True,
            }).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path == "/health":
            elapsed = time.time() - START
            body = json.dumps({
                "uptimeMs": int(elapsed * 1000),
                "totalFrames": FRAMES[0],
                "totalBytes": BYTES[0],
                "fps": (FRAMES[0] / elapsed) if elapsed > 0.5 else 0.0,
            }).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path in ("/video", "/"):
            self.send_response(200)
            self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
            self.send_header("Cache-Control", "no-cache, private")
            self.end_headers()
            try:
                while True:
                    jpg = make_frame(FRAMES[0])
                    FRAMES[0] += 1
                    BYTES[0] += len(jpg)
                    chunk = (
                        b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "
                        + str(len(jpg)).encode()
                        + b"\r\n\r\n" + jpg + b"\r\n"
                    )
                    self.wfile.write(chunk)
                    self.wfile.flush()
                    time.sleep(1.0 / FPS)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                return
            return
        self.send_response(404)
        self.end_headers()


class ThreadedServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


if __name__ == "__main__":
    srv = ThreadedServer(("127.0.0.1", PORT), Handler)
    print(f"fake streamer on http://127.0.0.1:{PORT}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
