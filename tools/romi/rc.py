"""Romi 診断ファームへコマンドを送る。使い方: py -3.11 rc.py [--port COM17] "info" "lv" ...
各コマンドの応答は 'ok' か 'ERR' 行で終わるまで読む。DTR/RTS は触らない（リセットさせない）。"""
import sys, time, serial
sys.stdout.reconfigure(encoding="utf-8")
args = sys.argv[1:]
port = "COM17"
if args and args[0] == "--port":
    port = args[1]
    args = args[2:]
s = serial.Serial()
s.port = port
s.baudrate = 115200
s.timeout = 0.1
s.dtr = False
s.rts = False
s.open()
time.sleep(0.2)
s.reset_input_buffer()
for cmd in args:
    s.write((cmd + "\n").encode())
    print(">", cmd)
    buf = b""
    t0 = time.time()
    while time.time() - t0 < 12:
        buf += s.read(4096)
        txt = buf.decode("utf-8", "replace")
        if txt.rstrip().endswith("ok") or "ERR" in txt:
            break
    print(buf.decode("utf-8", "replace").rstrip())
s.close()
