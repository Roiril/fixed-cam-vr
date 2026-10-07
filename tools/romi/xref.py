import re, struct, sys
sys.stdout.reconfigure(encoding="utf-8")
base = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\output\romi"
img = open(base + r"\esp32-flash-backup-20261007.bin", "rb").read()[0x10000:0x10000 + 0x140000]
nseg = img[1]; off = 24; segs = []
for i in range(nseg):
    la, ln = struct.unpack("<II", img[off:off + 8]); segs.append((la, off + 8, ln)); off += 8 + ln
irom = [s for s in segs if s[0] == 0x400D0020][0]
data = img[irom[1]:irom[1] + irom[2]]
targets = {0x3F4003AF: "PWM pin/freq/res", 0x3F400397: "ledcAttach FAILED", 0x3F4003CE: "duty limit/SPEED_LIMIT",
           0x3F40037A: "banner Romi/Joy-Con", 0x3F40027D: "[safe] armed", 0x3F4003F9: "jc2-romi", 0x3F400172: "[DISARMED]"}
lits = {}
for i in range(0, len(data) - 3, 4):
    v = struct.unpack_from("<I", data, i)[0]
    if v in targets:
        lits[irom[0] + i] = targets[v]
        print("literal @0x%08X = 0x%08X (%s)" % (irom[0] + i, v, targets[v]))
# l32r の参照を disasm から拾う
txt = open(base + r"\disasm.txt", encoding="utf-8").read().splitlines()
hits = {}
for n, line in enumerate(txt):
    m = re.search(r"l32r\s+\w+,\s+0x([0-9a-f]+)", line)
    if m:
        a = int(m.group(1), 16)
        if a in lits:
            hits.setdefault(lits[a], []).append(n)
for k, v in hits.items():
    print(k, "-> disasm lines", v[:6])
# ledcAttach FAILED の周辺（セットアップ関数）を出す
n0 = hits.get("ledcAttach FAILED", [None])[0]
if n0 is not None:
    open(base + r"\setup_region.txt", "w", encoding="utf-8").write("\n".join(txt[max(0, n0 - 260): n0 + 60]))
    print("wrote setup_region.txt around line", n0)
