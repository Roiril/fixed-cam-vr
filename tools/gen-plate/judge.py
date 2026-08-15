# 生成物を入力（暗くした種）と突き合わせて合否を出す。
#   使い方: py -3.11 judge.py <種.png> <生成物.png> [ラベル]
# 合否の線は先に決めてある（後から動かさない）。
import sys, warnings, statistics as st
warnings.filterwarnings("ignore")
from PIL import Image, ImageChops
sys.stdout.reconfigure(encoding="utf-8")


W, H = 640, 480
BL = (0, 0, W // 2, int(H * 0.45))      # 人形が 1 体も居ない帯（左）
BR = (W // 2, 0, W, int(H * 0.45))      # 同（右）
DOLL = (10, 280, 300, 470)              # 人形が居るはずの所
NODOLL = (340, 280, 630, 470)           # 人形が居てはいけない所（右下）

def stats(im, box):
    d = sorted(im.convert("L").crop(box).getdata())
    q = lambda p: d[max(0, int(len(d) * p / 100) - 1)]
    return dict(mean=sum(d) / len(d), sd=st.pstdev(d), p5=q(5), p95=q(95))

def main(seed_path, path, label):
    seed = Image.open(seed_path).convert("RGB")
    im = Image.open(path).convert("RGB")
    size_ok = im.size == (W, H)
    if not size_ok:
        im = im.resize((W, H), Image.LANCZOS)

    s_l, s_r = stats(seed, BL), stats(seed, BR)
    g_l, g_r = stats(im, BL), stats(im, BR)
    rl, rr = g_l["mean"] / s_l["mean"], g_r["mean"] / s_r["mean"]
    cl = g_l["sd"] / s_l["sd"]

    # 人形が居るか / 居ないはずの所に居ないか（プレートとの差の量で見る）
    d = ImageChops.difference(im.convert("L"), seed.convert("L"))
    dm = lambda box: sum(d.crop(box).getdata()) / (
        (box[2] - box[0]) * (box[3] - box[1]))

    checks = [
        ("寸法 640x480",        size_ok,                       f"{Image.open(path).size}"),
        ("左右の明るさが揃う",   abs(rl - rr) <= 0.03,          f"左 {rl:.3f} / 右 {rr:.3f}  差 {abs(rl-rr):.3f}"),
        ("入力の明るさを保つ",   0.95 <= rl <= 1.05 and 0.95 <= rr <= 1.05, f"左 {rl:.3f} / 右 {rr:.3f}"),
        ("コントラストを保つ",   0.85 <= cl <= 1.20,            f"左 sd 比 {cl:.3f}（{s_l['sd']:.1f} → {g_l['sd']:.1f}）"),
        ("左に人形が居る",       dm(DOLL) >= 12,                f"差 {dm(DOLL):.1f}"),
        ("右を触っていない",     dm(NODOLL) <= 8,               f"差 {dm(NODOLL):.1f}"),
    ]
    ng = [c for c in checks if not c[1]]
    print(f"== {label} ==")
    for name, ok, detail in checks:
        print(f"  {'OK ' if ok else 'NG '} {name:22s} {detail}")
    print(f"  → {'合格' if not ng else f'不合格 {len(ng)} 件'}")
    return 0 if not ng else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2],
                  sys.argv[3] if len(sys.argv) > 3 else sys.argv[2]))
