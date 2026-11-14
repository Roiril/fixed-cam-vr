"""画面ごと差し替わる子供部屋（anomaly/kidsroom-fusuma.md）の候補を、下絵と襖の奥の版の組ごとに 1 本ずつ焼く。

    blender.exe --background --python tools/gen-plate/kidsroom_block.py -- --out Logs/gen-plate/kr_block   # 先に下絵
    py -3.11 tools/gen-plate/bake-kidsroom.py --jobs front:a,front:e,corner:e,lap:a [--blocks Logs/gen-plate/kr_block]

参照①は下絵（block_<視点>.png）、参照②は実物の灰色の幕（<blocks>/curtain_ref.jpg。無人の写真から切り出す）。
同時に走らせない（別の絵を並列に焼くと走行の間で絵が入れ替わる — memory/codex_image_pipeline.md §4）。
出力は Logs/gen-plate/kr_<時刻>_<視点>_<版>/out.png。見るのは目と、魚眼を掛けた画（fisheye_preview.py）。
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
RUNNER = Path(os.environ["USERPROFILE"]) / ".claude" / "scripts" / "codex-run.ps1"

FUSUMA = {
    "a": """## 襖の奥（この版: 何も居ない）

- 隙間の中は**真っ黒なだけ**。何も描かない
- 見ている側が「何か居るのでは」と思ってしまうのは、隙間がこちらを向いて開いているからだけ""",
    "e": """## 襖の奥（この版: 居るとも居ないとも言えない）

- 隙間の暗がりの中、**床から 60〜90cm の高さ**に、周りの黒よりほんの少しだけ明るい、**縦に長い曖昧なむら**が 1 つあります
- それは**顔とも、掛かった布とも、ただの暗さのむらとも取れる**。輪郭を描かない。目・口・手・髪を描かない。光らせない
- 明るさは隙間の黒からごくわずかに浮く程度（はっきり読めたら描きすぎ）。**二度見して、やっと気になる**くらい
- 隙間の縁より手前には何も出ていない""",
    "f": """## 襖の奥（この版: 縁に寄った暗い形）

- 隙間の、**開いている襖の縁に寄った側**だけ、黒がわずかに濃く丸く膨らんでいます。床から 70cm ほどの高さ。
  誰かが縁から片目だけ出して覗いているときの頭の丸みに**見えなくもない**形
- **黒の中の黒**です。明るい部分・目・肌・髪の筋を描かない。襖の紙に掛かる影として、縁から 2cm ほどだけ紙の側へ滲む
- はっきり頭だと読めたら描きすぎ。襖の影のむらだと言われればそう見える程度""",
}

SHOT = {
    "front": "",
    "corner": "",
    "lap": """## 画面の下の縁にあるもの（人形の自分の体）

- 下絵の赤い四角と白い 2 つの丸は、**見ている人形自身の膝と手**です
- 膝は**色あせた赤い縮緬の着物**。埃をかぶり、柄はほとんど読めない
- 手は**胡粉を塗った人形の小さな手**。白く、艶は無く、細かいひびが入っている。膝の上に揃えて置かれ、動かない
- ピントは奥の部屋に合っていて、膝と手は少しぼける""",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", required=True, help="視点:版 を , で並べる（視点 front/corner/lap・版 a/e/f）")
    ap.add_argument("--blocks", default="Logs/gen-plate/kr_block")
    args = ap.parse_args()

    blocks = (REPO / args.blocks).resolve()
    curtain = blocks / "curtain_ref.jpg"
    if not curtain.exists():
        sys.exit(f"灰色の幕の参照が無い: {curtain}")
    lines = (HERE / "anomaly" / "kidsroom-fusuma.md").read_text(encoding="utf-8").split("\n")
    body = "\n".join(lines[lines.index("---", 1) + 1:])
    body = body[body.index("参照画像①は、3D で組んだ"):].strip()

    jobs = []
    for j in [s.strip() for s in args.jobs.split(",") if s.strip()]:
        shot, _, v = j.partition(":")
        if shot not in SHOT or v not in FUSUMA:
            sys.exit(f"組が無い: {j}")
        if not (blocks / f"block_{shot}.png").exists():
            sys.exit(f"下絵が無い: {blocks / f'block_{shot}.png'}")
        jobs.append((shot, v))

    stamp = time.strftime("%Y%m%d_%H%M%S")
    bad = 0
    for shot, v in jobs:
        d = REPO / "Logs" / "gen-plate" / f"kr_{stamp}_{shot}_{v}"
        d.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(blocks / f"block_{shot}.png", d / "block.png")
        shutil.copyfile(curtain, d / "curtain.jpg")
        prompt = body.replace("{{FUSUMA}}", FUSUMA[v]).replace("{{SHOT}}", SHOT[shot]) + f"""

## この画について（機械が書いた節）

- **参照画像①（下絵。構図はこれに従う）**: {d / 'block.png'}
- **参照画像②（右の幕の布。この灰色のカーテンにする。写っている金具・棚・床は取らない）**: {d / 'curtain.jpg'}
- **寸法**: 640 x 480（この寸法のまま出す）
"""
        (d / "prompt.txt").write_text(prompt, encoding="utf-8", newline="\n")
        t0 = time.time()
        r = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(RUNNER),
             "-PromptFile", str(d / "prompt.txt"), "-Mode", "image",
             "-Cwd", str(d), "-OutDir", str(d), "-OutImage", str(d / "out.png")],
            stdin=subprocess.DEVNULL, capture_output=True, timeout=900)
        ok = (d / "out.png").exists()
        bad += 0 if ok else 1
        print(f"{d.name}  rc={r.returncode}  {'OK' if ok else 'MISSING'}  {time.time() - t0:.0f}s", flush=True)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
