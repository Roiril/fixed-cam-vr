"""部屋の写真が替わったときに、2 周目 A の生成を前の版に揃えて作り直すためのプロンプトを書く。

    py -3.11 tools/gen-plate/stain3/refit_prompts.py <作業フォルダ> [--doll-dx 85]

元のプロンプト（prompt-bulge.md / prompt-right-bulge.md / prompt-right-doll-laugh.md / prompt-right-doll-watch.md）の
最後の 1 行（保存したら完了）の前に prompt-refit.md の段を足し、作業フォルダへ p_<段>.md として書く。
参照の版は各段の最後の画像として渡す（run_refit.ps1）。

--doll-dx は右の幕の縁の裏の人形の左右の位置を、生成の座標（1280 x 960）で何画素ずらすか。
その日の写真で右の幕の自由な縁が動いたぶんを入れる（2026-11-15 の写真は 9/24 より 42px（素材の座標）右 → 85）。
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
STEPS = {
    "left_bulge": "prompt-bulge.md",
    "right_bulge": "prompt-right-bulge.md",
    "doll_laugh": "prompt-right-doll-laugh.md",
    "doll_watch": "prompt-right-doll-watch.md",
}


def shift_doll_x(text: str, dx: int) -> str:
    """笑う人形の顔の中心と見えている幅の x を dx ずらす。どちらも見つからなければ落ちる。"""
    n_face = 0
    n_span = 0

    def face(m: re.Match) -> str:
        nonlocal n_face
        n_face += 1
        return f"x={int(m.group(1)) + dx}, y="

    def span(m: re.Match) -> str:
        nonlocal n_span
        n_span += 1
        return f"x={int(m.group(1)) + dx}〜{int(m.group(2)) + dx}"

    text = re.sub(r"x=(\d+), y=", face, text)
    text = re.sub(r"x=(\d+)〜(\d+)", span, text)
    if n_face != 1 or n_span != 1:
        raise SystemExit(f"人形の座標が見つからない（顔 {n_face} / 幅 {n_span}）")
    return text


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("work")
    ap.add_argument("--doll-dx", type=int, default=0)
    a = ap.parse_args()
    work = Path(a.work)
    work.mkdir(parents=True, exist_ok=True)
    add = (HERE / "prompt-refit.md").read_text(encoding="utf-8").strip("\n")
    for step, name in STEPS.items():
        text = (HERE / name).read_text(encoding="utf-8").rstrip("\n")
        head, last = text.rsplit("\n", 1)
        if not last.startswith("保存したら完了"):
            raise SystemExit(f"{name} の最後の行が想定と違う: {last}")
        if step == "doll_laugh" and a.doll_dx:
            head = shift_doll_x(head, a.doll_dx)
        out = work / f"p_{step}.md"
        out.write_text(f"{head}\n{add}\n\n{last}\n", encoding="utf-8", newline="\n")
        print(out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
