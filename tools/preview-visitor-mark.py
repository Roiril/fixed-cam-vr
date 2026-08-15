#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""体験者の報告ボタンの面（VisitorMarkPanel）の**表示の動き**を動画にする。

    py -3.11 tools/preview-visitor-mark.py

出力は `logs/preview/<日時>_visitor-mark.mp4` と、代表 4 枚の PNG。

⚠⚠ **これは Unity が描いた絵ではない。** 文字の並び・大きさ・進み方を、実装と同じ数値で
CPU に描かせたもの。**「読めるか」は答えていない** — 1 文字の見かけ角は 2 回続けて机上で
外している（`canon/LEDGER.md` 0035 / 0043）ので、実機で焼いて画を開くまで確認済みにしない。

数値の出どころ（**片方だけ直さない**）:

| ここ | 実装 |
|---|---|
| 2 秒長押し / 1 押し 1 回 / dt を 0.25s で切る | `Assets/Scripts/Input/VisitorMarkHoldLogic.cs` |
| 文言とゲージ（10 目盛・█░） | `Assets/Scripts/Diagnostics/VisitorMarkGuidance.cs` |
| 1 文字 0.015m・手元まで約 0.45m | `MainDemoSceneSetup.CreateVisitorMarkPanel` |
"""
from __future__ import annotations

import math
import subprocess
import sys
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / "Assets/Art/Fonts/SourceHanSansJP-Normal.otf"
OUTDIR = ROOT / "logs/preview"

# ---- 実装から写した定数（VisitorMarkHoldLogic / VisitorMarkGuidance）----
HOLD_SEC = 2.0
MAX_STEP_SEC = 0.25
CONFIRM_SEC = 1.2
SLOTS = 10
IDLE_LINE = "(X,Yで異変を報告)"
HOLDING_HEAD = "報告中"
CONFIRMED_LINE = "報告しました"

# ---- 見かけの大きさ（MainDemoSceneSetup.CreateVisitorMarkPanel）----
CHAR_M = 30 * 0.0005          # fontSize 30 × canvas scale 0.0005 = 1 文字 0.015m
HAND_DIST_M = 0.45            # 頭から左手の面まで（腕を下げた姿勢の概算）
CHAR_DEG = math.degrees(2 * math.atan(CHAR_M / 2 / HAND_DIST_M))

# ---- 動画 ----
W, H = 1280, 720
FPS = 30
VIEW_DEG = 46.0               # この 1280px が視界の何度分か
PX_PER_DEG = W / VIEW_DEG
CHAR_PX = CHAR_DEG * PX_PER_DEG
TEXT_RGB = (209, 199, 184)    # TMP の (0.82, 0.78, 0.72)
BG_RGB = (26, 24, 22)         # 暗い部屋のパススルー相当
CAPTION_RGB = (110, 120, 130)


def progress_bar(p: float, slots: int = SLOTS) -> str:
    """RegistrationGuidance.ProgressBar と同じ式（四捨五入は 0.5 を切り上げ）。"""
    p = min(1.0, max(0.0, p))
    filled = int(math.floor(p * slots + 0.5))
    return "█" * filled + "░" * (slots - filled)


def line_for(progress01: float, confirming: bool) -> str:
    """VisitorMarkGuidance.Line と同じ分岐。"""
    if confirming:
        return CONFIRMED_LINE
    if progress01 <= 0.0:
        return IDLE_LINE
    return HOLDING_HEAD + "\n" + progress_bar(progress01)


class Hold:
    """VisitorMarkHoldLogic の写し。"""

    def __init__(self) -> None:
        self.elapsed = 0.0
        self.consumed = False
        self.confirm_left = 0.0

    @property
    def progress01(self) -> float:
        return min(1.0, max(0.0, self.elapsed / HOLD_SEC))

    @property
    def confirming(self) -> bool:
        return self.confirm_left > 0.0

    def tick(self, dt: float, held: bool) -> bool:
        step = min(max(dt, 0.0), MAX_STEP_SEC)
        if self.confirm_left > 0.0:
            self.confirm_left = max(0.0, self.confirm_left - step)
        if not held:
            self.elapsed = 0.0
            self.consumed = False
            return False
        self.elapsed += step
        if self.consumed or self.elapsed < HOLD_SEC:
            return False
        self.consumed = True
        self.confirm_left = CONFIRM_SEC
        return True


# 台本: (押している秒数, 離している秒数) を並べる。
#   ① 2.4 秒握る → 通る    ② 1.2 秒で離す → 通らない    ③ もう一度 2.4 秒 → また通る
SCRIPT = [(0.0, 1.2), (2.4, 1.4), (1.2, 1.0), (2.4, 1.2)]


def press_at(t: float) -> bool:
    cursor = 0.0
    for hold_sec, rest_sec in SCRIPT:
        if cursor <= t < cursor + hold_sec:
            return True
        cursor += hold_sec
        if cursor <= t < cursor + rest_sec:
            return False
        cursor += rest_sec
    return False


def total_sec() -> float:
    return sum(a + b for a, b in SCRIPT)


def draw_controller(d: ImageDraw.ImageDraw, cx: int, cy: int) -> None:
    """左コントローラの影。**面が「少し上・少し奥」に居る**ことを見せるためだけの添え物。"""
    body = (100, 96, 92)
    d.rounded_rectangle([cx - 34, cy - 10, cx + 34, cy + 96], radius=26, fill=(46, 44, 42), outline=body, width=2)
    d.ellipse([cx - 58, cy - 46, cx + 58, cy + 6], outline=body, width=3)
    for i, label in enumerate(("Y", "X")):
        bx, by = cx - 14 + i * 28, cy + 26
        d.ellipse([bx - 11, by - 11, bx + 11, by + 11], fill=(66, 63, 60), outline=body, width=2)


def render(t: float, logic: Hold, fired_marks: int) -> Image.Image:
    img = Image.new("RGB", (W, H), BG_RGB)
    d = ImageDraw.Draw(img)

    font = ImageFont.truetype(str(FONT), int(round(CHAR_PX)))
    cap = ImageFont.truetype(str(FONT), 22)

    # 手元（左下寄り）と、その少し上・少し奥に立つ面。
    hand_x, hand_y = int(W * 0.36), int(H * 0.70)
    draw_controller(d, hand_x, hand_y)

    body = line_for(logic.progress01, logic.confirming)
    lines = body.split("\n")
    line_h = CHAR_PX * 1.15
    top = hand_y - 62 - line_h * len(lines)
    for i, ln in enumerate(lines):
        w = d.textlength(ln, font=font)
        d.text((hand_x + 26 - w / 2, top + i * line_h), ln, font=font, fill=TEXT_RGB)

    # ---- 以下は検証用の添え書き（実機には出ない）----
    d.line([(0, H - 92), (W, H - 92)], fill=(48, 46, 44), width=1)
    state = "余韻（報告しました）" if logic.confirming else ("長押し中" if logic.progress01 > 0 else "待ち")
    d.text((28, H - 74),
           f"t={t:5.2f}s   X/Y={'押している' if press_at(t) else '離している'}   {state}"
           f"   進み={logic.progress01:4.2f}   通った報告={fired_marks} 回",
           font=cap, fill=CAPTION_RGB)
    d.text((28, H - 42),
           f"1 文字 {CHAR_M*100:.1f}cm・手元まで {HAND_DIST_M:.2f}m ＝ 見かけ {CHAR_DEG:.1f}°"
           f"（下限 1.5°）／この動画は Unity が描いた絵ではない",
           font=cap, fill=CAPTION_RGB)
    return img


def main() -> int:
    if not FONT.exists():
        print(f"フォントが無い: {FONT}")
        return 1
    OUTDIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    mp4 = OUTDIR / f"{stamp}_visitor-mark.mp4"

    import imageio_ffmpeg

    dt = 1.0 / FPS
    frames = int(round(total_sec() / dt))
    logic = Hold()
    fired = 0
    still_at = {int(0.6 / dt): "idle", int(3.0 / dt): "holding",
                int(3.7 / dt): "confirmed", int(6.4 / dt): "short-press"}

    writer = imageio_ffmpeg.write_frames(str(mp4), (W, H), fps=FPS, quality=8, macro_block_size=1)
    writer.send(None)
    for i in range(frames):
        t = i * dt
        if logic.tick(dt, press_at(t)):
            fired += 1
        img = render(t, logic, fired)
        if i in still_at:
            img.save(OUTDIR / f"{stamp}_visitor-mark_{still_at[i]}.png")
        writer.send(img.tobytes())
    writer.close()

    print(f"1 文字 {CHAR_M*100:.1f}cm / 手元まで {HAND_DIST_M}m ＝ 見かけ {CHAR_DEG:.2f}°")
    print(f"通った報告 {fired} 回（台本は 2 回通り 1 回落ちる）")
    print(f"→ {mp4.relative_to(ROOT)}  ({frames} フレーム / {total_sec():.1f} 秒)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
