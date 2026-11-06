"""増えるスクリーンの座席表を焼く（3-C・参考 mul.jpg）。

頭を中心にした角度の空間へ、大・中・小の順に面を置く。大きいものほど手前、小さいものほど奥。
面どうしは重ねない。メインスクリーンの後ろは避けない（首を振るとメインだけが面の前を滑り、後ろの面が出てくる）。

出力: Assets/Scripts/Streaming/ScreenMultiplySeats.cs（手で直さない。直すなら定数か seed を変えて焼き直す）
使い方: py -3.11 tools/make-screen-wall.py [--seed 11]
"""
import argparse
import io
import math
import os
import random

CASE_ASPECT = 1280 / 1030
OUT = os.path.join("Assets", "Scripts", "Streaming", "ScreenMultiplySeats.cs")

# (幅の下限, 上限, 距離の下限, 上限, 枚数, 面どうしの隙間 度)
TIERS = [
    (20.0, 27.0, 2.7, 3.4, 14, 2.4),
    (13.0, 19.0, 3.2, 4.4, 36, 1.6),
    (7.5, 12.0, 4.0, 6.0, 44, 1.2),
]
FIRST = dict(az=13.0, el=24.0, w=21.0, d=3.3, ty=-8.0, tp=-5.0, r=-3.0, tint=1, delay=0)
FRONT_HALF_DEG = 112.0
BACK_GAP_SCALE = 3.0
RIPPLE_POWER = 0.6  # 順位 = (並び順/枚数)^これ。0.6 でさざめき（順位 0.14 まで）が 4 枚になる
DELAY_SLOTS = 16


def wrap(deg):
    return (deg + 180.0) % 360.0 - 180.0


def angular_gap(a, b):
    daz = abs(wrap(a["az"] - b["az"])) * math.cos(math.radians((a["el"] + b["el"]) / 2))
    return daz, abs(a["el"] - b["el"])


def generate(seed):
    rng = random.Random(seed)
    seats = [dict(FIRST)]

    def fits(cand, gap):
        for s in seats:
            dx, dy = angular_gap(cand, s)
            if dx < (cand["w"] + s["w"]) / 2 + gap and dy < (cand["w"] + s["w"]) / CASE_ASPECT / 2 + gap:
                return False
        return True

    for wmin, wmax, dmin, dmax, count, gap in TIERS:
        placed = tries = 0
        while placed < count and tries < 40000:
            tries += 1
            front = rng.random() < 0.86
            az = rng.uniform(-FRONT_HALF_DEG, FRONT_HALF_DEG) if front else rng.uniform(FRONT_HALF_DEG, 360 - FRONT_HALF_DEG)
            az = wrap(az)
            cand = dict(az=round(az, 1), el=round(rng.triangular(-58, 70, 8), 1), w=round(rng.uniform(wmin, wmax), 1))
            if fits(cand, gap * (1.0 if abs(az) < FRONT_HALF_DEG else BACK_GAP_SCALE)):
                cand["d"] = round(rng.uniform(dmin, dmax), 2)
                seats.append(cand)
                placed += 1
        assert placed == count, (wmin, placed)

    for s in seats[1:]:
        inward = -1.0 if s["az"] > 0 else 1.0
        s["ty"] = round(inward * rng.uniform(0, 6) + rng.gauss(0, 11), 1)
        s["tp"] = round(-s["el"] * 0.10 + rng.gauss(0, 7), 1)
        s["r"] = round(rng.gauss(0, 4.5) + (rng.choice([-1, 1]) * rng.uniform(6, 12) if rng.random() < 0.16 else 0), 1)
        s["tint"] = 2 if rng.random() < 0.07 else (1 if rng.random() < 0.22 else 0)
        s["delay"] = rng.randrange(DELAY_SLOTS)

    first = seats[0]
    for s in seats:
        dx, dy = angular_gap(s, first)
        s["_order"] = math.hypot(dx, dy) + rng.uniform(-12, 12)
    ordered = sorted(seats[1:], key=lambda s: s["_order"])
    first["rank"] = 0.0
    for i, s in enumerate(ordered):
        s["rank"] = round(((i + 1) / len(ordered)) ** RIPPLE_POWER, 3)
    return seats


def fmt(v):
    return f"{v:.3f}f".replace(".000f", "f") if isinstance(v, float) else str(v)


def write(seats, seed):
    lines = [
        "// 焼いたもの。手で直さない — tools/make-screen-wall.py --seed " + str(seed),
        "namespace FixedCamVr.Streaming",
        "{",
        "    public static partial class ScreenMultiplyLayout",
        "    {",
        "        /// <summary>座席表。0 番が最初に点く 1 枚。</summary>",
        "        public static readonly ScreenSeat[] Seats =",
        "        {",
    ]
    for s in seats:
        args = [s["az"], s["el"], s["w"], s["d"], s["ty"], s["tp"], s["r"], s["tint"], s["delay"], s["rank"]]
        lines.append("            new ScreenSeat(" + ", ".join(fmt(float(a)) if i not in (7, 8) else str(a) for i, a in enumerate(args)) + "),")
    lines += ["        };", "    }", "}", ""]
    tmp = OUT + ".tmp"
    with io.open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    os.replace(tmp, OUT)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=11)
    a = ap.parse_args()
    seats = generate(a.seed)
    write(seats, a.seed)
    ripple = sum(1 for s in seats[1:] if s["rank"] <= 0.14)
    front = sum(1 for s in seats if abs(s["az"]) < 60)
    print(f"{OUT}: {len(seats)} seats / ripple={ripple} / within 60deg={front}")


if __name__ == "__main__":
    main()
