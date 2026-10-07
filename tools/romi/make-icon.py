"""ホーム画面用のアイコン（ジョイスティックの絵）を作り、ファームに埋め込む icon.h を書き出す。
使い方: py -3.11 tools/romi/make-icon.py
出力: tools/romi/diag/icon.h（PNG のバイト列）と output/romi/icon.png（確認用）"""
import io, sys
from pathlib import Path
from PIL import Image, ImageDraw
sys.stdout.reconfigure(encoding="utf-8")

S = 192
SS = 4  # 4 倍で描いて縮小（縁をなめらかに）
big = S * SS
img = Image.new("RGB", (big, big), "#101418")
d = ImageDraw.Draw(img)
c = big // 2
# 外側の円（パッド）。maskable の安全域を考え、全体の 80% に収める
r_pad = int(big * 0.40)
d.ellipse([c - r_pad, c - r_pad, c + r_pad, c + r_pad], fill="#1b2229", outline="#36404a", width=SS * 3)
# 十字の目盛り
t = SS * 2
d.rectangle([c - t // 2, c - int(r_pad * 0.82), c + t // 2, c + int(r_pad * 0.82)], fill="#36404a")
d.rectangle([c - int(r_pad * 0.82), c - t // 2, c + int(r_pad * 0.82), c + t // 2], fill="#36404a")
# つまみ（少し前へ倒した位置）
r_knob = int(big * 0.15)
ky = c - int(r_pad * 0.28)
d.ellipse([c - r_knob, ky - r_knob, c + r_knob, ky + r_knob], fill="#5b9bff")
img = img.resize((S, S), Image.LANCZOS)
img = img.quantize(colors=16, method=Image.MEDIANCUT)
buf = io.BytesIO()
img.save(buf, format="PNG", optimize=True)
data = buf.getvalue()

root = Path(__file__).resolve().parents[2]
(root / "output" / "romi").mkdir(parents=True, exist_ok=True)
(root / "output" / "romi" / "icon.png").write_bytes(data)
rows = []
for i in range(0, len(data), 16):
    rows.append("  " + ", ".join("0x%02X" % b for b in data[i:i + 16]))
src = ("// make-icon.py が生成した PNG（%dx%d・%d バイト）。手で直さない。\n#pragma once\n#include <pgmspace.h>\n"
       "static const uint8_t ICON_PNG[] PROGMEM = {\n%s\n};\nstatic const size_t ICON_PNG_LEN = %d;\n") % (S, S, len(data), ",\n".join(rows), len(data))
(root / "tools" / "romi" / "diag" / "icon.h").write_text(src, encoding="utf-8", newline="\n")
print("icon.png %d bytes -> tools/romi/diag/icon.h" % len(data))
