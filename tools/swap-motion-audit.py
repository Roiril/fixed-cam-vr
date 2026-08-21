#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""黒い波（入れ替わりの覆い）の**動き**を、白地の連番 PNG から測る。

    .\\tools\\unity.ps1 menu swap -Set plate=white,layers=none   # 前の版
    py -3.11 tools/swap-motion-audit.py --label none
    .\\tools\\unity.ps1 menu swap -Set plate=white               # 全層
    py -3.11 tools/swap-motion-audit.py --label all --baseline none

設計は `reports/2026-08-21_swap-wave-design.html`。**機械の門は 3 つ**（設計 5 節）:

| 門 | 何を見る | 通る条件 |
|---|---|---|
| ① 隠し切り不変 | 覆い切ったコマの「人の灰色」 | 前の版から**増えていない**（+0.5 ポイント以内） |
| ② 山が動く | **前の版との差**が帯の方向へ動く速さ | 0.8 帯/コマ 以上・向きが行き先と一致 |
| ③ 針の裾 | 帯の縦中央だけ余分に伸びた分 | 前の版より**最大が 1.15 倍以上**／中央値 ÷ 最大が参考画像のオーダー |

⚠⚠ **3 つとも前の版（`-Set layers=none`）と比べないと判定できない。**
`--baseline` 無しで回すと数値は出るが**門は 1 つも判定しない**。理由は門②が典型 —
前の版にも山と**同じ向きへ動くもの**が 2 つある（覆いの前線と、縮む / 育つ人型の縁）ので、
「一方向へ動いたか」だけでは山を足したかどうかが分からない（実測: 前の版でも偏り 0.7〜1.1）。
プレビューは決定論なので、**同じコマ番号の差 ＝ 足した層そのもの**になる。

⚠⚠ **白地でしか測れない。** 現場の映像は右半分が元から真っ黒なので、黒い覆いが背景と
同化して 1 つも分離できない（`canon/LEDGER.md` 0098 続き 2 と同じ理由）。

⚠⚠ **数値が緑でも動画を必ず見る。** ここで測れるのは「動いたか」「増えていないか」だけで、
**かっこいいか**は 1 ビットも言っていない（`reference/why.md`「品質を採点しない」）。

⚠⚠ **測る窓を人型の周りに絞る**（2026-08-21 に踏んだ）。枠は 16:9 で映像は 4:3 なので
**画の左右 25% は元から真っ黒**（レターボックス）。絞らずに「行の黒の左端〜右端」を測ると
どの行も画幅いっぱいになり、山も針も 1 つも分離できない（最初の版がそうなった）。

⚠ 明暗のしきいは**そのコマの中央値に対する比**で決める（白地 0.90 に post の周辺減光と
夜間モードが掛かるので、絶対値で切ると版ごとにずれる）。
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
FRAMES = ROOT / "Assets/Screenshots/swap"
OUTDIR = ROOT / "logs/swap-motion"

# 出力 PNG の下 24% はキャプション（`ShowCompositePreview.CaptionHeightRatio`）。
CAPTION_RATIO = 0.24

# 帯の行数（`_SwapSmear.x`）。**シェーダと対**。変えたらここも直す。
ROWS = 48

# 明るさの相対しきい（そのコマの中央値に対する比）。
DARK_K = 0.35    # これより暗い ＝ 覆い（黒）
MID_K = 0.78     # これより暗い ＝ 人・人形・影（＝「隠し切れていない灰色」）

# 人型の周りに絞る窓の半幅（映像の幅に対する割合）。棒 ＋ 針の最大到達（figure 0.85 ＝
# 映像幅の約 10%）に対して十分広く、レターボックスを含まない値。
WINDOW_K = 0.22


def load_frames(d: Path) -> list[Path]:
    return sorted(p for p in d.glob("f*.png") if p.suffix == ".png")


def luma(path: Path) -> np.ndarray:
    """画の部分だけを 0..1 の輝度で返す（キャプションを落とす）。"""
    a = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0
    return (0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2])[
        :int(round(a.shape[0] / (1.0 + CAPTION_RATIO)))]


def content_columns(g: np.ndarray) -> tuple[int, int]:
    """映像が入っている列の範囲（レターボックスの黒帯を外す）。

    判定は「その列の**明るい画素**が行の 3 割以上」。覆いが出ていないコマ（頭の
    「ふつうの画」）で取るので、覆いを映像と間違えることは無い。
    """
    med = float(np.median(g))
    lit = (g > med * 0.55).mean(axis=0) > 0.30
    idx = np.flatnonzero(lit)
    if idx.size < 16:
        return 0, g.shape[1]
    return int(idx[0]), int(idx[-1] + 1)


def frame_stats(g: np.ndarray, x0: int, x1: int) -> tuple[float, float, float]:
    """（黒の割合, 人の灰色の割合, 地の中央値）。**映像の中だけ**を数える。"""
    c = g[:, x0:x1]
    med = float(np.median(c))
    dark = c < med * DARK_K
    grey = (c < med * MID_K) & ~dark
    return float(dark.mean()), float(grey.mean()), med


def dark_mask(g: np.ndarray, x0: int, x1: int) -> np.ndarray:
    c = g[:, x0:x1]
    return c < float(np.median(c)) * DARK_K


def figure_window(g: np.ndarray, x0: int, x1: int) -> tuple[int, int]:
    """覆い切ったコマから人型の左右の窓を解く（黒がいちばん濃い列を中心に）。"""
    d = dark_mask(g, x0, x1)
    col = d.sum(axis=0).astype(np.float32)
    k = max(3, (x1 - x0) // 64)
    col = np.convolve(col, np.ones(k) / k, mode="same")
    center = int(np.argmax(col))
    half = int((x1 - x0) * WINDOW_K)
    lo = max(0, center - half)
    hi = min(x1 - x0, center + half)
    return lo, hi


def band_extent(d: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """帯ごとの黒の横幅（画素）を 2 通りで返す。

    - `edge`: 帯の**上端と下端**の走査線の幅 ＝ 針を含まない棒の幅（跳びはここに出る）
    - `mid` : 帯の**縦中央**の走査線の幅 ＝ 棒 ＋ 針

    幅は「その走査線で最も左の黒」から「最も右の黒」まで（途切れは含めて数える —
    参考画像の線も途切れていて、それは筆のかすれとして読める）。
    """
    h = d.shape[0]
    edge = np.zeros(ROWS, dtype=np.float32)
    mid = np.zeros(ROWS, dtype=np.float32)
    for b in range(ROWS):
        y0 = int(round(b * h / ROWS))
        y1 = int(round((b + 1) * h / ROWS))
        if y1 <= y0 + 2:
            continue
        edge[b] = max(_line_extent(d[y0]), _line_extent(d[y1 - 1]))
        mid[b] = _line_extent(d[(y0 + y1) // 2])
    return edge, mid


def _line_extent(line: np.ndarray) -> float:
    idx = np.flatnonzero(line)
    return float(idx[-1] - idx[0] + 1) if idx.size else 0.0


def segments(n: int) -> list[tuple[str, int, int]]:
    """連番を 2 方向へ割る。**尺から解く**（コマ名に向きは入っていない）。"""
    half = n // 2
    return [("toDoll", 0, half), ("toHuman", half, n)]


def _smooth_bands(a: np.ndarray, k: int) -> np.ndarray:
    """帯の方向へ移動平均（空間の低域）。"""
    ker = np.ones(k, dtype=np.float32) / k
    return np.stack([
        np.convolve(np.pad(v, (k // 2, k // 2), mode="edge"), ker, mode="valid")[:a.shape[1]]
        for v in a])


def crest_highpass(edges: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """山だけを残した (コマ × 帯) の場と、そのコマが生きているかを返す。

    2 段で落とす:
      - **時間平均**を引く … 体の形（肩は太く頭と脚は細い）は帯ごとに固定なので消える
      - **帯方向の移動平均**を引く … 覆いの前線が作る「台」（覆われた帯はどれも広い）が消える

    残るのは 6 帯ほどの幅の膨らみ ＝ 走る波。⚠ 既存の波（`SwapWaveExt`）の横ずれは
    **左右の端へ逆符号**で効くので幅には出ない（残るのは伸縮ぶんの ±14px だけ）。
    """
    active = edges.sum(axis=1) > 0
    if active.sum() < 8:
        return np.zeros_like(edges), active
    res = edges - edges[active].mean(axis=0, keepdims=True)
    hp = res - _smooth_bands(res, 13)
    return np.where(edges > 2.0, hp, 0.0), active


def travel(hp: np.ndarray, active: np.ndarray) -> dict:
    """模様が帯の方向へ 1 コマあたり何帯ぶん動いたか。

    1 コマ後の場を帯方向へ s ずらして相関を取り、**正の相関の重心**を速さとする
    （+ = 下へ ＝ 帯の番号が増える向き ＝ 画では頭から足へ）。
    止まっている模様は s=0 に集まり、走っている模様だけが片側へ寄る。
    """
    if active.sum() < 8:
        return {}
    c = {}
    for s in range(-6, 7):
        tot = 0.0
        for i in range(len(hp) - 1):
            if not (active[i] and active[i + 1]):
                continue
            tot += float((hp[i] * np.roll(hp[i + 1], -s)).sum())
        c[s] = tot
    pos = {s: v for s, v in c.items() if v > 0.0}
    if not pos or sum(pos.values()) <= 0.0:
        return {}
    speed = sum(s * v for s, v in pos.items()) / sum(pos.values())
    return {
        "speed": float(speed),
        "dir": "down" if speed > 0 else "up",
        "energy": float(np.abs(hp[active]).mean()),
        "frames": int(active.sum()),
    }


def crest_track(edges: np.ndarray) -> dict:
    """その版だけで見た模様の動き（参考値）。

    ⚠⚠ **これ単独では門にできない**（2026-08-21 に 3 通り試して全部だめだった）。
      前の版にも**同じ向きへ動くもの**が 2 つある — 覆いの前線（`SwapFront`）と、
      縮む / 育つ人型の縁。どちらも山と同じ向きへ進むので、
      「一方向へ動いたか」では山を足したかどうかが分からない（前の版でも偏り 0.7〜1.1 が出る）。
    ⇒ **門②は前の版との差**（`travel_delta`）で判定する。
    """
    hp, active = crest_highpass(edges)
    return travel(hp, active)


def travel_delta(now: np.ndarray, base: np.ndarray) -> dict:
    """**前の版との差**だけの動き（門②の本体）。

    プレビューは決定論なので（乱数を使わず `_SwapSeed` はコマの時刻）、同じコマ番号の
    2 つの版の差は**足した層そのもの**になる。人型・前線・縮みは完全に消え、
    残るのは走る波・針・跳びだけ。その差が一方向へ動いていれば、山は確かに走っている。

    ⚠⚠ **差の「増えた側」だけを見る。** 山・針・跳びは黒を**足す**が、余韻（H）は
    晴れる段で黒を**引く**。引く側は帯ごとの hash で決まるので**走らない**（実測 0.1 帯/コマ）。
    符号つきで混ぜると引く側がラグ 0 を重くして、走っている山が薄まる
    （実測: 人形 → 人が -0.24 と出て「走っていない」と誤判定した）。
    ⚠ **差がはっきり出ているコマだけを見る。** 人形 → 人の前半は人型が人形の大きさ
    （画で 5 帯ほど）なので、山が足す量も数画素しか無い。
    """
    if now.shape != base.shape:
        return {"error": f"コマ数が違う（{now.shape} と {base.shape}）"}
    d = np.clip(now - base, 0.0, None)
    active = d.max(axis=1) > 8.0
    hp = d - _smooth_bands(d, 13)
    return travel(np.where(d > 0.0, hp, 0.0), active)


def needle_stats(edges: np.ndarray, mids: np.ndarray) -> dict:
    """針の長さの分布（門③）。帯の縦中央が上下端より余分に伸びた分（片側 ＝ 半分）。"""
    live = edges > 2.0
    extra = (mids - edges) * 0.5
    vals = extra[live & (extra > 0.5)]
    if vals.size < 20:
        return {}
    return {
        "n": int(vals.size),
        "median": float(np.median(vals)),
        "max": float(vals.max()),
        "p99": float(np.percentile(vals, 99)),
        "ratio": float(np.median(vals) / max(float(vals.max()), 1e-6)),
    }


def spike_stats(edges: np.ndarray) -> dict:
    """同時に跳んでいる帯の数（設計 C の入れすぎ検査。**同時 2 本以下**）。

    ⚠⚠ **「幅が広い帯」を数えると体の形を拾う**（肩は太く頭と脚は細い）。跳びは
    **1 帯だけが自分の時間平均から突き出る**事象なので、①隣の帯より高い ②自分の平均より高い
    の 2 つで絞る。山（設計 A）は 6 帯ほどまとめて持ち上げるので①で落ちる。

    ⚠ それでも体の形の残りを拾うので、**判定は前の版との差**で行う（絶対値には意味が無い）。
    """
    active = edges.sum(axis=1) > 0
    if active.sum() < 8:
        return {"worst": 0, "mean": 0.0}
    res = edges - edges[active].mean(axis=0, keepdims=True)
    worst, tot, cnt = 0, 0.0, 0
    for i, f in enumerate(res):
        if not active[i]:
            continue
        neigh = np.maximum(np.roll(f, 1), np.roll(f, -1))
        n = int(((f > neigh + 20.0) & (f > 20.0)).sum())
        worst = max(worst, n)
        tot += n
        cnt += 1
    return {"worst": worst, "mean": tot / cnt if cnt else 0.0}


def track_png(edges: np.ndarray, out: Path) -> None:
    """帯の幅を (行 × コマ) の地図にして焼く。**山は斜めの縞として目に見える。**"""
    if edges.size == 0 or edges.max() <= 0:
        return
    # ⚠ 最大値で割ると**棘 1 本のせいで全体が真っ黒**になる（実測）。98% 分位で伸ばし、
    #   さらに平方根を掛けて中くらいの値を見えるところへ持ってくる。
    hi = max(float(np.percentile(edges[edges > 0], 98)), 1e-3)
    img = (np.sqrt(np.clip(edges.T / hi, 0, 1)) * 255).astype(np.uint8)
    img = np.repeat(np.repeat(img, 6, axis=0), 3, axis=1)
    out.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(img, mode="L").save(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=str(FRAMES), help="連番 PNG のフォルダ")
    ap.add_argument("--label", default="all", help="この計測の名前（比較用に残す）")
    ap.add_argument("--baseline", default=None, help="比べる相手の label（門①の判定に使う）")
    args = ap.parse_args()

    fs = load_frames(Path(args.dir))
    if len(fs) < 40:
        print(f"NG: 連番が足りない（{len(fs)} コマ）。先に "
              f".\\tools\\unity.ps1 menu swap -Set plate=white を回す")
        return 1
    print(f"{len(fs)} コマを測る: {args.dir}")

    # 1 巡目: 映像の列と、コマごとの黒 / 灰色の割合（覆い切りのコマを見つけるため）。
    x0, x1 = content_columns(luma(fs[0]))
    stats = [frame_stats(luma(p), x0, x1) for p in fs]
    print(f"  映像の列 {x0}..{x1}（画幅の {(x1 - x0) / luma(fs[0]).shape[1] * 100:.0f}%）")

    result = {"label": args.label, "frames": len(fs), "dirs": {}}
    fails: list[str] = []
    all_edges: list[np.ndarray] = []
    all_hp: list[np.ndarray] = []

    for name, lo, hi in segments(len(fs)):
        ci = lo + int(np.argmax([stats[i][0] for i in range(lo, hi)]))
        gc = luma(fs[ci])
        wlo, whi = figure_window(gc, x0, x1)

        # 2 巡目: 人型の窓の中だけで帯の幅を測る。
        edges, mids = [], []
        for i in range(lo, hi):
            d = dark_mask(luma(fs[i]), x0, x1)[:, wlo:whi]
            e, m = band_extent(d)
            edges.append(e)
            mids.append(m)
        E = np.stack(edges)
        M = np.stack(mids)
        all_edges.append(E)
        all_hp.append(np.clip(crest_highpass(E)[0], 0.0, None))

        cr = crest_track(E)
        nd = needle_stats(E, M)
        sp = spike_stats(E)
        result["dirs"][name] = {
            "coveredFrame": fs[ci].name, "coveredDark": stats[ci][0],
            "coveredGrey": stats[ci][1], "window": [wlo, whi],
            "crest": cr, "needle": nd, "spike": sp,
            # ⚠ 門②は**版の差**で判定するので、帯の幅の生の表を残しておく
            #   （次の版がこれを読んで差を取る。画像を 2 組ディスクに置かなくて済む）。
            "edges": np.round(E, 1).tolist(),
        }

        print(f"\n[{name}]  覆い切り {fs[ci].name}  黒 {stats[ci][0] * 100:.1f}%  "
              f"人の灰色 {stats[ci][1] * 100:.2f}%  （地の中央値 {stats[ci][2]:.2f} / "
              f"窓 {whi - wlo}px）")
        if cr:
            print(f"  （参考）この版の模様の動き {cr['speed']:+.2f} 帯/コマ"
                  f"（前線と縮みも混ざる。門②は下の差で見る）")
        else:
            print("  （参考）模様の動きを測れなかった（コマが足りない）")
        if nd:
            print(f"  ③ 針: 中央値 {nd['median']:.1f}px / 最大 {nd['max']:.1f}px / "
                  f"99% {nd['p99']:.1f}px / 中央÷最大 {nd['ratio']:.3f}（参考 0.05）/ n={nd['n']}")
            if not 0.005 <= nd["ratio"] <= 0.30:
                fails.append(f"{name}: 針の裾が参考画像のオーダーから外れている"
                             f"（中央÷最大 {nd['ratio']:.3f}）")
        else:
            print("  ③ 針: 測れなかった（中央だけ伸びた帯が 20 本未満）")
            fails.append(f"{name}: 針が 1 本も出ていない")
        print(f"  C 跳び: 同時に跳んでいる帯 最大 {sp['worst']} / 平均 {sp['mean']:.2f}"
              "（前の版との差で判定する）")

    track_png(np.concatenate(all_edges), OUTDIR / f"{args.label}_track.png")
    # ⭐ **山はこの絵で斜めの縞として目に見える**（数値だけで信じない）。
    track_png(np.concatenate(all_hp), OUTDIR / f"{args.label}_crest.png")

    # 門①②③は**前の版との比較**でしか判定できない（絶対値には意味が無い）。
    base_path = OUTDIR / f"{args.baseline}.json" if args.baseline else None
    if base_path and base_path.exists():
        base = json.loads(base_path.read_text(encoding="utf-8"))
        delta_maps: list[np.ndarray] = []
        print(f"\n{args.baseline} と比べる")
        for name in result["dirs"]:
            b = base.get("dirs", {}).get(name)
            if not b:
                continue
            cur = result["dirs"][name]
            now = cur["coveredGrey"]
            dg = (now - b["coveredGrey"]) * 100
            print(f"  [{name}]")
            print(f"    ① 隠し切り: 人の灰色 {b['coveredGrey'] * 100:.2f}% → {now * 100:.2f}%"
                  f"（{dg:+.2f} ポイント）/ 黒 {b['coveredDark'] * 100:.1f}% → "
                  f"{cur['coveredDark'] * 100:.1f}%")
            if dg > 0.5:
                fails.append(f"{name}: 隠し切りが弱くなった（人の灰色 {dg:+.2f} ポイント）")

            # ② 足した層だけの動き。**山は行き先の方向へ走る**（設計 A）—
            #    人 → 人形は下へ（人形は足元に現れる）、人形 → 人は上へ（人は上へ育つ）。
            want = "down" if name == "toDoll" else "up"
            E_now = np.asarray(cur["edges"], dtype=np.float32)
            E_base = np.asarray(b.get("edges") or [], dtype=np.float32)
            td = travel_delta(E_now, E_base)
            if E_now.shape == E_base.shape:
                # ⭐ **山はこの絵で斜めの縞として目に見える**（差だけなので前線と縮みが居ない）。
                delta_maps.append(np.clip(E_now - E_base, 0.0, None))
            if td.get("error"):
                print(f"    ② 山: 比べられない（{td['error']}）")
                fails.append(f"{name}: 山を前の版と比べられない（{td['error']}）")
            elif not td:
                print("    ② 山: 差が無い ＝ 足した層が画に 1 画素も出ていない")
                fails.append(f"{name}: 足した層が画に出ていない")
            else:
                print(f"    ② 山: 差だけの動き {td['speed']:+.2f} 帯/コマ"
                      f"（{td['dir']}／期待 {want}）/ 差の平均 {td['energy']:.1f}px")
                if abs(td["speed"]) < 0.8:
                    fails.append(f"{name}: 足した層が走っていない"
                                 f"（{td['speed']:+.2f} 帯/コマ・0.8 以上が要る）")
                elif td["dir"] != want:
                    fails.append(f"{name}: 山が行き先と逆へ走っている"
                                 f"（{td['dir']}／期待 {want}）")

            # ③ 針と C 跳びは**差**で見る（前の版にも体の形ぶんの値が出るため）。
            bn, cn = b.get("needle") or {}, cur.get("needle") or {}
            if bn and cn:
                print(f"    ③ 針: 中央値 {bn['median']:.1f} → {cn['median']:.1f}px / "
                      f"最大 {bn['max']:.1f} → {cn['max']:.1f}px")
                if cn["max"] <= bn["max"] * 1.15:
                    fails.append(f"{name}: 針が伸びていない"
                                 f"（最大 {bn['max']:.1f} → {cn['max']:.1f}px）")
            ds = cur["spike"]["worst"] - b["spike"]["worst"]
            print(f"    C 跳び: 同時 {b['spike']['worst']} → {cur['spike']['worst']} 本"
                  f"（{ds:+d}）")
            if ds > 2:
                fails.append(f"{name}: 同時に跳ぶ帯が {ds:+d} 本増えた"
                             "（設計 C は同時 2 本以下）")
        if delta_maps:
            track_png(np.concatenate(delta_maps), OUTDIR / f"{args.label}_delta.png")
            print(f"  ⭐ 足した層だけの地図: {args.label}_delta.png"
                  "（斜めの縞 ＝ 走る波。縞が無ければ動いていない）")
    elif args.baseline:
        print(f"\n⚠ 比べる相手が無い（{base_path} が無い）。門は 1 つも判定していない。"
              f"\n  先に `menu swap -Set plate=white,layers=none` で焼いて "
              f"`--label {args.baseline}` で測る")
    else:
        print("\n⚠ --baseline を渡していないので**門は 1 つも判定していない**"
              "（数値だけ出した）。判定は前の版との差でしかできない — 冒頭の表を読む")

    OUTDIR.mkdir(parents=True, exist_ok=True)
    (OUTDIR / f"{args.label}.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=1, default=float), encoding="utf-8")
    print(f"\n保存: {OUTDIR / (args.label + '.json')} / "
          f"{args.label}_track.png / {args.label}_crest.png（山の斜めの縞）")
    if fails:
        print("\nNG:")
        for f in fails:
            print(f"  - {f}")
        return 1
    if base_path and base_path.exists():
        print("\n門はすべて通った（⚠ 動画は必ず見る。ここでは「かっこいいか」を測っていない）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
