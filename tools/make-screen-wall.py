"""増えるスクリーンの座席表を焼く（3-C・参考 mul.jpg・走馬灯）。

頭を巻く帯に、頭の方を向いた面を並べる。散らばった残骸ではなく、秩序のある灯の列にする。
- 帯 1（上）: 仰角 +27°・半径 3.3m。0 番（メインの右上）はこの帯の 1 枚
- 帯 0（目の高さ）: 仰角 +2°・半径 3.9m。メインの後ろ（方位 ±26° 以内）は空ける
- 帯 2（最上）: 仰角 +46° 前後・半径 4.6m。小さい面がまばらに、方位 ±110° 以内だけ
- 帯 3（霧の奥）: 6.8〜10.5m。帯の隙間の奥に見える高さを優先して置く
帯 0 と 1 は 360° を巻き、後方（|方位| > 130°）は 3 枚に 1 枚抜く。
幅は 3 種類を周期の見えない並びで混ぜ（同じ幅が 3 枚続かない）、大きい面だけ帯の中心から上下と奥行きをずらす。
面どうしは重ねない（霧の奥の段だけ、手前の面の後ろへ少し隠れてよい）。

点く順（rank）は 0 番との方位差を主にし、帯ごとに少し遅らせる。左右の波は真後ろで出会う。
映す画の遅れ（delay）は 0 番との方位差に比例させる（180° で 15 枚前）。

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

# 帯の番号。ScreenMultiplyLayout.Band* と対。
EYE, UPPER, TOP, FAR = 0, 1, 2, 3

# 帯 0 と 1 の面の見かけの幅（度）と、選ぶ重み。
WIDTHS = (11.0, 15.5, 22.0)
WIDTH_WEIGHTS = (0.34, 0.40, 0.26)
GAP_MIN, GAP_MAX = 0.8, 1.6
BIG_EL_JITTER = 2.5
BIG_DIST_JITTER = 0.25
ROLL_MAX = 1.2
BACK_DEG = 130.0          # これより後ろは 3 枚に 1 枚抜く
BACK_DROP_EVERY = 3

# (仰角の中心, 半径 m)
RING = {UPPER: (27.0, 3.3), EYE: (2.0, 3.9)}
MAIN_CLEAR_DEG = 26.0     # 目の高さの帯はメインの後ろを空ける

TOP_EL, TOP_EL_JITTER, TOP_DIST = 46.0, 1.5, 4.6
TOP_W = (8.0, 10.0)
TOP_AZ = 110.0
TOP_COUNT = (10, 14)
TOP_GAP = 2.0

# 霧の奥の段。(幅の下限, 上限, 距離の下限, 上限, 枚数, 手前の面へ食い込んでよい度)。距離は ScreenMultiplyLayout.FarTierM より先。
FAR_TIER = (3.6, 6.4, 6.8, 10.5, 56, 1.6)
FRONT_HALF_DEG = 112.0
# 霧の奥の段を置く高さ（帯の隙間）。(中心, ばらつき, 重み)
FAR_EL_MIX = ((16.0, 2.5, 0.34), (40.0, 2.0, 0.22), (-14.0, 5.0, 0.24), (55.0, 4.0, 0.20))

FIRST = dict(az=13.0, el=24.5, w=22.0, d=3.3, band=UPPER)

# 点く順。帯ごとの遅れ（上 → 目の高さ → 最上 → 霧の奥）と、席ごとのゆらぎ（±）。
BAND_DELAY = {UPPER: 0.0, EYE: 0.06, TOP: 0.12, FAR: 0.18}
RANK_JITTER = 0.03
DELAY_SLOTS = 16
DELAY_AT_BACK = 15        # 方位差 180° の面が映す遅れ（取り込みの枚数）
PALE_SHARE = 0.25         # 淡い寒色の割合（残りは寒色）


def wrap(deg):
    return (deg + 180.0) % 360.0 - 180.0


def angular_gap(a, b):
    daz = abs(wrap(a["az"] - b["az"])) * math.cos(math.radians((a["el"] + b["el"]) / 2))
    return daz, abs(a["el"] - b["el"])


def overlaps(a, b, gap):
    dx, dy = angular_gap(a, b)
    return dx < (a["w"] + b["w"]) / 2 + gap and dy < (a["w"] + b["w"]) / CASE_ASPECT / 2 + gap


def az_span(w, el):
    """見かけの幅 w の面が、仰角 el で方位を何度占めるか。"""
    return w / math.cos(math.radians(el))


def pick_widths(rng, n, head=None, circular=False):
    """周期の見えない幅の並び。同じ幅が 3 枚続かない（circular なら末尾と頭もつなげて見る）。"""
    for _ in range(2000):
        seq = [] if head is None else [head]
        while len(seq) < n + (0 if head is None else 1):
            w = rng.choices(WIDTHS, WIDTH_WEIGHTS)[0]
            if len(seq) >= 2 and seq[-1] == seq[-2] == w:
                continue
            seq.append(w)
        if circular:
            ring = seq + seq[:2]
            if any(ring[i] == ring[i + 1] == ring[i + 2] for i in range(len(seq))):
                continue
        return seq if head is None else seq[1:]
    raise RuntimeError("width sequence")


def spread_gaps(rng, count, total):
    """count 個の隙間を total 度に配る。どれも GAP_MIN〜GAP_MAX。無理なら None。"""
    if not (count * GAP_MIN <= total <= count * GAP_MAX):
        return None
    extra = total - count * GAP_MIN
    room = GAP_MAX - GAP_MIN
    for _ in range(200):
        r = [rng.random() for _ in range(count)]
        s = extra / sum(r)
        if max(r) * s <= room:
            return [GAP_MIN + x * s for x in r]
    # 偏りの小さい配り方へ倒す
    return [GAP_MIN + extra / count] * count


def fill_arc(rng, start, length, el, n_hint, head=None, circular=False):
    """方位 start から length 度の弧に、幅を混ぜて面を詰める。返すのは (中心の方位, 幅) の列。"""
    for _ in range(4000):
        n = n_hint + rng.randint(-3, 3)
        if n < 3:
            continue
        widths = pick_widths(rng, n, head=head, circular=circular)
        spans = [az_span(w, el) for w in widths]
        # 弧の両端にも隙間を置く（circular なら頭の面の両隣の 2 つ）。隙間の度は方位の度。
        gaps = spread_gaps(rng, n + 1, (length - sum(spans)) * math.cos(math.radians(el)))
        if gaps is None:
            continue
        gaps = [g / math.cos(math.radians(el)) for g in gaps]
        out, at = [], start
        for i, (w, sp) in enumerate(zip(widths, spans)):
            at += gaps[i]
            out.append((wrap(at + sp / 2), w))
            at += sp
        return out
    raise RuntimeError(f"cannot fill arc start={start} length={length}")


def make_seat(rng, az, w, band):
    el0, d0 = RING[band]
    big = w == WIDTHS[-1]
    return dict(
        az=round(az, 1),
        el=round(el0 + (rng.uniform(-BIG_EL_JITTER, BIG_EL_JITTER) if big else 0.0), 1),
        w=w,
        d=round(d0 + (rng.uniform(-BIG_DIST_JITTER, BIG_DIST_JITTER) if big else 0.0), 2),
        band=band,
    )


def thin_back(seats):
    """後方（|方位| > BACK_DEG）の面を、並びの順に 3 枚に 1 枚抜く。"""
    kept, k = [], 0
    for s in sorted(seats, key=lambda s: (s["az"] + 360.0) % 360.0):
        if abs(s["az"]) > BACK_DEG:
            k += 1
            if k % BACK_DROP_EVERY == 0:
                continue
        kept.append(s)
    return kept


def three_in_a_row(seats, circular):
    """方位の順に並べて、同じ幅が 3 枚続く所があるか（後方を抜いた後で見る）。"""
    w = [s["w"] for s in sorted(seats, key=lambda s: (s["az"] + 360.0) % 360.0)]
    if circular:
        w = w + w[:2]
    return any(w[i] == w[i + 1] == w[i + 2] for i in range(len(w) - 2))


def band_ring(rng, band, start, length, n_hint, head=None, extra=()):
    """帯を 1 本詰めて後方を抜く。抜いた後に同じ幅が 3 枚並んだら詰め直す。"""
    el, _ = RING[band]
    for _ in range(500):
        arc = fill_arc(rng, start, length, el, n_hint, head=head, circular=head is not None)
        seats = thin_back([make_seat(rng, az, w, band) for az, w in arc])
        if not three_in_a_row(list(extra) + seats, circular=head is not None):
            return seats
    raise RuntimeError(f"band {band}: three in a row")


def generate(seed):
    rng = random.Random(seed)
    first = dict(FIRST)
    el_up, _ = RING[UPPER]

    # 上の帯。0 番の右の縁から 360° 回って左の縁まで。
    span0 = az_span(first["w"], el_up)
    upper = band_ring(rng, UPPER, first["az"] + span0 / 2, 360.0 - span0, 18, head=first["w"], extra=[first])

    # 目の高さの帯。メインの後ろ（±26°）を空けて、右から後ろを回って左まで。
    eye = band_ring(rng, EYE, MAIN_CLEAR_DEG, 360.0 - 2 * MAIN_CLEAR_DEG, 17)

    seats = [first] + upper + eye

    # 最上の帯。小さい面がまばらに。
    # 枚数は 10〜14 から選び、置ききれなければ 1 枚ずつ減らす（下限 10）。
    top_n = rng.randint(*TOP_COUNT)
    top = []
    for attempt in range(1000):
        if attempt and attempt % 200 == 0 and top_n > TOP_COUNT[0]:
            top_n -= 1
        top, tries = [], 0
        while len(top) < top_n and tries < 4000:
            tries += 1
            cand = dict(az=round(rng.uniform(-TOP_AZ, TOP_AZ), 1),
                        el=round(TOP_EL + rng.uniform(-TOP_EL_JITTER, TOP_EL_JITTER), 1),
                        w=round(rng.uniform(*TOP_W), 1), d=TOP_DIST, band=TOP)
            if all(not overlaps(cand, s, TOP_GAP) for s in seats + top):
                top.append(cand)
        if len(top) == top_n:
            break
    assert len(top) == top_n, ("top", len(top))
    seats += top

    # 面どうしは重ねない（念のため全組を見る）。
    for i in range(len(seats)):
        for j in range(i + 1, len(seats)):
            assert not overlaps(seats[i], seats[j], 0.0), (seats[i], seats[j])

    # 霧の奥の段は別の乱数で足す（手前の段を動かさない）。帯の隙間の高さを優先する。
    far_rng = random.Random(seed + 1000)
    wmin, wmax, dmin, dmax, count, overlap = FAR_TIER
    near_count = len(seats)
    far, tries = [], 0
    while len(far) < count and tries < 80000:
        tries += 1
        az = wrap(far_rng.uniform(-FRONT_HALF_DEG, FRONT_HALF_DEG) if far_rng.random() < 0.85
                  else far_rng.uniform(FRONT_HALF_DEG, 360 - FRONT_HALF_DEG))
        center, spread, _ = far_rng.choices(FAR_EL_MIX, [m[2] for m in FAR_EL_MIX])[0]
        cand = dict(az=round(az, 1), el=round(far_rng.gauss(center, spread), 1),
                    w=round(far_rng.uniform(wmin, wmax), 1), band=FAR)
        if not -55.0 <= cand["el"] <= 68.0:
            continue
        if any(overlaps(cand, s, -overlap) for s in seats[:near_count]):
            continue
        if any(overlaps(cand, s, 0.8) for s in far):
            continue
        cand["d"] = round(far_rng.uniform(dmin, dmax), 2)
        far.append(cand)
    assert len(far) == count, ("far", len(far))
    seats += far

    dress(seats, first, rng, far_rng)
    return seats


def dress(seats, first, rng, far_rng):
    """面は頭を向く（傾けない。roll だけ人が置いた程度にずらす）。色・遅れ・点く順。"""
    raw = []
    for s in seats:
        r = far_rng if s["band"] == FAR else rng
        daz = abs(wrap(s["az"] - first["az"]))
        s["ty"] = 0.0
        s["tp"] = 0.0
        s["r"] = 0.0 if s is first else round(r.uniform(-ROLL_MAX, ROLL_MAX), 1)
        s["tint"] = 1 if s is first else (1 if r.random() < PALE_SHARE else 0)
        s["delay"] = min(DELAY_SLOTS - 1, int(round(DELAY_AT_BACK * daz / 180.0)))
        raw.append(0.0 if s is first else daz / 180.0 + BAND_DELAY[s["band"]] + r.uniform(-RANK_JITTER, RANK_JITTER))
    top = max(raw)
    for s, v in zip(seats, raw):
        s["rank"] = 0.0 if s is first else round(max(0.001, v / top), 4)


def fmt(v):
    if not isinstance(v, float):
        return str(v)
    s = f"{v:.4f}".rstrip("0").rstrip(".")
    return ("0" if s in ("-0", "") else s) + "f"


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
        floats = [s["az"], s["el"], s["w"], s["d"], s["ty"], s["tp"], s["r"]]
        args = [fmt(float(a)) for a in floats] + [str(s["tint"]), str(s["delay"]), fmt(float(s["rank"])), str(s["band"])]
        lines.append("            new ScreenSeat(" + ", ".join(args) + "),")
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
    assert len(seats) <= 160, len(seats)
    write(seats, a.seed)
    bands = [sum(1 for s in seats if s["band"] == b) for b in (EYE, UPPER, TOP, FAR)]
    ripple = sum(1 for s in seats[1:] if s["rank"] <= 0.14)
    print(f"{OUT}: {len(seats)} seats / eye={bands[0]} upper={bands[1]} top={bands[2]} far={bands[3]} / ripple={ripple}")


if __name__ == "__main__":
    main()
