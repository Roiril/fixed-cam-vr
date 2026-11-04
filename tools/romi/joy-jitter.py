"""ジョイスティックの更新が無線で途切れて、台車の安全停止（400ms）が誤って掛かる回数を数える（2026-10-22）。

操作画面と同じ形で /joy を送る: 60ms ごと・前の応答が 250ms 返らなければ重ねて送る・打ち切り 500ms。
送るのは x=0 y=0（台車は動かない）。台車側では「受け付けた」扱いになり、
更新が 400ms 途切れると `EVT joy end ... reason=timeout-or-release` が出る。
ずっと送り続けているのに end が出たら、それが誤った安全停止。

  py -3.11 tools/romi/joy-jitter.py --sec 120
  py -3.11 tools/romi/joy-jitter.py --sec 120 --load      # タブレットと Quest の要求を重ねる

台車の /info に joyGapMaxMs（/joy の間隔の最大）/ joyDeadman（動かしている最中の途切れ停止）があれば（romi-diag-7〜）それも出す。シリアルは任意。
"""
import argparse, importlib.util, json, pathlib, statistics, sys, threading, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")
HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("torture", HERE / "net-torture.py")
torture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(torture)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="192.168.10.51")
    ap.add_argument("--port", default="COM17")
    ap.add_argument("--sec", type=int, default=60)
    ap.add_argument("--period", type=float, default=0.060)
    ap.add_argument("--load", action="store_true")
    a = ap.parse_args()
    torture.HOST = a.host

    serial_log = None
    if a.port:
        try:
            serial_log = torture.SerialLog(a.port)
            serial_log.start()
        except Exception as error:  # noqa: BLE001
            print("serial なし:", type(error).__name__)
    torture.info(clear=True)

    load = []
    if a.load:
        load = [torture.Client("tablet", "GET", "/ending", 1.0, 4.0),
                torture.Client("quest", "POST", "/ending/go", 8.0, 2.0,
                               lambda: {"src": "joy-jitter", "dry": 1, "key": f"jj-{time.time():.0f}"})]
        for c in load:
            c.start()

    rtts, fails, sent = [], [], 0
    inflight = {"at": None}
    lock = threading.Lock()
    url = f"http://{a.host}/joy?x=0.000&y=0.000&cap=25&flip=0"

    def one(tag=None):
        t0 = time.perf_counter()
        try:
            with urllib.request.urlopen(url, timeout=0.5) as res:
                res.read()
            with lock:
                rtts.append((time.perf_counter() - t0) * 1000)
        except Exception as error:  # noqa: BLE001
            with lock:
                fails.append((time.time(), type(error).__name__))
        finally:
            with lock:
                if tag is not None and inflight["at"] == tag:
                    inflight["at"] = None

    end = time.time() + a.sec
    start_lines = len(serial_log.lines) if serial_log else 0
    while time.time() < end:
        tick = time.perf_counter()
        with lock:
            busy = inflight["at"] is not None and tick - inflight["at"] < 0.250
        if not busy:
            with lock:
                inflight["at"] = tick
            threading.Thread(target=one, args=(tick,), daemon=True).start()
            sent += 1
        time.sleep(max(0.0, a.period - (time.perf_counter() - tick)))
    for _ in range(4):   # 離した後と同じく 0 を数回
        one()
    time.sleep(0.8)
    for c in load:
        c.stop = True

    q = sorted(rtts)
    p = lambda f: q[min(len(q) - 1, int(len(q) * f))] if q else 0
    print(f"/joy 送信 {sent}  成功 {len(q)}  失敗 {len(fails)}  中央 {statistics.median(q) if q else 0:.0f}ms  "
          f"p95 {p(0.95):.0f}ms  p99 {p(0.99):.0f}ms  最大 {q[-1] if q else 0:.0f}ms")
    for c in load:
        print(" ", torture.stats(c))
    if fails:
        kinds = {}
        for _, k in fails:
            kinds[k] = kinds.get(k, 0) + 1
        print("  失敗の内訳:", kinds)
    if serial_log:
        serial_log.stop = True
        lines = [l for _, l in serial_log.lines[start_lines:]]
        starts = sum(1 for l in lines if l.startswith("EVT joy start"))
        ends = [l for l in lines if l.startswith("EVT joy end")]
        print(f"serial: joy start {starts}  joy end {len(ends)}（最後の 1 回は送信を止めた分。それ以外が誤った安全停止）")
        for l in ends[:-1][:10]:
            print("   ", l)
    inf = torture.info()
    if inf:
        keys = ["rssi", "drops", "loopMaxMs", "joyGapMaxMs", "joyDeadman", "served", "reset"]
        print("台車:", "  ".join(f"{k} {inf[k]}" for k in keys if k in inf))


if __name__ == "__main__":
    main()
