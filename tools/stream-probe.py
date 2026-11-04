"""配信スマホ（fixed-cam-streamer）の MJPEG を PC から受けて、途切れを数える計器（2026-10-22）。

Quest と同じ GET /video を開き、各パートの X-Frame-Seq で欠けを、受信時刻で間隔を測る。
複数台を同時に受ける（体験中の Quest と同じ負荷）。

  py -3.11 tools/stream-probe.py                         # A・B・C を同時に 30 秒
  py -3.11 tools/stream-probe.py --sec 120 --cams 21,22
  py -3.11 tools/stream-probe.py --cycles 5               # 5 秒受けて 2 秒切る、を 5 回（休止からの立ち上がり）

出すもの: 最初の 1 枚まで（休止中の encode が立ち上がる時間）・受信 fps・間隔の p99 と最大・
200ms を越えた間隔の回数・通し番号の欠け・終わった後の /health（熱と throttleStage）。
⚠ PC の受信は Quest と同じ経路ではない（PC は有線、Quest は 5GHz の無線）。配信側の詰まりと Wi-Fi の切り分けに使う。
"""
import argparse, json, socket, statistics, sys, threading, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")


def health(ip):
    try:
        with urllib.request.urlopen(f"http://{ip}:8080/health", timeout=2) as r:
            return json.loads(r.read())
    except Exception:  # noqa: BLE001
        return None


class Recv(threading.Thread):
    def __init__(self, ip, sec):
        super().__init__(daemon=True)
        self.ip, self.sec = ip, sec
        self.times, self.seqs, self.bytes, self.first_ms, self.err = [], [], 0, None, None

    def run(self):
        t0 = time.perf_counter()
        try:
            s = socket.create_connection((self.ip, 8080), timeout=5)
            s.sendall(f"GET /video HTTP/1.1\r\nHost: {self.ip}\r\nConnection: close\r\n\r\n".encode())
            s.settimeout(3)
            buf = b""
            end = t0 + self.sec
            while time.perf_counter() < end:
                try:
                    chunk = s.recv(65536)
                except socket.timeout:
                    self.err = "受信が 3 秒止まった"
                    break
                if not chunk:
                    self.err = "相手が閉じた"
                    break
                self.bytes += len(chunk)
                buf += chunk
                # パートのヘッダ（X-Frame-Seq）を数える。本文の JPEG は数えずに読み飛ばす
                while True:
                    i = buf.find(b"X-Frame-Seq:")
                    if i < 0:
                        buf = buf[-64:]
                        break
                    j = buf.find(b"\r\n", i)
                    if j < 0:
                        buf = buf[i:]
                        break
                    now = time.perf_counter()
                    if self.first_ms is None:
                        self.first_ms = (now - t0) * 1000
                    self.times.append(now)
                    try:
                        self.seqs.append(int(buf[i + 12:j].strip()))
                    except ValueError:
                        pass
                    buf = buf[j:]
            s.close()
        except Exception as error:  # noqa: BLE001
            self.err = f"{type(error).__name__}: {error}"

    def report(self, label):
        n = len(self.times)
        if n < 2:
            return f"  {label}: フレーム {n}  {self.err or ''}"
        gaps = [(b - a) * 1000 for a, b in zip(self.times, self.times[1:])]
        q = sorted(gaps)
        p99 = q[min(len(q) - 1, int(len(q) * 0.99))]
        lost = sum(max(0, b - a - 1) for a, b in zip(self.seqs, self.seqs[1:]))
        dur = self.times[-1] - self.times[0]
        return (f"  {label}: 最初の 1 枚 {self.first_ms:5.0f}ms  受信 {n / dur:5.1f}fps  "
                f"間隔 中央 {statistics.median(q):4.0f} p99 {p99:4.0f} 最大 {q[-1]:5.0f}ms  "
                f">200ms {sum(1 for g in gaps if g > 200):3d} 回  欠け {lost:4d}  "
                f"{self.bytes * 8 / dur / 1e6:4.1f}Mbps" + (f"  ⚠ {self.err}" if self.err else ""))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cams", default="21,22,23")
    ap.add_argument("--sec", type=float, default=30)
    ap.add_argument("--cycles", type=int, default=0, help="N 回 5 秒受けて 2 秒切る")
    a = ap.parse_args()
    ips = [f"192.168.10.{c}" for c in a.cams.split(",")]
    names = {"192.168.10.21": "A", "192.168.10.22": "B", "192.168.10.23": "C"}
    rounds = [(a.sec, "連続")] if not a.cycles else [(5, f"{k + 1} 回目") for k in range(a.cycles)]
    for sec, label in rounds:
        rs = [Recv(ip, sec) for ip in ips]
        [r.start() for r in rs]
        [r.join(sec + 10) for r in rs]
        print(f"== {label}")
        for ip, r in zip(ips, rs):
            print(r.report(names.get(ip, ip)))
        if a.cycles:
            time.sleep(2)
    for ip in ips:
        h = health(ip)
        if h:
            print(f"  {names.get(ip, ip)} /health: fps {h.get('fps')}  熱 {h.get('thermalStatus')}  "
                  f"throttle {h.get('throttleStage')}  電池 {h.get('batteryTempC')}℃  clients {h.get('clientCount')}")


if __name__ == "__main__":
    main()
