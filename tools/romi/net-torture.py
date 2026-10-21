"""台車（Romi・ESP32）の HTTP を、本番の相手を真似た負荷と壊れた相手の組み合わせで叩く試験台（2026-10-21）。

本番で台車に話しかける相手（同時に居る）:
  tablet  博士タブレットの調整パネル  GET /ending を 1 秒ごと（前の応答を待つ）・打ち切り 4 秒
  phone   開発用スマホの操作画面     GET /state を 0.35 秒ごと・打ち切り 1.5 秒
  quest   Quest の終幕の合図         POST /ending/go を 8 秒ごと（dry:1 ＝台車は動かない）・打ち切り 2 秒

それに重ねる壊れた相手（攻撃）:
  idle      接続だけして何も送らない（ブラウザの preconnect）
  partial   要求の頭だけ送って止まる（電波が途中で切れた相手）
  slowpost  POST の本文を途中まで送って止まる
  burst     12 本を同時に接続
  kick      台車の Wi-Fi をシリアルから切る（再接続までの時間）

  py -3.11 tools/romi/net-torture.py                      # 全部（1 場面 30 秒）
  py -3.11 tools/romi/net-torture.py --only base,idle --sec 20
  py -3.11 tools/romi/net-torture.py --soak 600           # 全部の攻撃を混ぜて 10 分

合否: tablet と quest の失敗 0、loopMaxMs が 200 未満（走行の停止が遅れない）、再起動なし。
kick は再接続までの間の失敗を許し、復旧の秒数を出す。シリアル（COM17）の EVT 行も拾って数える。
"""
import argparse, json, random, socket, statistics, sys, threading, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")
HOST = "192.168.10.51"


def http(method, path, body=None, timeout=4.0):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(f"http://{HOST}{path}", data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data else {})
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            res.read()
            return (time.perf_counter() - t0) * 1000, None
    except Exception as error:  # noqa: BLE001
        return (time.perf_counter() - t0) * 1000, type(error).__name__


class Client(threading.Thread):
    def __init__(self, name, method, path, period, timeout, body=None):
        super().__init__(daemon=True)
        self.name, self.method, self.path, self.period, self.timeout, self.body = name, method, path, period, timeout, body
        self.ms, self.fails, self.stop = [], [], False

    def run(self):
        while not self.stop:
            t0 = time.perf_counter()
            body = self.body() if callable(self.body) else self.body
            ms, err = http(self.method, self.path, body, self.timeout)
            (self.fails.append((time.time(), err)) if err else self.ms.append(ms))
            time.sleep(max(0.0, self.period - (time.perf_counter() - t0)))


class Attack(threading.Thread):
    def __init__(self, kind, period, serial_log):
        super().__init__(daemon=True)
        self.kind, self.period, self.stop, self.count, self.serial = kind, period, False, 0, serial_log
        self.socks = []

    def _sock(self):
        s = socket.create_connection((HOST, 80), timeout=3)
        self.socks.append((time.time(), s))
        return s

    def run(self):
        while not self.stop:
            try:
                if self.kind == "idle":
                    self._sock()
                elif self.kind == "partial":
                    self._sock().sendall(b"GET /health HTTP/1.1\r\nHo")
                elif self.kind == "slowpost":
                    self._sock().sendall(b"POST /ending HTTP/1.1\r\nHost: r\r\nContent-Type: application/json\r\n"
                                         b"Content-Length: 60\r\n\r\n{\"en\":")
                elif self.kind == "burst":
                    ts = [threading.Thread(target=http, args=("GET", "/health", None, 4.0)) for _ in range(12)]
                    [t.start() for t in ts]
                elif self.kind == "kick" and self.serial:
                    self.serial.send("wifi kick")
                self.count += 1
            except Exception:  # noqa: BLE001 — 攻撃側の失敗は数えない
                pass
            # 古い壊れた接続は 10 秒で閉じる（本物の壊れた相手もいずれ消える）
            now = time.time()
            for item in list(self.socks):
                if now - item[0] > 10:
                    item[1].close()
                    self.socks.remove(item)
            time.sleep(self.period)
        for _, s in self.socks:
            s.close()


class SerialLog(threading.Thread):
    def __init__(self, port):
        super().__init__(daemon=True)
        import serial
        self.s = serial.Serial()
        self.s.port, self.s.baudrate, self.s.timeout, self.s.dtr, self.s.rts = port, 115200, 0.1, False, False
        self.s.open()
        self.lines, self.stop, self.lock = [], False, threading.Lock()

    def send(self, cmd):
        with self.lock:
            self.s.write((cmd + "\n").encode())

    def run(self):
        buf = b""
        while not self.stop:
            buf += self.s.read(4096)
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                text = line.decode("utf-8", "replace").strip()
                if text:
                    self.lines.append((time.time(), text))
        self.s.close()


def info(clear=False):
    try:
        with urllib.request.urlopen(f"http://{HOST}/info{'?clear=1' if clear else ''}", timeout=4) as res:
            return json.loads(res.read())
    except Exception:  # noqa: BLE001
        return None


def stats(c):
    q = sorted(c.ms)
    if not q:
        return f"{c.name:6s} 成功 0 失敗 {len(c.fails)}"
    p99 = q[min(len(q) - 1, int(len(q) * 0.99))]
    return (f"{c.name:6s} 成功 {len(q):4d} 失敗 {len(c.fails):3d}  中央 {statistics.median(q):5.0f}ms  "
            f"p99 {p99:5.0f}ms  最大 {q[-1]:5.0f}ms")


ATTACK_PERIOD = {"idle": 2.0, "partial": 3.0, "slowpost": 3.0, "burst": 4.0, "kick": 15.0}


def scenario(name, attacks, sec, serial_log):
    go_key = [0]

    def go_body():
        go_key[0] += 1
        return {"src": "torture", "dry": 1, "key": f"t{int(time.time())}-{go_key[0]}"}

    info(clear=True)
    before = info()
    start_lines = len(serial_log.lines) if serial_log else 0
    clients = [Client("tablet", "GET", "/ending", 1.0, 4.0), Client("phone", "GET", "/state", 0.35, 1.5),
               Client("quest", "POST", "/ending/go", 8.0, 2.0, go_body)]
    hits = [Attack(k, ATTACK_PERIOD[k], serial_log) for k in attacks]
    t_start = time.time()
    for t in clients + hits:
        t.start()
    time.sleep(sec)
    for t in clients + hits:
        t.stop = True
    for t in clients:
        t.join(timeout=6)
    for t in hits:
        t.join(timeout=6)
    after = info()
    print(f"\n== {name}  （攻撃: {','.join(attacks) or 'なし'} {' '.join(f'{h.kind}×{h.count}' for h in hits)}）")
    for c in clients:
        print("  " + stats(c))
    if "kick" in attacks:
        # 切った時刻の後で tablet が最初に成功するまで（失敗の連なりの長さ）を復旧時間とみなす
        fails = [t for t, _ in clients[0].fails]
        print(f"  kick 中の tablet 失敗 {len(fails)} 回" + (f"（最初 {fails[0]-t_start:.1f}s・最後 {fails[-1]-t_start:.1f}s）" if fails else ""))
    rebooted = bool(before and after and after.get("up", 0) < before.get("up", 0))
    if after:
        print(f"  台車: loopMaxMs {after.get('loopMaxMs')}  idleDrops {after.get('idleDrops')}  "
              f"rssi {after.get('rssi')}  drops {after.get('drops')}  heap {after.get('heap')}  reset {after.get('reset')}"
              + ("  ⚠ 再起動した" if rebooted else ""))
    else:
        print("  台車: /info が取れない")
    if serial_log:
        evts = [l for _, l in serial_log.lines[start_lines:] if l.startswith("EVT net") or "boot" in l.lower()
                or "Guru" in l or "rst:" in l or "WDT" in l]
        for line in evts[:12]:
            print("  serial:", line)
    ok = (not clients[0].fails or "kick" in attacks) and (not clients[2].fails or "kick" in attacks) \
        and after is not None and (after.get("loopMaxMs") or 0) < 200 and not rebooted
    print("  判定:", "OK" if ok else "NG")
    return ok


def main():
    global HOST
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default=HOST)
    ap.add_argument("--port", default="COM17", help="シリアル（無ければ kick と EVT の収集を飛ばす）")
    ap.add_argument("--sec", type=int, default=30)
    ap.add_argument("--only", default="")
    ap.add_argument("--soak", type=int, default=0, help="全部の攻撃を混ぜて N 秒")
    a = ap.parse_args()
    HOST = a.host
    serial_log = None
    try:
        serial_log = SerialLog(a.port)
        serial_log.start()
    except Exception as error:  # noqa: BLE001
        print("シリアルを開けません（kick と EVT は飛ばす）:", error)
    first = info()
    print("台車:", {k: first.get(k) for k in ("version", "rssi", "up", "reset", "heap")} if first else "応答なし")
    plan = [("base", []), ("idle", ["idle"]), ("partial", ["partial"]), ("slowpost", ["slowpost"]),
            ("burst", ["burst"]), ("kick", ["kick"])]
    if a.soak:
        plan = [("soak", ["idle", "partial", "slowpost", "burst"])]
        a.sec = a.soak
    if a.only:
        want = set(a.only.split(","))
        plan = [p for p in plan if p[0] in want]
    results = [(name, scenario(name, atk, a.sec, serial_log)) for name, atk in plan]
    if serial_log:
        serial_log.stop = True
    print("\n== まとめ: " + "  ".join(f"{n} {'OK' if ok else 'NG'}" for n, ok in results))
    sys.exit(0 if all(ok for _, ok in results) else 1)


if __name__ == "__main__":
    main()
