"""台車（Romi・ESP32）の HTTP が詰まる原因を確かめる計器（2026-10-21）。

台車の WebServer は接続を 1 本ずつ捌き、接続しただけで要求を送らない相手を最大 5 秒待つ。
その間ほかの要求は全部待たされる、というのが「タブレットから台車に繋がらないことが多い」の第一候補。
これを、空の接続を開けない回（素の状態）と開ける回（ブラウザの preconnect の真似）で比べて確かめる。

  py -3.11 tools/romi/http-probe.py                 # 素の状態で /health を 1 秒ごとに 30 回
  py -3.11 tools/romi/http-probe.py --idle          # 3 回に 1 回、直前に空の接続を開けてから同じことをする
  py -3.11 tools/romi/http-probe.py --count 60 --timeout 4

読み方: 素の状態で失敗・遅延が出るなら電波（/info の rssi・drops）を疑う。--idle の回だけ 4〜5 秒の遅延と失敗が
出るなら仮説どおり（romi-diag-5 まで）。romi-diag-6 以降は空の接続を 0.8 秒で切るので、--idle でも 1 秒前後に収まり、
/info の idleDrops が増える。最後に /info を表示する。
"""
import argparse, json, socket, statistics, sys, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")


def get(url, timeout):
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(url, timeout=timeout) as res:
            res.read()
        return (time.perf_counter() - t0) * 1000, None
    except Exception as error:  # noqa: BLE001 — 計器なので理由を文字で残す
        return (time.perf_counter() - t0) * 1000, type(error).__name__


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="192.168.10.51")
    ap.add_argument("--count", type=int, default=30)
    ap.add_argument("--interval", type=float, default=1.0)
    ap.add_argument("--timeout", type=float, default=4.0, help="タブレットの打ち切りと同じ 4 秒が既定")
    ap.add_argument("--idle", action="store_true", help="3 回に 1 回、空の TCP 接続を開けたまま要求する")
    a = ap.parse_args()
    base = f"http://{a.host}"
    ok_ms, fails, idle_socks = [], [], []
    for i in range(a.count):
        tag = ""
        if a.idle and i % 3 == 0:
            host, _, port = a.host.partition(":")
            s = socket.create_connection((host, int(port or 80)), timeout=2)   # 何も送らない（preconnect の真似）
            idle_socks.append(s)
            tag = " (空の接続あり)"
        ms, err = get(f"{base}/health", a.timeout)
        if err:
            fails.append((i, err, ms))
            print(f"{i:3d} 失敗 {err} {ms:6.0f}ms{tag}")
        else:
            ok_ms.append(ms)
            print(f"{i:3d} ok   {ms:6.0f}ms{tag}")
        time.sleep(max(0.0, a.interval - ms / 1000))
    for s in idle_socks:
        s.close()
    print()
    print(f"成功 {len(ok_ms)}/{a.count}  失敗 {len(fails)}")
    if ok_ms:
        q = sorted(ok_ms)
        print(f"応答 中央値 {statistics.median(q):.0f}ms  最大 {q[-1]:.0f}ms  1 秒超 {sum(1 for x in q if x > 1000)} 回")
    ms, err = get(f"{base}/info", a.timeout)
    if not err:
        with urllib.request.urlopen(f"{base}/info", timeout=a.timeout) as res:
            info = json.loads(res.read())
        keys = ["version", "rssi", "drops", "lastReason", "reset", "up", "idleDrops"]
        print("/info", {k: info.get(k) for k in keys})
    else:
        print("/info 取得失敗", err)


if __name__ == "__main__":
    main()
