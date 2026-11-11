"""ツイン走行（tools/twin-verify.py）の目線の動画を、場面ごとに「見られる形」へ切る。

    py -3.11 tools/twin-look.py scenes [走行]            # 場面の時刻表（look/scenes.md・scenes.json）
    py -3.11 tools/twin-look.py cut [走行] [--scene 3-A]  # 場面ごとのフル解像度のコマと索引シート
    py -3.11 tools/twin-look.py pair <走行A> <走行B>      # 同じ場面・同じ出来事のコマを左右に並べる
    py -3.11 tools/twin-look.py pair <走行>               # 同じ走り方の直前の走行と並べる
    py -3.11 tools/twin-look.py cut --scene 3-A --crop    # 画面だけへ寄る（視野の縁は切れる）

走行は Drive の renders/twin-verify/ のフォルダ名（一部でよい）かパス。省くと latest.json の走行。
出力は走行フォルダの look/、pair は renders/twin-verify/_pair/<A>__<B>/。

コマを切る時刻は「場面の頭・出来事の 0.4 秒後・間が空いた所・場面の終わり」。ラベルは画の下の帯に、その走行の
データから書く（画そのものには何も重ねない）。

⚠ 時計は 2 つある。動画と揃うのは再生のログ（replay_xp.log の tau=）。走行のログ（events.json）は同じ場面が
   数秒ずれる。再生のログが無い走行は走行の時計で切り、帯と scenes.md にそう書く。
⚠ シートは索引。判定は個別の PNG を開いて見る。
⚠ pair は片方でも動画か場面が欠けたら、代わりを置かずに落ちる。
"""
from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

DRIVE = Path(r"G:\マイドライブ\shubie\renders\twin-verify")
XP_LINE = re.compile(r"^(?:tau=(?P<tau>-?[\d.]+) )?.*?\[XP\]\s+t=(?P<t>[\d.]+)\s+ev=(?P<ev>\S+)\s*(?P<rest>.*)$")
CAMS = "ABC"
# 毎秒の標本。出来事として数えない。
SAMPLES = {"sum", "effects", "xprec", "fps", "stream", "config"}
# 画に何かが出る・変わる出来事。この 0.4 秒後を切る。音（sfx・bgm）は画に出ないので切らない。
MARK_EVS = {"intro", "title", "guide", "cue", "comms", "commsPossess", "commsInvasion", "commsCurse",
            "commsRecover", "swap", "mark", "farewell", "twinfarewell", "outro", "screen", "step", "gate"}
AFTER = 0.4      # 出来事から画に出るまでの待ち
SETTLED = 1.5    # 差し込み（cue）が出切った頃
MERGE = 0.25     # これより近いコマは 1 枚にまとめる
FONTS = ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/YuGothM.ttc", "C:/Windows/Fonts/msgothic.ttc")


@dataclass
class Scene:
    name: str
    t0: float
    t1: float
    marks: list[tuple[float, str, str]] = field(default_factory=list)   # (時刻, 署名, 表示)
    counts: Counter = field(default_factory=Counter)

    @property
    def slug(self) -> str:
        return self.name.replace("#", "_")


def kv(text: str) -> dict[str, str]:
    return dict(p.split("=", 1) for p in text.split() if "=" in p)


def signature(ev: str, text: str) -> str | None:
    """出来事を走行どうしで突き合わせる名前。None は「切らない」。"""
    if ev not in MARK_EVS:
        return None
    d = kv(text)
    if ev == "cue":
        return f"cue:{d.get('id', '?')}" if d.get("st") == "on" else None
    if ev in ("intro", "outro"):
        return f"{ev}:{d.get('stage', '?')}"
    if ev in ("commsPossess", "farewell"):
        return f"{ev}:{d.get('phase', '?')}"
    if ev == "commsInvasion":
        return "commsInvasion"      # v= は連続値。値で分けると走行ごとに別の名前になる
    first = next(iter(d.items()), None)
    return f"{ev}:{first[1]}" if first else ev


def parse_log(text: str) -> list[dict]:
    """replay_xp.log（tau= つき）。tau が動画の秒。"""
    out = []
    for line in text.splitlines():
        m = XP_LINE.match(line.strip())
        if not m or m.group("tau") is None or m.group("ev") in SAMPLES:
            continue
        out.append({"t": float(m.group("tau")), "ev": m.group("ev"), "text": m.group("rest")})
    return out


def build_scenes(events: list[dict], duration: float) -> list[Scene]:
    """導入 → 区間（進行の周-カメラ）→ 終幕。区間の名前は plap を使う（体験者向けの数え方。4-A が最後の A）。"""
    events = sorted(events, key=lambda e: e["t"])
    cuts: list[tuple[float, str]] = [(0.0, "導入")]
    seen_seg = False
    for e in events:
        if e["ev"] == "seg":
            d = kv(e["text"])
            cam = int(d.get("cam", -1))
            lap = d.get("plap", d.get("lap", "?"))
            cuts.append((e["t"], f"{lap}-{CAMS[cam] if 0 <= cam < 3 else '?'}"))
            seen_seg = True
        elif e["ev"] == "outro" and seen_seg and cuts[-1][1] != "終幕" and kv(e["text"]).get("stage", "Off") != "Off":
            cuts.append((e["t"], "終幕"))
    names: Counter = Counter()
    scenes: list[Scene] = []
    for i, (t, name) in enumerate(cuts):
        t0 = max(0.0, t)
        t1 = cuts[i + 1][0] if i + 1 < len(cuts) else duration
        t1 = min(max(t1, t0), duration)
        names[name] += 1
        label = name if names[name] == 1 else f"{name}#{names[name]}"   # 引き返しで同じ区間に 2 度入る
        scenes.append(Scene(f"{len(scenes):02d}_{label}", t0, t1))
    for e in events:
        sc = next((s for s in reversed(scenes) if e["t"] >= s.t0 or s is scenes[0]), scenes[0])
        if e["t"] > duration:
            continue
        sc.counts[e["ev"]] += 1
        sig = signature(e["ev"], e["text"])
        if sig and e["t"] >= 0:
            sc.marks.append((e["t"], sig, f"{e['ev']} {e['text'][:60]}".strip()))
    return scenes


def picks(sc: Scene, gap: float) -> list[tuple[float, str, str]]:
    """切る時刻。(時刻, 鍵, 表示)。鍵は pair の突き合わせに使う（head / end / 署名#出現順 / fill#n）。"""
    if sc.t1 - sc.t0 < 0.1:
        return []
    last = sc.t1 - 0.05
    clamp = lambda t: min(max(t, sc.t0), last)
    seen: Counter = Counter()
    raw = [(clamp(sc.t0 + AFTER), "head", "場面の頭")]
    for t, sig, label in sc.marks:
        seen[sig] += 1
        raw.append((clamp(t + AFTER), f"{sig}#{seen[sig]}", label))
        if sig.startswith("cue:") and gap > 0:
            # 差し込みは立ち上がりに時間がかかる。出た直後と、出切った頃の 2 枚で見る。
            raw.append((clamp(t + SETTLED), f"{sig}#{seen[sig]}+", f"{label}（{SETTLED} 秒後）"))
    raw.append((clamp(sc.t1 - 0.3), "end", "場面の終わり"))
    if gap <= 0:
        # pair 用。場面の長さが走行で違っても同じ鍵で突き合わせられるよう、長さの割合で切る。
        raw += [(clamp(sc.t0 + (sc.t1 - sc.t0) * q / 4), f"q{q}", f"場面の {q * 25}%") for q in (1, 2, 3)]
    raw.sort(key=lambda r: r[0])
    out: list[tuple[float, str, str]] = []
    for t, key, label in raw:
        if out and t - out[-1][0] < MERGE:
            pt, pkey, plabel = out[-1]
            out[-1] = (pt, pkey if pkey != "end" else key, f"{plabel} / {label}")
        else:
            out.append((t, key, label))
    filled: list[tuple[float, str, str]] = []
    n = 0
    for i, row in enumerate(out):
        filled.append(row)
        if i + 1 < len(out) and gap > 0:
            span = out[i + 1][0] - row[0]
            steps = math.ceil(span / gap - 1e-6)
            for k in range(1, steps):
                n += 1
                filled.append((row[0] + span * k / steps, f"fill#{n}", "間"))
    return filled


# ---------------------------------------------------------------- 走行を読む

def resolve(run: str | None) -> Path:
    if run is None:
        latest = DRIVE / "latest.json"
        if not latest.exists():
            raise SystemExit(f"走行を指定してください（{latest} が無い）")
        run = json.loads(latest.read_text(encoding="utf-8"))["stamp"]
    p = Path(run)
    if p.is_dir():
        return p
    if (DRIVE / run).is_dir():
        return DRIVE / run
    hits = [d for d in DRIVE.iterdir() if d.is_dir() and not d.name.startswith("_") and run in d.name]
    if len(hits) != 1:
        raise SystemExit(f"走行 '{run}' は {len(hits)} 件に当たる: {[h.name for h in hits][:8]}")
    return hits[0]


def load(run: Path) -> tuple[list[dict], str, str]:
    """(出来事, 時計, 注記)。時計は 'replay'（動画と揃う）か 'run'（数秒ずれる）。"""
    rep = run / "replay_xp.log"
    if rep.exists():
        ev = parse_log(rep.read_text(encoding="utf-8", errors="replace"))
        if ev:
            return ev, "replay", "再生のログ（動画と同じ時計）"
    ej = run / "events.json"
    if not ej.exists():
        raise SystemExit(f"{run} に replay_xp.log も events.json も無い")
    cmp_ = run / "compare.txt"
    lines = cmp_.read_text(encoding="utf-8", errors="replace").splitlines() if cmp_.exists() else []
    gap = lines[1].strip() if len(lines) > 1 else "差は未測定"
    return (json.loads(ej.read_text(encoding="utf-8")), "run",
            f"走行のログ（動画とずれる。{gap}）。場面の頭は画の変化で確かめる")


def video_of(run: Path) -> Path:
    v = run / "eye.mp4"
    if not v.exists():
        raise SystemExit(f"{v} が無い（--no-video の走行はコマを切れない）")
    return v


def duration_of(video: Path) -> float:
    import imageio_ffmpeg
    return float(imageio_ffmpeg.count_frames_and_secs(str(video))[1])


def scenes_of(run: Path) -> tuple[list[Scene], str, str, float]:
    events, clock, note = load(run)
    dur = duration_of(video_of(run)) if (run / "eye.mp4").exists() else max((e["t"] for e in events), default=0.0)
    return build_scenes(events, dur), clock, note, dur


def match_scene(scenes: list[Scene], want: str | None) -> list[Scene]:
    if not want:
        return scenes
    hit = [s for s in scenes if s.name.split("_", 1)[1].split("#")[0] == want]
    if not hit:
        raise SystemExit(f"場面 '{want}' が無い。あるのは {[s.name for s in scenes]}")
    return hit


# ---------------------------------------------------------------- 画

def font(size: int):
    from PIL import ImageFont
    for f in FONTS:
        if Path(f).exists():
            return ImageFont.truetype(f, size)
    print("⚠ 日本語の書体が見つからない。帯の日本語は化ける", file=sys.stderr)
    return ImageFont.load_default()


def grab(video: Path, t: float, tmp: Path):
    """1 コマを取り出して PIL の画像で返す。"""
    import imageio_ffmpeg
    from PIL import Image
    out = tmp / f"g_{int(t * 1000):09d}.png"
    r = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-v", "error", "-y", "-ss", f"{t:.3f}", "-i", str(video),
                        "-frames:v", "1", str(out)], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0 or not out.exists():
        raise SystemExit(f"コマを取り出せない t={t:.2f} {video}: {r.stderr.strip()[:300]}")
    with Image.open(out) as im:
        return im.convert("RGB")


def banded(im, text: str, width: int | None = None):
    """画の下に帯を足してラベルを書く。画の画素には触らない。"""
    from PIL import Image, ImageDraw
    if width and im.width != width:
        im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
    size = max(14, im.width // 80)
    band = size + 12
    out = Image.new("RGB", (im.width, im.height + band), (16, 16, 16))
    out.paste(im, (0, 0))
    ImageDraw.Draw(out).text((8, im.height + 5), text, fill=(235, 235, 235), font=font(size))
    return out


def lit_crop(im, margin: float = 0.08, floor: int = 24):
    """黒い部屋の中で光っている所（画面）だけへ寄る。視野の縁で起きることは切れるので、既定では使わない。"""
    box = im.convert("L").point(lambda v: 255 if v > floor else 0).getbbox()
    if not box:
        return im
    mx, my = round(im.width * margin), round(im.height * margin)
    return im.crop((max(0, box[0] - mx), max(0, box[1] - my), min(im.width, box[2] + mx), min(im.height, box[3] + my)))


def sheet(frames: list, cols: int = 4, width: int = 480):
    from PIL import Image
    thumbs = [f.resize((width, round(f.height * width / f.width)), Image.LANCZOS) for f in frames]
    h = max(t.height for t in thumbs)
    rows = (len(thumbs) + cols - 1) // cols
    out = Image.new("RGB", (cols * width, rows * h), (0, 0, 0))
    for i, t in enumerate(thumbs):
        out.paste(t, ((i % cols) * width, (i // cols) * h))
    return out


def safe(key: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "-", key).strip("-")[:48] or "x"


# ---------------------------------------------------------------- コマンド

def scenes_md(run: Path, scenes: list[Scene], clock: str, note: str, dur: float) -> str:
    out = [f"# 場面の時刻表 {run.name}", "", f"時計: {note}", f"動画の長さ: {dur:.1f} 秒", "",
           "| 場面 | 頭 | 長さ | 画に出る出来事 | 音 | 報告 |", "|---|---|---|---|---|---|"]
    for s in scenes:
        marks = Counter(sig.split(":")[0] for _, sig, _ in s.marks)
        out.append(f"| {s.name} | {s.t0:.1f} | {s.t1 - s.t0:.1f} | "
                   + (" ".join(f"{k}×{v}" for k, v in sorted(marks.items())) or "—")
                   + f" | {s.counts.get('sfx', 0)} | {s.counts.get('mark', 0)} |")
    out += ["", "## 出来事", ""]
    for s in scenes:
        out.append(f"### {s.name}（{s.t0:.1f}〜{s.t1:.1f}）")
        out += [f"- {t:7.2f}  +{t - s.t0:5.1f}  {label}" for t, _, label in s.marks] or ["- （画に出る出来事は無い）"]
        out.append("")
    return "\n".join(out)


def cmd_scenes(a) -> int:
    run = resolve(a.run)
    scenes, clock, note, dur = scenes_of(run)
    look = run / "look"
    look.mkdir(exist_ok=True)
    md = scenes_md(run, scenes, clock, note, dur)
    (look / "scenes.md").write_text(md, encoding="utf-8", newline="\n")
    (look / "scenes.json").write_text(json.dumps(
        {"run": run.name, "clock": clock, "note": note, "duration": dur,
         "scenes": [{"name": s.name, "t0": round(s.t0, 2), "t1": round(s.t1, 2), "counts": dict(s.counts),
                     "marks": [{"t": round(t, 2), "sig": sig, "label": label} for t, sig, label in s.marks]}
                    for s in scenes]}, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    print(md.split("## 出来事")[0])
    print(f"→ {look / 'scenes.md'}")
    return 0


def cmd_cut(a) -> int:
    run = resolve(a.run)
    video = video_of(run)
    scenes, clock, note, dur = scenes_of(run)
    cmd_scenes(a)
    look = run / "look"
    total = 0
    with tempfile.TemporaryDirectory() as td:
        for s in match_scene(scenes, a.scene):
            rows = picks(s, a.gap)
            if not rows:
                continue
            d = look / s.slug
            d.mkdir(parents=True, exist_ok=True)
            for old in d.glob("*.png"):
                old.unlink()
            frames = []
            for t, key, label in rows:
                text = f"{run.name}  {s.name} +{t - s.t0:.1f}s  t={t:.1f}  {label}" + ("" if clock == "replay" else "  ※走行の時計")
                im = grab(video, t, Path(td))
                if a.crop:
                    im = lit_crop(im)
                    text += "  ※画面だけ切り出し"
                im = banded(im, text)
                im.save(d / f"{s.slug}_t{t:07.2f}_{safe(key)}.png")
                frames.append(im)
            sheet(frames).save(look / f"sheet_{s.slug}.jpg", quality=85)
            total += len(frames)
            print(f"  {s.name}: {len(frames)} 枚")
    print(f"{total} 枚 → {look}\nシートは索引。判定は個別の PNG を開いて見る。" + ("" if clock == "replay" else f"\n⚠ {note}"))
    return 0


def verdict_diff(a: Path, b: Path) -> list[str]:
    def read(p: Path) -> dict[str, str]:
        f = p / "verdict.json"
        if not f.exists():
            return {}
        return {v["text"]: v["level"] for v in json.loads(f.read_text(encoding="utf-8")).get("verdicts", [])}
    va, vb = read(a), read(b)
    out = []
    for text in sorted(set(va) | set(vb)):
        la, lb = va.get(text, "—"), vb.get(text, "—")
        if la != lb:
            out.append(f"| {la} | {lb} | {text[:140]} |")
    return out


def previous_same_args(run: Path) -> Path:
    """同じ走り方（verdict.json の args）で、動画のある直前の走行。比べる基準はこれ。"""
    def args(p: Path):
        try:
            return json.loads((p / "verdict.json").read_text(encoding="utf-8")).get("args")
        except (OSError, ValueError):
            return None
    want = args(run)
    if want is None:
        raise SystemExit(f"{run.name} の verdict.json が読めない。比べる相手を名前で渡す")
    for d in sorted((x for x in run.parent.iterdir() if x.is_dir() and x.name < run.name and not x.name.startswith("_")), reverse=True):
        if args(d) == want and (d / "eye.mp4").exists():
            return d
    raise SystemExit(f"{run.name} と同じ走り方（{want}）で動画のある前の走行が無い。比べる相手を名前で渡す")


def cmd_pair(a) -> int:
    if a.b is None:
        rb = resolve(a.a)
        ra = previous_same_args(rb)
    else:
        ra, rb = resolve(a.a), resolve(a.b)
    va, vb = video_of(ra), video_of(rb)          # 片方でも無ければここで落ちる
    sa, clock_a, note_a, _ = scenes_of(ra)
    sb, clock_b, note_b, _ = scenes_of(rb)
    base = lambda s: s.name.split("_", 1)[1]
    by_b = {base(s): s for s in sb}
    common = [(s, by_b[base(s)]) for s in sa if base(s) in by_b]
    if a.scene:
        common = [(x, y) for x, y in common if base(x).split("#")[0] == a.scene]
    if not common:
        raise SystemExit(f"両方にある場面が無い。A={[base(s) for s in sa]} B={[base(s) for s in sb]}")
    out_dir = DRIVE / "_pair" / f"{ra.name}__{rb.name}"
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.png"):       # 前に組んだ版を残すと、新旧が同じ棚に並ぶ
        old.unlink()
    from PIL import Image
    md = [f"# 並べて見る  A={ra.name}  B={rb.name}", "", f"A の時計: {note_a}", f"B の時計: {note_b}", "",
          "| 場面 | A の長さ | B の長さ | A だけの出来事 | B だけの出来事 | 数が違う出来事（A→B） |", "|---|---|---|---|---|---|"]
    only_a = [base(s) for s in sa if base(s) not in by_b]
    only_b = [base(s) for s in sb if base(s) not in {base(x) for x in sa}]
    n = 0
    with tempfile.TemporaryDirectory() as td:
        for x, y in common:
            pa = {k: (t, lab) for t, k, lab in picks(x, 0)}
            pb = {k: (t, lab) for t, k, lab in picks(y, 0)}
            keys = [k for k in pa if k in pb]
            for k in keys:
                (ta, la), (tb, lb) = pa[k], pb[k]
                left = banded(grab(va, ta, Path(td)), f"A {ra.name}  {x.name} +{ta - x.t0:.1f}s  {la}", a.width)
                right = banded(grab(vb, tb, Path(td)), f"B {rb.name}  {y.name} +{tb - y.t0:.1f}s  {lb}", a.width)
                img = Image.new("RGB", (left.width + right.width + 8, max(left.height, right.height)), (90, 0, 0))
                img.paste(left, (0, 0))
                img.paste(right, (left.width + 8, 0))
                img.save(out_dir / f"{x.slug}_{keys.index(k):02d}_{safe(k)}.png")
                n += 1
            diff = [f"{ev} {x.counts.get(ev, 0)}→{y.counts.get(ev, 0)}" for ev in sorted(set(x.counts) | set(y.counts))
                    if x.counts.get(ev, 0) != y.counts.get(ev, 0)]
            md.append(f"| {base(x)} | {x.t1 - x.t0:.1f} | {y.t1 - y.t0:.1f} | "
                      f"{' '.join(k for k in pa if k not in pb) or '—'} | {' '.join(k for k in pb if k not in pa) or '—'} | "
                      f"{' / '.join(diff) or '—'} |")
    if only_a or only_b:
        md += ["", f"A だけにある場面: {only_a or '—'}", f"B だけにある場面: {only_b or '—'}"]
    vd = verdict_diff(ra, rb)
    md += ["", "## 判定の差", ""] + (["| A | B | 判定 |", "|---|---|---|"] + vd if vd else ["差は無い"])
    md += ["", "数と長さは材料。良し悪しは左右の PNG を開いて見る。"]
    (out_dir / "pair.md").write_text("\n".join(md), encoding="utf-8", newline="\n")
    print("\n".join(md[:7 + len(common)]))
    print(f"{n} 組 → {out_dir}")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("scenes")
    p.add_argument("run", nargs="?")
    p = sub.add_parser("cut")
    p.add_argument("run", nargs="?")
    p.add_argument("--scene", help="1-A・導入・終幕 など。省くと全部")
    p.add_argument("--crop", action="store_true", help="光っている所（画面）だけへ寄って切る。視野の縁は切れる")
    p.add_argument("--gap", type=float, default=4.0, help="出来事の間がこれより空いたら埋める（秒。0 で埋めない）")
    p = sub.add_parser("pair")
    p.add_argument("a")
    p.add_argument("b", nargs="?", help="省くと、a と同じ走り方の直前の走行を左（A）に置く")
    p.add_argument("--scene")
    p.add_argument("--width", type=int, default=1280, help="片側の幅（px）")
    a = ap.parse_args()
    return {"scenes": cmd_scenes, "cut": cmd_cut, "pair": cmd_pair}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
