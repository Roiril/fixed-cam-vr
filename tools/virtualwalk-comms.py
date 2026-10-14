#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""VirtualWalkRun が書いた comms.tsv から、VR 内の「通信パネル」の振る舞いを測り、コンタクトシートにする。

    py -3.11 tools/virtualwalk-comms.py Logs/virtualwalk/20261014_111327_comms_before2
    py -3.11 tools/virtualwalk-comms.py --compare <before のフォルダ> <after のフォルダ>

相対パスはリポジトリのルートから解く（virtualwalk-video.py と同じ）。絶対パスもそのまま通る。

見る 2 つの瞬間（events.tsv）:
  swap   最初の `comms_deliver` で detail が `BeginHow` で始まるもの（同じパネルで文面が差し替わる）
         窓 = [t_ev - 0.1, t_ev + 0.6]
  press  最初の `press` で detail が `start`（押し始め。流れている文面は消えなければならない）
         窓 = [t_ev - 0.1, t_ev + 0.7]

窓の中の comms.tsv の行（90Hz）から、刻みごとの変化を 72Hz の表示 1 コマ分（×90/72）に直して測る:
  maxPerceivedDrop72 知覚量 p = textAlpha ** (1/2.2) の 1 刻みの落ち幅の最大   swap <= 0.20 / press <= 0.25
  maxTextDrop72      textAlpha（線形）の 1 刻みの落ち幅の最大                    参考値（判定なし）
  swapWhileVisible   文字が見えたまま textId が変わった刻みの数                 == 0
  maxBoxJump72       boxH の 1 刻みの変化の最大 / 実行全体の最大 boxH            <= 0.15
  boxMovedWhileText  文字が見えている刻みで boxH が動いた数                      == 0
  clearToFirstChar / firstCharAfterEvent / boxSettleBeforeFirstChar  (swap のみ・参考値)
    「文字が消えた」= textAlpha==0 になった、または textId がイベント前の文面から変わった。
    見えたまま差し替わった場合（旧実装）は 0.000 (swapped while visible)。
    boxSettleBeforeFirstChar = 新しい文面の最初の 1 文字の行と、その 3 行前の |boxH の差|（m）。
    最初の文字が出た時に箱がまだ動いていたかを見る。

出力（単独）: `<フォルダ>/comms_report.md`・`comms_sheet_swap.png`・`comms_sheet_press.png`
出力（--compare）: `<after のフォルダ>/comms_compare.md`・`comms_compare_swap.png`・`comms_compare_press.png`
  （上段 = BEFORE、下段 = AFTER。見出しはフォルダ名から焼く。取り違えは絵の上で見える）

⚠ **実機の代わりではない**（VirtualWalkRun と同じ）。見るのは時刻・順序・曲線。
⚠ **無い入力を別のもので埋めない。** comms.tsv / events.tsv / 窓の中のイベント・コマが 1 つでも
   欠けたら、メッセージを出して終了コード 1 で落ちる。
⚠ textId はパネル文面の先頭 4 文字。先頭 4 文字が同じ文面同士の差し替えは swapWhileVisible に出ない。
"""
from __future__ import annotations

import argparse
import bisect
import math
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError as e:
    print(f"⚠ 依存が無い（py -3.11 -m pip install pillow）: {e}", file=sys.stderr)
    raise SystemExit(1)

ROOT = Path(__file__).resolve().parent.parent

TICK_HZ = 90.0                # comms.tsv の刻み
DISPLAY_HZ = 72.0             # 実機の表示コマ
K72 = TICK_HZ / DISPLAY_HZ    # 1 刻みの変化 → 72Hz の 1 コマ分
FRAME_FPS = 30                # fNNNNN.jpg は tick 3k に書かれる = t = k/30
TICK_PER_FRAME = 3
EPS = 1e-6                    # 3 桁に丸めた t どうしの比較の許容
FRAME_EPS = 6e-4              # コマの厳密な時刻 k/30 と、3 桁に丸めた窓・イベント t の比較の許容（丸め誤差 5e-4 より大きく、1 刻み 0.011 より十分小さい）
TICK_DT = 1.0 / TICK_HZ

# 窓: (イベントの前の秒, 後の秒)
WINDOW_SEC = {"swap": (0.1, 0.6), "press": (0.1, 0.7)}
PERCEIVED_DROP_MAX = {"swap": 0.20, "press": 0.25}
PERCEIVED_GAMMA = 1.0 / 2.2   # 知覚量 p = textAlpha ** (1/2.2)（virtualwalk-video.py の perceived と同じ指数）
SETTLE_ROWS = 3               # boxSettleBeforeFirstChar: 最初の 1 文字の行から何行前と比べるか
BOX_JUMP_MAX = 0.15
BOX_MOVE_EPS = 1e-4           # boxMovedWhileText で「動いた」とみなす m
CLEAR_SEARCH_SEC = 2.0        # clearToFirstChar: イベント後に textAlpha==0 の行を探す上限（秒）
WHERE_MAX = 3                 # 値の脇に出す時刻の数

TILE_W = 320
COLS = 7
PAD = 4
LABEL_H = 16
BG = (24, 24, 24)
LABEL_FG = (225, 225, 225)
MARK_FG = (255, 110, 90)      # イベント直後の最初のコマの文字色

COMMS_COLS = ["tick", "t", "stage", "chars", "textId", "textAlpha", "boxH"]
JP_FONTS = ["C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/YuGothM.ttc", "C:/Windows/Fonts/msgothic.ttc"]


class Fail(Exception):
    """入力が足りない・食い違う。メッセージを出して終了コード 1 にする。"""


# ---------------------------------------------------------------- 読み込み

@dataclass
class Comms:
    tick: list
    t: list
    stage: list
    chars: list
    text_id: list
    text_alpha: list
    box_h: list


@dataclass
class Run:
    dir: Path
    comms: Comms
    events: list              # (t, kind, detail)
    max_box_h: float


def read_tsv(path: Path) -> tuple[list[str], list[list[str]]]:
    if not path.is_file():
        raise Fail(f"{path.name} が無い: {path.parent}")
    # splitlines() は U+2028 等でも割るので使わない。textId に日本語が入る
    lines = [(n, ln.rstrip("\r")) for n, ln in enumerate(path.read_text(encoding="utf-8-sig").split("\n"), 1)]
    lines = [(n, ln) for n, ln in lines if ln.strip()]
    if not lines:
        raise Fail(f"{path.name} が空: {path}")
    header = lines[0][1].split("\t")
    rows = []
    for n, ln in lines[1:]:
        cells = ln.split("\t")
        if len(cells) != len(header):
            raise Fail(f"{path.name} の {n} 行目の列数が {len(cells)}（見出しは {len(header)}）: {path}")
        rows.append(cells)
    return header, rows


def load_comms(path: Path) -> Comms:
    header, rows = read_tsv(path)
    for c in COMMS_COLS:
        if c not in header:
            raise Fail(f"{path.name} に列 '{c}' が無い（見出し: {header}）")
    ix = {c: header.index(c) for c in COMMS_COLS}
    if len(rows) < 2:
        raise Fail(f"{path.name} の行が少ない（{len(rows)} 行）: {path}")
    try:
        return Comms(
            tick=[int(float(r[ix["tick"]])) for r in rows],
            t=[float(r[ix["t"]]) for r in rows],
            stage=[r[ix["stage"]] for r in rows],
            chars=[int(float(r[ix["chars"]])) for r in rows],
            text_id=[r[ix["textId"]] for r in rows],
            text_alpha=[float(r[ix["textAlpha"]]) for r in rows],
            box_h=[float(r[ix["boxH"]]) for r in rows],
        )
    except ValueError as e:
        raise Fail(f"{path.name} に数値でない値がある: {e}") from e


def load_events(path: Path) -> list:
    header, rows = read_tsv(path)
    for c in ("t", "kind", "detail"):
        if c not in header:
            raise Fail(f"{path.name} に列 '{c}' が無い（見出し: {header}）")
    try:
        return [(float(r[header.index("t")]), r[header.index("kind")], r[header.index("detail")]) for r in rows]
    except ValueError as e:
        raise Fail(f"{path.name} に数値でない t がある: {e}") from e


def load_run(folder: Path) -> Run:
    if not folder.is_dir():
        raise Fail(f"フォルダが無い: {folder}")
    comms = load_comms(folder / "comms.tsv")
    events = load_events(folder / "events.tsv")
    max_box = max(comms.box_h)
    if max_box <= 0:
        raise Fail(f"boxH が全行 0 以下で、箱の動きを正規化できない: {folder / 'comms.tsv'}")
    return Run(folder, comms, events, max_box)


def resolve(arg: str) -> Path:
    p = Path(arg)
    return p.resolve() if p.is_absolute() else (ROOT / p).resolve()


# ---------------------------------------------------------------- 指標

@dataclass
class Metric:
    window: str
    name: str
    value: float | int | None
    at: list = field(default_factory=list)   # 最大・発生の時刻（先頭 WHERE_MAX 個）
    limit: float | None = None               # PASS は value <= limit。None は参考値
    unit: str = ""
    na_reason: str = ""
    suffix: str = ""                         # 値の後ろに添える注記

    @property
    def passed(self) -> bool | None:
        if self.limit is None or self.value is None:
            return None
        return self.value <= self.limit + 1e-9

    def value_text(self) -> str:
        if self.value is None:
            return f"n/a: {self.na_reason}" if self.na_reason else "n/a"
        if isinstance(self.value, int):
            s = str(self.value)
        else:
            s = f"{self.value:.3f}"
        if self.unit:
            s += f" {self.unit}"
        if self.suffix:
            s += f" ({self.suffix})"
        if self.at and (self.value if self.value is not None else 0) > 0:
            s += " @t=" + ",".join(f"{a:.3f}" for a in self.at[:WHERE_MAX])
        return s

    def limit_text(self) -> str:
        if self.limit is None:
            return "info"
        if self.name in ("swapWhileVisible", "boxMovedWhileText"):
            return "== 0"
        return f"<= {self.limit:.2f}"

    def result_text(self) -> str:
        p = self.passed
        return "-" if p is None else ("PASS" if p else "FAIL")


@dataclass
class WindowResult:
    name: str
    t_ev: float
    event_desc: str
    lo: float
    hi: float
    n_rows: int
    frames: list              # フレーム番号 k
    stages: str
    metrics: list


def find_event(run: Run, window: str) -> tuple[float, str]:
    if window == "swap":
        hits = [e for e in run.events if e[1] == "comms_deliver" and e[2].startswith("BeginHow")]
        desc = 'comms_deliver "BeginHow…"'
    else:
        hits = [e for e in run.events if e[1] == "press" and e[2].strip() == "start"]
        desc = 'press "start"'
    if not hits:
        raise Fail(f"events.tsv に {desc} が無い（{window} の窓が作れない）: {run.dir}")
    ev = min(hits, key=lambda e: e[0])
    return ev[0], f'{desc} → "{ev[2]}"'


def compress(seq) -> list:
    out = []
    for s in seq:
        if not out or out[-1] != s:
            out.append(s)
    return out


def top_at(pairs, value_of_max) -> list:
    """(時刻, 値) のうち最大に等しいものの時刻。"""
    return [t for t, v in pairs if abs(v - value_of_max) < 1e-12]


def compute_window(run: Run, window: str) -> WindowResult:
    c = run.comms
    t_ev, desc = find_event(run, window)
    pre, post = WINDOW_SEC[window]
    lo, hi = t_ev - pre, t_ev + post

    idx = [i for i, t in enumerate(c.t) if lo - EPS <= t <= hi + EPS]
    if len(idx) < 2:
        raise Fail(f"{window} の窓 [{lo:.3f}, {hi:.3f}] に comms.tsv の行が {len(idx)} 行しか無い: {run.dir}")
    if c.t[idx[0]] - lo > TICK_DT + 1e-3 or hi - c.t[idx[-1]] > TICK_DT + 1e-3:
        raise Fail(f"{window} の窓 [{lo:.3f}, {hi:.3f}] が comms.tsv の範囲 "
                   f"[{c.t[0]:.3f}, {c.t[-1]:.3f}] からはみ出す（実行が窓の途中で終わっている）: {run.dir}")
    for a, b in zip(idx, idx[1:]):
        if c.tick[b] - c.tick[a] != 1:
            raise Fail(f"{window} の窓の中で tick が連続していない（{c.tick[a]} → {c.tick[b]}）: {run.dir}")

    ts = [c.t[i] for i in idx]
    ta = [c.text_alpha[i] for i in idx]
    bx = [c.box_h[i] for i in idx]
    tid = [c.text_id[i] for i in idx]
    n = len(idx)
    ms: list[Metric] = []

    # 1) 文字の落ち。判定は知覚量（ガンマ補正後）、線形は参考値
    pa = [max(a, 0.0) ** PERCEIVED_GAMMA for a in ta]
    pdrops = [((pa[j - 1] - pa[j]) * K72, ts[j]) for j in range(1, n)]
    pworst = max(0.0, max(d for d, _ in pdrops))
    ms.append(Metric(window, "maxPerceivedDrop72", pworst,
                     top_at([(t, d) for d, t in pdrops], pworst) if pworst > 0 else [], PERCEIVED_DROP_MAX[window]))
    drops = [((ta[j - 1] - ta[j]) * K72, ts[j]) for j in range(1, n)]
    worst = max(0.0, max(d for d, _ in drops))
    ms.append(Metric(window, "maxTextDrop72", worst, top_at([(t, d) for d, t in drops], worst) if worst > 0 else [],
                     None))

    # 2) 文字が見えたまま差し替わった
    swaps = [ts[j] for j in range(1, n) if ta[j - 1] > 0 and ta[j] > 0 and tid[j] != tid[j - 1]]
    ms.append(Metric(window, "swapWhileVisible", len(swaps), swaps, 0))

    # 3) 箱の跳ね
    jumps = [(abs(bx[j] - bx[j - 1]) * K72 / run.max_box_h, ts[j]) for j in range(1, n)]
    jmax = max(v for v, _ in jumps)
    ms.append(Metric(window, "maxBoxJump72", jmax, top_at([(t, v) for v, t in jumps], jmax) if jmax > 0 else [],
                     BOX_JUMP_MAX))

    # 4) 文字が見えている間に箱が動いた
    moved = [ts[j] for j in range(1, n) if ta[j - 1] > 0 and ta[j] > 0 and abs(bx[j] - bx[j - 1]) > BOX_MOVE_EPS]
    ms.append(Metric(window, "boxMovedWhileText", len(moved), moved, 0))

    # 5) swap のみ: 消えてから最初の 1 文字まで
    if window == "swap":
        ms.extend(clear_metrics(run, t_ev))

    frames = frames_in(run, window, lo, hi)
    return WindowResult(window, t_ev, desc, lo, hi, n, frames, "→".join(compress(c.stage[i] for i in idx)), ms)


def clear_metrics(run: Run, t_ev: float) -> list:
    """イベント以後で「文字が消えた」行 → そこから最初の「文字あり」の行、までを測る。
    消えた = textAlpha==0 になった、または textId がイベント直前の文面から変わった。
    見えたまま差し替わった（旧実装）なら、その行がそのまま最初の 1 文字の行になり 0.000。
    探す範囲は次の comms_deliver / press までに限る（別の場面の 0 を拾わない）。"""
    c = run.comms
    nxt = [e[0] for e in run.events
           if e[0] > t_ev + EPS and (e[1] == "comms_deliver" or e[1] == "press")]
    stop_moment = min(nxt) if nxt else float("inf")
    clear_stop = min(stop_moment, t_ev + CLEAR_SEARCH_SEC)
    i0 = next((i for i, t in enumerate(c.t) if t >= t_ev - EPS), None)
    clear_i = None
    if i0 is not None:
        ref_id = c.text_id[i0 - 1] if i0 > 0 else c.text_id[i0]
        for i in range(i0, len(c.t)):
            if c.t[i] > clear_stop + EPS:
                break
            if c.text_alpha[i] <= 1e-9 or c.text_id[i] != ref_id:
                clear_i = i
                break
    reason = ""
    clear = first = settle = None
    suffix = ""
    char_i = None
    if clear_i is None:
        reason = f"イベント後 {min(stop_moment - t_ev, CLEAR_SEARCH_SEC):.2f}s 以内に textAlpha==0 も textId の変化も無い"
    else:
        # 消えた行そのものも候補に入れる（見えたまま差し替わったなら、その行が最初の 1 文字）
        for i in range(clear_i, len(c.t)):
            if c.t[i] > stop_moment + EPS:
                break
            if c.chars[i] > 0 and c.text_alpha[i] > 0:
                char_i = i
                break
        if char_i is None:
            reason = f"t={c.t[clear_i]:.3f} で消えた後、文字が出ない"
        else:
            clear = c.t[char_i] - c.t[clear_i]
            first = c.t[char_i] - t_ev
            if c.text_alpha[clear_i] > 1e-9:
                suffix = "swapped while visible"
    settle_reason = reason
    if char_i is not None:
        if char_i - SETTLE_ROWS < 0:
            settle_reason = f"最初の 1 文字の行（tick {c.tick[char_i]}）の {SETTLE_ROWS} 行前が無い"
        else:
            settle = abs(c.box_h[char_i] - c.box_h[char_i - SETTLE_ROWS])
    settle_at = [c.t[char_i]] if char_i is not None and settle is not None else []
    return [Metric("swap", "clearToFirstChar", clear, [], None, "s", reason, suffix),
            Metric("swap", "firstCharAfterEvent", first, [], None, "s", reason, suffix),
            Metric("swap", "boxSettleBeforeFirstChar", settle, settle_at, None, "m", settle_reason)]


def frames_in(run: Run, window: str, lo: float, hi: float) -> list:
    c = run.comms
    k0 = math.ceil((lo - FRAME_EPS) * FRAME_FPS)
    k1 = math.floor((hi + FRAME_EPS) * FRAME_FPS)
    ks = list(range(max(k0, 0), k1 + 1))
    if not ks:
        raise Fail(f"{window} の窓 [{lo:.3f}, {hi:.3f}] にコマが 1 枚も無い: {run.dir}")
    last_tick = c.tick[-1]
    for k in ks:
        p = run.dir / f"f{k:05d}.jpg"
        if k * TICK_PER_FRAME > last_tick:
            raise Fail(f"コマ f{k:05d}（tick {k * TICK_PER_FRAME}）が comms.tsv の最後の tick {last_tick} より先: {run.dir}")
        if not p.is_file():
            raise Fail(f"{window} の窓の中のコマが無い: {p}")
    return ks


# ---------------------------------------------------------------- レポート

def md_table(rows: list[list[str]], head: list[str]) -> list[str]:
    out = ["| " + " | ".join(head) + " |", "|" + "|".join("---" for _ in head) + "|"]
    out += ["| " + " | ".join(r) + " |" for r in rows]
    return out


def window_lines(w: WindowResult) -> list[str]:
    return [f"- **{w.name}**: {w.event_desc} at t={w.t_ev:.3f}s、窓 [{w.lo:.3f}, {w.hi:.3f}]s、"
            f"{w.n_rows} 行・{len(w.frames)} コマ（f{w.frames[0]:05d}〜f{w.frames[-1]:05d}）",
            f"  - stage: `{w.stages}`"]


def verdict(results: list[WindowResult]) -> tuple[int, int]:
    checks = [m for w in results for m in w.metrics if m.passed is not None]
    return sum(1 for m in checks if not m.passed), len(checks)


def single_report(run: Run, results: list[WindowResult]) -> str:
    lines = [f"# comms report: {run.dir.name}", "",
             f"- run: `{run.dir}`",
             f"- k72 = 90/72 = {K72:.3f}、maxBoxH(run 全体) = {run.max_box_h:.3f} m", ""]
    for w in results:
        lines += window_lines(w)
    lines.append("")
    rows = [[m.window, m.name, m.value_text(), m.limit_text(), m.result_text()] for w in results for m in w.metrics]
    lines += md_table(rows, ["window", "metric", "value", "threshold", "result"])
    bad, total = verdict(results)
    lines += ["", f"**RESULT: {'PASS' if bad == 0 else 'FAIL'}**（{total} 項目中 {bad} 件 FAIL）", ""]
    return "\n".join(lines)


def compare_report(b: Run, a: Run, bres: list[WindowResult], ares: list[WindowResult]) -> str:
    lines = [f"# comms compare: BEFORE {b.dir.name} / AFTER {a.dir.name}", "",
             f"- BEFORE: `{b.dir}`  maxBoxH={b.max_box_h:.3f} m",
             f"- AFTER:  `{a.dir}`  maxBoxH={a.max_box_h:.3f} m"]
    if b.dir == a.dir:
        lines.append("- ⚠ BEFORE と AFTER が同じフォルダ（取り違えでなければ動作確認用）")
    lines += ["- k72 = 90/72 = 1.250。判定は AFTER のみ", ""]
    for tag, res in (("BEFORE", bres), ("AFTER", ares)):
        for w in res:
            lines.append(f"- {tag} {w.name}: t_ev={w.t_ev:.3f}s、{w.n_rows} 行・{len(w.frames)} コマ、stage `{w.stages}`")
    lines.append("")
    rows = []
    for wb, wa in zip(bres, ares):
        for mb, ma in zip(wb.metrics, wa.metrics):
            if (mb.window, mb.name) != (ma.window, ma.name):
                raise Fail(f"BEFORE と AFTER で指標の並びが違う: {mb.name} / {ma.name}")
            rows.append([ma.window, ma.name, mb.value_text(), ma.value_text(), ma.limit_text(), ma.result_text()])
    lines += md_table(rows, ["window", "metric", "before", "after", "threshold", "after result"])
    bad, total = verdict(ares)
    lines += ["", f"**AFTER RESULT: {'PASS' if bad == 0 else 'FAIL'}**（{total} 項目中 {bad} 件 FAIL）", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------- コンタクトシート

def load_font(size: int | None, text: str = ""):
    """size=None は PIL 既定のフォント。日本語を含む見出しだけ Windows の日本語フォントを探す。"""
    if size is not None and text and not text.isascii():
        for p in JP_FONTS:
            try:
                return ImageFont.truetype(p, size)
            except OSError:
                continue
    if size is None:
        return ImageFont.load_default()
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def nearest_row(c: Comms, t: float) -> int:
    j = bisect.bisect_left(c.t, t)
    cand = [i for i in (j - 1, j) if 0 <= i < len(c.t)]
    return min(cand, key=lambda i: abs(c.t[i] - t))


def make_tile(run: Run, k: int, mark: bool) -> Image.Image:
    path = run.dir / f"f{k:05d}.jpg"
    try:
        with Image.open(path) as im:
            im = im.convert("RGB")
    except OSError as e:
        raise Fail(f"コマを開けない: {path}: {e}") from e
    h = max(1, round(im.height * TILE_W / im.width))
    im = im.resize((TILE_W, h), Image.LANCZOS)
    t = k * TICK_PER_FRAME / TICK_HZ
    i = nearest_row(run.comms, t)
    label = f"t={t:.3f} a={run.comms.text_alpha[i]:.2f} box={run.comms.box_h[i]:.3f} {run.comms.stage[i]}"
    tile = Image.new("RGB", (TILE_W, h + LABEL_H), BG)
    tile.paste(im, (0, 0))
    ImageDraw.Draw(tile).text((3, h + 2), label, fill=MARK_FG if mark else LABEL_FG, font=load_font(None))
    return tile


def build_grid(run: Run, w: WindowResult) -> Image.Image:
    first_after = next((k for k in w.frames if k * TICK_PER_FRAME / TICK_HZ >= w.t_ev - FRAME_EPS), None)
    tiles = [make_tile(run, k, k == first_after) for k in w.frames]
    rows = math.ceil(len(tiles) / COLS)
    cell_h = max(t.height for t in tiles)
    width = COLS * (TILE_W + PAD) + PAD
    sheet = Image.new("RGB", (width, rows * (cell_h + PAD) + PAD), BG)
    for n, tile in enumerate(tiles):
        r, col = divmod(n, COLS)
        sheet.paste(tile, (PAD + col * (TILE_W + PAD), PAD + r * (cell_h + PAD)))
    return sheet


def header_strip(width: int, big: str, small: str, color: tuple, big_px: int, small_px: int) -> Image.Image:
    big_font = load_font(big_px, big)
    small_font = load_font(small_px, small)
    h = big_px + small_px + 22
    strip = Image.new("RGB", (width, h), color)
    d = ImageDraw.Draw(strip)
    d.text((10, 6), big, fill=(255, 255, 255), font=big_font)
    d.text((10, 6 + big_px + 6), small, fill=(235, 235, 235), font=small_font)
    return strip


def vstack(parts: list[Image.Image], gap: int = 0) -> Image.Image:
    width = max(p.width for p in parts)
    sheet = Image.new("RGB", (width, sum(p.height for p in parts) + gap * (len(parts) - 1)), BG)
    y = 0
    for p in parts:
        sheet.paste(p, (0, y))
        y += p.height + gap
    return sheet


def window_caption(w: WindowResult) -> str:
    return (f"{w.name} window {w.lo:.3f}-{w.hi:.3f}s  event t={w.t_ev:.3f}s  "
            f"{len(w.frames)} frames  stage {w.stages}  (red label = first frame at/after event)")


def single_sheet(run: Run, w: WindowResult) -> Image.Image:
    grid = build_grid(run, w)
    head = header_strip(grid.width, run.dir.name, window_caption(w), (40, 40, 40), 20, 13)
    return vstack([head, grid])


def compare_sheet(b: Run, a: Run, bw: WindowResult, aw: WindowResult) -> Image.Image:
    parts = []
    for tag, run, w, color in (("BEFORE", b, bw, (150, 45, 45)), ("AFTER", a, aw, (35, 115, 65))):
        grid = build_grid(run, w)
        parts.append(header_strip(grid.width, f"{tag} {run.dir.name}", window_caption(w), color, 40, 14))
        parts.append(grid)
    return vstack(parts, gap=PAD)


# ---------------------------------------------------------------- 書き出し

def write_text_atomic(path: Path, text: str) -> None:
    tmp = path.with_name(path.name + ".tmp")
    try:
        tmp.write_text(text, encoding="utf-8", newline="\n")
        os.replace(tmp, path)
    finally:
        if tmp.exists():
            tmp.unlink()


def write_png_atomic(img: Image.Image, path: Path) -> None:
    tmp = path.with_name(path.name + ".tmp")
    try:
        img.save(tmp, format="PNG")
        os.replace(tmp, path)
    finally:
        if tmp.exists():
            tmp.unlink()


# ---------------------------------------------------------------- main

def run_single(folder: Path) -> int:
    run = load_run(folder)
    results = [compute_window(run, "swap"), compute_window(run, "press")]
    sheets = {w.name: single_sheet(run, w) for w in results}
    report = single_report(run, results)
    write_text_atomic(run.dir / "comms_report.md", report)
    for name, img in sheets.items():
        write_png_atomic(img, run.dir / f"comms_sheet_{name}.png")
    print(report)
    print(f"wrote: {run.dir / 'comms_report.md'}\n       {run.dir / 'comms_sheet_swap.png'}\n"
          f"       {run.dir / 'comms_sheet_press.png'}")
    return 0


def run_compare(before: Path, after: Path) -> int:
    b, a = load_run(before), load_run(after)
    bres = [compute_window(b, "swap"), compute_window(b, "press")]
    ares = [compute_window(a, "swap"), compute_window(a, "press")]
    report = compare_report(b, a, bres, ares)
    sheets = {bw.name: compare_sheet(b, a, bw, aw) for bw, aw in zip(bres, ares)}
    write_text_atomic(a.dir / "comms_compare.md", report)
    for name, img in sheets.items():
        write_png_atomic(img, a.dir / f"comms_compare_{name}.png")
    print(report)
    print(f"wrote: {a.dir / 'comms_compare.md'}\n       {a.dir / 'comms_compare_swap.png'}\n"
          f"       {a.dir / 'comms_compare_press.png'}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="VR 通信パネルの swap / press 窓を測り、コンタクトシートを書く")
    ap.add_argument("run_dir", nargs="?", help="実行フォルダ（単独モード）")
    ap.add_argument("--compare", nargs=2, metavar=("BEFORE", "AFTER"), help="before と after のフォルダ")
    args = ap.parse_args()
    if args.compare and args.run_dir:
        ap.error("単独のフォルダと --compare は同時に指定できない")
    if not args.compare and not args.run_dir:
        ap.error("実行フォルダか --compare BEFORE AFTER を指定する")
    try:
        if args.compare:
            return run_compare(resolve(args.compare[0]), resolve(args.compare[1]))
        return run_single(resolve(args.run_dir))
    except Fail as e:
        print(f"⚠ {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
