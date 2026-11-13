# -*- coding: utf-8 -*-
"""「滲んで現れる」「長押しで布が膨らむ」を、シェーダと同じ式で試し描きする（0349）。

    py -3.11 tools/gen-plate/bleed_maps.py --recipe tools/gen-plate/mask-recipes/bleed-stain-a-20261113.json
    py -3.11 tools/gen-plate/bleed_maps.py --recipe tools/gen-plate/mask-recipes/bleed-hands-b-20261113.json
    py -3.11 tools/gen-plate/bleed_preview.py            # 4 本すべて
    py -3.11 tools/gen-plate/bleed_preview.py --only press --no-video

出力は既定で `Logs/gen-plate/bleed/preview/`。1 本ごとに一定間隔のコマを並べた PNG と、30fps の mp4。
コマには時刻と数値（reveal・press・重み）を焼く。

| 本 | 中身 | コマの間隔 |
|---|---|---|
| `stain` | 2-A の幕のシミが滲んで現れる（無人 / 体の前 / 従来の差し替え式 / 順の前線） | 0.3 秒 |
| `press` | 長押しで布が膨らみ、成功の手前で離す（バネで戻る） | 0.1 秒 |
| `hands` | 1-B の壁に手形が手前から奥へ現れる | 0.3 秒 |
| `done` | 長押しを成功まで続け、膨らみを畳み、人形が 0.4 秒覗く | 0.05 秒 |

⚠ これは `CrtCompositeShared.hlsl` の `SampleBase` の**差し替えの段だけ**の写し。post（粒・明暗曲線・管）は
   掛けない。式を変えたら `SampleBase` / `ScreenOverlayController` / `OverlayPressLogic` と同時にここも直す
   （定数は下の「シェーダと同じ数」。`tools/test-tools.py` が C# と HLSL の値と突き合わせる）。
⚠ 体の入った実写が 2-A / 1-B の該当位置に無いので、体は**暗い人型を描いて代用する**（コマに「代用」と焼く）。
⚠ 依存は PIL と numpy だけ。動画は ffmpeg（PATH）。無ければ PNG だけ出して終わる。
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bleed_maps as bm  # noqa: E402

FRAME_W, FRAME_H = bm.FRAME_W, bm.FRAME_H
ROOT = bm.ROOT

# ---------------------------------------------------------------- シェーダと同じ数
# SampleBase（CrtCompositeShared.hlsl）・ScreenOverlayController・OverlayPressLogic と対。
REVEAL_SOFT = 0.12            # _OverlayRevealSoft の既定
EDGE_DARK_MUL = 0.22          # 掛け算の前線の暗い縁: ratio * (1 - 0.22 * edge)
EDGE_DARK_REPLACE = 0.30      # 差し替えの前線の暗い縁: rgb * (1 - 0.30 * edge)
REVEAL_CURVE_POW = 2.2        # 進み具合 1 - (1 - t)^2.2
BREATH_MIN = 0.985            # 出切った後の呼吸の下端（上端は 1）
BREATH_PERIOD_SEC = 6.0
REVEAL_OUT_DEFAULT = 0.45
PRESS_PULL = 0.12             # uv' = uv + (center - uv) * PRESS_PULL * p * m
PRESS_W_EDGE = (0.30, 0.62)   # w = smoothstep(lo, hi, saturate(p)) * m
SPRING_HZ = 2.4
SPRING_DAMPING = 0.38
PRESS_MIN, PRESS_MAX = -0.15, 1.1
SPRING_SUBSTEP_SEC = 1.0 / 120.0
SPRING_MAX_DT = 0.25
PRESS_COLLAPSE_SEC = 0.12
HOLD_SEC = 1.0                # VisitorMarkHoldLogic.DefaultHoldSec
MARK_DONE_SEC = 0.4           # L2C0#0 の markDoneSec

FPS = 30


# ---------------------------------------------------------------- 時間の式（C# の写し）

def reveal_in(t: float, delay: float, sec: float) -> tuple[float, bool]:
    """出始めからの reveal と、出切ったか。"""
    if t < delay:
        return 0.0, False
    x = (t - delay) / max(sec, 1e-3)
    if x >= 1.0:
        return 1.0, True
    return 1.0 - (1.0 - x) ** REVEAL_CURVE_POW, False


def breath(t_since_full: float) -> float:
    """出切った後の呼吸。1 から始まり 0.985 まで沈んで戻る（6 秒周期）。"""
    k = 0.5 - 0.5 * np.cos(2.0 * np.pi * t_since_full / BREATH_PERIOD_SEC)
    return 1.0 - (1.0 - BREATH_MIN) * float(k)


def reveal_out(r0: float, u: float) -> float:
    """引っ込み。最後に出た所（v の大きい所）から先に消える。u = 経過 / revealOutSec。"""
    u = min(max(u, 0.0), 1.0)
    return r0 * (1.0 - u) ** REVEAL_CURVE_POW


class PressSpring:
    """OverlayPressLogic の写し。押している間は smoothstep(進み具合) へ、離したら 0 へ減衰振動で寄る。"""

    def __init__(self) -> None:
        self.p = 0.0
        self.vel = 0.0
        self.collapse_from = None
        self.collapse_t = 0.0

    def tick(self, dt: float, held: bool, progress: float) -> float:
        dt = min(max(dt, 0.0), SPRING_MAX_DT)
        if self.collapse_from is not None:
            self.collapse_t += dt
            u = min(self.collapse_t / PRESS_COLLAPSE_SEC, 1.0)
            self.p = self.collapse_from * (1.0 - u)
            self.vel = 0.0
            return self.p
        x = min(max(progress, 0.0), 1.0)
        target = x * x * (3.0 - 2.0 * x) if held else 0.0
        w = 2.0 * np.pi * SPRING_HZ
        n = max(1, int(np.ceil(dt / SPRING_SUBSTEP_SEC)))
        h = dt / n
        for _ in range(n):
            acc = w * w * (target - self.p) - 2.0 * SPRING_DAMPING * w * self.vel
            self.vel += acc * h
            self.p += self.vel * h
            if self.p < PRESS_MIN or self.p > PRESS_MAX:
                self.p = min(max(self.p, PRESS_MIN), PRESS_MAX)
                self.vel = 0.0
        return self.p

    def collapse(self) -> None:
        self.collapse_from = self.p
        self.collapse_t = 0.0


# ---------------------------------------------------------------- 画の読み込み（実機と同じ解釈）

def device_tex(p: Path) -> np.ndarray:
    """URL から読んだテクスチャを実機が読む値（sRGB として線形へ戻す）。"""
    return bm.srgb_to_lin(np.asarray(Image.open(p).convert("RGB"), dtype=np.float32) / 255.0)


def contain_fit(rgb: np.ndarray, outside: float) -> np.ndarray:
    """素材を枠へ contain-fit（ContainUv と同じ）。枠の外は outside。"""
    x0, y0, w, h = bm.frame_rect(rgb.shape[1], rgb.shape[0])
    im = Image.fromarray(np.round(np.clip(bm.lin_to_srgb(rgb), 0, 1) * 255).astype(np.uint8))
    a = bm.srgb_to_lin(np.asarray(im.resize((w, h), Image.BILINEAR), dtype=np.float32) / 255.0)
    out = np.full((FRAME_H, FRAME_W, 3), outside, np.float32)
    out[y0:y0 + h, x0:x0 + w] = a
    return out


def ratio_fit(rgb: np.ndarray) -> np.ndarray:
    """比の画の contain-fit。比は線形値をそのまま縮める（枠の外は 1 ＝ 何もしない）。"""
    x0, y0, w, h = bm.frame_rect(rgb.shape[1], rgb.shape[0])
    out = np.ones((FRAME_H, FRAME_W, 3), np.float32)
    for c in range(3):
        out[y0:y0 + h, x0:x0 + w, c] = np.asarray(
            Image.fromarray(rgb[..., c], "F").resize((w, h), Image.BILINEAR), dtype=np.float32)
    return out


def frame_gray(p: Path) -> np.ndarray:
    a = device_tex(p)[..., 0]
    if a.shape != (FRAME_H, FRAME_W):
        raise ValueError(f"枠空間 640x360 ではない: {p}")
    return a


def sample(img: np.ndarray, x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """双線形で読む（画素の添字座標・端は端の値）。"""
    h, w = img.shape[:2]
    x = np.clip(x, 0.0, w - 1.0)
    y = np.clip(y, 0.0, h - 1.0)
    x0 = np.floor(x).astype(np.int32)
    y0 = np.floor(y).astype(np.int32)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = (x - x0).astype(np.float32)
    fy = (y - y0).astype(np.float32)
    if img.ndim == 3:
        fx, fy = fx[..., None], fy[..., None]
    a = img[y0, x0] * (1 - fx) + img[y0, x1] * fx
    b = img[y1, x0] * (1 - fx) + img[y1, x1] * fx
    return a * (1 - fy) + b * fy


GY, GX = np.mgrid[0:FRAME_H, 0:FRAME_W].astype(np.float32)


def composite(live: np.ndarray, layer1: dict | None, press: dict | None, layer2: dict | None) -> tuple[np.ndarray, dict]:
    """SampleBase の差し替えの段（press の寄せ → 1 層目 → 膨らみ → 2 層目）。戻り値は線形 RGB。"""
    x, y = GX, GY
    m = None
    p = press["p"] if press else 0.0
    if press and abs(p) > 1e-3:
        m = press["mask"]
        k = PRESS_PULL * p * m
        x = x + (press["center"][0] - x) * k
        y = y + (press["center"][1] - y) * k
    col = sample(live, x, y)
    stats = {}
    if layer1 and layer1["strength"] > 0.0:
        tex = sample(layer1["tex"], x, y)
        mask = sample(layer1["mask"], x, y)
        a = np.ones_like(mask)
        edge = np.zeros_like(mask)
        if layer1.get("reveal") is not None:
            v = sample(layer1["reveal"], x, y)
            soft = max(layer1.get("soft", REVEAL_SOFT), 1e-3)
            a = np.clip((layer1["R"] * (1.0 + soft) - v) / soft, 0.0, 1.0)
            edge = a * (1.0 - a) * 4.0
        alpha = np.clip(mask * a * layer1["strength"], 0.0, 1.0)[..., None]
        if layer1["mode"] == "multiply":
            ratio = tex * (1.0 - EDGE_DARK_MUL * edge[..., None])
            col = col * (1.0 + (ratio - 1.0) * alpha)
        else:
            over = tex * (1.0 - EDGE_DARK_REPLACE * edge[..., None])
            col = col + (over - col) * alpha
        stats["edgePx"] = int((edge > 0.5).sum())
    if press and abs(p) > 1e-3:
        bulge = sample(press["tex"], x, y)
        t = np.clip((min(max(p, 0.0), 1.0) - PRESS_W_EDGE[0]) / (PRESS_W_EDGE[1] - PRESS_W_EDGE[0]), 0.0, 1.0)
        wgt = (t * t * (3.0 - 2.0 * t)) * m
        col = col + (bulge - col) * wgt[..., None]
        stats["wMax"] = round(float(wgt.max()), 3)
    if layer2 and layer2["strength"] > 0.0:
        a2 = np.clip(sample(layer2["mask"], x, y) * layer2["strength"], 0.0, 1.0)[..., None]
        col = col + (sample(layer2["tex"], x, y) - col) * a2
    return col, stats


# ---------------------------------------------------------------- 体（代用）

def silhouette(spec: list[int]) -> np.ndarray:
    """暗い人型（枠空間・0..1 の被覆）。[中心 x, 頭の上 y, 背の高さ px]。"""
    cx, top, hgt = spec
    s = hgt / 330.0
    im = Image.new("L", (FRAME_W, FRAME_H), 0)
    d = ImageDraw.Draw(im)

    def P(dx, dy):
        return (cx + dx * s, top + dy * s)
    d.ellipse([P(-22, 0), P(22, 54)], fill=255)                        # 頭
    d.polygon([P(-12, 48), P(12, 48), P(14, 66), P(-14, 66)], fill=255)  # 首
    d.polygon([P(-56, 70), P(56, 70), P(48, 190), P(-48, 190)], fill=255)  # 胴
    d.polygon([P(-56, 72), P(-70, 80), P(-74, 200), P(-58, 204), P(-50, 96)], fill=255)  # 腕
    d.polygon([P(56, 72), P(70, 80), P(74, 200), P(58, 204), P(50, 96)], fill=255)
    d.polygon([P(-48, 186), P(-4, 186), P(-8, 330), P(-40, 330)], fill=255)  # 脚
    d.polygon([P(4, 186), P(48, 186), P(40, 330), P(8, 330)], fill=255)
    im = im.filter(ImageFilter.GaussianBlur(1.2))
    return np.asarray(im, dtype=np.float32) / 255.0


def with_body(live: np.ndarray, spec: list[int], seed: int) -> np.ndarray:
    cov = silhouette(spec)[..., None]
    rng = np.random.default_rng(seed)
    shade = 0.018 + 0.03 * (1.0 - GY / FRAME_H)[..., None]       # 上が少し明るい（天井の光）
    shade = shade * (1.0 + 0.25 * bm.fbm(FRAME_H, FRAME_W, rng)[..., None])
    body = np.concatenate([shade * 1.05, shade, shade * 0.95], -1)
    return live * (1.0 - cov) + body * cov


# ---------------------------------------------------------------- 描画

_FONT_CACHE: dict[int, ImageFont.FreeTypeFont] = {}


def font(size: int):
    if size not in _FONT_CACHE:
        for f in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/YuGothM.ttc", "C:/Windows/Fonts/msgothic.ttc"):
            try:
                _FONT_CACHE[size] = ImageFont.truetype(f, size)
                break
            except OSError:
                continue
        else:
            _FONT_CACHE[size] = ImageFont.load_default()
    return _FONT_CACHE[size]


def to_img(lin: np.ndarray) -> Image.Image:
    return Image.fromarray(np.round(np.clip(bm.lin_to_srgb(lin), 0, 1) * 255).astype(np.uint8), "RGB")


def tile(lin: np.ndarray, label: str, sub: str = "") -> Image.Image:
    im = to_img(lin)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, FRAME_W, 22], fill=(0, 0, 0))
    d.text((6, 2), label, fill=(255, 230, 120), font=font(14))
    if sub:
        d.rectangle([0, FRAME_H - 22, FRAME_W, FRAME_H], fill=(0, 0, 0))
        d.text((6, FRAME_H - 20), sub, fill=(200, 220, 255), font=font(13))
    return im


def order_view(v: np.ndarray, R: float, soft: float = REVEAL_SOFT) -> np.ndarray:
    """順の画を色で塗り、いまの前線（a が 0..1 の帯）を白で重ねる（線形 RGB）。"""
    t = np.clip(v, 0.0, 1.0)
    col = np.stack([np.clip(1.5 - np.abs(4 * t - 3), 0, 1), np.clip(1.5 - np.abs(4 * t - 2), 0, 1),
                    np.clip(1.5 - np.abs(4 * t - 1), 0, 1)], -1) * 0.35
    col[v >= 0.999] = 0.0
    a = np.clip((R * (1.0 + soft) - v) / soft, 0.0, 1.0)
    shown = (a > 0.5) & (v < 0.999)
    col[shown] = col[shown] * 2.2
    band = (a > 0.02) & (a < 0.98) & (v < 0.999)
    col[band] = 1.0
    return bm.srgb_to_lin(np.clip(col, 0, 1)).astype(np.float32)


def grid(tiles: list[Image.Image], cols: int, head: str) -> Image.Image:
    rows = (len(tiles) + cols - 1) // cols
    out = Image.new("RGB", (cols * FRAME_W, rows * FRAME_H + 30), (16, 16, 16))
    d = ImageDraw.Draw(out)
    d.text((8, 5), head, fill=(255, 255, 255), font=font(16))
    for i, t in enumerate(tiles):
        out.paste(t, ((i % cols) * FRAME_W, 30 + (i // cols) * FRAME_H))
    return out


def contact(frames: list[Image.Image], cols: int, scale: float) -> Image.Image:
    w, h = int(frames[0].width * scale), int(frames[0].height * scale)
    rows = (len(frames) + cols - 1) // cols
    out = Image.new("RGB", (cols * w, rows * h), (0, 0, 0))
    for i, f in enumerate(frames):
        out.paste(f.resize((w, h), Image.LANCZOS), ((i % cols) * w, (i // cols) * h))
    return out


class Writer:
    """mp4 へ流す（ffmpeg が無ければ何もしない）と、一定間隔のコマを取り置く。"""

    def __init__(self, path: Path, step_sec: float, video: bool):
        self.path = path
        self.step = step_sec
        self.next_t = 0.0
        self.keep: list[Image.Image] = []
        self.proc = None
        exe = shutil.which("ffmpeg") if video else None
        self.video_path = path.with_suffix(".mp4") if exe else None
        self.exe = exe

    def put(self, t: float, im: Image.Image) -> None:
        if self.exe and self.proc is None:
            self.proc = subprocess.Popen(
                [self.exe, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
                 "-s", f"{im.width}x{im.height}", "-r", str(FPS), "-i", "-",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", str(self.video_path)],
                stdin=subprocess.PIPE)
        if self.proc:
            self.proc.stdin.write(im.tobytes())
        if t + 1e-6 >= self.next_t:
            self.keep.append(im)
            self.next_t += self.step

    def close(self, cols: int, scale: float) -> list[Path]:
        out = []
        if self.proc:
            self.proc.stdin.close()
            if self.proc.wait() != 0:
                raise RuntimeError(f"ffmpeg が失敗した: {self.video_path}")
            out.append(self.video_path)
        sheet = self.path.with_name(self.path.stem + "_sheet.png")
        contact(self.keep, cols, scale).save(sheet, optimize=True)
        out.append(sheet)
        fdir = self.path.with_name(self.path.stem + "_frames")
        fdir.mkdir(exist_ok=True)
        for old in fdir.glob("*.png"):
            old.unlink()
        for i, f in enumerate(self.keep):
            f.save(fdir / f"{i:03d}.png", optimize=True)
        out.append(fdir)
        return out


# ---------------------------------------------------------------- 素材

def load_set(recipe: Path, maps_dir: Path) -> dict:
    rec = json.loads(recipe.read_text(encoding="utf-8"))
    roots = bm.search_roots()
    names = rec["outputs"]
    maps = json.loads((maps_dir / names["maps"]).read_text(encoding="utf-8"))
    pv = rec["preview"]
    s = {
        "rec": rec,
        "live": contain_fit(device_tex(bm.find_input(pv["live"], roots)), 0.0),
        "source": contain_fit(device_tex(bm.find_input(rec["source"], roots)), 0.0),
        "ratio": ratio_fit(device_tex(maps_dir / names["ratio"])),
        "reveal": frame_gray(maps_dir / names["reveal"]),
        "cueMask": frame_gray(bm.find_input(pv["cueMask"], roots)),
        "silhouette": pv["silhouette"],
        "cue": rec.get("cue", {}),
        "maps": maps,
    }
    if names.get("pressMask"):
        s["pressMask"] = frame_gray(maps_dir / names["pressMask"])
        s["pressCenter"] = maps["pressCenterPx"]
        s["bulge"] = contain_fit(device_tex(bm.find_input(rec["press"]["source"], roots)), 0.0)
    if names.get("dollMask"):
        s["dollMask"] = frame_gray(maps_dir / names["dollMask"])
        s["doll"] = contain_fit(device_tex(bm.find_input(rec["doll"]["source"], roots)), 0.0)
    s["body"] = with_body(s["live"], s["silhouette"], 7)
    return s


# ---------------------------------------------------------------- 4 本

def run_reveal(s: dict, name: str, title: str, out: Path, video: bool) -> list[Path]:
    cue = s["cue"]
    delay, sec = float(cue["revealDelaySec"]), float(cue["revealSec"])
    total = delay + sec + 1.5
    w = Writer(out / f"{name}.png", 0.3, video)
    n = int(round(total * FPS)) + 1
    t_full = None
    for i in range(n):
        t = i / FPS
        R, full = reveal_in(t, delay, sec)
        if full:
            t_full = t if t_full is None else t_full
            R = breath(t - t_full)
        mul = dict(mode="multiply", tex=s["ratio"], mask=s["cueMask"], reveal=s["reveal"], R=R, strength=1.0)
        rep = dict(mode="replace", tex=s["source"], mask=s["cueMask"], reveal=s["reveal"], R=R, strength=1.0)
        a, st = composite(s["live"], mul, None, None)
        b, _ = composite(s["body"], mul, None, None)
        c, _ = composite(s["body"], rep, None, None)
        sub = f"t={t:4.2f}s  reveal={R:.3f}  delay={delay} sec={sec} soft={REVEAL_SOFT}"
        tiles = [tile(a, "掛け算（blend=multiply）・無人", sub),
                 tile(b, "掛け算・体の前（体は代用）", sub),
                 tile(c, "比べ: 従来の差し替え式に reveal だけ足した場合・体の前（代用）", sub),
                 tile(order_view(s["reveal"], R), "順の画 v（青=先・赤=後）と前線（白）", f"edge>0.5 の画素 {st.get('edgePx', 0)}")]
        w.put(t, grid(tiles, 2, f"{title}  t={t:5.2f}s"))
    return w.close(4, 0.25)


def hold_script(t: float, release_at: float | None) -> tuple[bool, float]:
    """長押しの台本。release_at までは押す（進み具合は 1 秒で 1）、以後は離す。"""
    held = release_at is None or t < release_at
    prog = min(t / HOLD_SEC, 1.0) if held else 0.0
    return held, prog


def run_press(s: dict, out: Path, video: bool) -> list[Path]:
    """押して、成功の手前（進み具合 0.85）で離す。膨らみはバネで戻り、シミは残る。"""
    release_at = 0.85 * HOLD_SEC
    total = release_at + 1.6
    spring = PressSpring()
    w = Writer(out / "press.png", 0.1, video)
    n = int(round(total * FPS)) + 1
    for i in range(n):
        t = i / FPS
        held, prog = hold_script(t, release_at)
        p = spring.tick(0.0 if i == 0 else 1.0 / FPS, held, prog)
        stain = dict(mode="multiply", tex=s["ratio"], mask=s["cueMask"], reveal=s["reveal"], R=1.0, strength=1.0)
        press = dict(p=p, mask=s["pressMask"], center=s["pressCenter"], tex=s["bulge"])
        a, st = composite(s["live"], stain, press, None)
        b, _ = composite(s["body"], stain, press, None)
        sub = f"t={t:4.2f}s  {'押下' if held else '離した'}  進み具合={prog:.2f}  p={p:+.3f}  w最大={st.get('wMax', 0):.2f}"
        mview = np.repeat((s["pressMask"] * (1.0 + 0 * p))[..., None], 3, -1) * 0.6
        tiles = [tile(a, "膨らむ・無人", sub), tile(b, "膨らむ・体の前（代用）", sub),
                 tile(s["bulge"], "膨らみの素材（そのまま）"), tile(mview, "press マスク m（中心 +）", f"中心 px {s['pressCenter']}")]
        g = grid(tiles, 2, f"press: 成功の手前で離す  t={t:5.2f}s")
        d = ImageDraw.Draw(g)
        cx, cy = s["pressCenter"]
        ox, oy = FRAME_W + cx, 30 + FRAME_H + cy
        d.line([ox - 8, oy, ox + 8, oy], fill=(255, 80, 80), width=2)
        d.line([ox, oy - 8, ox, oy + 8], fill=(255, 80, 80), width=2)
        w.put(t, g)
    return w.close(6, 0.25)


def run_done(s: dict, out: Path, video: bool) -> list[Path]:
    """成功まで押す → 膨らみを 0.12 秒で畳む・シミを 0.25 秒で引く・人形を 0.4 秒覗かせる → 乱れ → ライブ。"""
    out_sec = float(s["cue"].get("markDoneRevealOutSec", 0.25))
    total = HOLD_SEC + MARK_DONE_SEC + 0.5
    spring = PressSpring()
    w = Writer(out / "done.png", 0.05, video)
    n = int(round(total * FPS)) + 1
    collapsed = False
    for i in range(n):
        t = i / FPS
        success = t >= HOLD_SEC
        if success and not collapsed:
            spring.collapse()
            collapsed = True
        held, prog = hold_script(t, None)
        p = spring.tick(0.0 if i == 0 else 1.0 / FPS, held and not success, prog)
        R = reveal_out(1.0, (t - HOLD_SEC) / out_sec) if success else 1.0
        stain = dict(mode="multiply", tex=s["ratio"], mask=s["cueMask"], reveal=s["reveal"], R=R, strength=1.0)
        press = dict(p=p, mask=s["pressMask"], center=s["pressCenter"], tex=s["bulge"])
        doll_on = success and t < HOLD_SEC + MARK_DONE_SEC
        doll = dict(tex=s["doll"], mask=s["dollMask"], strength=1.0 if doll_on else 0.0)
        after = success and not doll_on
        phase = "長押し" if not success else ("人形が覗く" if doll_on else "乱れ → ライブ（既存の乱れは再現しない）")
        frames = []
        for live, lab in ((s["live"], "無人"), (s["body"], "体の前（代用）")):
            if after:
                col = live
            else:
                col, _ = composite(live, stain, press, doll)
            frames.append(tile(col, f"{phase}・{lab}", f"t={t:4.2f}s  p={p:+.3f}  reveal={R:.3f}  人形={'表示' if doll_on else '-'}"))
        frames.append(tile(s["dollMask"][..., None].repeat(3, -1) * 0.6, "人形の周りのマスク（新）"))
        frames.append(tile(s["cueMask"][..., None].repeat(3, -1) * 0.6, "従来のマスク（幕の左半分）"))
        w.put(t, grid(frames, 2, f"done: 成功 → 人形 {MARK_DONE_SEC}s  t={t:5.2f}s"))
    return w.close(8, 0.2)


# ---------------------------------------------------------------- 本体

def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    rdir = Path(__file__).resolve().parent / "mask-recipes"
    ap.add_argument("--stain-recipe", default=str(rdir / "bleed-stain-a-20261113.json"))
    ap.add_argument("--hands-recipe", default=str(rdir / "bleed-hands-b-20261113.json"))
    ap.add_argument("--maps", default=str(ROOT / "Logs/gen-plate/bleed/out"))
    ap.add_argument("--out", default=str(ROOT / "Logs/gen-plate/bleed/preview"))
    ap.add_argument("--only", choices=["stain", "press", "hands", "done"], action="append")
    ap.add_argument("--no-video", action="store_true")
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    maps = Path(a.maps)
    want = set(a.only or ["stain", "press", "hands", "done"])
    video = not a.no_video
    if video and not shutil.which("ffmpeg"):
        print("ffmpeg が PATH に無い。PNG だけ出す")
    made: list[Path] = []
    if want & {"stain", "press", "done"}:
        st = load_set(Path(a.stain_recipe), maps)
        if "stain" in want:
            made += run_reveal(st, "stain", "stain: 2-A の幕のシミが滲んで現れる", out, video)
        if "press" in want:
            made += run_press(st, out, video)
        if "done" in want:
            made += run_done(st, out, video)
    if "hands" in want:
        hs = load_set(Path(a.hands_recipe), maps)
        made += run_reveal(hs, "hands", "hands: 1-B の壁に手形が現れる", out, video)
    for p in made:
        print(p)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
