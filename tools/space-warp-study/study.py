"""「画像が波打つ」と「空間が波打つ」を、同じ部屋の写真で並べる試作（本編のシェーダは通らない）。

  py -3.11 tools/space-warp-study/study.py --depth <bake_depth の出力> --out <出力> [--cams A,B,C] [--variants a,b] [--sec 6.4]

手法ごとに 1 本の mp4（名前と式の要点を画に焼く）と、カメラごとの並べた mp4 を作る。
入力が欠けていたら組まずに落ちる。
"""
from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
PLATES = {
    "A": ROOT / "tools/twin/plates/plate_A_20260905_193511.jpg",
    "B": ROOT / "tools/web-compositor/captures/plate_B_20260924_172122_544dad7d.jpg",
    "C": ROOT / "tools/web-compositor/captures/plate_C_20260924_172123_3e6c860b.jpg",
}
W, H = 960, 720
FPS = 30
TAU = math.tau
OVERSCAN = 0.85  # 全手法で同じだけ内側を見る（比較の条件を揃える）
LIMIT = 1 - OVERSCAN  # 変位の頭打ち。写真の外を引かない
GRID_W, GRID_H = 40, 30  # 本番で毎コマ計算して貼る粗い格子
FONT = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 22)
FONT_S = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 16)


MAX_INV_DEPTH_STEP = 0.05


def limit_gradient(iz, g, iters=200):
    iz = iz.copy()
    k = np.ones((3, 3), np.float32) / 9
    for _ in range(iters):
        gx = np.abs(np.diff(iz, axis=1))
        gy = np.abs(np.diff(iz, axis=0))
        bad = np.zeros_like(iz, bool)
        bad[:, :-1] |= gx > g
        bad[:, 1:] |= gx > g
        bad[:-1, :] |= gy > g
        bad[1:, :] |= gy > g
        if not bad.any():
            break
        blur = cv2.filter2D(iz, -1, k, borderType=cv2.BORDER_REPLICATE)
        iz[bad] = blur[bad]
    return iz


class Cam:
    def __init__(self, cid: str, depth_dir: Path):
        z = np.load(depth_dir / f"depth_{cid}.npz")
        self.id = cid
        self.origin = z["origin"].astype(np.float64)
        self.rot = z["rot"].astype(np.float64)
        self.tanh, self.tanv = [float(v) for v in z["tan"]]
        d = np.minimum(z["depth"].astype(np.float32), 6.0)
        # 本番で持てる解像度（64x48）へ中央値で落とす。細い物（手すり）は消え、面だけが残る
        d = cv2.medianBlur(cv2.resize(d, (64, 48), interpolation=cv2.INTER_AREA), 3)
        d = cv2.GaussianBlur(cv2.resize(d, (W, H), interpolation=cv2.INTER_CUBIC), (0, 0), 8)
        self.depth = d.astype(np.float64)
        n = cv2.resize(z["normal"].astype(np.float32), (64, 48), interpolation=cv2.INTER_AREA)
        n = cv2.GaussianBlur(cv2.resize(n, (W, H), interpolation=cv2.INTER_CUBIC), (0, 0), 14)
        self.normal = n.astype(np.float64) / np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-4)
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float64)
        self.nx = (xx + 0.5) / W * 2 - 1
        self.ny = 1 - (yy + 0.5) / H * 2
        # 見ている範囲は内側（OVERSCAN）。深度もそこを引く
        mapx = ((self.nx * OVERSCAN + 1) / 2 * W - 0.5).astype(np.float32)
        mapy = (((1 - self.ny * OVERSCAN) / 2) * H - 0.5).astype(np.float32)
        self.depth_v = cv2.remap(self.depth.astype(np.float32), mapx, mapy, cv2.INTER_LINEAR).astype(np.float64)
        self.normal_v = cv2.remap(self.normal.astype(np.float32), mapx, mapy, cv2.INTER_LINEAR).astype(np.float64)
        self.vx = self.nx * OVERSCAN
        self.vy = self.ny * OVERSCAN
        # 視点のごく近く（B の床）は変位が発散するので、見かけの近さを頭打ちにする
        center = (np.abs(self.vx) < 0.25) & (np.abs(self.vy) < 0.25)
        self.z_far = float(np.median(self.depth_v[center])) + 0.8
        z = cv2.GaussianBlur(np.minimum(self.depth_v, self.z_far).astype(np.float32), (0, 0), 10).astype(np.float64)
        self.z_soft = np.sqrt(z ** 2 + 0.45 ** 2)
        self.world = self.to_world(self.vx, self.vy, self.z_soft)
        # 変形の支点: 画面中央付近の奥。ここは動かない
        self.pivot = np.median(self.world[center], axis=0)
        gy, gx = np.mgrid[0:GRID_H, 0:GRID_W].astype(np.float64)
        self.gx = (gx / (GRID_W - 1) * 2 - 1) * OVERSCAN
        self.gy = (1 - gy / (GRID_H - 1) * 2) * OVERSCAN
        gz = cv2.resize(self.z_soft.astype(np.float32), (GRID_W, GRID_H), interpolation=cv2.INTER_AREA)
        # 奥行きの急な段で画が裏返る。逆深度の勾配を 1 目あたり MAX_INV_DEPTH_STEP までに抑える（tune で 3 カメラとも裏返りが 0）
        self.gworld = self.to_world(self.gx, self.gy, (1 / limit_gradient(1 / gz, MAX_INV_DEPTH_STEP)).astype(np.float64))
        self.gnormal = cv2.resize(self.normal_v.astype(np.float32), (GRID_W, GRID_H), interpolation=cv2.INTER_AREA).astype(np.float64)

    def to_world(self, nx, ny, z, origin=None, rot=None):
        o = self.origin if origin is None else origin
        r = self.rot if rot is None else rot
        c = np.stack([nx * self.tanh * z, ny * self.tanv * z, -z], axis=-1)
        return o + c @ r.T

    def project(self, X):
        c = (X - self.origin) @ self.rot
        zc = np.maximum(-c[..., 2], 1e-3)
        return c[..., 0] / zc / self.tanh, c[..., 1] / zc / self.tanv


def soft_limit(x, knee=0.9):
    a = np.abs(x)
    over = a > knee
    out = a.copy()
    out[over] = knee + (1 - knee) * np.tanh((a[over] - knee) / (1 - knee))
    return np.sign(x) * out


def sample(plate, nx, ny):
    nx = soft_limit(nx)
    ny = soft_limit(ny)
    mx = ((nx + 1) / 2 * W - 0.5).astype(np.float32)
    my = (((1 - ny) / 2) * H - 0.5).astype(np.float32)
    return cv2.remap(plate, mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)


def sample_raw(plate, nx, ny):
    mx = ((nx + 1) / 2 * W - 0.5).astype(np.float32)
    my = (((1 - ny) / 2) * H - 0.5).astype(np.float32)
    return cv2.remap(plate, mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)


def plane_waves(X, t, spec):
    """進む平面波の和。spec: (kx, ky, kz, Hz, 位相, ax, ay, az)。単位は m。"""
    D = np.zeros_like(X)
    for kx, ky, kz, hz, ph, ax, ay, az in spec:
        s = np.sin(X[..., 0] * kx + X[..., 1] * ky + X[..., 2] * kz - TAU * hz * t + ph)
        D[..., 0] += ax * s
        D[..., 1] += ay * s
        D[..., 2] += az * s
    return D


# 波長は 1.3〜2.6m（部屋の大きさ）。周波数は参照の実測 .16 / .29 / .47Hz。
# 進行方向に揺れる波（疎密 = 引き延ばしと収縮）と、横に揺れる波（面の傾き）を混ぜる。
WAVES = [
    (2.6, 1.1, 0.6, 0.16, 0.7, 0.085, 0.036, 0.020),   # 疎密
    (-1.0, 3.1, 1.2, 0.29, 2.1, -0.018, 0.070, 0.028),  # 疎密
    (1.7, -1.6, 3.4, 0.23, 4.0, 0.040, 0.044, 0.060),   # 斜めの横波
    (3.9, 2.2, -1.7, 0.47, 1.1, 0.030, 0.018, 0.022),   # 細かい方
]


def field2d(px, py, t, aspect=4 / 3):
    qx, qy = px * aspect, py
    a, b, c = t * TAU * 0.16, t * TAU * 0.29, t * TAU * 0.47
    wx = qx + 0.45 * np.sin(qy * 1.9 + a + 1.0)
    wy = qy + 0.45 * np.sin(qx * 1.6 - b * 0.7 + 2.3)
    dx = 0.135 * np.sin(wx * 2.4 + a * 0.9 + 0.6) + 0.085 * np.sin(wy * 3.6 - b + 2.1) + 0.05 * np.sin((wx + wy) * 5.2 + c)
    dy = 0.145 * np.sin(wy * 2.7 - a + 3.9) + 0.085 * np.sin(wx * 3.2 + b * 1.1 + 0.4) + 0.05 * np.sin((wx - wy) * 4.8 - c + 1.7)
    return dx / aspect, dy


# ---- 手法 --------------------------------------------------------------

def v_static(cam, plate, t):
    return sample(plate, cam.vx, cam.vy)


def v_wave2d(cam, plate, t):
    """対照: 画面座標の波。深度を知らない。"""
    dx, dy = field2d(cam.vx, cam.vy, t)
    return sample(plate, cam.vx + 0.72 * dx, cam.vy + 0.72 * dy)


def v_radial2d(cam, plate, t):
    """参照の実測の形: 中心はほぼ止まり、外へ行くほど動く。深度は使わない。"""
    dx, dy = field2d(cam.vx, cam.vy, t)
    r = np.sqrt(cam.vx ** 2 + cam.vy ** 2) / 1.41
    g = 0.25 + 1.15 * r
    return sample(plate, cam.vx + 0.72 * g * dx, cam.vy + 0.72 * g * dy)


def v_depthwave(cam, plate, t, gain=1.0):
    """部屋の中に置いた波。各画素の 3D 位置を動かしてカメラへ投影し直す。近い物ほど大きく動く。"""
    D = plane_waves(cam.world, t, WAVES) * gain
    u, v = cam.project(cam.world - D)
    return sample(plate, u, v)


def v_sway(cam, plate, t):
    """カメラだけが揺れる（空間は剛体のまま）。視差は出るが波打たない。"""
    T = np.array([0.075 * math.sin(TAU * 0.16 * t + 0.7), 0.060 * math.sin(TAU * 0.23 * t + 2.0), 0.050 * math.sin(TAU * 0.29 * t + 4.0)])
    a = math.radians(1.1) * math.sin(TAU * 0.19 * t + 1.0)
    rz = np.array([[math.cos(a), -math.sin(a), 0], [math.sin(a), math.cos(a), 0], [0, 0, 1]])
    X = cam.to_world(cam.vx, cam.vy, cam.z_soft, origin=cam.origin + T, rot=cam.rot @ rz)
    u, v = cam.project(X)
    return sample(plate, u, v)


def v_dolly(cam, plate, t):
    """ドリーズーム: 距離 d0 の面の大きさを保ったまま、前後へ動いて画角を変える。手前と奥が逆に伸び縮みする。"""
    d0 = float(np.median(cam.z_soft))
    a = 0.26 * math.sin(TAU * 0.16 * t) + 0.08 * math.sin(TAU * 0.29 * t + 1.3)
    forward = cam.rot @ np.array([0.0, 0.0, -1.0])
    zoom = max((d0 - a) / d0, 0.3)
    z2 = np.maximum(cam.z_soft - a, 0.15)
    # 視点を前へ a だけ動かし、画角を zoom 倍に狭めて d0 の面を同じ大きさに保つ
    X = cam.to_world(cam.vx * zoom, cam.vy * zoom, z2, origin=cam.origin + forward * a)
    u, v = cam.project(X)
    return sample(plate, u, v)


def surface_height(X, t):
    h = 0.075 * np.sin(X[..., 0] * 2.3 + X[..., 1] * 1.4 + X[..., 2] * 1.7 - TAU * 0.19 * t + 0.4)
    h += 0.045 * np.sin(-X[..., 0] * 1.6 + X[..., 1] * 3.0 + X[..., 2] * 2.4 - TAU * 0.31 * t + 2.2)
    return h


def v_heave(cam, plate, t, shade=True):
    """面そのものが法線方向に盛り上がる。光（電球）に対する向きが変わるので明暗も動く。"""
    X = cam.world
    n = cam.normal_v
    h = surface_height(X, t)
    u, v = cam.project(X - n * h[..., None])
    img = sample(plate, u, v)
    if not shade:
        return img
    e = 0.03
    grad = np.stack([(surface_height(X + np.eye(3)[k] * e, t) - h) / e for k in range(3)], axis=-1)
    tangent = grad - n * np.sum(grad * n, axis=-1, keepdims=True)
    n2 = n - tangent
    n2 /= np.maximum(np.linalg.norm(n2, axis=-1, keepdims=True), 1e-4)
    L = np.array([0.02, -0.02, 2.55]) - X
    L /= np.maximum(np.linalg.norm(L, axis=-1, keepdims=True), 1e-4)
    a = np.maximum(np.sum(n * L, axis=-1), 0.12)
    b = np.maximum(np.sum(n2 * L, axis=-1), 0.0)
    k = np.clip(b / a, 0.45, 1.6)
    return np.clip(img.astype(np.float32) * k[..., None], 0, 255).astype(np.uint8)


def room_matrix(t, gain=1.0):
    """部屋全体に掛ける 3x3。伸縮と傾き（せん断）だけで、3D の直線は直線のまま。"""
    f = lambda hz, ph: math.sin(TAU * hz * t + ph)
    M = np.eye(3)
    M[0, 0] += 0.085 * f(0.13, 0.7)              # 左右の伸縮
    M[1, 1] += -0.075 * f(0.17, 2.0)             # 奥行きの伸縮
    M[2, 2] += 0.055 * f(0.47, 4.1) + 0.04 * f(0.11, 1.0)  # 高さの伸縮
    M[0, 2] += 0.105 * f(0.17, 3.3)              # 壁が左右へ傾く
    M[1, 2] += 0.070 * f(0.13, 5.2)              # 壁が前後へ傾く
    M[1, 0] += 0.085 * f(0.23, 1.2)              # 床の菱形
    M[0, 1] += 0.070 * f(0.19, 4.4)
    return np.eye(3) + (M - np.eye(3)) * gain


def room_persp(t, gain=1.0):
    """奥へ向かって細る量（1/m）。"""
    f = lambda hz, ph: math.sin(TAU * hz * t + ph)
    return gain * np.array([0.050 * f(0.13, 2.9), 0.060 * f(0.19, 0.3), 0.035 * f(0.23, 5.5)])


# 部屋より長い波。面をゆっくり反らせる。波長は 3.5〜7m
LONG_WAVES = [
    (0.9, 0.5, 1.1, 0.17, 0.4, 0.045, 0.020, 0.030),
    (-0.6, 1.3, 0.8, 0.13, 3.0, 0.020, 0.050, 0.020),
    (1.5, -0.7, 1.0, 0.47, 1.7, 0.016, 0.012, 0.018),
]


ROOM_GAIN = 1.4
CURVE_GAIN = 1.7


# 「波打つ」を字義どおり強くした版。波長 2.4〜4.5m で、部屋の中を波が通る
ROOM_WAVES = [
    (1.5, 0.7, 1.4, 0.17, 0.4, 0.075, 0.030, 0.060),
    (-0.9, 2.0, 1.1, 0.13, 3.0, 0.030, 0.080, 0.035),
    (2.2, -1.0, 1.6, 0.47, 1.7, 0.022, 0.016, 0.026),
]


def room_points(cam, t, curve, waves=None):
    Y = cam.gworld - cam.pivot
    Z = Y @ room_matrix(t, ROOM_GAIN).T
    w = 1.0 + Y @ room_persp(t, ROOM_GAIN)
    X = cam.pivot + Z / np.maximum(w[..., None], 0.4)
    if curve:
        # 支点の周りは動かさない（参照は中心がほぼ止まっている）
        r = np.linalg.norm(Y, axis=-1, keepdims=True)
        near = np.clip((r - 0.3) / 1.2, 0, 1)
        near = near * near * (3 - 2 * near)
        X = X - plane_waves(cam.gworld, t, LONG_WAVES if waves is None else waves) * curve * CURVE_GAIN * near
    return X


def limit_offset(dx, dy):
    """変位の大きさを LIMIT で頭打ちにする。座標でなく変位を抑えるので、縁で線が流れない。"""
    n = np.maximum(np.hypot(dx, dy), 1e-6)
    k = LIMIT * np.tanh(n / LIMIT) / n
    return dx * k, dy * k


def upsample(a):
    return cv2.resize(a.astype(np.float32), (W, H), interpolation=cv2.INTER_LINEAR).astype(np.float64)


def warp_room(cam, plate, t, curve, waves=None):
    u, v = cam.project(room_points(cam, t, curve, waves))
    dx, dy = limit_offset(u - cam.gx, v - cam.gy)
    return sample_raw(plate, cam.vx + upsample(dx), cam.vy + upsample(dy))


def v_room_affine(cam, plate, t):
    """部屋そのものの伸縮と傾き。直線は直線のまま、面と面の継ぎ目も離れない。"""
    return warp_room(cam, plate, t, 0.0)


def v_room_bend(cam, plate, t):
    """部屋の伸縮 + 部屋より長い波。面がゆっくり反る。"""
    return warp_room(cam, plate, t, 1.0)


def v_room_wave(cam, plate, t):
    """部屋の伸縮 + 部屋の中を通る波（字義どおりの「波打つ」）。"""
    return warp_room(cam, plate, t, 1.0, ROOM_WAVES)


def v_room_bend_light(cam, plate, t):
    """部屋の伸縮と反り + 電球に対して面の向きが変わった分の明暗。"""
    img = warp_room(cam, plate, t, 1.0).astype(np.float32)
    X = room_points(cam, t, 1.0)
    n2 = cam.gnormal @ np.linalg.inv(room_matrix(t, ROOM_GAIN))
    n2 /= np.maximum(np.linalg.norm(n2, axis=-1, keepdims=True), 1e-4)
    n1 = cam.gnormal / np.maximum(np.linalg.norm(cam.gnormal, axis=-1, keepdims=True), 1e-4)
    bulb = np.array([0.02, -0.02, 2.55])
    La = bulb - cam.gworld
    La /= np.maximum(np.linalg.norm(La, axis=-1, keepdims=True), 1e-4)
    Lb = bulb - X
    Lb /= np.maximum(np.linalg.norm(Lb, axis=-1, keepdims=True), 1e-4)
    a = np.maximum(np.sum(n1 * La, axis=-1), 0.25)
    b = np.maximum(np.sum(n2 * Lb, axis=-1), 0.0)
    k = upsample(np.clip(1.0 + 1.4 * (b / a - 1.0), 0.72, 1.28))
    return np.clip(img * k[..., None], 0, 255).astype(np.uint8)


VARIANTS = {
    "wave2d": ("1 画面の波（対照）", "画面座標に正弦。深度なし", v_wave2d),
    "radial2d": ("2 外ほど大きい画面の波", "参照の実測の形だけ真似る。深度なし", v_radial2d),
    "sway": ("3 カメラの揺れ（対照）", "深度で視差。空間は剛体", v_sway),
    "dolly": ("4 ドリーズーム", "面の大きさを保ち前後へ", v_dolly),
    "depthwave": ("5 部屋の中の短い波", "3D 位置を波で動かす。波長 1.3〜2.6m", v_depthwave),
    "heave": ("6 面の盛り上がり + 光", "面の法線方向へ。電球の明暗", v_heave),
    "room": ("7 部屋の伸縮と傾き", "3D の線形変形。直線は直線のまま", v_room_affine),
    "bend": ("8 7 + 部屋より長い波", "波長 3.5〜7m で面が反る", v_room_bend),
    "bendlight": ("9 8 + 面の向きの明暗", "電球に対する向きの変化だけ明暗に", v_room_bend_light),
    "roomwave": ("10 7 + 部屋を通る波", "波長 2.4〜4.5m。字義どおりの波打ち", v_room_wave),
}


def fold_stats(cam, t, curve, waves=None):
    """格子 1 目の面積比。0 以下は画が裏返っている。"""
    u, v = cam.project(room_points(cam, t, curve, waves))
    dx, dy = limit_offset(u - cam.gx, v - cam.gy)
    sx = cam.gx + dx
    sy = cam.gy + dy
    ax, ay = np.diff(sx, axis=1)[:-1, :], np.diff(sy, axis=1)[:-1, :]
    bx, by = np.diff(sx, axis=0)[:, :-1], np.diff(sy, axis=0)[:, :-1]
    gdx = cam.gx[0, 1] - cam.gx[0, 0]
    gdy = cam.gy[1, 0] - cam.gy[0, 0]
    return (ax * by - ay * bx) / (gdx * gdy)


def burn(img, title, sub):
    pil = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    d = ImageDraw.Draw(pil)
    d.rectangle([0, 0, W, 62], fill=(0, 0, 0))
    d.text((12, 4), title, font=FONT, fill=(255, 255, 255))
    d.text((12, 36), sub, font=FONT_S, fill=(210, 210, 210))
    return cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)


class Writer:
    def __init__(self, path: Path, w: int, h: int):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.p = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{w}x{h}", "-r", str(FPS), "-i", "-",
                                   "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", str(path)], stdin=subprocess.PIPE)

    def add(self, frame):
        self.p.stdin.write(frame.tobytes())

    def close(self):
        self.p.stdin.close()
        if self.p.wait() != 0:
            raise RuntimeError("ffmpeg failed")


def imwrite(path: Path, img):
    ok, b = cv2.imencode(".png", img)
    assert ok
    path.write_bytes(b.tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--depth", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cams", default="A,B,C")
    ap.add_argument("--variants", default=",".join(VARIANTS))
    ap.add_argument("--sec", type=float, default=6.4)
    ap.add_argument("--video", help="写真の代わりに流す映像。{cid} をカメラ名に置く")
    ap.add_argument("--start", type=float, default=0.0, help="--video の開始秒")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    names = a.variants.split(",")
    folds = {}
    for n in names:
        if n not in VARIANTS:
            raise SystemExit(f"手法が無い: {n}")
    for cid in a.cams.split(","):
        if not PLATES[cid].exists():
            raise SystemExit(f"写真が無い: {PLATES[cid]}")
        plate = cv2.resize(cv2.imread(str(PLATES[cid])), (W, H), interpolation=cv2.INTER_CUBIC)
        video = None
        if a.video:
            vp = Path(a.video.replace("{cid}", cid))
            if not vp.exists():
                raise SystemExit(f"映像が無い: {vp}")
            video = cv2.VideoCapture(str(vp))
            video.set(cv2.CAP_PROP_POS_MSEC, a.start * 1000)
            vfps = video.get(cv2.CAP_PROP_FPS)
        cam = Cam(cid, Path(a.depth))
        n_frames = int(a.sec * FPS)
        cols = 3 if len(names) > 4 else 2
        rows = math.ceil(len(names) / cols)
        tw, th = W // 2, H // 2
        grid = Writer(out / f"{cid}_compare.mp4", tw * cols, th * rows)
        singles = {n: Writer(out / f"{cid}_{n}.mp4", W, H) for n in names}
        stills = {}
        for i in range(n_frames):
            t = i / FPS
            if video is not None:
                ok, fr = video.read()
                if not ok:
                    raise SystemExit("映像が足りない")
                plate = cv2.resize(fr, (W, H), interpolation=cv2.INTER_AREA)
                for _ in range(max(0, round(vfps / FPS) - 1)):
                    video.grab()
            tiles = []
            for n in names:
                title, sub, fn = VARIANTS[n]
                img = burn(fn(cam, plate, t), f"{cid} / {title}", sub)
                singles[n].add(img)
                tiles.append(cv2.resize(img, (tw, th), interpolation=cv2.INTER_AREA))
                if i in (int(1.2 * FPS), int(3.6 * FPS)):
                    imwrite(out / f"{cid}_{n}_t{i:03d}.png", img)
            while len(tiles) < rows * cols:
                tiles.append(np.zeros((th, tw, 3), np.uint8))
            grid.add(np.vstack([np.hstack(tiles[r * cols:(r + 1) * cols]) for r in range(rows)]))
        grid.close()
        det = np.stack([fold_stats(cam, i / FPS, 1.0) for i in range(0, n_frames, 6)])
        folds[cid] = {"cell_area_min": round(float(det.min()), 3), "cell_area_p1": round(float(np.percentile(det, 1)), 3),
                      "cell_area_p99": round(float(np.percentile(det, 99)), 3), "cells_below_0.25": round(float((det < 0.25).mean()), 4)}
        print("[fold]", cid, folds[cid], flush=True)
        det2 = np.stack([fold_stats(cam, i / FPS, 1.0, ROOM_WAVES) for i in range(0, n_frames, 6)])
        print("[fold roomwave]", cid, round(float(det2.min()), 3), round(float((det2 < 0.25).mean()), 4), flush=True)
        for w in singles.values():
            w.close()
        print("[study]", cid, "frames", n_frames, flush=True)
    (out / "params.json").write_text(json.dumps({"overscan": OVERSCAN, "folds": folds, "waves": WAVES, "plates": {k: str(v) for k, v in PLATES.items()}}, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
