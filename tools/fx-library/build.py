"""演出ライブラリー。ツイン走行の目線の動画から、演出ごとに「山場の 1 枚」と「そこへ至る動画」を切り出して並べる。

    py -3.11 tools/fx-library/build.py                 # 目録の走行を探して組む → Drive の reports/fx-library/<時刻>/
    py -3.11 tools/fx-library/build.py --sheet         # 山場の秒を選ぶための密なシート（look/）も出す
    py -3.11 tools/fx-library/build.py --only takeover # 1 つだけ切り直す（ページと年表は組み直す）
    py -3.11 tools/fx-library/build.py --run main=20261111_130000_fxlib-main

入力は tools/fx-library/catalog.json。走行はその runs の tag を名前に含む、いちばん新しい Drive の走行。
走行を作るのは tools/twin-verify.py（README の「走行を作る」）。

時刻は再生のログ（replay_xp.log の tau=）で決める。動画と同じ時計なのはこれだけ（tools/twin-look.py と同じ）。
出力:
  index.html   ライブラリー本体（年表・演出ごとの山場・動画・コマ送り）
  poster.html / poster.png  年表 1 枚（~/.agents/skills/timeline の検査を通す）
  library.json 機械が読む版（走行・時刻・ファイル・区間の出来事）
  fx/<id>/hero.jpg・clip.mp4・strip.jpg

⚠ 動画（faststart で書き直す）は G: へ直接書かない。ローカルの Logs/fx-library/ で組んでから Drive へ写す。
"""
from __future__ import annotations

import argparse
import html
import json
import re
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
CATALOG = HERE / "catalog.json"
RUNS = Path(r"G:\マイドライブ\shubie\renders\twin-verify")
DRIVE_OUT = Path(r"G:\マイドライブ\shubie\reports\fx-library")
LOCAL_OUT = ROOT / "Logs" / "fx-library"
TIMELINE = Path.home() / ".agents" / "skills" / "timeline" / "tools" / "timeline.mjs"

XP_LINE = re.compile(r"^tau=(?P<tau>-?[\d.]+) \[XP\]\s+(?:t=[\d.]+\s+)?ev=(?P<ev>\S+)\s*(?P<rest>.*)$")
NOISE = {"sum", "track", "clip", "commsError", "storm"}
STRIP_N = 10


@dataclass
class Event:
    tau: float
    ev: str
    rest: str


@dataclass
class Run:
    key: str
    folder: Path
    events: list[Event]
    duration: float

    @property
    def video(self) -> Path:
        return self.folder / "eye.mp4"


@dataclass
class Cut:
    fx: dict
    run: Run
    anchor: float
    start: float
    end: float
    hero: float
    files: dict[str, str] = field(default_factory=dict)
    events: list[dict] = field(default_factory=list)


# ------------------------------------------------------------------ 走行

def probe_duration(video: Path) -> float:
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(video)],
                         capture_output=True, text=True, check=True).stdout
    return float(out.strip())


def parse_log(path: Path) -> list[Event]:
    events = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = XP_LINE.match(line)
        if m:
            events.append(Event(float(m["tau"]), m["ev"], m["rest"]))
    return events


def find_run(key: str, tag: str, pinned: dict[str, str]) -> Run:
    if key in pinned:
        cands = [d for d in RUNS.iterdir() if d.is_dir() and pinned[key] in d.name]
    else:
        cands = [d for d in RUNS.iterdir() if d.is_dir() and d.name.endswith("_" + tag)]
    cands = [d for d in sorted(cands, reverse=True) if (d / "eye.mp4").exists() and (d / "replay_xp.log").exists()]
    if not cands:
        raise SystemExit(f"走行 {key}（tag={tag}）が {RUNS} に無い。tools/fx-library/README.md の「走行を作る」で作る")
    folder = cands[0]
    events = parse_log(folder / "replay_xp.log")
    if not events:
        raise SystemExit(f"{folder.name}/replay_xp.log に tau= の行が無い（11-11 より前の走行は時計が合わない）")
    return Run(key, folder, events, probe_duration(folder / "eye.mp4"))


def resolve(fx: dict, run: Run) -> Cut:
    at = fx["at"]
    if "video" in at:
        anchor = float(at["video"])
    else:
        hits = [e for e in run.events if e.ev == at["ev"] and at.get("has", "") in (e.rest + " ")]
        nth = int(at.get("nth", 1))
        if len(hits) < nth:
            raise SystemExit(f"{fx['id']}: {run.folder.name} に ev={at['ev']} {at.get('has', '')!r} が {nth} 回目まで無い")
        anchor = hits[nth - 1].tau
    clamp = lambda t: min(max(t, 0.0), run.duration - 0.05)
    cut = Cut(fx, run, anchor, clamp(anchor + fx["from"]), clamp(anchor + fx["to"]), clamp(anchor + fx["hero"]))
    if not cut.start <= cut.hero <= cut.end:
        raise SystemExit(f"{fx['id']}: 山場 {cut.hero:.2f} が区間 {cut.start:.2f}〜{cut.end:.2f} の外")
    cut.events = [{"t": round(e.tau - anchor, 2), "ev": e.ev, "text": e.rest[:120]}
                  for e in run.events if cut.start - 0.05 <= e.tau <= cut.end + 0.05 and e.ev not in NOISE and e.rest]
    return cut


# ------------------------------------------------------------------ 切り出し

def grab(video: Path, t: float, dest: Path) -> Image.Image:
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", f"{t:.3f}", "-i", str(video), "-frames:v", "1", str(dest)], check=True)
    return Image.open(dest).convert("RGB")


def mmss(t: float) -> str:
    t = max(t, 0.0)
    return f"{int(t // 60)}:{t % 60:04.1f}"


def font(px: int) -> ImageFont.FreeTypeFont:
    for name in ("BIZ-UDGothicR.ttc", "meiryo.ttc", "msgothic.ttc"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def cut_one(cut: Cut, crop: list[int], dest: Path, tmp: Path, sheet: bool) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    fx_id = cut.fx["id"]
    x0, y0, x1, y1 = crop
    video = cut.run.video

    hero = grab(video, cut.hero, tmp / f"{fx_id}_hero.png").crop((x0, y0, x1, y1))
    hero.save(dest / "hero.jpg", quality=92)
    thumb = hero.copy()
    thumb.thumbnail((640, 640))
    thumb.save(dest / "thumb.jpg", quality=88)

    # そこへ至る動画。faststart は書き直すのでローカルで書く（drive-offload.md §4）
    w, h = x1 - x0, y1 - y0
    vf = f"crop={w}:{h}:{x0}:{y0},scale='min(1280,iw)':-2"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", f"{cut.start:.3f}", "-i", str(video), "-t", f"{cut.end - cut.start:.3f}",
                    "-vf", vf, "-an", "-c:v", "libx264", "-crf", "22", "-preset", "medium", "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart", str(dest / "clip.mp4")], check=True)

    # コマ送り: 区間を等分した 10 コマ。山場にいちばん近いコマに赤枠
    times = [cut.start + (cut.end - cut.start) * i / (STRIP_N - 1) for i in range(STRIP_N)]
    near = min(range(STRIP_N), key=lambda i: abs(times[i] - cut.hero))
    times[near] = cut.hero
    tw = 320
    th = round(tw * h / w)
    strip = Image.new("RGB", (tw * 5 + 4 * 6, (th + 30) * 2 + 6), (14, 14, 15))
    dr = ImageDraw.Draw(strip)
    f = font(18)
    for i, t in enumerate(times):
        im = grab(video, t, tmp / f"{fx_id}_s{i}.png").crop((x0, y0, x1, y1)).resize((tw, th), Image.LANCZOS)
        x, y = (i % 5) * (tw + 6), (i // 5) * (th + 36)
        strip.paste(im, (x, y + 30))
        label = f"{t - cut.anchor:+.1f}s" + ("  山場" if i == near else "")
        dr.text((x + 4, y + 5), label, fill=(232, 228, 223) if i != near else (225, 70, 70), font=f)
        if i == near:
            dr.rectangle((x, y + 30, x + tw - 1, y + 30 + th - 1), outline=(200, 35, 43), width=4)
    strip.save(dest / "strip.jpg", quality=88)

    if sheet:
        # 山場の秒を選び直すための密なシート（0.2 秒刻み）。判定はフル解像度の hero で見る
        n = max(2, int((cut.end - cut.start) / 0.2) + 1)
        cols = 8
        sw = 240
        sh = round(sw * h / w)
        rows = (n + cols - 1) // cols
        big = Image.new("RGB", (cols * sw, rows * (sh + 22)), (30, 30, 30))
        d2 = ImageDraw.Draw(big)
        f2 = font(14)
        for i in range(n):
            t = cut.start + i * 0.2
            im = grab(video, t, tmp / f"{fx_id}_d{i}.png").crop((x0, y0, x1, y1)).resize((sw, sh))
            x, y = (i % cols) * sw, (i // cols) * (sh + 22)
            big.paste(im, (x, y + 22))
            d2.text((x + 3, y + 3), f"{t - cut.anchor:+.1f}", fill=(255, 220, 0), font=f2)
        (dest.parent.parent / "look").mkdir(exist_ok=True)
        big.save(dest.parent.parent / "look" / f"{fx_id}.jpg", quality=80)

    cut.files = {k: f"fx/{fx_id}/{k}.{ext}" for k, ext in (("hero", "jpg"), ("thumb", "jpg"), ("clip", "mp4"), ("strip", "jpg"))}


# ------------------------------------------------------------------ 年表 1 枚

POSTER_W = 2400


def poster_html(cuts: list[Cut], catalog: dict, stamp: str) -> str:
    """周ごとに 1 行。列はカメラ A・B・C。行の終わりから次の行の頭へ点線で戻る（部屋を 1 周して A に戻る）。"""
    W = POSTER_W
    left, right = 190, 2310
    colw = (right - left) / 3
    tw, th = 200, 152
    step = 236
    rowh = 380
    by = {c.fx["id"]: c for c in cuts}
    rows: list[tuple[str, list]] = [
        ("導入", [("all", ["onboard", "title-in", "title-out", "guide", "arrive", "tv-approach", "tv-power", "begin"])]),
        ("1", [("A", ["report-empty"]), ("B", ["handprint", "handprint-clear"]), ("C", [])]),
        ("2", [("A", ["stain", "stain-flutter"]), ("B", ["backrooms"]), ("C", ["pov-peek", "pov-chase", "takeover"])]),
        ("3", [("A", ["warp", "doll-a"]), ("B", ["doll-b", "report-possessed"]), ("C", ["screens"])]),
        ("4", [("A", ["final-enter", "halt", "prompt"]), ("B", ["return", "farewell"]), ("C", ["outro-return"])]),
        ("人形", [("B", ["sink"]), ("C", ["outro-doll"])]),
    ]
    top = 430
    # 行の頭の y。導入の下にカメラの見出しを置くので、周の行は 1 段下げる
    ys = [top] + [top + 90 + r * rowh for r in range(1, 5)]
    ys.append(ys[-1] + 400)
    parts, lines, nodes = [], [], []
    order = 0
    row_y: list[float] = []
    for r, (label, cells) in enumerate(rows):
        y = ys[r]
        row_y.append(y)
        line_y = y - 26
        xs: list[float] = []
        for col, ids in cells:
            if col == "all":
                x0 = left
                gap = (right - left - tw) / (len(ids) - 1)
                positions = [x0 + i * gap for i in range(len(ids))]
            else:
                ci = "ABC".index(col)
                x0 = left + ci * colw + 18
                positions = [x0 + i * step for i in range(len(ids))]
                if not ids:
                    parts.append(f'<div class="empty" style="left:{x0:.0f}px;top:{y:.0f}px;width:{tw}px;height:{th}px">'
                                 f'<span>{col} は演出なし<br>ライブだけ</span></div>')
                    xs.append(x0 + tw / 2)
            for fid, x in zip(ids, positions):
                c = by.get(fid)
                if c is None:
                    continue
                order += 1
                xs.append(x + tw / 2)
                parts.append(
                    f'<div class="ev" data-tl-event data-date="{order:04d}" style="left:{x:.0f}px;top:{y:.0f}px;width:{tw}px">'
                    f'<div class="node" data-tl-node style="left:{tw / 2 - 9:.0f}px;top:-35px"></div>'
                    f'<img data-tl-figure src="{c.files["thumb"]}" alt="{html.escape(c.fx["name"])}" style="width:{tw}px;height:{th}px">'
                    f'<div class="tm" data-tl-role="date">{html.escape(c.fx["seg"])}&nbsp;&nbsp;{mmss(c.hero)}</div>'
                    f'<div class="nm" data-tl-role="title">{html.escape(c.fx["name"])}</div></div>')
        x_end = max(xs) if xs else left
        if label == "人形":
            # 最後の A の ③b（3 枚目）の札の真下から枝分かれする（報告しなかった人）。B 列の図版には線を通さない
            bx = left + 18 + 2 * step + tw / 2
            lines.append(f'<path d="M{bx:.0f} {row_y[-2] + th + 118:.0f} L{bx:.0f} {line_y:.0f} L{x_end:.0f} {line_y:.0f}" class="trk"/>')
            parts.append(f'<div class="fork" style="left:{bx - 290:.0f}px;top:{line_y - 16:.0f}px;width:270px;text-align:right">報告しなかった</div>')
            continue
        lines.append(f'<path d="M{left + tw / 2 - 30:.0f} {line_y:.0f} L{x_end:.0f} {line_y:.0f}" class="trk"/>')
        if r + 1 < len(rows) and rows[r + 1][0] != "人形":
            # 次の周へ: 右端から下りて、行の間を A まで戻る（点線）
            ny = ys[r + 1] - 26
            mid = ny - 62
            lines.append(f'<path d="M{x_end:.0f} {line_y:.0f} L{right + 40:.0f} {line_y:.0f} L{right + 40:.0f} {mid:.0f} '
                         f'L{left - 60:.0f} {mid:.0f} L{left - 60:.0f} {ny:.0f} L{left + tw / 2 - 30:.0f} {ny:.0f}" class="back"/>')
        if label not in ("導入",):
            nodes.append(f'<div class="lap" style="top:{line_y - 64:.0f}px">{label}'
                         f'<small>{"最後の A" if label == "4" else "周目"}</small></div>')
    hdr_y = top + 290
    cams = "".join(f'<div class="cam" style="left:{left + i * colw + 18:.0f}px;top:{hdr_y:.0f}px">カメラ {c}</div>' for i, c in enumerate("ABC"))
    H = int(row_y[-1] + 440)
    n = len(cuts)
    span = max(c.hero for c in cuts if c.run.key == "main")
    runs = " / ".join(f'{c["say"]}' for c in catalog["runs"].values())
    return f"""<!doctype html><html lang="ja"><head><meta charset="utf-8"><title>廻リ視 演出ライブラリー 年表</title>
<style>
:root{{--bg:#0b0b0c;--ink:#e8e4df;--dim:#8f8a84;--red:#c8232b}}
body{{margin:0;background:var(--bg)}}
#p{{position:relative;width:{W}px;height:{H}px;background:var(--bg);color:var(--ink);font-family:'BIZ UDPGothic',sans-serif;overflow:hidden}}
svg{{position:absolute;left:0;top:0}}
.trk{{fill:none;stroke:var(--red);stroke-width:7;stroke-linejoin:round}}
.back{{fill:none;stroke:#4a1a1d;stroke-width:3;stroke-dasharray:14 12;stroke-linejoin:round}}
.ev{{position:absolute}}
.ev img{{display:block;object-fit:cover;border-radius:14px;border:3px solid #2a2a2c;background:#000}}
.node{{position:absolute;width:18px;height:18px;border-radius:50%;background:var(--bg);border:5px solid var(--red);box-sizing:border-box}}
.tm{{font-family:Consolas,'BIZ UDGothic',monospace;font-size:21px;color:var(--dim);margin-top:12px;letter-spacing:.02em}}
.nm{{font-size:29px;font-weight:700;line-height:1.25;margin-top:4px}}
.empty{{position:absolute;border:3px dashed #3a3a3d;border-radius:14px;display:flex;align-items:center;justify-content:center;text-align:center;color:var(--dim);font-size:24px;line-height:1.4}}
.lap{{position:absolute;left:28px;width:150px;font-family:'BIZ UDPMincho',serif;font-size:74px;font-weight:700;line-height:1;color:var(--ink)}}
.lap small{{display:block;font-family:'BIZ UDPGothic',sans-serif;font-size:24px;font-weight:400;color:var(--dim);margin-top:8px}}
.cam{{position:absolute;font-family:Consolas,monospace;font-size:28px;color:var(--dim);letter-spacing:.1em}}
.fork{{position:absolute;font-size:24px;color:var(--dim)}}
h1{{position:absolute;left:190px;top:64px;margin:0;font-family:'BIZ UDPMincho',serif;font-size:118px;font-weight:700;letter-spacing:.04em;line-height:1}}
h1 span{{color:var(--red)}}
.sub{{position:absolute;left:196px;top:206px;font-size:30px;color:var(--dim)}}
.big{{position:absolute;right:90px;top:52px;text-align:right;font-family:Consolas,monospace;font-size:148px;font-weight:700;line-height:1;color:var(--ink)}}
.big small{{display:block;font-family:'BIZ UDPGothic',sans-serif;font-size:28px;font-weight:400;color:var(--dim);margin-top:10px}}
.foot{{position:absolute;left:190px;bottom:44px;font-size:24px;color:var(--dim);width:2100px;line-height:1.5}}
</style></head><body>
<div id="p" data-tl-poster data-tl-width="{W}" data-tl-scale="ordinal">
<svg width="{W}" height="{H}">{''.join(lines)}</svg>
<h1>廻リ<span>視</span>　演出ライブラリー</h1>
<div class="sub">体験者の目で、演出 {n} 本を起きる順に。上から 3 周して、最後の A で 2 つに分かれる。</div>
<div class="big">{n}<small>本の演出 ／ 被ってから {mmss(span)}</small></div>
{cams}
{''.join(nodes)}
{''.join(parts)}
<div class="foot">デジタルツインでの目線の録画（{stamp}）。時刻は被ってからの経過で、歩く速さで変わる。{html.escape(runs)}。人形 End の枝は別の走行。音とパススルーは映らない。</div>
</div></body></html>"""


def build_poster(out: Path, cuts: list[Cut], catalog: dict, stamp: str) -> str:
    (out / "poster.html").write_text(poster_html(cuts, catalog, stamp), encoding="utf-8", newline="\n")
    if not TIMELINE.exists():
        return f"検査器が無い: {TIMELINE}"
    r = subprocess.run(["node", str(TIMELINE), "both", str(out / "poster.html"), "-o", str(out / "poster.png")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (r.stdout + r.stderr).strip()


# ------------------------------------------------------------------ ページ

def page_html(cuts: list[Cut], catalog: dict, stamp: str, check: str) -> str:
    esc = html.escape
    nav, cards = [], []
    part = None
    for c in cuts:
        fx = c.fx
        if fx["part"] != part:
            part = fx["part"]
            nav.append(f'<li class="np">{esc(part)}</li>')
            cards.append(f'<h2 id="p-{esc(part)}">{esc(part)}</h2>')
        nav.append(f'<li><a href="#{fx["id"]}"><b>{esc(fx["seg"])}</b> {esc(fx["name"])}</a></li>')
        evs = "".join(f'<li><code>{e["t"]:+.1f}s</code> {esc(e["ev"])} <span>{esc(e["text"][:90])}</span></li>' for e in c.events[:14])
        caveat = f'<p class="cv">{esc(fx["caveat"])}</p>' if fx.get("caveat") else ""
        cards.append(f"""<section class="fx" id="{fx['id']}">
<div class="hd"><span class="seg">{esc(fx['seg'])}</span><span class="t">{mmss(c.hero)}</span><h3>{esc(fx['name'])}</h3></div>
<figure class="hero"><img src="{c.files['hero']}" alt="{esc(fx['name'])}の山場" loading="lazy"></figure>
<div class="side">
<p class="say">{esc(fx['say'])}</p>{caveat}
<dl><dt>NOW.md</dt><dd>{esc(fx['now'])}</dd><dt>根拠</dt><dd>LEDGER {esc(fx['ledger'])}</dd>
<dt>走行</dt><dd>{esc(c.run.folder.name)} の {c.start:.1f}〜{c.end:.1f} 秒（山場 {c.hero:.1f}）</dd></dl>
<video src="{c.files['clip']}" muted loop playsinline controls preload="metadata"></video>
</div>
<img class="strip" src="{c.files['strip']}" alt="{esc(fx['name'])}のコマ送り" loading="lazy">
<details><summary>この区間のテレメトリ（{len(c.events)} 行）</summary><ul class="ev">{evs}</ul></details>
</section>""")
    runs = "".join(f'<li><b>{esc(k)}</b> {esc(v["say"])}：<code>{esc(next((c.run.folder.name for c in cuts if c.run.key == k), "-"))}</code></li>'
                   for k, v in catalog["runs"].items())
    return f"""<!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>廻リ視 演出ライブラリー</title>
<style>
:root{{--bg:#0b0b0c;--panel:#141416;--ink:#e8e4df;--dim:#9a958f;--red:#c8232b;--line:#2a2a2d}}
*{{box-sizing:border-box}}
body{{margin:0;background:var(--bg);color:var(--ink);font-family:'BIZ UDPGothic','Hiragino Sans',sans-serif;line-height:1.7}}
.wrap{{display:grid;grid-template-columns:260px 1fr;max-width:1680px;margin:0 auto}}
nav{{position:sticky;top:0;height:100vh;overflow:auto;padding:28px 16px;border-right:1px solid var(--line);font-size:13px}}
nav ul{{list-style:none;margin:0;padding:0}}
nav li{{margin:2px 0}} nav .np{{color:var(--red);margin-top:16px;font-weight:700}}
nav a{{color:var(--ink);text-decoration:none}} nav a b{{color:var(--dim);font-weight:400;font-family:Consolas,monospace}}
main{{padding:32px 40px 120px;min-width:0}}
h1{{font-family:'BIZ UDPMincho',serif;font-size:40px;margin:0 0 6px;letter-spacing:.04em}} h1 span{{color:var(--red)}}
.lead{{color:var(--dim);margin:0 0 20px}}
.poster img{{width:100%;height:auto;display:block;border:1px solid var(--line)}}
.notes{{background:var(--panel);border:1px solid var(--line);padding:14px 20px;margin:20px 0 8px;font-size:14px;color:var(--dim)}}
.notes ul{{margin:4px 0;padding-left:20px}}
h2{{font-family:'BIZ UDPMincho',serif;font-size:30px;margin:64px 0 8px;padding-bottom:8px;border-bottom:2px solid var(--red)}}
.fx{{display:grid;grid-template-columns:minmax(0,1.55fr) minmax(0,1fr);gap:18px 26px;padding:30px 0;border-bottom:1px solid var(--line)}}
.hd{{grid-column:1/-1;display:flex;align-items:baseline;gap:16px;flex-wrap:wrap}}
.hd h3{{margin:0;font-size:26px}} .seg{{color:var(--red);font-family:Consolas,monospace;font-size:18px}} .t{{color:var(--dim);font-family:Consolas,monospace}}
.hero{{margin:0}} .hero img{{width:100%;display:block;border-radius:6px;background:#000}}
.side video{{width:100%;display:block;background:#000;border-radius:6px;margin-top:14px}}
.say{{margin:0;font-size:17px}} .cv{{color:#d7a35a;font-size:14px;margin:8px 0 0}}
dl{{display:grid;grid-template-columns:auto 1fr;gap:2px 12px;font-size:13px;color:var(--dim);margin:14px 0 0}} dt{{color:var(--ink)}} dd{{margin:0}}
.strip{{grid-column:1/-1;width:100%;display:block;border-radius:4px}}
details{{grid-column:1/-1;font-size:12px;color:var(--dim)}} summary{{cursor:pointer}}
ul.ev{{list-style:none;padding:0;margin:6px 0;font-family:Consolas,monospace}} ul.ev span{{color:#6f6a65}}
code{{font-family:Consolas,monospace}}
@media (max-width:900px){{.wrap{{display:block}} nav{{display:none}} main{{padding:16px}} .fx{{grid-template-columns:1fr}} h1{{font-size:30px}}}}
</style></head><body><div class="wrap">
<nav><ul>{''.join(nav)}</ul></nav>
<main>
<h1>廻リ<span>視</span>　演出ライブラリー</h1>
<p class="lead">体験者の目で見た演出 {len(cuts)} 本を、起きる順に並べた。各演出は山場の 1 枚と、そこへ至る動画とコマ送り。組んだ日時 {stamp}。</p>
<div class="poster"><img src="poster.png" alt="演出の年表"></div>
<div class="notes"><b>見るときの注意</b><ul>
<li>デジタルツインを本番の Main シーンで歩いた目線の録画。圧縮・遅延・両眼・ディスプレイ・パススルー・音は入らない。</li>
<li>目線の動画は縦 52 度で切っている。視野の端で起きることは画に入らない。</li>
<li>時刻は被ってからの経過で、歩く速さで変わる。各演出の秒数は、その走行のテレメトリ（replay_xp.log の tau）から決めた。</li>
<li>体験者に見えることの正は <code>canon/NOW.md</code>。説明文はそこから短く写した。</li>
</ul><ul>{runs}</ul><details><summary>年表の検査</summary><pre>{esc(check)}</pre></details></div>
{''.join(cards)}
</main></div>
<script>
// 画面に入った動画だけ再生する（30 本を同時に回さない）
const io=new IntersectionObserver(es=>es.forEach(e=>{{const v=e.target;if(e.isIntersecting)v.play().catch(()=>{{}});else v.pause();}}),{{threshold:.4}});
document.querySelectorAll('video').forEach(v=>io.observe(v));
</script></body></html>"""


# ------------------------------------------------------------------ 本体

def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", action="append", default=[], help="key=走行フォルダ名の一部（目録の tag より優先）")
    ap.add_argument("--only", default="", help="この id だけ切り直す（前回の出力の他の演出を使う）")
    ap.add_argument("--sheet", action="store_true", help="山場を選ぶための 0.2 秒刻みのシートも出す（look/）")
    ap.add_argument("--no-drive", action="store_true", help="Drive へ写さない")
    a = ap.parse_args()

    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    pinned = dict(s.split("=", 1) for s in a.run)
    runs = {k: find_run(k, v["tag"], pinned) for k, v in catalog["runs"].items()}
    for r in runs.values():
        print(f"走行 {r.key}: {r.folder.name}（{r.duration:.1f} 秒・出来事 {len(r.events)} 行）")
    cuts = [resolve(fx, runs[fx["run"]]) for fx in catalog["fx"]]

    stamp = time.strftime("%Y%m%d_%H%M")
    if a.only:
        prev = sorted(LOCAL_OUT.glob("2*"))
        if not prev:
            raise SystemExit("--only は前回のローカル出力（Logs/fx-library/）が要る")
        out = prev[-1]
        stamp = out.name
    else:
        out = LOCAL_OUT / stamp
    out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for c in cuts:
            fid = c.fx["id"]
            if a.only and fid != a.only:
                c.files = {k: f"fx/{fid}/{k}.{ext}" for k, ext in (("hero", "jpg"), ("thumb", "jpg"), ("clip", "mp4"), ("strip", "jpg"))}
                continue
            crop = catalog["crops"][c.fx["crop"]] if isinstance(c.fx["crop"], str) else c.fx["crop"]
            cut_one(c, crop, out / "fx" / fid, tmp, a.sheet)
            print(f"  {fid:18s} 山場 {mmss(c.hero)}（{c.run.key} {c.hero:.2f}s）区間 {c.end - c.start:.1f}s")

    check = build_poster(out, cuts, catalog, stamp)
    print(check)
    (out / "index.html").write_text(page_html(cuts, catalog, stamp, check), encoding="utf-8", newline="\n")
    lib = {
        "built": stamp,
        "runs": {k: r.folder.name for k, r in runs.items()},
        "fx": [{**{k: c.fx[k] for k in ("id", "part", "seg", "name", "say", "now", "ledger")},
                "run": c.run.folder.name, "anchor": round(c.anchor, 2), "start": round(c.start, 2), "end": round(c.end, 2),
                "hero": round(c.hero, 2), "files": c.files, "events": c.events} for c in cuts],
    }
    (out / "library.json").write_text(json.dumps(lib, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")

    if not a.no_drive:
        dest = DRIVE_OUT / out.name
        if dest.exists():
            shutil.rmtree(dest)
        shutil.copytree(out, dest)
        tmpj = DRIVE_OUT / "latest.json.tmp"
        tmpj.write_text(json.dumps({"stamp": out.name}), encoding="utf-8")
        tmpj.replace(DRIVE_OUT / "latest.json")
        print(f"→ {dest / 'index.html'}")
    else:
        print(f"→ {out / 'index.html'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
