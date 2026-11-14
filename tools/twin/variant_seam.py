"""「1 周目 A で、幕の合わせ目が開く」版を作り、検証用の木へ当てる（本番の台本と APK には入らない）。

1 周目 A で、体験者の体が L の角（幕の合わせ目）の前を抜けた後、合わせ目が手のひら幅だけ開く。
1 秒かけて合わせ目の中心から左右へ広がり、区間を出るまで開いたまま。報告では消えない。
隙間の奥は現実の部屋の壁ではなく暖かい黄色の灯りで、灯りは縁の襞と床へ少し漏れる。開くきっかけは床の線。

使い方:
  py -3.11 tools/twin/variant_seam.py make       # ツインで 3 枚焼いて素材を作る → tools/twin/assets/twin_seamA_*.png
  py -3.11 tools/twin/variant_seam.py check      # 束の 1 周目 A のコマへ重ねて、前後の画と体との重なりを出す
  py -3.11 tools/twin-tree.py sync
  py -3.11 tools/twin/variant_seam.py apply --root ../fcv-twin-tree
  py -3.11 tools/twin-tree.py run -- --fast --calib twin --tag seam-after

make: Blender で固定カメラ A の静止画を 3 枚焼く（閉じた合わせ目・開いた合わせ目・開いて灯りあり。
  twin_scene.py の TWIN_SEAM_GAP / TWIN_SEAM_GLOW）。差分を束の無人写真（seq の plate.npy）の線形の値へ足して、
  束と同じ仕上げ（twin_post の phone・plate の露出・seed 1）で素材にする。階調（feed_tone）は make_assets.py が掛ける。
  - 素材の画: 640x480。マスクの外は束の無人写真そのもの
  - マスク: 枠空間 640x360。灯りで変わった画素（開いた・灯りあり と 開いた・灯りなし の差）を少し広げてぼかし、
    合わせ目の線分から MASK_R1 px より遠い所は 0。開いた幕と閉じた幕の差は数えない（下の ⚠）
  - 順の画: 枠空間 640x360。合わせ目の中心線からの水平距離が小さいほど先に出る。入りの曲線 1-(1-t)^2.2 は
    頭が速いので、隙間の幅の割合 f の所が t≒f 秒で半分出るよう v = 1.12·ease(f) - 0.06 で逆に曲げる（上限 0.95）。
    隙間の外の幕の縁（〜0.97）と、隙間の下の床へ漏れた灯り（〜0.98）は最後。外は 1。見えて開き切るのは 0.8 秒ほど
  ⚠ 合わせ目を開くと twin_scene.py の _curtain が幕の襞を全部並べ直す（幕の長さが変わり、襞の切れ目の乱数が
    幕の端から振り直される）。閉じた画と開いた画の差は幕全体に出るので、それをマスクにすると幕が丸ごと差し替わる。
    ここでは灯りの差だけを拾い、マスクの縁で襞が数 px だけ食い違うのを受け入れている
apply: 木の assets.json と焼いた show.json へ cue・線・take を足し、manifest.json の configSha256 を合わせる。
  素材 3 枚は木の tools/twin/assets/ へ写す。こちらの木（メイン）には書かない。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
MAIN = HERE.parents[1]
sys.path.insert(0, str(HERE))
import feed_tone  # noqa: E402
import stage_map  # noqa: E402
import twin_post  # noqa: E402

BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
W, H = 640, 480                 # 映像（4:3）
FW, FH = 640, 360               # マスクと順の画の枠（16:9）
ASSETS = HERE / "assets"
SRC_NAME = "twin_seamA_open.png"
MASK_NAME = "twin_seamA_mask.png"
REVEAL_NAME = "twin_seamA_reveal.png"
PLATE_NPY = MAIN / "Logs/twin-cycles/seq/forward/A/plate.npy"   # 束の A の無人写真（seq の背景・線形）
PREVIEW = MAIN / "reports/2026-11-14_seam-preview.jpg"

GAP_M = float(os.environ.get("SEAM_GAP_M", "0.05"))   # 片側の引き幅（m）。既定は手のひら幅（両側で 10cm）
RENDER_W, RENDER_SPP = 640, 192
LIGHT_THR = 18                  # 灯りで変わったとみなす差（8 bit・仕上げ後）
MASK_GROW = 5                   # 変わった画素を広げる半径（px）
MASK_R0, MASK_R1 = 36, 72       # 合わせ目の線分からこの距離（px）で、マスクを 1 → 0 へ落とす
MASK_SOFT = 3.0                 # マスクの縁のぼかし（px・σ）
REVEAL_GAP = 0.95               # 隙間の幅いっぱいが出る順
REVEAL_EDGE = 0.97              # 隙間の外の、灯りを受けた幕の縁が出る順
REVEAL_FLOOR = 0.98             # 床へ漏れた灯りのいちばん遠い所が出る順（出切った後の息 0.985 でも全部出ている上限）
REVEAL_SEC = 1.0
REVEAL_OUT_SEC = 0.45           # 区間を出て畳むときの引き（OverlayRevealLogic.DefaultOutSec と同じ）
SOFT = 0.12                     # OverlayRevealLogic.DefaultSoft（試し描き用の写し）
BREATH_MIN = 0.985              # OverlayRevealLogic.BreathMin（出切った後の息の底）

# 線: 体が合わせ目の前を抜けた直後に横切る所。1 周目 A の道（手すりから 0.24m・x=-0.26 を -z へ歩く）に直交する横線。
# 角の二等分線 line_center_A を越えても（道の上では z=0.26）、体は近づいて大きくなるので合わせ目を隠し続け、
# 2 周目 A の背の線 trail_A_behind（z=-0.2）でもまだ合わせ目のマスクの 2.6% に掛かる。束の 1 周目 A で
# 体とマスクの重なりが 0 になるのは z=-0.29（区間の進み 0.64）。少し余裕を取って z=-0.35（進み 0.67）に引く。
# 幅は w2 の幕（x=-0.5）から x=0.1 まで。道を幕から離れて歩く人も拾う。数え方は check の表
SEAM_LINE_Z = -0.35
SEAM_LINE = {"id": "seam_A_clear", "camera": 0, "x1": -0.5, "z1": SEAM_LINE_Z, "x2": 0.1, "z2": SEAM_LINE_Z,
             "dir": "both", "label": "1周目A 体が合わせ目の前を抜けた所（ここで合わせ目が開く）", "mirrorOf": ""}
CUE_ID = "seam_A_open"
TAKE_ID = "L1C0#0"


def imwrite(p: Path, img) -> None:
    ok, buf = cv2.imencode(p.suffix, img)
    if not ok:
        sys.exit(f"書けない: {p}")
    p.parent.mkdir(parents=True, exist_ok=True)
    buf.tofile(str(p))


def srgb_to_lin(a: np.ndarray) -> np.ndarray:
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(a: np.ndarray) -> np.ndarray:
    a = np.clip(a, 0.0, 1.0)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * np.power(a, 1 / 2.4) - 0.055)


def save_data(a: np.ndarray, p: Path) -> None:
    """データの画（マスク・順の画）は sRGB で符号化して書く。実機は sRGB として読み、線形へ戻した値を使う。"""
    v = np.round(lin_to_srgb(a) * 255.0).astype(np.uint8)
    imwrite(p, cv2.merge([v, v, v]))


def load_data(p: Path) -> np.ndarray:
    """save_data の逆（実機がシェーダで読む値）。"""
    m = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_GRAYSCALE)
    if m is None:
        sys.exit(f"読めない: {p}")
    return srgb_to_lin(m.astype(np.float32) / 255.0)


def to_frame(a: np.ndarray, outside: float) -> np.ndarray:
    """映像の座標（640x480）の値を、枠空間（640x360）へ置く。映像は枠の中央に contain-fit で入る。"""
    sw = int(round(FH * W / H))
    off = (FW - sw) // 2
    out = np.full((FH, FW), outside, np.float32)
    out[:, off:off + sw] = cv2.resize(a.astype(np.float32), (sw, FH), interpolation=cv2.INTER_AREA)
    return out


def from_frame(a: np.ndarray) -> np.ndarray:
    """枠空間の値を映像の座標へ切り出す（to_frame の逆）。"""
    sw = int(round(FH * W / H))
    off = (FW - sw) // 2
    return cv2.resize(a[:, off:off + sw], (W, H), interpolation=cv2.INTER_LINEAR)


# ---- make ---------------------------------------------------------------------------------------------

def render(tag: str, gap: float, glow: bool) -> Path:
    """固定カメラ A の静止画を 1 枚焼き、線形の値（.npy）があるフォルダを返す。束の feeds は書き換えない。"""
    env = dict(os.environ, TWIN_SEAM_GAP=str(gap), TWIN_SEAM_GLOW="1" if glow else "")
    p = subprocess.run([BLENDER, "--background", "--factory-startup", "--python-exit-code", "1", "--python",
                        str(HERE / "twin_render.py"), "--", "still", "--tag", tag, "--shots", "A",
                        "--w", str(RENDER_W), "--spp", str(RENDER_SPP)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    m = re.search(r"^\[twin\] EXR (.+)$", p.stdout, re.M)
    if p.returncode != 0 or not m:
        print(p.stdout[-3000:])
        print(p.stderr[-3000:])
        sys.exit(f"焼けない: {tag}")
    d = Path(m.group(1).strip())
    print(f"[seam] {tag}: {d}")
    return d


def load_lin(d: Path) -> np.ndarray:
    a = np.load(d / "A.npy").astype(np.float32)
    if a.shape != (H, W, 3):
        sys.exit(f"大きさが違う: {d} {a.shape}（{W}x{H} のはず）")
    return a


def finish(lin: np.ndarray, exposure: float) -> np.ndarray:
    """束と同じ仕上げ（twin_post の phone・露出は束の無人写真から・seed 1）。RGB の uint8 を返す。"""
    return twin_post.process(lin, "phone", seed=1, exposure=exposure)[..., ::-1].copy()


def seam_line(light: np.ndarray) -> tuple[np.ndarray, int, int]:
    """灯りの差から、隙間の中心線（行ごとの x）と縦の範囲を出す。中心線は行ごとの重心を直線で当てはめる。"""
    band = slice(W // 2 - 80, W // 2 + 80)
    ys, cs = [], []
    for y in range(H):
        xs = np.nonzero(light[y, band] > 60)[0]
        if 3 <= len(xs) <= 40:
            ys.append(y)
            cs.append(float(xs.mean()) + band.start)
    if len(ys) < 40:
        sys.exit(f"隙間が見つからない（灯りの差が 60 を超える行が {len(ys)} 行）")
    a, b = np.polyfit(np.array(ys, np.float32), np.array(cs, np.float32), 1)
    xs_line = a * np.arange(H, dtype=np.float32) + b
    return xs_line, int(min(ys)), int(max(ys))


def distance_to_seam(xs_line: np.ndarray, y0: int, y1: int) -> tuple[np.ndarray, np.ndarray]:
    """各画素から合わせ目の線分までの距離（px）と、隙間の下端より下の床か（bool）。"""
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    yc = np.clip(yy, y0, y1)
    xc = xs_line[yc.astype(int)]
    d = np.hypot(xx - xc, yy - yc)
    return d, yy > y1


def make(a) -> int:
    if not PLATE_NPY.is_file():
        sys.exit(f"束の無人写真が無い: {PLATE_NPY}（twin_render.py seq で A を焼く）")
    if a.reuse:
        dirs = [Path(p) for p in a.reuse.split(",")]
        if len(dirs) != 3:
            sys.exit("--reuse は 閉じた,開いた灯りなし,開いた灯りあり の 3 つ")
    else:
        dirs = [render("seamA-closed", 0.0, False), render("seamA-open-dark", GAP_M, False), render("seamA-open-glow", GAP_M, True)]
    closed, dark, glow = (load_lin(d) for d in dirs)
    plate = np.load(PLATE_NPY).astype(np.float32)
    if plate.shape != (H, W, 3):
        sys.exit(f"束の無人写真の大きさが違う: {plate.shape}")
    exposure = twin_post.auto_exposure(plate, twin_post.LOOKS["phone"]["key"])

    # 灯りの差（同じ seed で仕上げるので粒は消し合う）。開いた幕と閉じた幕の差は使わない（冒頭の ⚠）
    f_dark, f_glow = finish(dark, exposure), finish(glow, exposure)
    light = np.abs(f_glow.astype(np.int16) - f_dark.astype(np.int16)).max(axis=2).astype(np.float32)
    xs_line, y0, y1 = seam_line(light)
    d, below = distance_to_seam(xs_line, y0, y1)
    win = np.clip((MASK_R1 - d) / (MASK_R1 - MASK_R0), 0.0, 1.0)

    changed = (light > LIGHT_THR).astype(np.uint8)
    changed = cv2.morphologyEx(changed, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    changed &= (win > 0).astype(np.uint8)
    # 合わせ目に繋がった塊だけ残す（遠くの粒の差を拾わない）
    n, lab, _, _ = cv2.connectedComponentsWithStats(changed, 8)
    keep = {int(v) for v in np.unique(lab[(d < 4) & (changed > 0)]) if v}
    changed = np.isin(lab, list(keep)).astype(np.uint8)
    if not changed.any():
        sys.exit("合わせ目に繋がる灯りの差が無い（TWIN_SEAM_GLOW が効いていない）")
    k = MASK_GROW * 2 + 1
    core = cv2.dilate(changed, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))).astype(np.float32)
    mask = np.clip(cv2.GaussianBlur(core, (0, 0), MASK_SOFT), 0, 1) * win

    # 素材の画: 束の無人写真へ、灯りあり と 閉じた の差を足す（マスクの外は無人写真のまま）
    wide = np.clip(cv2.GaussianBlur(cv2.dilate(core, np.ones((25, 25), np.uint8)), (0, 0), 6.0), 0, 1)
    src = finish(np.maximum(plate + wide[..., None] * (glow - closed), 0.0), exposure)

    # 順の画: 隙間の中は中心線からの水平距離の割合 f で、t≒f 秒に出る形。隙間の外の縁と下の床へ漏れた灯りは最後
    half = float(np.percentile(np.abs(np.arange(W)[None, :] - xs_line[y0:y1 + 1, None])[light[y0:y1 + 1] > 60], 95)) + 2.0
    f = np.clip(d / half, 0.0, 1.0)
    gap_v = np.clip((1.0 + SOFT) * (1.0 - (1.0 - f) ** 2.2) - SOFT / 2, 0.0, REVEAL_GAP)
    edge_v = REVEAL_GAP + (REVEAL_EDGE - REVEAL_GAP) * np.clip((d - half) / (MASK_R1 - half), 0, 1)
    v = np.where(d <= half, gap_v, edge_v)
    v = np.where(below & (d > half), REVEAL_GAP + (REVEAL_FLOOR - REVEAL_GAP) * np.clip(d / MASK_R1, 0, 1), v)
    v = np.where(mask > 1e-3, v, 1.0)

    ASSETS.mkdir(parents=True, exist_ok=True)
    imwrite(ASSETS / SRC_NAME, src[..., ::-1])
    save_data(to_frame(mask, 0.0), ASSETS / MASK_NAME)
    save_data(to_frame(v, 1.0), ASSETS / REVEAL_NAME)
    gap_px = light[y0:y1 + 1] > 60
    gap_col = f_glow[y0:y1 + 1][gap_px & (np.abs(np.arange(W)[None, :] - xs_line[y0:y1 + 1, None]) < 3)]
    print(f"[seam] 露出 {exposure:.2f} / 合わせ目 x={xs_line[y0]:.1f}→{xs_line[y1]:.1f} y={y0}..{y1} / 隙間の半幅 {half:.1f}px")
    print(f"[seam] マスク: 画の {float((mask > 0.5).mean()) * 100:.2f}%（0.5 以上）・{float((mask > 1e-3).mean()) * 100:.2f}%（0 でない）")
    print(f"[seam] 隙間の芯の色（仕上げ後・階調前）中央値 {np.median(gap_col, 0).astype(int).tolist()}"
          f" / 階調後 {feed_tone.lut_for('A')[np.median(gap_col, 0).astype(int)].tolist()}")
    for name in (SRC_NAME, MASK_NAME, REVEAL_NAME):
        print(f"[seam] → {ASSETS / name}")
    return 0


# ---- check --------------------------------------------------------------------------------------------

def reveal_alpha(v: np.ndarray, r: float) -> np.ndarray:
    """シェーダの a = saturate((reveal*(1+soft) - v) / soft)（CrtCompositeShared.hlsl）。"""
    return np.clip((r * (1 + SOFT) - v) / SOFT, 0.0, 1.0)


def ease_in(t: float) -> float:
    """OverlayRevealLogic の入りの曲線 1-(1-t)^2.2。"""
    t = min(1.0, max(0.0, t))
    return 1.0 - (1.0 - t) ** 2.2


def composite(live: np.ndarray, src: np.ndarray, mask: np.ndarray, v: np.ndarray, r: float) -> np.ndarray:
    """差し替えの合成（線形で lerp。前線の帯は 0.30 だけ暗く）。live / src は階調後の RGB uint8。"""
    a = reveal_alpha(v, r)
    edge = a * (1 - a) * 4.0
    lv = srgb_to_lin(live.astype(np.float32) / 255.0)
    ov = srgb_to_lin(src.astype(np.float32) / 255.0) * (1.0 - 0.30 * edge)[..., None]
    alpha = np.clip(mask * a, 0, 1)[..., None]
    return np.round(lin_to_srgb(lv + (ov - lv) * alpha) * 255.0).astype(np.uint8)


def read_video(path: Path, count: int) -> list:
    """動画の頭から count コマを RGB で読む（cv2 は日本語のパスを開けないので ffmpeg から）。"""
    p = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-frames:v", str(count), "-f", "rawvideo",
                        "-pix_fmt", "rgb24", "-"], capture_output=True, check=True)
    n = len(p.stdout) // (W * H * 3)
    return [np.frombuffer(p.stdout, np.uint8, W * H * 3, i * W * H * 3).reshape(H, W, 3) for i in range(n)]


def label(img: np.ndarray, text: str) -> np.ndarray:
    out = img.copy()
    cv2.rectangle(out, (0, 0), (W, 26), (0, 0, 0), -1)
    cv2.putText(out, text, (8, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)
    return out


def check(a) -> int:
    for p in (ASSETS / SRC_NAME, ASSETS / MASK_NAME, ASSETS / REVEAL_NAME, stage_map.PACK, stage_map.GAME / "A.mp4"):
        if not p.exists():
            sys.exit(f"無い: {p}")
    pack = json.loads(stage_map.PACK.read_text(encoding="utf-8"))
    xs, zs, fps = pack["xs"], pack["zs"], float(pack["fps"])
    grid = json.loads((MAIN / "Assets/StreamingAssets/show/show.json").read_text(encoding="utf-8-sig"))["layout"]["grid"]
    zones = [stage_map.zone_at(x, z, grid) for x, z in zip(xs, zs)]
    # 1 周目 A = 歩き出しから、最初に A を出るまで
    end = next(i for i, zz in enumerate(zones) if zz != 0)
    seg = list(range(end))
    rows = [{"x": xs[i], "z": zs[i]} for i in seg]
    hit = stage_map.crossing_index(rows, SEAM_LINE)
    center = stage_map.crossing_index(rows, {"x1": -0.5, "z1": 0.5, "x2": 0.9, "z2": -0.9})
    if not hit:
        sys.exit("1 周目 A の道が線 seam_A_clear を横切らない")
    k0 = hit[0]
    frames = read_video(stage_map.GAME / "A.mp4", end)
    plate = stage_map.imread(stage_map.GAME / "A_plate.jpg")[..., ::-1]
    mask = from_frame(load_data(ASSETS / MASK_NAME))
    v = from_frame(load_data(ASSETS / REVEAL_NAME))
    src = np.asarray(feed_tone.apply(Image.open(ASSETS / SRC_NAME).convert("RGB"), "A"))
    mbin = mask > 0.5

    print(f"[seam] 1 周目 A: コマ 0..{end - 1}（{end / fps:.1f} 秒・束の速さ）/ 角の二等分線 {center[:1]} / "
          f"seam_A_clear {k0}（進み {k0 / max(1, end - 1):.2f}・床 x={xs[k0]:.2f} z={zs[k0]:.2f}）")
    print("[seam] コマ | 体の画素 | マスク(0.5 以上)と重なる割合 | マスクの重み付きの割合")
    shares, wshares = [], []
    for i in range(max(0, (center[0] if center else k0) - 2), end):
        body = stage_map.person_mask(frames[i][..., ::-1], plate[..., ::-1]).astype(bool)
        area = int(body.sum())
        s = float((body & mbin).sum()) / area if area else 0.0
        ws = float(mask[body].sum()) / area if area else 0.0
        mark = " ← 線" if i == k0 else ""
        if i >= k0:
            shares.append(s)
            wshares.append(ws)
        if i <= k0 + 20 or i % 5 == 0:
            print(f"[seam] {i:4d} | {area:6d} | {s * 100:5.1f}% | {ws * 100:5.1f}%{mark}")
    after = np.array(shares)
    print(f"[seam] 線を越えた後 {len(after)} コマ: 重なりが 2% を超えるコマ {int((after > 0.02).sum())} / "
          f"最大 {after.max() * 100:.1f}%（0.5 以上）・{max(wshares) * 100:.1f}%（重み付き）")

    # 前後の画: 線を越えて 1 秒（開き切った所）のコマ
    j = min(end - 1, k0 + int(round(REVEAL_SEC * fps)))
    live = np.asarray(feed_tone.apply(Image.fromarray(frames[j]), "A"))
    states = [("before (live)", None), ("0.25 s", ease_in(0.25 / REVEAL_SEC)),
              ("0.5 s", ease_in(0.5 / REVEAL_SEC)), ("1.0 s (open)", BREATH_MIN)]
    tiles = []
    for name, r in states:
        img = live if r is None else composite(live, src, mask, v, r)
        tiles.append(label(img, f"frame {j} / {name}"))
    crop = lambda im: cv2.resize(im[120:420, 220:440], (W // 2, int(300 * (W // 2) / 220)), interpolation=cv2.INTER_NEAREST)  # noqa: E731
    zoom = [crop(tiles[0]), crop(tiles[3])]
    mview = (plate * 0.5).astype(np.uint8)
    mview[..., 0] = np.maximum(mview[..., 0], (mask * 255).astype(np.uint8))
    vv = np.where(mask > 1e-3, (1 - v) * 255, 0).astype(np.uint8)
    mview[..., 1] = np.maximum(mview[..., 1], vv)
    zoom_h = zoom[0].shape[0]
    side = cv2.resize(label(mview, "mask (red) / order (green: early)"), (W, H))
    top = np.concatenate(tiles[:2], 1)
    mid = np.concatenate(tiles[2:], 1)
    bot = np.concatenate([np.concatenate(zoom, 1)[:min(zoom_h, H)], side[:min(zoom_h, H)]], 1)
    sheet = np.concatenate([top, mid, bot], 0)
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(sheet).save(PREVIEW, quality=90)
    print(f"[seam] 前後の画 → {PREVIEW}")
    return 0


# ---- apply --------------------------------------------------------------------------------------------

def step(source: str, **kw) -> dict:
    """焼いた台本と同じ既定（-1 は「素材の値を継ぐ」）で 1 カットを作る。"""
    d = {"source": source, "camera": -1, "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
         "trimStartSec": -1, "trimEndSec": -1, "durKind": "sec", "lineId": "", "durSec": 0,
         "transition": "cut", "cg": "", "cgMode": "follow", "switchSfx": False}
    d.update(kw)
    return d


def patch_show(show: dict) -> None:
    def sa(name: str) -> str:
        return "sa://assets/" + name

    show["cues"] = [c for c in show["cues"] if c["id"] != CUE_ID] + [{
        "id": CUE_ID, "name": "幕の合わせ目が開き、奥に暖かい灯り（1周目A）", "camera": "A",
        "sourceUrl": sa(SRC_NAME), "maskUrl": sa(MASK_NAME), "revealUrl": sa(REVEAL_NAME),
        "revealSec": REVEAL_SEC, "revealDelaySec": 0.0, "revealOutSec": REVEAL_OUT_SEC,
        "strength": 1, "loop": False, "fadeIn": 0, "fadeOut": 0}]
    show["layout"]["lines"] = [ln for ln in show["layout"]["lines"] if ln["id"] != SEAM_LINE["id"]] + [dict(SEAM_LINE)]

    take = {"id": TAKE_ID, "name": "1周目A 体が抜けた後、幕の合わせ目が開く（区間を出るまで）", "at": "line",
            "lineId": SEAM_LINE["id"], "offsetSec": 0, "ifMissed": "skip", "policy": "yield", "wait": "segment",
            "once": True, "maxDurationSec": 120, "dismissible": False,
            "steps": [step("inherit", camera=0, cueId=CUE_ID, durKind="untilZoneChange")]}
    segs = show["timeline"]["segments"]
    seg = next((s for s in segs if int(s.get("lap", 0)) == 1 and int(s.get("camera", -1)) == 0), None)
    if seg is None:
        seg = {"lap": 1, "camera": 0, "hasPost": False, "hasBgm": False, "takes": []}
        segs.append(seg)
        segs.sort(key=lambda s: (int(s.get("lap", 0)), int(s.get("camera", 0))))
    others = [t for t in seg["takes"] if t.get("id") != TAKE_ID]
    if others:
        sys.exit(f"1 周目 A に別の演出がある（{[t.get('id') for t in others]}）。重ならない前提が崩れている")
    seg["takes"] = [take]


def apply(a) -> int:
    root = Path(a.root).resolve()
    if root == MAIN.resolve():
        sys.exit("メインの木には当てない（本番の台本を書き換えない）。検証用の木を指す")
    show_p = root / "Assets/StreamingAssets/show/show.json"
    man_p = root / "Assets/StreamingAssets/show/manifest.json"
    spec_p = root / "tools/twin/assets.json"
    dst = root / "tools/twin/assets"
    for p in (show_p, man_p, spec_p, dst):
        if not p.exists():
            sys.exit(f"無い: {p}（先に py -3.11 tools/twin-tree.py sync）")
    for name in (SRC_NAME, MASK_NAME, REVEAL_NAME):
        if not (ASSETS / name).is_file():
            sys.exit(f"素材が無い: {ASSETS / name}（先に make）")
        shutil.copy2(ASSETS / name, dst / name)

    spec = json.loads(spec_p.read_text(encoding="utf-8"))
    spec["cues"][CUE_ID] = {"source": SRC_NAME, "mask": MASK_NAME, "reveal": REVEAL_NAME}
    spec_p.write_text(json.dumps(spec, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")

    raw = show_p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    show = json.loads(raw.decode("utf-8-sig"))
    patch_show(show)
    data = json.dumps(show, ensure_ascii=False).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    show_p.write_bytes(data)
    man = json.loads(man_p.read_text(encoding="utf-8-sig"))
    man["configSha256"] = hashlib.sha256(data).hexdigest()
    man_p.write_text(json.dumps(man, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print(f"[variant] 当てた: {root}（1 周目 A・線 {SEAM_LINE['id']} z={SEAM_LINE_Z}）")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("make", help="ツインで焼いて素材を作る")
    m.add_argument("--reuse", default="", help="焼いた EXR のフォルダ 3 つ（閉じた,開いた灯りなし,開いた灯りあり）を使い回す")
    sub.add_parser("check", help="束の 1 周目 A のコマへ重ねて前後の画と重なりを出す")
    p = sub.add_parser("apply", help="検証用の木へ当てる")
    p.add_argument("--root", required=True, help="当てる木（検証用の木）")
    a = ap.parse_args()
    return {"make": make, "check": check, "apply": apply}[a.cmd](a)


if __name__ == "__main__":
    raise SystemExit(main())
