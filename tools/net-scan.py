"""展示ネットワークに何が居るかを、接続に触らずに一覧する。

    py -3.11 tools/net-scan.py            # 既定 192.168.10.0/24
    py -3.11 tools/net-scan.py 192.168.11 # 別のサブネットを見る

ping スイープ → ARP で MAC → 各 IP の `:8080/info` と `/health` を引く。
配信アプリは `/info` に **cameraId** を載せているので、**どの IP がどのカメラか機械で決まる**
（設営のたびに人が突き合わせる作業が消える）。

⚠ **接続を切らない。** 端末の Wi-Fi を入れ直すと保存済みネットワークの中から別の AP が選ばれ、
   全台が会場や研究室の AP へ移る（2026-08-30 に実演した）。確認は読み取りだけで済ませる。

⚠ 自分自身（この PC）は ARP に載らないので出てこない。PC の IP は `ipconfig` で見る。

判定の勘どころ（`rules/streaming.md` の「/health から読むときの 2 つの罠」）:
  - **`fps` で「回っている」を判定しない。** encode が止まっても最後の値を返し続ける
  - **`latestFrameAgeMs` の `-1` は「たったいま」ではなく「1 枚も作っていない」**
  - **`encodeIdle=true` は異常ではない。** 需要駆動なので、誰も見ていなければ止まる
"""
import concurrent.futures as cf
import json
import re
import subprocess
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

NET = (sys.argv[1] if len(sys.argv) > 1 else "192.168.10").rstrip(".") + "."
RANGE = range(2, 41)
PORT = 8080


def ping(i: int) -> None:
    subprocess.run(["ping", "-n", "1", "-w", "400", f"{NET}{i}"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def arp_table() -> dict:
    out = subprocess.run(["arp", "-a"], capture_output=True, text=True,
                         encoding="cp932", errors="replace").stdout
    esc = NET.replace(".", r"\.")
    tbl = {}
    for line in out.splitlines():
        m = re.match(rf"\s+({esc}\d+)\s+([0-9a-f-]{{17}})\s+(\S+)", line, re.I)
        if m:
            tbl[m.group(1)] = (m.group(2).lower().replace("-", ":"), m.group(3))
    return tbl


def get_json(ip: str, path: str, timeout=1.5):
    try:
        with urllib.request.urlopen(f"http://{ip}:{PORT}{path}", timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8", "replace"))
    except Exception:
        return None


def probe(ip: str):
    return get_json(ip, "/info"), get_json(ip, "/health")


def main():
    with cf.ThreadPoolExecutor(max_workers=48) as ex:
        list(ex.map(ping, RANGE))

    tbl = arp_table()
    if not tbl:
        print(f"{NET}x に 1 件も居ない。この PC が同じサブネットに居ないか、全端末が黙っている")
        return

    ips = sorted(tbl, key=lambda s: int(s.rsplit(".", 1)[1]))
    with cf.ThreadPoolExecutor(max_workers=16) as ex:
        got = dict(zip(ips, ex.map(probe, ips)))

    print(f"{'IP':<16}{'MAC':<20}正体")
    print("-" * 92)
    cams = 0
    for ip in ips:
        mac, _kind = tbl[ip]
        info, health = got[ip]
        if info:
            cams += 1
            cam = info.get("cameraId") or "?"
            wh = f"{info.get('widthPx','?')}x{info.get('heightPx','?')}"
            tilt = info.get("tiltState", "-")
            who = f"配信 cameraId={cam} {wh} tilt={tilt}"
            if health:
                # 止まっているかは encodeIdle と latestFrameAgeMs で見る（fps では見ない）
                age = health.get("latestFrameAgeMs")
                idle = health.get("encodeIdle")
                total = health.get("totalFrames")
                age_s = "未生成" if age == -1 else f"{age}ms前"
                who += f"  frames={total} 最終={age_s} idle={idle} clients={health.get('clientCount')}"
        else:
            who = "(HTTP 応答なし — Quest / ルータ / ブロードキャスト)"
        print(f"{ip:<16}{mac:<20}{who}")

    print()
    print(f"在席 {len(ips)} 件 ／ 配信アプリ {cams} 件")
    if cams:
        print("⚠ tilt=steep は「まだ据えていない」（ほぼ真下を向いている）。据え終われば ok になる")


if __name__ == "__main__":
    main()
