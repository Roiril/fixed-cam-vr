"""台の姿勢を解き直したあと、その台に合わせて作った合成素材を新しい姿勢へ写し直す（Blender 不要・1 分弱）。

    py -3.11 tools/twin/repose_assets.py --cam A --old-rev 27916975            # 写し方と動く量を見るだけ
    py -3.11 tools/twin/repose_assets.py --cam A --old-rev 27916975 --write    # tools/twin/assets/ を書き換える
    py -3.11 tools/twin/repose_assets.py --cam A --old-rev 27916975 --write --sheet <画>   # 前後を並べた画も

古い姿勢は --old-rev のコミットの rig.json（か --old-rig のファイル）、新しい姿勢は今の rig.json。
新しい無人写真（plates/twin_<台>_plate.jpg）を先に焼き直しておく。写した先で古い画に無かった所はそれで埋める。

写し方は 2 つ。
- 面: 素材の中身が幕・床・壁に貼り付いているもの（顔のしみ・幕の縁の人形・マスク・比の画・順の画）。
  新しい姿勢の画素ごとに、視線が L の幕・床・西の壁・棚の前面のどれに当たるかを解き、古い姿勢でその点が
  写っていた画素を引く。幕の襞（数 cm）と手すり・支柱（枠の芯から 8cm）は面から外れるので、そこは数 px ずれる
- 射影: 床に立つ人形の群れ（4-A）。人形は面に貼り付いていないので、面の写し方を左半分で最もよく近似する
  射影 1 枚で動かす（人形が裂けない）。背景の残差は下の表に出す

マスク・順の画は枠空間（640x360・映像は中央に contain-fit）なので、映像の座標へ戻して写し、枠へ戻す。
⚠ 素材を作った元の生成（tools/gen-plate/stain3 の build_right_burst.py など）は古い姿勢の画を入力にしている。
作り直したら、その出力にもう一度これを通す（--old-rev は素材を作ったときの rig.json のコミット）。
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

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import check_rig as cr  # noqa: E402  （cam_axes を共有する。投影の式を 2 つにしない）

W, H = 640, 480
FW, FH = 640, 360
ASSETS = HERE / "assets"

# 台ごとの素材と写し方。kind: image（画。古い画に無かった所は新しい無人写真）/ ratio（比の画。外は 1）/
# frame0（枠空間のマスク。外は 0）/ frame1（枠空間の順の画。外は 1）/ video（動画の画）。
# model: surface（面）/ homography_left（左半分の射影）。
# stand は「幕の縁の外に立つ人形」の所。その中は奥の壁や棚ではなく、幕の面を端の外へ延ばした面に当てる
# （人形は幕の端のすぐ外に立つ）。box は古い画の矩形、mask は古い画の枠空間のマスク（15px 広げる）。
# 境は 4px でぼかして 2 つの写し方を混ぜる（人形の縁で画が裂けない）。
SPECS = {
    "A": {
        "stands": {
            "right": {"box": (478, 236, 630, 476)},          # 右の幕の縁の裏の人形（build_right_burst.py の BOX_DOLL）
            "left": {"mask": "twin_stainA_dollmask.png"},     # 左の幕の縁から覗く人形
        },
        "files": {
            "twin_stainA_idle.png": ("image", "surface", None),
            "twin_stainA_bulge.png": ("image", "surface", None),
            "twin_stainA_doll.png": ("image", "surface", "left"),
            "twin_stainA_right_watch.png": ("image", "surface", "right"),
            "twin_stainA_ratio.png": ("ratio", "surface", None),
            "twin_stainA_both_ratio.png": ("ratio", "surface", "right"),
            "twin_stainA_right_ratio.png": ("ratio", "surface", "right"),
            "twin_stainA_mask.png": ("frame0", "surface", None),
            "twin_stainA_both_mask.png": ("frame0", "surface", "right"),
            "twin_stainA_right_mask.png": ("frame0", "surface", "right"),
            "twin_stainA_right_ratio_mask.png": ("frame0", "surface", "right"),
            "twin_stainA_pressmask.png": ("frame0", "surface", None),
            "twin_stainA_dollmask.png": ("frame0", "surface", "left"),
            "twin_stainA_reveal.png": ("frame1", "surface", None),
            "twin_stainA_right_burst.mp4": ("video", "surface", "right"),
            "twin_dollsA_left_freeze.png": ("image", "homography_left", None),
            "twin_dollsA_in.mp4": ("video", "homography_left", None),
        },
        # 画素の座標を持つ JSON（build_right_burst.py の確認用の値）。点を写し直す
        "meta": "twin_stainA_right_burst.json",
    },
}


def load_rig_rev(rev: str) -> dict:
    out = subprocess.run(["git", "-C", str(ROOT), "show", f"{rev}:tools/twin/rig.json"], capture_output=True, check=True)
    return json.loads(out.stdout.decode("utf-8"))


def cam_of(rig: dict, cid: str) -> dict:
    return next(c for c in rig["cameras"] if c["id"] == cid)


def fpx(cam: dict) -> float:
    return (W / 2) / math.tan(math.radians(cam["hfovDeg"]) / 2)


def rays(cam: dict, u: np.ndarray, v: np.ndarray):
    o, r, up, f = cr.cam_axes(cam)
    k = fpx(cam)
    d = f[None] + r[None] * ((u - W / 2) / k)[:, None] - up[None] * ((v - H / 2) / k)[:, None]
    return o, d


def project(cam: dict, x: np.ndarray) -> np.ndarray:
    o, r, up, f = cr.cam_axes(cam)
    k = fpx(cam)
    dd = x - o
    z = np.maximum(dd @ f, 1e-6)
    return np.stack([W / 2 + k * (dd @ r) / z, H / 2 - k * (dd @ up) / z], 1)


class Surface:
    """視線が最初に当たる面。L の幕 2 枚（show.json の壁）・床・西と北の壁・棚の前面。"""

    def __init__(self, rig: dict):
        w1, w2 = cr.SHOW["layout"]["room"]["walls"]
        self.walls = [w1, w2]   # x1,z1 が角・x2,z2 が端（curtain_guide.py と同じ）
        self.room = rig["room"]
        self.shelf = rig["shelf"]

    def hit(self, cam: dict, u: np.ndarray, v: np.ndarray, stand_px: np.ndarray | None = None) -> np.ndarray:
        """画素 (u, v) の視線が当たる点。stand_px が真の画素は、幕の面を端の外まで延ばして当てる。"""
        o, d = rays(cam, u, v)
        best = np.full(len(u), np.inf)
        ext = np.zeros(len(u), bool) if stand_px is None else stand_px

        def take(t, ok):
            nonlocal best
            ok = ok & (t > 1e-4) & (t < best)
            best = np.where(ok, t, best)

        with np.errstate(divide="ignore", invalid="ignore"):
            for w in self.walls:
                a = np.array([w["x1"], w["z1"]], float)
                b = np.array([w["x2"], w["z2"]], float)
                tdir = (b - a) / np.linalg.norm(b - a)
                n = np.array([-tdir[1], tdir[0]])
                den = d[:, 0] * n[0] + d[:, 2] * n[1]
                t = ((a - o[[0, 2]]) @ n) / den
                p = o[None] + t[:, None] * d
                s = (p[:, [0, 2]] - a) @ tdir
                length = float(np.linalg.norm(b - a))
                inside = (s >= -0.02) & (s <= length + 0.02)
                # 角の側は 2 枚の幕どうしで閉じる。端の外は棚と壁へ抜ける（人形の矩形だけ延ばす）
                take(t, (inside | (ext & (s > length))) & (p[:, 1] <= 1.9))
            take((0.0 - o[1]) / d[:, 1], np.ones(len(u), bool))
            take((self.room["xMin"] - o[0]) / d[:, 0], np.ones(len(u), bool))
            take((self.room["zMax"] - o[2]) / d[:, 2], np.ones(len(u), bool))
            sh = self.shelf
            t = (sh["min"][2] - o[2]) / d[:, 2]
            p = o[None] + t[:, None] * d
            take(t, (p[:, 0] >= sh["min"][0]) & (p[:, 0] <= sh["max"][0]) & (p[:, 1] <= sh["max"][1]))
        best = np.where(np.isfinite(best), best, 50.0)
        return o[None] + best[:, None] * d


def stand_weight(spec: dict | None) -> np.ndarray | None:
    """人形の所の重み（古い画の 640x480・0〜1）。"""
    if spec is None:
        return None
    m = np.zeros((H, W), np.float32)
    if "box" in spec:
        x0, y0, x1, y1 = spec["box"]
        m[y0:y1, x0:x1] = 1.0
    if "mask" in spec:
        f = imread(ASSETS / spec["mask"])[..., 0].astype(np.float32) / 255.0
        sw = int(round(FH * W / H))
        off = (FW - sw) // 2
        video = cv2.resize(f[:, off:off + sw], (W, H), interpolation=cv2.INTER_LINEAR)
        m = np.maximum(m, cv2.dilate((video > 0.1).astype(np.float32), np.ones((31, 31), np.uint8)))
    return np.clip(cv2.GaussianBlur(m, (0, 0), 4.0), 0, 1)


def sample(weight: np.ndarray, q: np.ndarray) -> np.ndarray:
    x = np.clip(np.round(q[:, 0]).astype(int), 0, W - 1)
    y = np.clip(np.round(q[:, 1]).astype(int), 0, H - 1)
    inside = (q[:, 0] >= 0) & (q[:, 0] < W) & (q[:, 1] >= 0) & (q[:, 1] < H)
    return np.where(inside, weight[y, x], 0.0)


def surface_map(old: dict, new: dict, surf: Surface, weight: np.ndarray | None) -> tuple[np.ndarray, np.ndarray]:
    """新しい画の画素ごとに、古い画で引く座標（cv2.remap の map）。

    人形の所は古い画の座標で決まっているので、新しい画の画素を 2 通りの面で古い画へ写し、
    引いた先の重みで 2 つの座標を混ぜる。
    """
    uu, vv = np.meshgrid(np.arange(W, dtype=np.float64), np.arange(H, dtype=np.float64))
    u, v = uu.ravel(), vv.ravel()
    q = project(old, surf.hit(new, u, v))
    if weight is not None:
        q2 = project(old, surf.hit(new, u, v, stand_px=np.ones(len(u), bool)))
        k = np.maximum(sample(weight, q), sample(weight, q2))[:, None]
        q = q * (1 - k) + q2 * k
    return q[:, 0].reshape(H, W).astype(np.float32), q[:, 1].reshape(H, W).astype(np.float32)


def forward_points(old: dict, new: dict, surf: Surface, pts: np.ndarray, weight: np.ndarray | None = None) -> np.ndarray:
    """古い画の点を新しい画へ（面の写し方の向き）。"""
    u, v = pts[:, 0].astype(float), pts[:, 1].astype(float)
    q = project(new, surf.hit(old, u, v))
    if weight is not None:
        k = sample(weight, pts)[:, None]
        q = q * (1 - k) + project(new, surf.hit(old, u, v, stand_px=np.ones(len(u), bool))) * k
    return q


def homography_left(old: dict, new: dict, surf: Surface) -> tuple[np.ndarray, dict]:
    """左半分の面の写し方を最もよく近似する射影（古い画 → 新しい画）と、その残差。"""
    uu, vv = np.meshgrid(np.arange(4, 320, 8, dtype=np.float64), np.arange(4, H, 8, dtype=np.float64))
    u, v = uu.ravel(), vv.ravel()
    src = np.stack([u, v], 1)
    dst = forward_points(old, new, surf, src)
    hm, _ = cv2.findHomography(src.astype(np.float32), dst.astype(np.float32), 0)
    pr = cv2.perspectiveTransform(src[None].astype(np.float32), hm)[0]
    e = np.linalg.norm(pr - dst, axis=1)
    return hm, {"medianPx": round(float(np.median(e)), 2), "p90Px": round(float(np.percentile(e, 90)), 2), "maxPx": round(float(e.max()), 2)}


def homography_map(hm: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    inv = np.linalg.inv(hm)
    uu, vv = np.meshgrid(np.arange(W, dtype=np.float32), np.arange(H, dtype=np.float32))
    pts = np.stack([uu.ravel(), vv.ravel()], 1)[None]
    q = cv2.perspectiveTransform(pts, inv)[0]
    return q[:, 0].reshape(H, W), q[:, 1].reshape(H, W)


def valid_of(mx: np.ndarray, my: np.ndarray) -> np.ndarray:
    """古い画の中を引いている画素の重み（縁は 3px でぼかす）。"""
    ok = ((mx >= 0) & (mx <= W - 1) & (my >= 0) & (my <= H - 1)).astype(np.float32)
    return cv2.GaussianBlur(cv2.erode(ok, np.ones((3, 3), np.uint8)), (0, 0), 1.5)[..., None]


def warp_image(img: np.ndarray, mx, my, plate: np.ndarray) -> np.ndarray:
    out = cv2.remap(img, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE).astype(np.float32)
    a = valid_of(mx, my)
    return np.clip(out * a + plate.astype(np.float32) * (1 - a), 0, 255).astype(np.uint8)


def warp_ratio(img: np.ndarray, mx, my) -> np.ndarray:
    return cv2.remap(img, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=(255, 255, 255))


def warp_frame(img: np.ndarray, mx, my, outside: int) -> np.ndarray:
    """枠空間（640x360）の画を、映像の座標で写して枠へ戻す。映像の外（左右の帯）は元のまま。"""
    sw = int(round(FH * W / H))
    off = (FW - sw) // 2
    video = cv2.resize(img[:, off:off + sw], (W, H), interpolation=cv2.INTER_LINEAR)
    moved = cv2.remap(video, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT,
                      borderValue=(outside, outside, outside))
    out = img.copy()
    out[:, off:off + sw] = cv2.resize(moved, (sw, FH), interpolation=cv2.INTER_AREA)
    return out


def imread(p: Path) -> np.ndarray:
    a = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_COLOR)
    if a is None:
        raise SystemExit(f"読めない: {p}")
    return a


def imwrite(p: Path, img: np.ndarray) -> None:
    ok, buf = cv2.imencode(p.suffix, img)
    if not ok:
        raise SystemExit(f"書けない: {p}")
    tmp = p.with_name(p.stem + ".tmp" + p.suffix)
    buf.tofile(str(tmp))
    tmp.replace(p)


def warp_video(src: Path, dst: Path, mx, my, plate: np.ndarray) -> int:
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                            "stream=width,height,r_frame_rate", "-of", "csv=p=0", str(src)],
                           capture_output=True, text=True, check=True).stdout.strip().split(",")
    w, h, rate = int(probe[0]), int(probe[1]), probe[2]
    if (w, h) != (W, H):
        raise SystemExit(f"{src.name} が {w}x{h}。640x480 だけ写せる")
    tmp = dst.with_name(dst.stem + ".tmp" + dst.suffix)
    dec = subprocess.Popen(["ffmpeg", "-v", "error", "-i", str(src), "-f", "rawvideo", "-pix_fmt", "bgr24", "-"],
                           stdout=subprocess.PIPE)
    enc = subprocess.Popen(["ffmpeg", "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{W}x{H}",
                            "-framerate", rate, "-i", "-", "-c:v", "libx264", "-crf", "16", "-preset", "medium",
                            "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(tmp)], stdin=subprocess.PIPE)
    assert dec.stdout is not None and enc.stdin is not None
    n = 0
    while True:
        buf = dec.stdout.read(W * H * 3)
        if len(buf) < W * H * 3:
            break
        frame = np.frombuffer(buf, np.uint8).reshape(H, W, 3)
        enc.stdin.write(warp_image(frame, mx, my, plate).tobytes())
        n += 1
    enc.stdin.close()
    if dec.wait() != 0 or enc.wait() != 0 or n == 0:
        tmp.unlink(missing_ok=True)
        raise SystemExit(f"動画を写せない: {src}（{n} コマ）")
    tmp.replace(dst)
    return n


def first_frame(p: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", str(p), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgr24", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.uint8).reshape(H, W, 3).copy()


def remap_meta(path: Path, old: dict, new: dict, surf: Surface, weight: np.ndarray | None) -> dict:
    meta = json.loads(path.read_text(encoding="utf-8"))
    changed = {}
    for key in ("dollLaughBox", "dollWatchBox"):
        if key in meta:
            x0, y0, x1, y1 = meta[key]
            q = forward_points(old, new, surf, np.array([[x0, y0], [x1, y0], [x0, y1], [x1, y1]], float), weight)
            # 画の外へ出た分は切る（人形が画の端で切れる）
            meta[key] = [int(np.clip(round(q[:, 0].min()), 0, W)), int(np.clip(round(q[:, 1].min()), 0, H)),
                         int(np.clip(round(q[:, 0].max()), 0, W)), int(np.clip(round(q[:, 1].max()), 0, H))]
            changed[key] = meta[key]
    for key in ("bulgeTipX", "bulgeRootX"):
        if key in meta:
            # 伸び出しは顔の高さ（右の幕のしみの中ほど）で測っている
            q = forward_points(old, new, surf, np.array([[meta[key], 140.0]]))
            meta[key] = round(float(q[0, 0]), 1)
            changed[key] = meta[key]
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    tmp.replace(path)
    return changed


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--cam", default="A", choices=sorted(SPECS))
    ap.add_argument("--old-rev", help="素材を作ったときの rig.json があるコミット")
    ap.add_argument("--old-rig", help="古い rig.json のファイル（--old-rev の代わり）")
    ap.add_argument("--write", action="store_true", help="tools/twin/assets/ の素材を書き換える")
    ap.add_argument("--sheet", help="前後を並べた画")
    a = ap.parse_args()
    if bool(a.old_rev) == bool(a.old_rig):
        raise SystemExit("--old-rev か --old-rig のどちらか 1 つを渡す")
    old_rig = load_rig_rev(a.old_rev) if a.old_rev else json.loads(Path(a.old_rig).read_text(encoding="utf-8"))
    old, new = cam_of(old_rig, a.cam), cam_of(cr.RIG, a.cam)
    if old["pos"] == new["pos"] and old["lookAt"] == new["lookAt"] and old["rollDeg"] == new["rollDeg"]:
        print(f"{a.cam}: 姿勢が同じ。写し直す物は無い")
        return 0
    spec = SPECS[a.cam]
    surf = Surface(cr.RIG)
    plate = imread(HERE / "plates" / f"twin_{a.cam}_plate.jpg")

    # 人形の所の重みは、素材を書き換える前に古い画から作る
    weights = {k: stand_weight(v) for k, v in spec["stands"].items()}
    maps = {("surface", None): surface_map(old, new, surf, None)}
    for k, wgt in weights.items():
        maps[("surface", k)] = surface_map(old, new, surf, wgt)
    hm, res = homography_left(old, new, surf)
    maps[("homography_left", None)] = homography_map(hm)
    mx, my = maps[("surface", None)]
    uu, vv = np.meshgrid(np.arange(W, dtype=np.float32), np.arange(H, dtype=np.float32))
    shift = np.hypot(mx - uu, my - vv)
    print(f"{a.cam}: 面の写し方で動く量 中央 {np.median(shift):.1f}px・最大 {shift.max():.1f}px")
    print(f"   左半分の射影の残差（面の写し方との差）: {res}")
    if (HERE / "plates" / f"twin_{a.cam}_plate.jpg").stat().st_mtime < (HERE / "rig.json").stat().st_mtime:
        print(f"⚠ plates/twin_{a.cam}_plate.jpg が rig.json より古い。先に無人写真を焼き直す（README）")

    before, after = [], []
    for name, (kind, model, stand) in spec["files"].items():
        p = ASSETS / name
        if not p.is_file():
            raise SystemExit(f"素材が無い: {p}")
        mx, my = maps[(model, stand)]
        how = model + (f"・{stand}" if stand else "")
        if kind == "video":
            before.append((name, first_frame(p)))
            if a.write:
                n = warp_video(p, p, mx, my, plate)
                after.append((name, first_frame(p)))
                print(f"  {name}: {n} コマ（{how}）")
            else:
                after.append((name, warp_image(before[-1][1], mx, my, plate)))
            continue
        img = imread(p)
        if kind == "image":
            out = warp_image(img, mx, my, plate)
        elif kind == "ratio":
            out = warp_ratio(img, mx, my)
        else:
            out = warp_frame(img, mx, my, 0 if kind == "frame0" else 255)
        before.append((name, img))
        after.append((name, out))
        if a.write:
            imwrite(p, out)
            print(f"  {name}（{kind}・{how}）")
    if a.write and spec.get("meta"):
        print(f"  {spec['meta']}: {remap_meta(ASSETS / spec['meta'], old, new, surf, weights.get('right'))}")
    if a.sheet:
        tiles = []
        for (name, b), (_, c) in zip(before, after):
            row = []
            for img in (b, c):
                t = cv2.resize(img, (320, 240 if img.shape[0] == H else 180))
                t = cv2.copyMakeBorder(t, 0, 240 - t.shape[0] + 18, 0, 0, cv2.BORDER_CONSTANT, value=(0, 0, 0))
                row.append(t)
            r = np.concatenate(row, 1)
            cv2.putText(r, name, (4, 254), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1)
            tiles.append(r)
        cols = 2
        while len(tiles) % cols:
            tiles.append(np.zeros_like(tiles[0]))
        sheet = np.concatenate([np.concatenate(tiles[i:i + cols], 1) for i in range(0, len(tiles), cols)], 0)
        imwrite(Path(a.sheet), sheet)
        print(f"前後: {a.sheet}")
    if not a.write:
        print("（見るだけ。書き換えるなら --write）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
