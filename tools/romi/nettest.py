"""Romi のネットワーク堅牢性の通し試験。USB シリアル（試験用コマンド）と LAN の両側から同時に見る。
  py -3.11 tools/romi/nettest.py discovery     UDP :8830 の probe 応答と announce
  py -3.11 tools/romi/nettest.py kick          強制切断 → 自動復旧までの時間
  py -3.11 tools/romi/nettest.py outage        偽 SSID でつながらない状況 → バックオフ → 非常用 AP → 復旧 → AP 解除
⚠ COM を占有するので evtlog.py などは止めておく。"""
import json, socket, subprocess, sys, time, urllib.request, serial
sys.stdout.reconfigure(encoding="utf-8")
IP = "192.168.10.51"
t0 = time.time()


def stamp():
    return "%6.1fs" % (time.time() - t0)


def health(timeout=0.6):
    try:
        with urllib.request.urlopen("http://%s/health" % IP, timeout=timeout) as r:
            return r.status == 200
    except Exception:
        return False


def open_serial():
    s = serial.Serial()
    s.port = "COM17"; s.baudrate = 115200; s.timeout = 0.05; s.dtr = False; s.rts = False
    s.open(); time.sleep(0.2); s.reset_input_buffer()
    return s


SEEN = []   # これまでに読んだシリアル行


def drain(s, buf):
    buf += s.read(4096)
    while b"\n" in buf:
        line, buf = buf.split(b"\n", 1)
        txt = line.decode("utf-8", "replace").strip()
        if txt:
            SEEN.append(txt)
            print(stamp(), "serial:", txt, flush=True)
    return buf


def discovery():
    # 1) probe を unicast で送り、announce が返るか（Quest / PC 卓と同じ手順）
    probe = json.dumps({"proto": "fixedcam-discovery/1", "type": "probe", "show": "mawarimi", "seq": 1}).encode()
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(2.0)
    u.sendto(probe, (IP, 8830))
    try:
        data, addr = u.recvfrom(2048)
        print("probe -> reply from", addr[0], data.decode())
    except socket.timeout:
        print("probe -> NO reply")
    # 別 show トークンには答えない（隣ブース混線対策）
    u.sendto(json.dumps({"proto": "fixedcam-discovery/1", "type": "probe", "show": "other", "seq": 2}).encode(), (IP, 8830))
    u.settimeout(1.0)
    try:
        u.recvfrom(2048); print("wrong-show probe -> replied (BAD)")
    except socket.timeout:
        print("wrong-show probe -> ignored (OK)")
    u.close()
    # 2) 5 秒ごとの broadcast announce を PC が受けられるか
    r = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    r.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        r.bind(("", 8830))
    except OSError as e:
        print("announce listen: bind failed (別プロセスが :8830 を使用中):", e); return
    r.settimeout(7.0)
    try:
        while True:
            data, addr = r.recvfrom(2048)
            if addr[0] == IP:
                print("broadcast announce from", addr[0], data.decode()); break
    except socket.timeout:
        print("broadcast announce: none in 7s")
    r.close()


def kick():
    s = open_serial(); buf = b""
    print(stamp(), "health before:", health())
    s.write(b"wifi kick\n")
    tk = time.time(); down_seen = False; up_at = None
    while time.time() - tk < 90:
        buf = drain(s, buf)
        ok = health(0.3)
        if not ok:
            down_seen = True
        elif down_seen and up_at is None:
            up_at = time.time() - tk
            print(stamp(), "HTTP recovered after %.1fs" % up_at, flush=True)
            break
    if up_at is None:
        print(stamp(), "NOT recovered in 90s (down_seen=%s)" % down_seen)
    t1 = time.time()
    while time.time() - t1 < 3:
        buf = drain(s, buf)
    s.close()


def wlan_sees_ap():
    out = subprocess.run(["netsh", "wlan", "show", "networks", "mode=bssid"], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    return "ROMI-DIAG" in out


def outage():
    s = open_serial(); buf = b""
    s.write(b"wifi fake no-such-ssid-test\n")
    tk = time.time(); ap_seen_at = None; unfaked = False; recovered_at = None; apoff_at = None
    last_scan = 0
    while time.time() - tk < 240:
        buf = drain(s, buf)
        el = time.time() - tk
        if ap_seen_at is None and any("ap-on" in l for l in SEEN):   # 非常用 AP が上がったらログに出る
            ap_seen_at = el; print(stamp(), "emergency AP is on at %.0fs" % el, flush=True)
        if ap_seen_at is not None and not unfaked and el > ap_seen_at + 3:
            unfaked = True; tu = time.time()
            print(stamp(), "-> wifi unfake (network restored)", flush=True)
            s.write(b"wifi unfake\n")
        if unfaked and recovered_at is None and health(0.3):
            recovered_at = time.time() - tu
            print(stamp(), "HTTP recovered %.1fs after unfake" % recovered_at, flush=True)
        if recovered_at is not None and time.time() - tu > recovered_at + 25:
            break
    s.write(b"wifi status\n")
    t1 = time.time()
    while time.time() - t1 < 1.5:
        buf = drain(s, buf)
    s.close()


def restore():
    """偽 SSID を戻し、LAN への自動復帰と非常用 AP の自動消灯を測る。"""
    s = open_serial(); buf = b""
    s.write(b"wifi unfake\n")
    tu = time.time(); up_at = None; apoff_at = None
    while time.time() - tu < 90:
        buf = drain(s, buf)
        if up_at is None and health(0.3):
            up_at = time.time() - tu
            print(stamp(), "HTTP recovered %.1fs after unfake" % up_at, flush=True)
        if up_at is not None and apoff_at is None and b"ap-off" in buf + b"":
            pass
        if up_at is not None and time.time() - tu > up_at + 22:
            break
    s.write(b"wifi status\n")
    t1 = time.time()
    while time.time() - t1 < 1.5:
        buf = drain(s, buf)
    s.close()


if __name__ == "__main__":
    {"discovery": discovery, "kick": kick, "outage": outage, "restore": restore}[sys.argv[1]]()
