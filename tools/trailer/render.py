"""予告動画を編集表（edl.json）どおりに組み立てる。

    py -3.11 tools/trailer/render.py                 # 全部組んで Drive へ置く
    py -3.11 tools/trailer/render.py --only 20,21    # その番号の区間だけ組み直す（他は前回の区間を使う）
    py -3.11 tools/trailer/render.py --no-publish    # Drive へ写さない

構成と時刻は旧版（edl.json の reference）のまま。体験の画は、ツインの通し走行（edl.json の run）の
目線の動画 eye.mp4 と、同じ走行で残したカメラ 3 台の生映像 cam1〜3.mp4（mjr2mp4.py が作る）から取る。
卓の画面は ui-shots.cjs が撮った Logs/trailer/ui/*.png。音は旧版の音をそのまま載せる。

出力（Logs/trailer/<日時>/）:
  <out_name>.mp4    出来上がり（1920x1080・60fps・旧版の音）
  compare.mp4       旧版（左）と新版（右）を並べたもの（音つき）
  pairs.jpg         区間ごとに旧版と新版の真ん中のコマを並べた一覧
  seg/NN.mp4        区間ごとの映像（--only で組み直すときに使い回す）
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
EDL = HERE / "edl.json"
RUNS = Path(r"G:\マイドライブ\shubie\renders\twin-verify")
DRIVE_OUT = Path(r"G:\マイドライブ\shubie\renders\trailer")
WORK = ROOT / "Logs" / "trailer"
UI = WORK / "ui"
ASSETS = ROOT / "Assets" / "StreamingAssets" / "show" / "assets"
FONT_PATH = Path(os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\Windows\Fonts\NotoSansJP-VariableFont_wght.ttf"))

W, H, FPS = 1920, 1080, 60
# 目線の動画（1920x1080・画角 40 度）でテレビが占める範囲。tv は 16:9 に寄せた切り抜き、tvbox は枠ぎりぎり
CROPS = {
    "full": None,
    "tv": (229, 262, 1440, 810),
    "tvbox": (365, 272, 1168, 784),
}
ENC = ["-c:v", "libx264", "-preset", "medium", "-crf", "16", "-pix_fmt", "yuv420p", "-r", str(FPS)]
# カメラごとの色（卓の床の図と周回タイムラインの色）
CAM_COLOR = {1: (63, 168, 120), 2: (66, 121, 190), 3: (196, 128, 64)}
CAM_NAME = {1: "A", 2: "B", 3: "C"}
ACCENT = (233, 197, 138)
SEG_LINE = re.compile(r"^tau=(?P<tau>-?[\d.]+) \[XP\]\s+t=[\d.]+\s+ev=seg\s+lap=(?P<lap>\d+)\s+cam=(?P<cam>\d+)")
SUM_LINE = re.compile(r"^tau=[\d.]+ \[XP\]\s+t=[\d.]+\s+ev=sum\b.*?worst=(?P<worst>\d+)")


# ------------------------------------------------------------------ 道具

def ff(args: list[str]) -> None:
    r = subprocess.run(["ffmpeg", "-v", "error", "-y", *args])
    if r.returncode != 0:
        raise SystemExit(f"ffmpeg が失敗した（exit {r.returncode}）: {' '.join(args)[:400]}")


def frames(t0: float, t1: float) -> int:
    """出来上がりの時刻をコマ番号で数える（区間の継ぎ目で 1 コマもずれないように）"""
    return round(t1 * FPS) - round(t0 * FPS)


def font(px: int, weight: str = "Medium") -> ImageFont.FreeTypeFont:
    if not FONT_PATH.exists():
        raise SystemExit(f"書体が無い: {FONT_PATH}（Noto Sans JP を入れる）")
    f = ImageFont.truetype(str(FONT_PATH), px)
    f.set_variation_by_name(weight.encode())
    return f


def grab(video: Path, t: float) -> Image.Image:
    r = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{t:.3f}", "-i", str(video), "-frames:v", "1",
                        "-f", "image2pipe", "-c:v", "png", "-"], capture_output=True)
    if r.returncode != 0 or not r.stdout:
        raise SystemExit(f"{video.name} の {t:.2f}s が取れない")
    from io import BytesIO
    return Image.open(BytesIO(r.stdout)).convert("RGB")


def crop_img(im: Image.Image, name: str) -> Image.Image:
    box = CROPS[name]
    if box is None:
        return im
    x, y, w, h = box
    return im.crop((x, y, x + w, y + h))


def crop_filter(name: str) -> str:
    box = CROPS[name]
    if box is None:
        return f"scale={W}:{H}"
    x, y, w, h = box
    return f"crop={w}:{h}:{x}:{y},scale={W}:{H}"


def fit(im: Image.Image, w: int, h: int, bg=(0, 0, 0)) -> Image.Image:
    """縦横比を保って w x h に収め、余りは bg で埋める（引き伸ばさない）"""
    s = min(w / im.width, h / im.height)
    r = im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.LANCZOS)
    out = Image.new("RGB", (w, h), bg)
    out.paste(r, ((w - r.width) // 2, (h - r.height) // 2))
    return out


def label_png(text: str, dest: Path) -> Path:
    """左下の演出名の札。旧版と同じ位置・大きさ（Noto Sans JP Medium 51px・左端・下から 14px）"""
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    f = font(51, "Medium")
    ImageDraw.Draw(shadow).text((4, 1066), text, font=f, fill=(0, 0, 0, 200), anchor="ls")
    shadow = shadow.filter(ImageFilter.GaussianBlur(4))
    im.alpha_composite(shadow)
    ImageDraw.Draw(im).text((2, 1064), text, font=f, fill=(255, 255, 255, 255), anchor="ls")
    im.save(dest)
    return dest


def luminance_to_alpha(im: Image.Image) -> Image.Image:
    """黒地に白で描かれた部品（旧版の矢印・字幕）を、白の透過画像にする"""
    g = im.convert("L")
    out = Image.new("RGBA", im.size, (255, 255, 255, 0))
    out.putalpha(g)
    return out


# ------------------------------------------------------------------ 素材

class Sources:
    def __init__(self, edl: dict, work: Path):
        self.edl = edl
        self.cache = WORK / "cache"
        self.cache.mkdir(parents=True, exist_ok=True)
        run = RUNS / edl["run"]
        if not run.is_dir():
            raise SystemExit(f"走行が無い: {run}（README の「素材を作る」）")
        self.ref = self.local(Path(edl["reference"]), "reference.mp4")
        self.eye = self.local(run / "eye.mp4", f"{edl['run']}_eye.mp4")
        self.cams = {i: self.local(run / f"cam{i}.mp4", f"{edl['run']}_cam{i}.mp4") for i in (1, 2, 3)}
        self.audio = self.cache / "reference_audio.wav"
        if not self.audio.exists():
            ff(["-i", str(self.ref), "-map", "0:a:0", "-c:a", "pcm_s16le", str(self.audio)])
        self.switches = self.read_switches(run / "replay_xp.log")
        self.work = work
        self.png = work / "png"
        self.png.mkdir(parents=True, exist_ok=True)

    def local(self, src: Path, name: str) -> Path:
        """Drive の素材は手元に写してから読む（G: を何度も seek すると遅い）"""
        if not src.exists():
            raise SystemExit(f"素材が無い: {src}")
        dst = self.cache / name
        if not dst.exists() or dst.stat().st_size != src.stat().st_size:
            print(f"  写す: {src.name} → cache/{name}")
            shutil.copyfile(src, dst)
        return dst

    @staticmethod
    def read_switches(log: Path) -> list[tuple[float, int]]:
        """区間の切り替わり（目線の動画の秒・カメラ番号 1〜3）。動画は時計より最初の詰まりの分だけ遅れて始まる"""
        lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
        lead = 0.0
        for ln in lines:
            m = SUM_LINE.match(ln)
            if m:
                worst = int(m["worst"])
                lead = worst / 1000 if worst >= 500 else 0.0
                break
        out = []
        for ln in lines:
            m = SEG_LINE.match(ln)
            if m:
                out.append((float(m["tau"]) - lead, int(m["cam"]) + 1))
        if not out:
            raise SystemExit(f"{log} に ev=seg が無い")
        return out

    def cam_at(self, t: float) -> int:
        cam = self.switches[0][1]
        for ts, c in self.switches:
            if ts <= t:
                cam = c
        return cam

    def ui(self, name: str) -> Image.Image:
        p = UI / f"{name}.png"
        if not p.exists():
            raise SystemExit(f"卓の画面が無い: {p}（卓を立てて node tools/trailer/ui-shots.cjs --out Logs/trailer/ui）")
        return Image.open(p).convert("RGB")

    def asset(self, name: str) -> Image.Image:
        p = ASSETS / name
        if not p.exists():
            raise SystemExit(f"同梱素材が無い: {p}")
        return Image.open(p).convert("RGB")


# ------------------------------------------------------------------ 区間

def seg_old(src: Sources, seg: dict, out: Path) -> None:
    t0, t1 = seg["t"]
    ff(["-ss", f"{t0:.4f}", "-i", str(src.ref), "-map", "0:v:0", "-vf", f"fps={FPS}",
        "-frames:v", str(frames(t0, t1)), *ENC, "-an", str(out)])


def eye_chain(seg: dict, dur: float) -> str:
    vf = []
    speed = float(seg.get("speed", 1.0))
    if speed != 1.0:
        vf.append(f"setpts=PTS/{speed}")
    vf += [f"fps={FPS}", crop_filter(seg.get("crop", "tv"))]
    if seg.get("fade_in"):
        vf.append(f"fade=t=in:st=0:d={seg['fade_in']}")
    if seg.get("fade_out"):
        vf.append(f"fade=t=out:st={dur - seg['fade_out']:.3f}:d={seg['fade_out']}")
    return ",".join(vf)


def run_graph(inputs: list[list[str]], graph: str, n: int, out: Path) -> None:
    args = []
    for i in inputs:
        args += i
    ff([*args, "-filter_complex", graph, "-map", "[out]", "-frames:v", str(n), *ENC, "-an", str(out)])


def still_input(p: Path, dur: float) -> list[str]:
    return ["-loop", "1", "-framerate", str(FPS), "-t", f"{dur + 0.5:.3f}", "-i", str(p)]


def seg_eye(src: Sources, seg: dict, out: Path) -> None:
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    inputs = [["-ss", f"{seg['src']:.3f}", "-i", str(src.eye)]]
    graph = f"[0:v]{eye_chain(seg, dur)}[v]"
    last = "v"
    if seg.get("label"):
        inputs.append(still_input(label_png(seg["label"], src.png / f"label_{t0:.3f}.png"), dur))
        graph += f";[{last}][1:v]overlay=0:0[out]"
    else:
        graph += f";[{last}]null[out]"
    run_graph(inputs, graph, n, out)


def read_frames(args: list[str], w: int, h: int):
    """ffmpeg が出すコマを 1 枚ずつ numpy の配列（h x w x 3）で返す"""
    p = subprocess.Popen(["ffmpeg", "-v", "error", *args, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    size = w * h * 3
    try:
        while True:
            buf = p.stdout.read(size)
            if len(buf) < size:
                break
            yield np.frombuffer(buf, np.uint8).reshape(h, w, 3)
    finally:
        p.stdout.close()
        p.kill()
        p.wait()


class Writer:
    """numpy の配列（H x W x 3）を受け取って区間の mp4 にする"""

    def __init__(self, out: Path):
        self.p = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
                                   "-r", str(FPS), "-i", "-", *ENC, "-an", str(out)], stdin=subprocess.PIPE)

    def write(self, arr: np.ndarray) -> None:
        self.p.stdin.write(np.ascontiguousarray(arr, np.uint8).tobytes())

    def close(self) -> None:
        self.p.stdin.close()
        if self.p.wait() != 0:
            raise SystemExit("ffmpeg（書き出し）が失敗した")


def old_eye_box(frame: np.ndarray) -> tuple[int, int, int, int] | None:
    """旧版の体験者視点の区間で、左に置かれた体験者の画の外接矩形。
    黒地の上で、上の札（y<150）と下の字幕（y>890）を除いた帯の、いちばん左の明るい塊を取る"""
    y_lo, y_hi = 150, 890
    band = frame[y_lo:y_hi, :1200].max(axis=2) > 14
    cols = band.sum(axis=0) > 4
    xs = np.flatnonzero(cols)
    if xs.size == 0:
        return None
    x0 = int(xs[0])
    gaps = np.flatnonzero(np.diff(xs) > 24)  # 塊と塊のあいだの黒
    x1 = int(xs[gaps[0]]) + 1 if gaps.size else int(xs[-1]) + 1
    if x1 - x0 < 40 or x1 > 1180:
        return None
    rows = np.flatnonzero(band[:, x0:x1].sum(axis=1) > 4)
    if rows.size == 0:
        return None
    return x0, y_lo + int(rows[0]), x1, y_lo + int(rows[-1]) + 1


def seg_pip(src: Sources, seg: dict, out: Path) -> None:
    """体験者視点（左）と三人称の実写（右下）。旧版のコマをそのまま土台にし、左の体験者の画だけを今の本編に替える。
    旧版の頭（42.4〜44.0）で三人称が中央から右下へ縮み、体験者の画が育つ動きも旧版のまま残る。
    新しい画は旧版の画の外接矩形を幅 scale 倍した 16:9 に置く（旧版は傾いた 4:3 だったので、そのままの幅だと小さい）"""
    t0, t1 = seg["t"]
    n = frames(t0, t1)
    scale = float(seg.get("eye_scale", 1.25))
    settle = float(seg.get("settle", 44.0)) - t0  # これより後は旧版の画が頭の動きで揺れるだけなので、置き場所を固定する
    ref_args = ["-ss", f"{t0:.4f}", "-i", str(src.ref), "-vf", f"fps={FPS}", "-frames:v", str(n)]
    boxes = []
    last = None
    for fr in read_frames(ref_args, W, H):
        b = old_eye_box(fr)
        last = b or last
        boxes.append(last)
    if len(boxes) != n or any(b is None for b in boxes):
        raise SystemExit(f"旧版の体験者の画が見つからないコマがある（{sum(b is None for b in boxes)}/{len(boxes)}）")
    arr = np.array(boxes, float)
    k = round(settle * FPS)
    hold = np.median(arr[k:], axis=0)
    smooth = arr.copy()
    for i in range(k):  # 育つ動きは 5 コマの移動平均でならす
        lo, hi = max(0, i - 2), min(k, i + 3)
        smooth[i] = arr[lo:hi].mean(axis=0)
    smooth[k:] = hold
    eye_args = ["-ss", f"{seg['src']:.3f}", "-i", str(src.eye), "-vf", f"fps={FPS},{crop_filter('tv').replace(f'scale={W}:{H}', 'scale=960:540')}",
                "-frames:v", str(n)]
    wr = Writer(out)
    for i, (fr, ef) in enumerate(zip(read_frames(ref_args, W, H), read_frames(eye_args, 960, 540))):
        canvas = fr.copy()
        bx0, by0, bx1, by1 = boxes[i]
        canvas[max(0, by0 - 8):by1 + 8, max(0, bx0 - 8):bx1 + 8] = 0  # 旧版の画を消す
        sx0, sy0, sx1, sy1 = smooth[i]
        cx, cy = (sx0 + sx1) / 2, (sy0 + sy1) / 2
        w = max(16, round((sx1 - sx0) * scale))
        h = round(w * 9 / 16)
        x, y = round(cx - w / 2), round(cy - h / 2)
        pic = np.asarray(Image.fromarray(ef).resize((w, h), Image.LANCZOS))
        x0c, y0c = max(0, x), max(150, y)
        x1c, y1c = min(1200, x + w), min(890, y + h)
        canvas[y0c:y1c, x0c:x1c] = pic[y0c - y:y1c - y, x0c - x:x1c - x]
        wr.write(canvas)
    wr.close()


def floor_map(src: Sources, active: int, size: int) -> Image.Image:
    """卓の床の図（floormap-hi）。いま映っているカメラの範囲だけ明るく、他は沈める"""
    hi = src.ui("floormap-hi")
    sc = hi.width / W
    m = np.asarray(hi.crop((0, 0, round(470 * sc), round(470 * sc)))).astype(np.int16)
    r, g, b = m[..., 0], m[..., 1], m[..., 2]
    lit = m.max(axis=2) >= 40
    zone = np.zeros(r.shape, np.int8)
    zone[lit & (g > r + 10) & (g > b)] = 1
    zone[lit & (b > r + 25) & (b > g)] = 2
    zone[lit & (r > b + 25) & (r >= g)] = 3
    dim = (zone > 0) & (zone != active)
    out = m.copy()
    out[dim] = out[dim] * 30 // 100
    return Image.fromarray(out.astype(np.uint8)).resize((size, size), Image.LANCZOS)


def seg_multicam(src: Sources, seg: dict, out: Path) -> None:
    """3 台の生映像（左の縦並び）・体験者に見えている映像（中央）・体験エリアの図（右）。
    いま映っているカメラの枠と図の範囲が、走行の区間の切り替わりと同じ時刻で移る"""
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    s0 = seg["src"]
    tile_w, tile_h, tile_x = 320, 240, 70
    tile_y = {1: 120, 2: 400, 3: 680}
    eye_x, eye_y, eye_w, eye_h = 460, 200, 1000, 562
    map_x, map_y, map_s = 1500, 200, 360
    base = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(base)
    small = font(26, "Medium")
    d.text((tile_x, tile_y[1] - 46), "3 台のカメラの映像", font=small, fill=(170, 170, 176))
    d.text((eye_x, eye_y - 46), "体験者に見えている映像", font=small, fill=(170, 170, 176))
    d.text((map_x, map_y - 46), "体験エリア（上から）", font=small, fill=(170, 170, 176))
    base_p = src.png / "multicam_base.png"
    base.save(base_p)
    # いま映っているカメラごとの上掛け（タイルの枠・札・床の図）
    states = []
    cams_in = [(max(s0, ts) - s0, c) for ts, c in src.switches if ts < s0 + dur]
    start_cam = src.cam_at(s0)
    seq = [(0.0, start_cam)] + [(t, c) for t, c in cams_in if t > 0]
    for i, (st, cam) in enumerate(seq):
        en = seq[i + 1][0] if i + 1 < len(seq) else dur + 1
        im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        dd = ImageDraw.Draw(im)
        for c in (1, 2, 3):
            y = tile_y[c]
            col = CAM_COLOR[c]
            dd.rectangle((tile_x, y - 2, tile_x + 10, y + 26), fill=col)
            name_col = (240, 240, 240) if c == cam else (130, 130, 136)
            dd.text((tile_x + 18, y - 4), f"カメラ {CAM_NAME[c]}", font=font(24, "SemiBold"), fill=name_col)
            if c == cam:
                dd.rectangle((tile_x - 5, y + 31, tile_x + tile_w + 4, y + 36 + tile_h + 4), outline=ACCENT, width=4)
                dd.text((tile_x + tile_w, y - 4), "表示中", font=font(22, "SemiBold"), fill=ACCENT, anchor="ra")
            else:
                dd.rectangle((tile_x, y + 36, tile_x + tile_w - 1, y + 36 + tile_h - 1), fill=(0, 0, 0, 120))
        dd.rectangle((eye_x - 3, eye_y - 3, eye_x + eye_w + 2, eye_y + eye_h + 2), outline=CAM_COLOR[cam], width=3)
        im.paste(floor_map(src, cam, map_s), (map_x, map_y))
        p = src.png / f"multicam_state{i}.png"
        im.save(p)
        states.append((st, en, p))
    inputs = [["-ss", f"{s0:.3f}", "-i", str(src.eye)]]
    inputs += [["-ss", f"{s0:.3f}", "-i", str(src.cams[c])] for c in (1, 2, 3)]
    inputs.append(still_input(base_p, dur))
    for _, _, p in states:
        inputs.append(still_input(p, dur))
    g = [f"color=0x0b0b0d:s={W}x{H}:r={FPS}[bg]",
         f"[0:v]fps={FPS},{crop_filter('tv')},scale={eye_w}:{eye_h}[eye]"]
    for k, c in enumerate((1, 2, 3)):
        g.append(f"[{k + 1}:v]fps={FPS},scale={tile_w}:{tile_h}[c{c}]")
    g.append(f"[bg][eye]overlay={eye_x}:{eye_y}:shortest=1[l0]")
    last = "l0"
    for c in (1, 2, 3):
        g.append(f"[{last}][c{c}]overlay={tile_x}:{tile_y[c] + 36}[l{c}]")
        last = f"l{c}"
    g.append(f"[{last}][4:v]overlay=0:0[lb]")
    last = "lb"
    for i, (st, en, _) in enumerate(states):
        g.append(f"[{last}][{5 + i}:v]overlay=0:0:enable='between(t,{st:.3f},{en:.3f})'[s{i}]")
        last = f"s{i}"
    # 字幕は旧版の同じ時刻から、明るさを透明度にして載せる（旧版の出し入れの間合いがそのまま残る）
    cx, cy, cw, ch = 520, 945, 860, 115
    inputs.append(["-ss", f"{t0:.4f}", "-i", str(src.ref)])
    k = len(inputs) - 1
    g.append(f"[{k}:v]fps={FPS},crop={cw}:{ch}:{cx}:{cy},format=gray[capa]")
    g.append(f"color=white:s={cw}x{ch}:r={FPS}[capw]")
    g.append("[capw][capa]alphamerge[cap]")
    g.append(f"[{last}][cap]overlay={cx}:{cy}[out]")
    run_graph(inputs, ";".join(g), n, out)


def seg_chain(src: Sources, seg: dict, out: Path) -> None:
    """① 監視カメラ風に見せる加工: カメラごとに 1 段で「カメラの生の映像 → 体験者に見える画」。
    旧版と同じく、上の段から順に「生 → 矢印 → 加工後」と現れる。矢印は旧版のもの"""
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    rows = seg["rows"]
    arrow = luminance_to_alpha(grab(src.ref, 66.0).crop((500, 132, 712, 186)))
    box = CROPS["tvbox"]
    raw_h = pro_h = 300 if len(rows) >= 3 else 420
    raw_w = raw_h * 4 // 3
    pro_w = round(pro_h * box[2] / box[3])
    gap = 250
    x0 = (W - (raw_w + gap + pro_w)) // 2
    top = (1000 - len(rows) * raw_h - (len(rows) - 1) * 24) // 2
    rows_y = [top + i * (raw_h + 24) for i in range(len(rows))]
    step = float(seg.get("reveal", 0.5))  # 段ごとの間合い（秒）
    arrow_p = src.png / "chain_arrow.png"
    arrow.save(arrow_p)
    label = label_png(seg["label"], src.png / f"label_{t0:.3f}.png")
    inputs, g = [], [f"color=black:s={W}x{H}:r={FPS}[bg]"]
    last = "bg"

    def appear(stream: str, at: float) -> str:
        return f"{stream},format=rgba,fade=t=in:st={at:.3f}:d=0.25:alpha=1"

    for i, row in enumerate(rows):
        y = rows_y[i]
        t_raw, t_arrow, t_pro = i * step, i * step + 0.2, i * step + 0.32
        inputs.append(["-ss", f"{row['src']:.3f}", "-i", str(src.cams[row["cam"]])])
        k_raw = len(inputs) - 1
        inputs.append(["-ss", f"{row['src']:.3f}", "-i", str(src.eye)])
        k_pro = len(inputs) - 1
        inputs.append(still_input(arrow_p, dur))
        k_arrow = len(inputs) - 1
        bx, by, bw, bh = box
        g.append(appear(f"[{k_raw}:v]fps={FPS},scale={raw_w}:{raw_h}", t_raw) + f"[r{i}]")
        g.append(appear(f"[{k_pro}:v]fps={FPS},crop={bw}:{bh}:{bx}:{by},scale={pro_w}:{pro_h}", t_pro) + f"[p{i}]")
        g.append(appear(f"[{k_arrow}:v]fps={FPS}", t_arrow) + f"[ar{i}]")
        ax = x0 + raw_w + (gap - arrow.width) // 2
        g.append(f"[{last}][r{i}]overlay={x0}:{y}:shortest=1[a{i}]")
        g.append(f"[a{i}][ar{i}]overlay={ax}:{y + raw_h // 2 - arrow.height // 2}[m{i}]")
        g.append(f"[m{i}][p{i}]overlay={x0 + raw_w + gap}:{y}[b{i}]")
        last = f"b{i}"
    inputs.append(still_input(label, dur))
    g.append(f"[{last}][{len(inputs) - 1}:v]overlay=0:0[out]")
    run_graph(inputs, ";".join(g), n, out)


def seg_black(src: Sources, seg: dict, out: Path) -> None:
    """黒（旧版で次の場面の前に置かれた間）"""
    t0, t1 = seg["t"]
    run_graph([], f"color=black:s={W}x{H}:r={FPS}[out]", frames(t0, t1), out)


def gen_ai_image(src: Sources, ai) -> Image.Image:
    if isinstance(ai, str):
        return src.asset(ai)
    # 比の画像（AI の出した画 ÷ 無人写真）を無人写真に掛け戻して、AI が出した画を見せる
    from PIL import ImageChops
    plate = src.asset(ai["plate"])
    ratio = src.asset(ai["ratio"]).resize(plate.size, Image.LANCZOS)
    return ImageChops.multiply(plate, ratio)


def seg_gen(src: Sources, seg: dict, out: Path) -> None:
    """② 生成された世界の演出: 旧版と同じ「① 種 → ② AI が出したもの → ③ 体験者に届く画」の 2 段"""
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    im = Image.new("RGB", (W, H), (8, 8, 9))
    d = ImageDraw.Draw(im)
    panel_bg = (22, 22, 24)
    title_f, head_f = font(21, "Bold"), font(15, "Bold")
    origins = [(0, 0), (231, 503)]
    cols = [("① 種（AI へ渡した入力）", 493), ("② AI が出したもの", 493), ("③ 体験者に届く画", 657)]
    for (ox, oy), panel in zip(origins, seg["panels"]):
        pw = 1688 if ox == 0 else W - ox
        d.rectangle((ox, oy, ox + pw - 1, oy + 445), fill=panel_bg)
        d.text((ox + 12, oy + 26), panel["title"], font=title_f, fill=(236, 236, 236), anchor="ls")
        x = ox + 12
        imgs = [src.asset(panel["seed"]), gen_ai_image(src, panel["ai"]),
                crop_img(grab(src.eye, panel["eye"]), "tvbox")]
        for (head, cw), pic in zip(cols, imgs):
            d.text((x, oy + 52), head, font=head_f, fill=(214, 214, 214), anchor="ls")
            im.paste(fit(pic, cw, 370), (x, oy + 58))
            d.rectangle((x - 1, oy + 57, x + cw, oy + 58 + 370), outline=(60, 60, 64))
            x += cw + 11
    still = src.png / f"gen_{t0:.3f}.png"
    im.save(still)
    label = label_png(seg["label"], src.png / f"label_{t0:.3f}.png")
    run_graph([still_input(still, dur), still_input(label, dur)], "[0:v][1:v]overlay=0:0,fps=60[out]", n, out)


def seg_tvpip(src: Sources, seg: dict, out: Path) -> None:
    """③④: 体験者に見える画の右上に、旧版の同じ時刻の三人称の小窓を置く"""
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    x, y, w, h = seg["tp_box"]
    label = label_png(seg["label"], src.png / f"label_{t0:.3f}.png")
    inputs = [["-ss", f"{seg['src']:.3f}", "-i", str(src.eye)],
              ["-ss", f"{t0:.3f}", "-i", str(src.ref)],
              still_input(label, dur)]
    graph = (f"[0:v]{eye_chain({'crop': 'tv'}, dur)}[e];"
             f"[1:v]fps={FPS},crop={w}:{h}:{x}:{y}[tp];"
             f"[e][tp]overlay={x}:{y}[a];[a][2:v]overlay=0:0[out]")
    run_graph(inputs, graph, n, out)


def seg_mosaic(src: Sources, seg: dict, out: Path) -> None:
    """⑧ 卓の画面 4 枚を白い線で区切って並べる（旧版の⑧と同じ組み方）"""
    t0, t1 = seg["t"]
    n, dur = frames(t0, t1), t1 - t0
    shots = seg["shots"]
    im = Image.new("RGB", (W, H), (245, 245, 245))
    cw, ch, gap = (W - 6) // 2, (H - 6) // 2, 6
    cells = [(0, 0), (cw + gap, 0), (0, ch + gap), (cw + gap, ch + gap)]
    for (x, y), name in zip(cells, shots):
        if name.startswith("floormap"):
            # 床の図は画面の左上に小さく描かれるので、高い画素密度で撮った版の図の部分だけを使う
            hi = src.ui("floormap-hi")
            sc = hi.width / W
            pic = hi.crop((0, 0, round(470 * sc), round(470 * sc)))
            bg = hi.getpixel((hi.width - 4, hi.height - 4))
            pic = fit(pic, cw, ch, bg)
        else:
            pic = src.ui(name)
            pic = pic.crop((0, 0, pic.width, round(pic.width * ch / cw))).resize((cw, ch), Image.LANCZOS)
        im.paste(pic, (x, y))
    im = im.convert("RGBA")
    im.alpha_composite(Image.open(label_png(seg["label"], src.png / f"label_{t0:.3f}.png")))
    still = src.png / "mosaic.png"
    im.convert("RGB").save(still)
    # 旧版の⑧も止め絵だった（94.6 と 98.3 のコマの差は 30 階調未満）
    run_graph([still_input(still, dur)], f"[0:v]fps={FPS}[out]", n, out)


KINDS = {"old": seg_old, "eye": seg_eye, "pip": seg_pip, "multicam": seg_multicam, "chain": seg_chain,
         "gen": seg_gen, "tvpip": seg_tvpip, "mosaic": seg_mosaic, "black": seg_black}


# ------------------------------------------------------------------ 仕上げ

def check_edl(edl: dict) -> None:
    segs = edl["segments"]
    for i, s in enumerate(segs):
        if s["kind"] not in KINDS:
            raise SystemExit(f"区間 {i}: kind={s['kind']} を知らない")
        if i and abs(segs[i - 1]["t"][1] - s["t"][0]) > 1e-6:
            raise SystemExit(f"区間 {i}: 前の区間の終わり {segs[i - 1]['t'][1]} と始まり {s['t'][0]} が繋がっていない")
    if segs[0]["t"][0] != 0:
        raise SystemExit("最初の区間が 0 秒から始まっていない")


def pairs_sheet(src: Sources, edl: dict, final: Path, dest: Path) -> None:
    cells = []
    f = font(20, "Medium")
    for i, s in enumerate(edl["segments"]):
        t = (s["t"][0] + s["t"][1]) / 2
        a = grab(src.ref, t).resize((480, 270))
        b = grab(final, t).resize((480, 270))
        row = Image.new("RGB", (960, 300), (24, 24, 26))
        row.paste(a, (0, 30))
        row.paste(b, (480, 30))
        note = s.get("label") or s.get("note", "")
        ImageDraw.Draw(row).text((6, 4), f"{i:02d}  {s['t'][0]:.2f}–{s['t'][1]:.2f}  {s['kind']}  {note}", font=f, fill=(255, 220, 0))
        cells.append(row)
    cols = 2
    rows = (len(cells) + cols - 1) // cols
    sheet = Image.new("RGB", (960 * cols, 300 * rows), (0, 0, 0))
    for i, c in enumerate(cells):
        sheet.paste(c, ((i % cols) * 960, (i // cols) * 300))
    sheet.save(dest, quality=88)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="この番号の区間だけ組み直す（カンマ区切り。他は前回の seg/ を使う）")
    ap.add_argument("--work", default="", help="--only で使う前回の出力フォルダ（既定は最新）")
    ap.add_argument("--no-publish", action="store_true", help="Drive へ写さない")
    a = ap.parse_args()
    edl = json.loads(EDL.read_text(encoding="utf-8"))
    check_edl(edl)
    only = {int(x) for x in a.only.split(",") if x.strip()}
    if only:
        prev = Path(a.work) if a.work else max((d for d in WORK.iterdir() if d.is_dir() and (d / "seg").is_dir()), key=lambda d: d.name)
        work = prev
        print(f"前回の出力を使い回す: {work}")
    else:
        work = WORK / time.strftime("%Y%m%d_%H%M%S")
    (work / "seg").mkdir(parents=True, exist_ok=True)
    src = Sources(edl, work)
    segs = edl["segments"]
    started = time.time()
    for i, s in enumerate(segs):
        out = work / "seg" / f"{i:02d}.mp4"
        if only and i not in only and out.exists():
            continue
        t = time.time()
        KINDS[s["kind"]](src, s, out)
        got = int(subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries",
                                  "stream=nb_read_frames", "-of", "csv=p=0", str(out)], capture_output=True, text=True).stdout.strip() or 0)
        want = frames(*s["t"])
        flag = "" if got == want else f"  ⚠ コマ数 {got} ≠ {want}"
        print(f"  {i:02d} {s['kind']:8s} {s['t'][0]:7.3f}–{s['t'][1]:7.3f} {time.time() - t:5.1f}s  {s.get('label') or s.get('note', '')}{flag}")
        if got != want:
            raise SystemExit(f"区間 {i} のコマ数が合わない（{got} ≠ {want}）")
    lst = work / "concat.txt"
    lst.write_text("".join(f"file 'seg/{i:02d}.mp4'\n" for i in range(len(segs))), encoding="utf-8")
    video = work / "video.mp4"
    ff(["-f", "concat", "-safe", "0", "-i", str(lst), "-c", "copy", str(video)])
    final = work / f"{edl['out_name']}.mp4"
    # -shortest は付けない（旧版の音は 103.1996 秒で、映像の最後の 1 コマが落ちる）
    ff(["-i", str(video), "-i", str(src.audio), "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "256k",
        "-movflags", "+faststart", str(final)])
    cmp_ = work / "compare.mp4"
    tags = Image.new("RGBA", (1920, 540), (0, 0, 0, 0))
    td = ImageDraw.Draw(tags)
    for x, text in ((12, "旧版 v3-3"), (972, "新版")):
        td.rectangle((x - 6, 8, x + 150, 48), fill=(0, 0, 0, 170))
        td.text((x, 12), text, font=font(28, "Bold"), fill=(255, 220, 0))
    tags_p = work / "png" / "compare_tags.png"
    tags.save(tags_p)
    ff(["-i", str(src.ref), "-i", str(final), "-loop", "1", "-i", str(tags_p), "-filter_complex",
        "[0:v]scale=960:540,fps=30[a];[1:v]scale=960:540,fps=30[b];[a][b]hstack[h];[h][2:v]overlay=0:0:shortest=1[out]",
        "-map", "[out]", "-map", "1:a:0", "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", str(cmp_)])
    pairs_sheet(src, edl, final, work / "pairs.jpg")
    print(f"できた: {final}（{time.time() - started:.0f}s）")
    if not a.no_publish:
        dest = DRIVE_OUT / work.name
        dest.mkdir(parents=True, exist_ok=True)
        for p in (final, cmp_, work / "pairs.jpg"):
            shutil.copyfile(p, dest / p.name)
        shutil.copyfile(EDL, dest / "edl.json")
        for extra in edl.get("publish", []):
            shutil.copyfile(final, extra)
        print(f"Drive へ置いた: {dest}" + "".join(f" / {e}" for e in edl.get("publish", [])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
