"""Romi のシリアル出力（EVT 行）を時刻付きでファイルへ追記する常駐ロガー。
Web 操作の実行・停止・「結果」入力がここに流れる。使い方: py -3.11 tools/romi/evtlog.py [COM17]
⚠ ポートを占有するので、動かしている間は rc.py など他のシリアル操作はできない。"""
import sys, time, datetime, serial
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8")
port = sys.argv[1] if len(sys.argv) > 1 else "COM17"
out = Path(__file__).resolve().parents[2] / "output" / "romi" / "evt.log"
out.parent.mkdir(parents=True, exist_ok=True)
s = serial.Serial()
s.port = port; s.baudrate = 115200; s.timeout = 0.2; s.dtr = False; s.rts = False
s.open()
buf = b""
with out.open("a", encoding="utf-8", newline="\n") as f:
    f.write("# --- logger start %s %s\n" % (datetime.datetime.now().isoformat(timespec="seconds"), port))
    f.flush()
    while True:
        buf += s.read(4096)
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            txt = line.decode("utf-8", "replace").rstrip("\r")
            if txt:
                f.write("%s %s\n" % (datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3], txt))
                f.flush()
