"""USB シリアルのコマンド往復遅延と、駆動窓の実測長（PC 側の壁時計）を測る。モーターは duty 0 で回さない。"""
import sys, time, statistics, serial
sys.stdout.reconfigure(encoding="utf-8")
s = serial.Serial()
s.port = "COM17"; s.baudrate = 115200; s.timeout = 0.01; s.dtr = False; s.rts = False
s.open(); time.sleep(0.2); s.reset_input_buffer()

def roundtrip(cmd, limit=5.0):
    t0 = time.perf_counter()
    s.write((cmd + "\n").encode())
    buf = b""
    while time.perf_counter() - t0 < limit:
        buf += s.read(4096)
        if buf.rstrip().endswith(b"ok"):
            break
    return (time.perf_counter() - t0) * 1000, buf

lat = [roundtrip("stop")[0] for _ in range(200)]
lat.sort()
print("stop 往復 200 回 [ms]: min %.1f / median %.1f / p95 %.1f / p99 %.1f / max %.1f / stdev %.2f" % (
    lat[0], statistics.median(lat), lat[int(len(lat) * .95)], lat[int(len(lat) * .99)], lat[-1], statistics.pstdev(lat)))

# 駆動窓の実測（duty 0。1000ms を 20 回）。窓の長さ = 要求値からのずれ
win = []
for _ in range(20):
    ms, _b = roundtrip("d 0 0 0 1000", 5)
    win.append(ms)
print("d(duty0, 要求1000ms) 往復 20 回 [ms]: min %.1f / mean %.1f / max %.1f / stdev %.2f" % (min(win), statistics.mean(win), max(win), statistics.pstdev(win)))
s.close()
