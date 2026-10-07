import re, sys, collections
sys.stdout.reconfigure(encoding="utf-8")
base = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\output\romi"
txt = open(base + r"\disasm.txt", encoding="utf-8").read().splitlines()
# ユーザーコードの範囲（setup 周辺 ≒ 0x400d2000〜0x400d6000）
rows = []
for n, l in enumerate(txt):
    m = re.match(r"\s*([0-9a-f]{8}):", l)
    if m:
        a = int(m.group(1), 16)
        if 0x400D0020 <= a < 0x400D6000:
            rows.append((a, l))
cnt = collections.Counter()
for a, l in rows:
    m = re.search(r"call8\s+0x([0-9a-f]+)", l)
    if m:
        cnt[m.group(1)] += 1
print("よく呼ばれる関数:", cnt.most_common(14))

PINMODE, DWRITE, LEDCATT = "400e914c", "400e91fc", "400e9510"
def regs(window):
    """直前の movi / movi.n から a10,a11,a12 の即値を拾う"""
    d = {}
    for a, l in window:
        m = re.search(r"movi(?:\.n)?\s+(a\d+),\s+(-?\d+|0x[0-9a-f]+)", l)
        if m:
            d[m.group(1)] = int(m.group(2), 0)
        m = re.search(r"mov\.n\s+(a\d+),\s+(a\d+)", l)
        if m and m.group(2) in d:
            d[m.group(1)] = d[m.group(2)]
    return d
for i, (a, l) in enumerate(rows):
    m = re.search(r"call8\s+0x([0-9a-f]+)", l)
    if not m:
        continue
    t = m.group(1)
    if t in (PINMODE, DWRITE, LEDCATT):
        d = regs(rows[max(0, i - 8): i])
        name = {PINMODE: "pinMode", DWRITE: "digitalWrite", LEDCATT: "ledcAttach"}[t]
        print("0x%08X %-13s pin(a10)=%s a11=%s a12=%s" % (a, name, d.get("a10"), d.get("a11"), d.get("a12")))
