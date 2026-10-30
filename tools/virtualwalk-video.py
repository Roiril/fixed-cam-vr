#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity の VirtualWalkRun が書いた NPC 視点の連番を、mp4 と指標レポートにする。

    py -3.11 tools/virtualwalk-video.py                 # Logs/virtualwalk/ の一番新しい実行
    py -3.11 tools/virtualwalk-video.py Logs/virtualwalk/20261014_180000_tag
    py -3.11 tools/virtualwalk-video.py --compare <before のフォルダ> <after のフォルダ> [--out <mp4>]

入力フォルダ（`Assets/Scripts/Streaming/Editor/VirtualWalk/VirtualWalkRun.cs` が書く）:
  f00000.jpg…  NPC の目から見た絵（30fps）
  frames.tsv   90Hz の刻み（tick t x z bodyYaw screenYaw pick active dipping dim glitch
               glitchCount collapse power report）
  events.tsv   t kind detail（commit / sfx_switch / sfx_glitch / glitch_visual_end / outro）

出力（単独）: `<フォルダ>/walk.mp4`・`report.md`・`sheet_cuts.png`・`sheet_outro.png`。
出力（--compare）: 左 before・右 after の mp4（既定は `Logs/virtualwalk/compare_<日時>.mp4`）と、
その隣の `<mp4 の名前>_cuts.png`（before / after の sheet_cuts を縦に積んだもの）。

⚠ **実機の代わりではない**（VirtualWalkRun と同じ）。見るのは時刻・順序・曲線・重なり。
⚠ **無いコマを別の絵で埋めない。** 連番に穴がある・数が刻みと合わない・要るコマが無いときは、
   メッセージを出して終了コード 1 で落ちる。
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

try:
    import imageio_ffmpeg
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont
except ImportError as e:
    print(f"⚠ 依存が無い（py -3.11 -m pip install imageio-ffmpeg pillow numpy）: {e}", file=sys.stderr)
    raise SystemExit(1)

ROOT = Path(__file__).resolve().parent.parent
RUNS = Path("G:/マイドライブ/shubie/renders/virtualwalk")
FPS = 30                      # 書き出しのコマ数（VirtualWalkRun の 90Hz ÷ OutEvery 3）
TICK_PER_FRAME = 3
SOUND_SEC = 0.26              # 切替の乱れの音は固定長のクリップ
GLITCH_ON = 0.01              # 乱れが「見えている」とみなす _Glitch の下限
RETURN_WITHIN_SEC = 2.0       # A→B→A の戻りがこの秒数以内なら「往復」
TURN_DEG_PER_SEC = 30.0       # bodyYaw がこれより速く動いている間を「旋回中」とする
SETTLED = 0.999               # 終幕の到達判定
CUT_OFFSETS = (-0.10, -0.05, 0.0, 0.05, 0.10)
CUT_MAX_ROWS = 6
TILE_W = 320
OUTRO_TILES = 10
OUTRO_COLS = 5

FRAME_COLS = ["tick", "t", "x", "z", "bodyYaw", "screenYaw", "pick", "active", "dipping", "dim",
              "glitch", "glitchCount", "collapse", "power", "report"]
JPG_RE = re.compile(r"^f(\d{5})\.jpg$")
RUN_RE = re.compile(r"^\d{8}_\d{6}")


class Fail(Exception):
    """入力が足りない・食い違う。メッセージを出して終了コード 1 にする。"""


# ---------------------------------------------------------------- 読み込み

@dataclass
class Run:
    dir: Path
    n_jpg: int
    cols: dict                 # 列名 → numpy 配列
    events: list               # (t, kind, detail)
    dt: float                  # 1 刻みの秒

    @property
    def n_ticks(self) -> int:
        return len(self.cols["t"])

    def jpg(self, idx: int) -> Path:
        if not 0 <= idx < self.n_jpg:
            raise Fail(f"コマ f{idx:05d} が無い（0〜{self.n_jpg - 1} しか無い）: {self.dir}")
        p = self.dir / f"f{idx:05d}.jpg"
        if not p.is_file():
            raise Fail(f"コマが無い: {p}")
        return p

    def tick_at(self, t: float) -> int:
        i = int(math.floor((t - float(self.cols["t"][0])) / self.dt + 0.5))
        if not 0 <= i < self.n_ticks:
            raise Fail(f"t={t:.3f}s の刻みが frames.tsv に無い（0〜{self.n_ticks - 1}）: {self.dir}")
        return i

    def events_of(self, kind: str) -> list:
        return [e for e in self.events if e[1] == kind]


def read_tsv(path: Path) -> tuple[list[str], list[list[str]]]:
    if not path.is_file():
        raise Fail(f"{path.name} が無い: {path.parent}")
    lines = [ln for ln in path.read_text(encoding="utf-8-sig").splitlines() if ln.strip()]
    if not lines:
        raise Fail(f"{path.name} が空: {path}")
    header = lines[0].split("\t")
    rows = []
    for n, ln in enumerate(lines[1:], start=2):
        cells = ln.split("\t")
        if len(cells) != len(header):
            raise Fail(f"{path.name} の {n} 行目の列数が {len(cells)}（見出しは {len(header)}）")
        rows.append(cells)
    return header, rows


def load_run(folder: Path) -> Run:
    if not folder.is_dir():
        raise Fail(f"フォルダが無い: {folder}")
    idxs = sorted(int(m.group(1)) for p in folder.iterdir() if (m := JPG_RE.match(p.name)))
    if not idxs:
        raise Fail(f"f00000.jpg 形式の連番が無い: {folder}")
    if idxs != list(range(len(idxs))):
        missing = sorted(set(range(idxs[-1] + 1)) - set(idxs))
        raise Fail(f"連番に穴がある（欠け {len(missing)} 個。先頭 {missing[:5]}）: {folder}")

    header, rows = read_tsv(folder / "frames.tsv")
    for c in FRAME_COLS:
        if c not in header:
            raise Fail(f"frames.tsv に列 '{c}' が無い（見出し: {header}）")
    if len(rows) < 2:
        raise Fail(f"frames.tsv の行が少ない（{len(rows)} 行）: {folder}")
    try:
        cols = {c: np.array([float(r[header.index(c)]) for r in rows], dtype=np.float64) for c in header}
    except ValueError as e:
        raise Fail(f"frames.tsv に数値でない値がある: {e}") from e
    t = cols["t"]
    dt = float(t[-1] - t[0]) / (len(t) - 1)
    if dt <= 0:
        raise Fail("frames.tsv の t が増えていない")

    expected = -(-len(rows) // TICK_PER_FRAME)   # 0, 3, 6… 刻みで 1 コマ
    if len(idxs) != expected:
        raise Fail(f"コマ数 {len(idxs)} と刻み数 {len(rows)}（→ {expected} コマのはず）が合わない: {folder}")

    eh, erows = read_tsv(folder / "events.tsv")
    for c in ("t", "kind", "detail"):
        if c not in eh:
            raise Fail(f"events.tsv に列 '{c}' が無い")
    events = []
    try:
        for r in erows:
            events.append((float(r[eh.index("t")]), r[eh.index("kind")], r[eh.index("detail")]))
    except ValueError as e:
        raise Fail(f"events.tsv に数値でない t がある: {e}") from e
    return Run(folder, len(idxs), cols, events, dt)


def kv(detail: str) -> dict:
    return dict(re.findall(r"(\w+)=(\S+)", detail))


def newest_run() -> Path:
    if not RUNS.is_dir():
        raise Fail(f"{RUNS.relative_to(ROOT)} が無い（先に VirtualWalkRun を回す）")
    runs = sorted(p for p in RUNS.iterdir() if p.is_dir() and RUN_RE.match(p.name))
    if not runs:
        raise Fail(f"{RUNS.relative_to(ROOT)} に実行フォルダが無い")
    return runs[-1]


def resolve(arg: str) -> Path:
    return (ROOT / arg).resolve()


# ---------------------------------------------------------------- 指標

def runs_of(mask) -> list[tuple[int, int]]:
    """True が続く区間 [start, end)。"""
    out, start = [], None
    for i, v in enumerate(mask):
        if v and start is None:
            start = i
        elif not v and start is not None:
            out.append((start, i))
            start = None
    if start is not None:
        out.append((start, len(mask)))
    return out


def delta_angle(a, b):
    """a - b を -180〜180 度に。"""
    return (np.asarray(a) - np.asarray(b) + 180.0) % 360.0 - 180.0


def perceived(dim: float) -> float:
    return max(0.0, 1.0 - dim) ** (1.0 / 2.2)


def stats(values) -> dict | None:
    if len(values) == 0:
        return None
    a = np.asarray(values, dtype=np.float64)
    return {"min": float(a.min()), "median": float(np.median(a)), "max": float(a.max()),
            "p95": float(np.percentile(a, 95))}


def analyze(run: Run) -> dict:
    c, dt = run.cols, run.dt
    t = c["t"]
    m: dict = {"dt": dt, "n_ticks": run.n_ticks, "n_jpg": run.n_jpg, "seconds": float(t[-1] - t[0]) + dt}

    # a) カット
    commits = []
    for et, _, detail in run.events_of("commit"):
        d = kv(detail)
        if "cam" not in d:
            raise Fail(f"commit の detail に cam= が無い: {detail!r}")
        commits.append({"t": et, "cam": int(d["cam"]), "src": d.get("src", "?")})
    intervals = [b["t"] - a["t"] for a, b in zip(commits, commits[1:])]
    for i, cm in enumerate(commits):
        cm["interval"] = intervals[i - 1] if i > 0 else None
    seq = [int(c["active"][0])] + [cm["cam"] for cm in commits]   # 先頭は歩き出しの映像
    round_trips = []
    for i in range(len(commits) - 1):
        prev, cur, nxt = seq[i], seq[i + 1], seq[i + 2]
        gap = commits[i + 1]["t"] - commits[i]["t"]
        if nxt == prev and cur != prev and gap <= RETURN_WITHIN_SEC:
            round_trips.append({"t": commits[i]["t"], "from": prev, "via": cur, "gap": gap})
    m["commits"] = commits
    m["intervals"] = stats(intervals)
    m["round_trips"] = round_trips

    # b) 暗転の曲線
    dips = []
    for s, e in runs_of(c["dipping"] > 0.5):
        dims = [float(round(v, 2)) for v in c["dim"][s:e]]
        perc = [perceived(float(v)) for v in c["dim"][s:e]]
        dips.append({"t": float(t[s]), "dur": (e - s) * dt, "n": e - s, "dims": dims,
                     "perceived": [round(p, 2) for p in perc], "mean_perceived": float(np.mean(perc)),
                     "min_dim": float(c["dim"][s:e].min()), "max_dim": float(c["dim"][s:e].max()),
                     "open": e == run.n_ticks})
    m["dips"] = dips

    # c) 乱れ
    ends = [(et, float(kv(d).get("dur", "nan"))) for et, _, d in run.events_of("glitch_visual_end")]
    sfx = [et for et, _, _ in run.events_of("sfx_glitch")]
    episodes, used_end = [], set()
    for s, e in runs_of(c["glitch"] > GLITCH_ON):
        onset = float(t[s])
        end_t = float(t[e]) if e < run.n_ticks else None
        dur = None
        if end_t is not None:
            for k, (et, d) in enumerate(ends):
                if k not in used_end and abs(et - end_t) <= dt * 1.5 and not math.isnan(d):
                    dur = d
                    used_end.add(k)
                    break
        episodes.append({"onset": onset, "end": end_t, "dur": dur, "run_dur": (e - s) * dt,
                         "peak": float(c["glitch"][s:e].max()), "sfx": []})
    stray_sfx = []
    for st in sfx:
        hit = None
        for ep in episodes:
            if ep["onset"] - 1e-6 <= st and (ep["end"] is None or st <= ep["end"] + 1e-6):
                hit = ep
        if hit is None:
            stray_sfx.append(st)
        else:
            hit["sfx"].append(st - hit["onset"])
    m["glitches"] = episodes
    m["stray_sfx"] = stray_sfx
    m["unmatched_visual_end"] = len(ends) - len(used_end)

    # d) スクリーンの追従
    lag = np.abs(delta_angle(c["bodyYaw"], c["screenYaw"]))
    rate = np.zeros(run.n_ticks)
    rate[1:] = np.abs(delta_angle(c["bodyYaw"][1:], c["bodyYaw"][:-1])) / dt
    turning = rate > TURN_DEG_PER_SEC
    m["lag_all"] = stats(lag)
    m["lag_turn"] = stats(lag[turning])
    m["lag_all_at"] = float(t[int(lag.argmax())])
    m["lag_turn_at"] = float(t[int(np.where(turning)[0][lag[turning].argmax()])]) if turning.any() else None
    m["turn_ticks"] = int(turning.sum())

    # e) 終幕
    outs = run.events_of("outro")
    col = c["collapse"]
    drops = np.diff(col)
    bad = np.where(drops < -1e-9)[0]
    m["collapse_monotonic"] = bad.size == 0
    m["collapse_violations"] = int(bad.size)
    m["collapse_worst_drop"] = float(-drops.min()) if bad.size else 0.0
    m["collapse_first_bad_t"] = float(t[bad[0] + 1]) if bad.size else None
    m["outro_t"] = outs[0][0] if outs else None
    m["collapse_done"] = m["report_done"] = None
    if m["outro_t"] is not None:
        after = t >= m["outro_t"] - dt * 0.5
        for key, name in (("collapse_done", "collapse"), ("report_done", "report")):
            hit = np.where(after & (c[name] >= SETTLED))[0]
            m[key] = float(t[hit[0]] - m["outro_t"]) if hit.size else None
        m["outro_to_end"] = float(t[-1] - m["outro_t"]) + dt
    return m


# ---------------------------------------------------------------- レポート

def f2(v, nd=2) -> str:
    return "-" if v is None else f"{v:.{nd}f}"


def lst(values, nd=2) -> str:
    return "[" + ", ".join(f"{v:.{nd}f}" for v in values) + "]"


def stat_line(s: dict | None, unit: str, nd=2) -> str:
    if s is None:
        return "なし"
    return f"最小 {s['min']:.{nd}f}{unit} / 中央値 {s['median']:.{nd}f}{unit} / 最大 {s['max']:.{nd}f}{unit}"


def render_report(run: Run, m: dict) -> str:
    L: list[str] = []
    A = L.append
    A(f"# VirtualWalk 指標レポート　{run.dir.name}")
    A("")
    A(f"- 長さ: {m['seconds']:.2f} 秒（{m['n_ticks']} 刻み / {1 / m['dt']:.1f}Hz、{m['n_jpg']} コマ / {FPS}fps）")
    A(f"- 生成: {datetime.now():%Y-%m-%d %H:%M:%S}")
    A("- ⚠ 実機の代わりではない。圧縮・伝送の痩せ・遅延・両眼視差・実機ディスプレイは入っていない。"
      "見るのは時刻・順序・曲線・重なり。")
    A("")

    A("## a) カット")
    A("")
    cm = m["commits"]
    A(f"- 切替の確定: {len(cm)} 回")
    A(f"- 確定どうしの間隔: {stat_line(m['intervals'], ' s')}")
    A(f"- 往復（A→B→A の戻りが {RETURN_WITHIN_SEC:.1f} s 以内）: {len(m['round_trips'])} 回")
    A("")
    if cm:
        A("| # | 時刻 s | カメラ | src | 前回からの間隔 s |")
        A("|---|---|---|---|---|")
        for i, x in enumerate(cm, 1):
            A(f"| {i} | {x['t']:.3f} | {x['cam']} | {x['src']} | {f2(x['interval'], 3)} |")
        A("")
    if m["round_trips"]:
        A("| 往復の始まり s | 元 | 経由 | 戻るまで s |")
        A("|---|---|---|---|")
        for r in m["round_trips"]:
            A(f"| {r['t']:.3f} | {r['from']} | {r['via']} | {r['gap']:.3f} |")
        A("")

    A("## b) 暗転の曲線")
    A("")
    A("- dim は 90Hz の 1 刻みごとの値（2 桁に丸め）。0 が明るい、1 が黒。")
    A("- 知覚輝度の目安 = (1 - dim)^(1/2.2)。曲線どうしを比べるための値で、実測の輝度ではない。")
    A("")
    if not m["dips"]:
        A("暗転は 1 回も無かった。")
        A("")
    else:
        A("| # | 開始 s | 長さ s | 刻み数 | 最小 dim | 最大 dim | 知覚輝度の平均 |")
        A("|---|---|---|---|---|---|---|")
        for i, d in enumerate(m["dips"], 1):
            note = "（終了前に打ち切り）" if d["open"] else ""
            A(f"| {i} | {d['t']:.3f} | {d['dur']:.4f}{note} | {d['n']} | {d['min_dim']:.2f} | "
              f"{d['max_dim']:.2f} | {d['mean_perceived']:.2f} |")
        A("")
        for i, d in enumerate(m["dips"], 1):
            A(f"- 暗転 {i}　dim: `{lst(d['dims'])}`")
            A(f"  知覚輝度: `{lst(d['perceived'])}`")
        A("")

    A("## c) 乱れ")
    A("")
    A(f"- 乱れの音は固定 {SOUND_SEC:.2f} s のクリップ。映像の長さと比べる。")
    A(f"- 映像の乱れは glitch > {GLITCH_ON} の間。音は sfx_glitch イベントの時刻。")
    A("")
    if not m["glitches"]:
        A("映像の乱れは 1 回も無かった。")
        A("")
    else:
        A("| # | 開始 s | 映像の長さ s | 音との差 s（映像 − 音） | 最大 glitch | 音までの遅れ s |")
        A("|---|---|---|---|---|---|")
        for i, g in enumerate(m["glitches"], 1):
            dur = g["dur"] if g["dur"] is not None else g["run_dur"]
            note = "（終了前に打ち切り）" if g["end"] is None else ("" if g["dur"] is not None else "（終了イベント無し）")
            delay = ", ".join(f"{x:.3f}" for x in g["sfx"]) if g["sfx"] else "音なし"
            A(f"| {i} | {g['onset']:.3f} | {dur:.3f}{note} | {dur - SOUND_SEC:+.3f} | {g['peak']:.2f} | {delay} |")
        A("")
    A(f"- 音の発火 {len(m['stray_sfx']) + sum(len(g['sfx']) for g in m['glitches'])} 回のうち、"
      f"対応する映像の乱れが無かったもの: {len(m['stray_sfx'])} 回"
      + (f"（t = {', '.join(f'{x:.3f}' for x in m['stray_sfx'])}）" if m["stray_sfx"] else ""))
    if m["unmatched_visual_end"]:
        A(f"- ⚠ frames.tsv に対応する区間が無い glitch_visual_end が {m['unmatched_visual_end']} 件")
    A("")

    A("## d) スクリーンの追従")
    A("")
    A("- |bodyYaw と screenYaw の差|（度）。")
    la = m["lag_all"]
    A(f"- 全体: 最大 {la['max']:.2f}°（t = {m['lag_all_at']:.3f} s）/ p95 {la['p95']:.2f}°")
    lt = m["lag_turn"]
    if lt is None:
        A(f"- 旋回中（bodyYaw が {TURN_DEG_PER_SEC:.0f}°/s より速く動く刻み）: 該当する刻みが無い")
    else:
        A(f"- 旋回中（bodyYaw が {TURN_DEG_PER_SEC:.0f}°/s より速く動く刻み {m['turn_ticks']} 個）: "
          f"最大 {lt['max']:.2f}°（t = {m['lag_turn_at']:.3f} s）/ p95 {lt['p95']:.2f}°")
    A("")

    A("## e) 終幕")
    A("")
    if m["collapse_monotonic"]:
        A("- collapse は単調非減少（逆戻りなし）")
    else:
        A(f"- ⚠ collapse が逆戻りしている: {m['collapse_violations']} 刻み、最大 {m['collapse_worst_drop']:.4f} 戻る、"
          f"最初は t = {m['collapse_first_bad_t']:.3f} s")
    if m["outro_t"] is None:
        A("- outro イベントが無い（終幕に入っていない）")
    else:
        A(f"- 終幕の開始: t = {m['outro_t']:.3f} s（以後 {m['outro_to_end']:.2f} s 記録）")
        A(f"- 開始から collapse ≥ {SETTLED} まで: " + (f"{m['collapse_done']:.3f} s" if m["collapse_done"] is not None else "届かなかった"))
        A(f"- 開始から report ≥ {SETTLED} まで: " + (f"{m['report_done']:.3f} s" if m["report_done"] is not None else "届かなかった"))
    A("")
    return "\n".join(L)


def write_text(path: Path, text: str) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8", newline="\n")
    os.replace(tmp, path)


# ---------------------------------------------------------------- シート

def font(size: int):
    for name in ("arial.ttf", "segoeui.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    try:
        return ImageFont.load_default(size)
    except TypeError:
        return ImageFont.load_default()


def tile(path: Path, lines: list[str], width: int = TILE_W) -> Image.Image:
    im = Image.open(path).convert("RGB")
    h = max(2, round(im.height * width / im.width))
    im = im.resize((width, h), Image.Resampling.LANCZOS)
    fs = 13
    f = font(fs)
    band = len(lines) * (fs + 3) + 4
    d = ImageDraw.Draw(im, "RGBA")
    d.rectangle([0, h - band, width, h], fill=(0, 0, 0, 170))
    for i, ln in enumerate(lines):
        d.text((4, h - band + 2 + i * (fs + 3)), ln, fill=(255, 255, 255, 255), font=f)
    return im


def banner(text: str, width: int, height: int = 22) -> Image.Image:
    im = Image.new("RGB", (width, height), (40, 40, 40))
    ImageDraw.Draw(im).text((6, 3), text, fill=(255, 255, 255), font=font(14))
    return im


def make_sheet_cuts(run: Run, out: Path) -> bool:
    commits = [(et, kv(d)) for et, _, d in run.events_of("commit")][:CUT_MAX_ROWS]
    if not commits:
        print(f"⚠ commit が無いので {out.name} は作らない: {run.dir.name}")
        return False
    gap, rows = 4, []
    for n, (ct, d) in enumerate(commits, 1):
        tiles = []
        for off in CUT_OFFSETS:
            t = ct + off
            idx = int(math.floor(t * FPS + 0.5))
            dim = float(run.cols["dim"][run.tick_at(t)])
            tiles.append(tile(run.jpg(idx), [f"{off:+.2f}s  f{idx:05d}", f"t={t:.3f}  dim {dim:.2f}"]))
        w = sum(x.width for x in tiles) + gap * (len(tiles) - 1)
        row = Image.new("RGB", (w, 22 + max(x.height for x in tiles)), (24, 24, 24))
        row.paste(banner(f"commit #{n}  t={ct:.3f}s  cam={d.get('cam', '?')} src={d.get('src', '?')}", w), (0, 0))
        x = 0
        for tl in tiles:
            row.paste(tl, (x, 22))
            x += tl.width + gap
        rows.append(row)
    sheet = Image.new("RGB", (rows[0].width, sum(r.height for r in rows) + gap * (len(rows) - 1)), (24, 24, 24))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y))
        y += r.height + gap
    sheet.save(out)
    return True


def make_sheet_outro(run: Run, m: dict, out: Path) -> bool:
    if m["outro_t"] is None:
        print(f"⚠ outro イベントが無いので {out.name} は作らない: {run.dir.name}")
        return False
    first = int(math.floor(m["outro_t"] * FPS + 0.5))
    last = run.n_jpg - 1
    if first > last:
        raise Fail(f"終幕の開始コマ f{first:05d} が連番（〜f{last:05d}）の外: {run.dir}")
    idxs = [int(math.floor(first + (last - first) * k / (OUTRO_TILES - 1) + 0.5)) for k in range(OUTRO_TILES)]
    tiles = []
    for idx in idxs:
        t = idx / FPS
        ti = run.tick_at(t)
        c = run.cols
        tiles.append(tile(run.jpg(idx), [
            f"f{idx:05d}  t={t:.2f} ({t - m['outro_t']:+.2f})",
            f"col {c['collapse'][ti]:.2f} pow {c['power'][ti]:.2f} rep {c['report'][ti]:.2f}"]))
    gap = 4
    th = max(x.height for x in tiles)
    nrows = -(-len(tiles) // OUTRO_COLS)
    sheet = Image.new("RGB", (OUTRO_COLS * TILE_W + gap * (OUTRO_COLS - 1), nrows * th + gap * (nrows - 1)), (24, 24, 24))
    for k, tl in enumerate(tiles):
        sheet.paste(tl, ((k % OUTRO_COLS) * (TILE_W + gap), (k // OUTRO_COLS) * (th + gap)))
    sheet.save(out)
    return True


def stack_cuts(before: Path, after: Path, out: Path) -> None:
    parts = []
    for label, p in (("before", before), ("after", after)):
        im = Image.open(p).convert("RGB")
        parts.append((label, im))
    w = max(im.width for _, im in parts)
    sheet = Image.new("RGB", (w, sum(im.height + 26 for _, im in parts)), (24, 24, 24))
    y = 0
    for label, im in parts:
        sheet.paste(banner(label, w, 24), (0, y))
        sheet.paste(im, (0, y + 26))
        y += im.height + 26
    sheet.save(out)


# ---------------------------------------------------------------- 動画

def verify_mp4(path: Path, expected: int) -> tuple[int, float, int]:
    if not path.is_file() or path.stat().st_size == 0:
        raise Fail(f"mp4 が出来ていない（無い or 0 バイト）: {path}")
    frames, secs = imageio_ffmpeg.count_frames_and_secs(str(path))
    if frames != expected:
        raise Fail(f"mp4 のコマ数 {frames} が連番の数 {expected} と合わない: {path}")
    return int(frames), float(secs), path.stat().st_size


def encode_dir(run: Run, out: Path, source: Path | None = None) -> None:
    source = source or run.dir
    indices = sorted(int(m.group(1)) for p in source.iterdir() if (m := JPG_RE.match(p.name)))
    if indices != list(range(run.n_jpg)):
        raise Fail(f"映像の連番が揃っていない: {source}")
    tmp = out.with_name(out.stem + ".tmp.mp4")
    cmd = [imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error",
           "-framerate", str(FPS), "-start_number", "0", "-i", str(source / "f%05d.jpg"),
           "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2",   # H.264 は縦横とも偶数
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-movflags", "+faststart", str(tmp)]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        tmp.unlink(missing_ok=True)
        raise Fail(f"ffmpeg が失敗した: {r.stderr.strip()[:300]}")
    os.replace(tmp, out)


def encode_compare(a: Run, b: Run, out: Path) -> int:
    n = min(a.n_jpg, b.n_jpg)
    sa = Image.open(a.jpg(0)).size
    sb = Image.open(b.jpg(0)).size
    if sa != sb:
        raise Fail(f"before {sa[0]}x{sa[1]} と after {sb[0]}x{sb[1]} で寸法が違う（縮尺を勝手に変えない）")
    w, h = sa
    W, H = (2 * w) - ((2 * w) % 2), h - (h % 2)
    fs = max(14, h // 18)
    f = font(fs)
    tmp = out.with_name(out.stem + ".tmp.mp4")
    writer = imageio_ffmpeg.write_frames(str(tmp), (W, H), fps=FPS, quality=None, macro_block_size=1,
                                         output_params=["-crf", "20"])
    writer.send(None)
    try:
        for i in range(n):
            canvas = Image.new("RGB", (2 * w, h))
            for k, (run, label) in enumerate(((a, "before"), (b, "after"))):
                im = Image.open(run.jpg(i)).convert("RGB")
                if im.size != (w, h):
                    raise Fail(f"{run.jpg(i).name} の寸法 {im.size} が先頭と違う: {run.dir}")
                d = ImageDraw.Draw(im, "RGBA")
                tw = d.textlength(label, font=f)
                d.rectangle([0, 0, tw + 16, fs + 10], fill=(0, 0, 0, 190))
                d.text((8, 4), label, fill=(255, 255, 255, 255), font=f)
                canvas.paste(im, (k * w, 0))
            writer.send(canvas.crop((0, 0, W, H)).tobytes())
        writer.close()
    except BaseException:
        try:
            writer.close()
        except Exception:
            pass
        tmp.unlink(missing_ok=True)
        raise
    os.replace(tmp, out)
    return n


# ---------------------------------------------------------------- 実行

def rel(p: Path) -> str:
    try:
        return str(p.relative_to(ROOT))
    except ValueError:
        return str(p)


def summary_line(m: dict) -> str:
    iv = m["intervals"]
    return (f"カット {len(m['commits'])} 回"
            + (f"（間隔 中央値 {iv['median']:.2f}s）" if iv else "")
            + f" / 往復 {len(m['round_trips'])} / 暗転 {len(m['dips'])} / 乱れ {len(m['glitches'])}"
            + f" / 追従 最大 {m['lag_all']['max']:.1f}° / collapse "
            + ("単調" if m["collapse_monotonic"] else "⚠逆戻りあり"))


def do_single(folder: Path) -> int:
    run = load_run(folder)
    # 新しい仮想歩行は画面の変化に加えて、実際の移動の離隔・向きも判定する。
    navigation_path = folder / "navigation.json"
    if navigation_path.is_file():
        nav = json.loads(navigation_path.read_text(encoding="utf-8-sig"))
        required = ("speed", "headingError", "clearance", "boundaryClearance", "animTime", "meshClearance")
        if any(c not in run.cols for c in required):
            raise Fail("歩行検証の列が不足している")
        if not all(np.isfinite(run.cols[c]).all() for c in required):
            raise Fail("歩行検証の数値に非有限値がある")
        min_clearance = float(run.cols["clearance"].min())
        moving = run.cols["speed"] > .02
        max_heading = float(run.cols["headingError"][moving].max()) if moving.any() else 0.
        if min_clearance < nav["wallMarginM"] - .002:
            raise Fail(f"壁との余白が不足: {min_clearance:.3f}m")
        if run.cols["boundaryClearance"].min() < -.002 or max_heading > 2.1:
            raise Fail("歩行範囲からのはみ出し、または横滑りがある")
        mesh_clearance = float(run.cols["meshClearance"].min())
        if mesh_clearance < nav["wallMarginM"] + .00499:
            raise Fail(f"手足を含む全身の掃引が壁または外縁へ接触: {mesh_clearance:.4f}m")
        if not moving.any() or np.ptp(run.cols["animTime"][moving]) < .1:
            raise Fail("歩行または歩行モーションが進んでいない")
        # C# の自己申告と別に、保存された壁寸法と実移動から離隔・向きを計算する。
        positions = np.column_stack((run.cols["x"], run.cols["z"]))
        independent_min = math.inf
        for wall in nav["walls"]:
            a = np.array((wall["x1"], wall["z1"]))
            b = np.array((wall["x2"], wall["z2"]))
            axis = b - a
            along = np.clip((positions - a) @ axis / max(float(axis @ axis), 1e-12), 0, 1)
            distances = np.linalg.norm(positions - (a + along[:, None] * axis), axis=1)
            independent_min = min(independent_min, float(distances.min()) - wall["thick"] / 2 - nav["bodyRadiusM"])
        # 90Hz の両端の誤差内でも壁を跨がないよう、最大1刻みの移動距離を余裕から引く。
        movement = np.diff(positions, axis=0)
        lengths = np.linalg.norm(movement, axis=1)
        if independent_min - float(lengths.max()) < nav["wallMarginM"] - .01:
            raise Fail(f"別計算でも壁に接近: 体との余白 {independent_min:.3f}m")
        real_moving = lengths > .001
        directions = np.degrees(np.arctan2(movement[:, 0], movement[:, 1]))
        errors = np.abs(delta_angle(run.cols["bodyYaw"][1:], directions))
        if real_moving.any() and errors[real_moving].max() > 4.:
            raise Fail("移動ベクトルと体の向きが一致しない")
        for view in ("observer", "overhead"):
            encode_dir(run, folder / f"{view}.mp4", folder / view)
            verify_mp4(folder / f"{view}.mp4", run.n_jpg)
        print(f"歩行検証: 立位包絡と壁の余白 最小 {min_clearance:.3f}m / 全身掃引の余白 最小 {mesh_clearance:.3f}m / 移動中の向き誤差 最大 {max_heading:.2f}°")
    m = analyze(run)
    mp4 = folder / "walk.mp4"
    encode_dir(run, mp4)
    frames, secs, size = verify_mp4(mp4, run.n_jpg)
    write_text(folder / "report.md", render_report(run, m))
    made = []
    if make_sheet_cuts(run, folder / "sheet_cuts.png"):
        made.append("sheet_cuts.png")
    if make_sheet_outro(run, m, folder / "sheet_outro.png"):
        made.append("sheet_outro.png")
    print(f"{rel(mp4)}  ({frames} コマ / {secs:.2f} 秒 / {size / 1024:.0f} KiB)")
    print(f"{rel(folder / 'report.md')}" + "".join(f" / {n}" for n in made))
    print(summary_line(m))
    return 0


def print_diff(ma: dict, mb: dict) -> None:
    def med(m):
        return f"{m['intervals']['median']:.2f}s" if m["intervals"] else "-"

    print("\n--- 指標の差（before → after）---")
    print(f"カット数      {len(ma['commits'])} → {len(mb['commits'])}")
    print(f"間隔の中央値  {med(ma)} → {med(mb)}")
    print(f"往復          {len(ma['round_trips'])} → {len(mb['round_trips'])}")
    print(f"暗転の回数    {len(ma['dips'])} → {len(mb['dips'])}")
    print(f"乱れの回数    {len(ma['glitches'])} → {len(mb['glitches'])}")
    print(f"追従の最大    {ma['lag_all']['max']:.1f}° → {mb['lag_all']['max']:.1f}°")
    print(f"collapse 到達 {f2(ma['collapse_done'], 2)}s → {f2(mb['collapse_done'], 2)}s（終幕開始から）")
    print("\n--- 暗転の曲線（先頭 3 回。dim / 知覚輝度）---")
    for i in range(3):
        da = ma["dips"][i] if i < len(ma["dips"]) else None
        db = mb["dips"][i] if i < len(mb["dips"]) else None
        print(f"[暗転 {i + 1}]")
        for label, d in (("before", da), ("after ", db)):
            if d is None:
                print(f"  {label}: なし")
            else:
                print(f"  {label}: {d['dur'] * 1000:.0f}ms  dim {lst(d['dims'])}")
                print(f"          知覚 {lst(d['perceived'])}  平均 {d['mean_perceived']:.2f}")


def do_compare(before: Path, after: Path, out: Path | None) -> int:
    ra, rb = load_run(before), load_run(after)
    ma, mb = analyze(ra), analyze(rb)
    if out is None:
        RUNS.mkdir(parents=True, exist_ok=True)
        out = RUNS / f"compare_{datetime.now():%Y%m%d_%H%M%S}.mp4"
    out.parent.mkdir(parents=True, exist_ok=True)
    n = encode_compare(ra, rb, out)
    frames, secs, size = verify_mp4(out, n)
    print(f"{rel(out)}  ({frames} コマ / {secs:.2f} 秒 / {size / 1024:.0f} KiB / 左 before 右 after)")
    if ra.n_jpg != rb.n_jpg:
        print(f"⚠ コマ数が違う（before {ra.n_jpg} / after {rb.n_jpg}）。短い方の {n} コマで切った")

    sheets = []
    for run, m in ((ra, ma), (rb, mb)):
        p = run.dir / "sheet_cuts.png"
        if not p.is_file() and not make_sheet_cuts(run, p):
            sheets = None
            break
        sheets.append(p)
    if sheets is None:
        print("⚠ commit が無い実行があるので compare の cuts 画像は作らない")
    else:
        cuts = out.with_name(out.stem + "_cuts.png")
        stack_cuts(sheets[0], sheets[1], cuts)
        print(rel(cuts))
    print_diff(ma, mb)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="VirtualWalkRun の出力を mp4 と指標にする")
    ap.add_argument("dir", nargs="?", help="Logs/virtualwalk/<日時>[_tag] のフォルダ（省略で一番新しいもの）")
    ap.add_argument("--compare", nargs=2, metavar=("BEFORE", "AFTER"), help="2 つの実行を左右に並べる")
    ap.add_argument("--out", help="--compare の mp4 の出力先")
    args = ap.parse_args()
    try:
        if args.compare:
            if args.dir:
                raise Fail("--compare とフォルダの位置引数は同時に使えない")
            return do_compare(resolve(args.compare[0]), resolve(args.compare[1]),
                              resolve(args.out) if args.out else None)
        if args.out:
            raise Fail("--out は --compare のときだけ使う")
        return do_single(resolve(args.dir) if args.dir else newest_run())
    except Fail as e:
        print(f"⚠ {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
