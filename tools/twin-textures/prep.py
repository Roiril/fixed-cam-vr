"""デジタルツインの部屋（VirtualRoom）のテクスチャを、Codex の生成画像から作り直す。

使い方:
    1. Codex で 3 枚を生成する（プロンプトはこのフォルダの prompt-*.txt。1024 角・均一な照明・タイル可）
       & "$env:USERPROFILE\\.claude\\scripts\\codex-run.ps1" -PromptFile prompt-wall.txt -Mode image -OutImage <dir>\\wall.png
    2. py -3.11 tools/twin-textures/prep.py <wall.png floor.png fabric.png のあるフォルダ>
       → Assets/Editor/VirtualWalkAssets/Twin/twin_{wall,floor,fabric}.jpg

生成元は Drive の renders/twin/textures-src-20261104/（wall_v1_unused.png は迷彩柄に見えて外した初版）。
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parents[2] / "Assets" / "Editor" / "VirtualWalkAssets" / "Twin"


def seam(a: np.ndarray) -> tuple[float, float, float]:
    return (float(np.abs(a[:, 0] - a[:, -1]).mean()), float(np.abs(a[0] - a[-1]).mean()),
            float(np.abs(a[:, 0] - a[:, 1]).mean()))


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    src = Path(sys.argv[1])
    for name in ("wall", "floor", "fabric"):
        a = np.asarray(Image.open(src / f"{name}.png").convert("RGB").resize((1024, 1024), Image.LANCZOS)).astype(np.float32)
        lr, tb, neighbour = seam(a)
        if lr > neighbour * 2.5 or tb > neighbour * 2.5:
            print(f"⚠ {name}: 継ぎ目が目立つ（左右 {lr:.1f} / 上下 {tb:.1f} / 隣の画素 {neighbour:.1f}）。生成し直す")
            return 1
        Image.fromarray(a.clip(0, 255).astype(np.uint8)).save(OUT / f"twin_{name}.jpg", quality=92)
        print(f"{name}: 平均 RGB {a.reshape(-1, 3).mean(0).round(1)} → {OUT / f'twin_{name}.jpg'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
