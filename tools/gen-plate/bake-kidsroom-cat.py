"""2 周目 B の飛び先の動画の、最初と最後のフレームを焼く（anomaly/kidsroom-cat.md・LEDGER 0361）。

    blender.exe --background --python tools/gen-plate/kidsroom_block.py -- --out Logs/gen-plate/kr_block --shots dark   # 先に下絵
    py -3.11 tools/gen-plate/bake-kidsroom-cat.py [--pairs 2] [--mood Logs/gen-plate/kr_block/mood_corridor.png]

1 組ごとに、最初のフレーム（参照① 下絵）を焼き、それを参照①にして最後のフレームを焼く。1 本ずつ直列（並列は絵が入れ替わる）。
出力は Logs/gen-plate/krcat_<時刻>_<n>/first.png・last.png（640x480）と、動画生成へ渡す 16:9 版 first_169.png・last_169.png
（等比で 1920x1440 に拡大し、左右に 320px ずつ黒帯。memory/codex_image_pipeline.md「動画生成用の入力画像」）。
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
RUNNER = Path(os.environ["USERPROFILE"]) / ".claude" / "scripts" / "codex-run.ps1"


def sections(dolls: bool):
    # 区切りは「行まるごと」のコメントだけ。本文中に同じ文字列があっても切らない
    # （2026-11-14、冒頭の説明に区切りを書いていて、最初のフレームの指示が丸ごと抜けた）。
    t = (HERE / "anomaly" / "kidsroom-cat.md").read_text(encoding="utf-8")
    parts: dict[str, list[str]] = {}
    cur = None
    for line in t.split("\n"):
        m = re.fullmatch(r"<!-- (/?\w+) -->", line.strip())
        if m:
            tag = m.group(1)
            cur = "common" if tag == "/dolls" else tag
            parts.setdefault(cur, [])
            continue
        if cur:
            parts[cur].append(line)
    for k in ("common", "dolls", "first", "last"):
        if not "".join(parts.get(k, [])).strip():
            sys.exit(f"節 {k} が空: anomaly/kidsroom-cat.md")
    common = "\n".join(parts["common"]).strip()
    if dolls:
        mark = "## やらないこと"
        if mark not in common:
            sys.exit("common に「やらないこと」の節が無い")
        common = common.replace(mark, "\n".join(parts["dolls"]).strip() + "\n\n" + mark, 1)
    return common, "\n".join(parts["first"]).strip(), "\n".join(parts["last"]).strip()


def bake(d: Path, name: str, body: str, refs: list[tuple[str, Path, str]]) -> Path:
    lines = []
    for label, src, what in refs:
        dst = d / f"{name}_{label}{src.suffix}"
        shutil.copyfile(src, dst)
        lines.append(f"- **参照画像{label}（{what}）**: {dst}")
    prompt = body + "\n\n## この画について（機械が書いた節）\n\n" + "\n".join(lines) + "\n- **寸法**: 640 x 480（この寸法のまま出す）\n"
    pf = d / f"{name}_prompt.txt"
    pf.write_text(prompt, encoding="utf-8", newline="\n")
    out = d / f"{name}.png"
    t0 = time.time()
    r = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(RUNNER),
         "-PromptFile", str(pf), "-Mode", "image", "-Cwd", str(d), "-OutDir", str(d / f"{name}_log"), "-OutImage", str(out)],
        stdin=subprocess.DEVNULL, capture_output=True, timeout=900)
    print(f"{d.name}/{name}  rc={r.returncode}  {'OK' if out.exists() else 'MISSING'}  {time.time() - t0:.0f}s", flush=True)
    return out


def pad169(src: Path, dst: Path):
    im = Image.open(src).convert("RGB")
    w, h = im.size
    if abs(w / h - 4 / 3) > 0.01:
        sys.exit(f"4:3 ではない: {src} {im.size}")
    im = im.resize((1920, 1440), Image.LANCZOS)
    canvas = Image.new("RGB", (2560, 1440), (0, 0, 0))
    canvas.paste(im, (320, 0))
    canvas.save(dst)
    px = canvas.load()
    assert all(px[x, y] == (0, 0, 0) for x in (0, 319, 2240, 2559) for y in (0, 720, 1439)), "黒帯が黒くない"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pairs", type=int, default=2)
    ap.add_argument("--blocks", default="Logs/gen-plate/kr_block")
    ap.add_argument("--mood", default="Logs/gen-plate/kr_block/mood_corridor.png")
    ap.add_argument("--dolls", action="store_true", help="部屋に人形がいっぱいある版（0362）。参照④に mood_dolls.png を渡す")
    a = ap.parse_args()
    blocks = (REPO / a.blocks).resolve()
    block, curtain, mood = blocks / "block_dark.png", blocks / "curtain_ref.jpg", (REPO / a.mood).resolve()
    for p in (block, curtain, mood):
        if not p.exists():
            sys.exit(f"無い: {p}")
    common, first, last = sections(a.dolls)
    dmood = blocks / "mood_dolls.png"
    if a.dolls and not dmood.exists():
        sys.exit(f"無い: {dmood}")
    extra = [("④", dmood, "部屋の人形の雰囲気だけ。壁の質感と構図は取らない")] if a.dolls else []
    stamp = time.strftime("%Y%m%d_%H%M%S")
    bad = 0
    for n in range(1, a.pairs + 1):
        d = REPO / "Logs" / "gen-plate" / f"krcat_{stamp}_{'dolls_' if a.dolls else ''}{n}"
        d.mkdir(parents=True, exist_ok=True)
        f = bake(d, "first", first + "\n\n" + common, [
            ("①", block, "下絵。構図はこれに従う"),
            ("②", mood, "雰囲気だけ。暗さ・色・壁の朽ち方・床の散らかり方"),
            ("③", curtain, "右の幕の布。この灰色のカーテンにする")] + extra)
        if not f.exists():
            bad += 1
            continue
        l = bake(d, "last", last + "\n\n## 参考（部屋の説明。参照画像①に既に写っている）\n\n" + common, [
            ("①", f, "同じ動画の最初のフレーム。部屋とカメラはこれと同じ"),
            ("②", mood, "雰囲気だけ"),
            ("③", curtain, "右の幕の布")] + extra)
        if not l.exists():
            bad += 1
            continue
        pad169(f, d / "first_169.png")
        pad169(l, d / "last_169.png")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
