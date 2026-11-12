"""位置で動く演出の版を、検証用の木へ当てる（本番の台本と APK には入らない）。

設計は reports/2026-11-12_position-driven-redesign.html。ここで当てるのは、いまのスキーマだけで組める所:

  2 周目 A  シミを左の幕の締まった形で出し、角の線で幕へ沈め、背の線で右の幕へ出す
  2 周目 C  人形視点を秒ではなく床の線で 1 カットずつ進め、カットの間のライブに人形を立たせる（--chase）

使い方（検証用の木を sync した後）:
  py -3.11 tools/twin-tree.py sync
  py -3.11 tools/twin/variant_posdriven.py --root ../fcv-twin-tree [--chase]
  py -3.11 tools/twin-tree.py run -- --fast --calib twin --tag posdrv-after

やること: 木の tools/twin/assets/ へ締まったマスクと右の幕の顔を作る → 木の assets.json と焼いた show.json へ
cue・線・take を足す → manifest.json の configSha256 を合わせる。こちらの木（メイン）には書かない。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
MAIN = HERE.parents[1]
W, H = 640, 480          # 映像（4:3）
FW, FH = 640, 360        # マスクの枠（16:9）

# 画の中の所（映像の 0〜1）。ツインの無人写真 A で、幕のひだとポールと手すりに合わせて決めた
LEFT_FACE = (0.215, 0.0, 0.435, 0.445)    # 左の幕の、ポールと角のあいだ。下の辺は手すりの上の縁
RIGHT_FACE = (0.575, 0.0, 0.795, 0.445)   # 右の幕の同じ高さ（左を x=0.505 で折り返した所）
LEFT_PEEK = (0.125, 0.14, 0.315, 0.96)    # 左の幕の外の縁（人形が半身で覗く）


def imread(p: Path, flags=cv2.IMREAD_COLOR):
    img = cv2.imdecode(np.fromfile(str(p), dtype=np.uint8), flags)
    if img is None:
        sys.exit(f"読めない: {p}")
    return img


def imwrite(p: Path, img) -> None:
    ok, buf = cv2.imencode(p.suffix, img)
    if not ok:
        sys.exit(f"書けない: {p}")
    buf.tofile(str(p))


def frame_mask(box, feather_px: int = 9) -> np.ndarray:
    """映像の矩形を、枠空間（16:9）のマスクにする。縁は内側へぼかす（外へはみ出さない）。"""
    x0, y0, x1, y1 = box
    src_w = FH * W / H                     # 枠の中で映像が占める幅（px）
    off = (FW - src_w) / 2
    m = np.zeros((FH, FW), np.float32)
    px0, px1 = int(round(off + x0 * src_w)), int(round(off + x1 * src_w))
    py0, py1 = int(round(y0 * FH)), int(round(y1 * FH))
    inset = feather_px
    m[py0 + (inset if y0 > 0 else 0):py1 - inset, px0 + inset:px1 - inset] = 1.0
    k = feather_px * 2 + 1
    m = cv2.GaussianBlur(m, (k, k), feather_px / 2)
    return (np.clip(m, 0, 1) * 255).astype(np.uint8)


def make_right_face(assets: Path, plate: np.ndarray) -> np.ndarray:
    """左の幕のシミを、右の幕へ折り返して写す。シミは布を暗くする比として取り出すので、右の幕のひだと光がそのまま残る。"""
    idle = imread(assets / "twin_stainA_idle.png").astype(np.float32)
    base = plate.astype(np.float32)
    lx0, ly0, lx1, ly1 = (int(LEFT_FACE[0] * W), int(LEFT_FACE[1] * H), int(LEFT_FACE[2] * W), int(LEFT_FACE[3] * H))
    ratio = (idle[ly0:ly1, lx0:lx1] + 4.0) / (base[ly0:ly1, lx0:lx1] + 4.0)
    ratio = cv2.GaussianBlur(np.clip(ratio, 0.15, 1.25), (3, 3), 0)[:, ::-1]
    rx0 = int(RIGHT_FACE[0] * W)
    out = base.copy()
    h, w = ratio.shape[:2]
    out[ly0:ly0 + h, rx0:rx0 + w] = np.clip(base[ly0:ly0 + h, rx0:rx0 + w] * ratio, 0, 255)
    return out.astype(np.uint8)


def step(source: str, **kw) -> dict:
    d = {"source": source, "camera": 0, "cueId": "", "durKind": "sec", "durSec": 0, "transition": "cut", "cgMode": "follow"}
    d.update(kw)
    return d


def patch_show(show: dict, chase: bool) -> None:
    def sa(name: str) -> str:
        return "sa://assets/" + name

    def cue(cid, name, cam, src, mask, **kw):
        c = {"id": cid, "name": name, "camera": cam, "maskUrl": sa(mask) if mask else "", "sourceUrl": sa(src),
             "strength": 1, "loop": False, "fadeIn": 0, "fadeOut": 0}
        c.update(kw)
        return c

    old = {c["id"]: c for c in show["cues"]}
    idle = old["cue_stain_A"]["sourceUrl"].rsplit("/", 1)[-1]
    bulge = old["stain_A_press_bulge"]["sourceUrl"].rsplit("/", 1)[-1]
    doll = old["stain_A_done_doll"]["sourceUrl"].rsplit("/", 1)[-1]
    show["cues"] = [c for c in show["cues"] if not c["id"].startswith("posdrv_")] + [
        cue("posdrv_stain_left", "顔のシミ（左の幕・締まった形）", "A", idle, "posdrv_stainA_left_mask.png", fadeOut=0.4),
        cue("posdrv_stain_left_bulge", "盛り上がり（左の幕・締まった形）", "A", bulge, "posdrv_stainA_left_mask.png"),
        cue("posdrv_stain_left_peek", "人形が覗く（左の幕の外の縁）", "A", doll, "posdrv_stainA_peek_mask.png"),
        cue("posdrv_stain_right", "顔のシミ（右の幕・背へ回った）", "A", "posdrv_stainA_right.png", "posdrv_stainA_right_mask.png",
            fadeIn=0.6, fadeOut=0.3),
    ]

    lines = [ln for ln in show["layout"]["lines"] if not ln["id"].startswith("posdrv_")]
    def line(lid, cam, x1, z1, x2, z2, label):
        return {"id": lid, "camera": cam, "x1": x1, "z1": z1, "x2": x2, "z2": z2, "dir": "both", "label": label, "mirrorOf": ""}
    lines += [
        line("posdrv_A_behind", 0, -0.5, -0.20, 0.0, -0.20, "A 背の線（左の幕の前へ出た所）"),
        line("posdrv_C_1", 2, -0.15, 0.42, -0.15, 0.95, "C 1 本目"),
        line("posdrv_C_3", 2, 0.40, 0.42, 0.40, 0.95, "C 3 本目"),
        line("posdrv_C_out", 2, 0.58, 0.42, 0.58, 0.95, "C 体が画から出る所"),
    ]
    show["layout"]["lines"] = lines

    for seg in show["timeline"]["segments"]:
        lap, cam = int(seg.get("lap", 0)), int(seg.get("camera", 0))
        if lap == 2 and cam == 0:
            seg["takes"] = [
                {"id": "L2C0#0", "name": "2周目A 顔のシミ（左の幕。角の線で幕へ沈む）", "at": "enter", "ifMissed": "skip",
                 "policy": "hold", "wait": "segment", "once": True, "dismissible": True,
                 "markStartCueId": "posdrv_stain_left_bulge", "markDoneCueId": "posdrv_stain_left_peek", "markDoneSec": 0.4,
                 "steps": [step("inherit", cueId="posdrv_stain_left", durKind="untilLine", lineId="line_center_A",
                                durSec=20, fadeOutSec=0.4)]},
                {"id": "L2C0#1", "name": "2周目A 顔のシミ（右の幕。背へ回る）", "at": "line", "lineId": "posdrv_A_behind",
                 "ifMissed": "skip", "policy": "hold", "wait": "segment", "once": True, "dismissible": True,
                 "steps": [step("inherit", cueId="posdrv_stain_right", durKind="untilZoneChange", fadeInSec=0.6)]},
            ]
        if chase and lap == 2 and cam == 2:
            def pov(n, sec, **kw):
                return step("clip", camera=2, cueId=f"pov_{n}", durSec=sec, transition="glitch", switchSfx=True, **kw)
            def behind(x, until):
                return step("live", camera=2, durKind="untilLine", lineId=until, durSec=10, transition="glitch",
                            cg="doll", cgMode="fixed", hasPlacement=True, placement={"x": x, "z": 0.74, "yawDeg": 90})
            seg["takes"] = [
                {"id": "L2C2#1", "name": "2周目C 追いつき（歩くたびに近づく）", "at": "line", "lineId": "posdrv_C_1",
                 "ifMissed": "skip", "policy": "yield", "wait": "segment", "once": True,
                 "steps": [pov(1, 0.7), behind(-0.42, "line_approach"),
                           pov(2, 0.6), behind(-0.20, "posdrv_C_3"),
                           pov(3, 0.6), behind(0.08, "posdrv_C_out"),
                           pov(4, 1.4, dollCall=True),
                           step("live", camera=2, durKind="untilZoneChange", transition="glitch", hasBgm=True,
                                bgm={"action": "play", "trackId": "bgm_LostPlace2", "loop": True, "fadeInSec": 3.0, "fadeOutSec": 3.0})]},
            ]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="当てる木（検証用の木）")
    ap.add_argument("--chase", action="store_true", help="2 周目 C の追いつきも位置で動く形にする")
    a = ap.parse_args()
    root = Path(a.root).resolve()
    if root == MAIN.resolve():
        sys.exit("メインの木には当てない（本番の台本を書き換えない）。検証用の木を指す")
    show_p = root / "Assets/StreamingAssets/show/show.json"
    man_p = root / "Assets/StreamingAssets/show/manifest.json"
    assets = root / "tools/twin/assets"
    spec_p = root / "tools/twin/assets.json"
    for p in (show_p, man_p, spec_p, assets / "twin_stainA_idle.png"):
        if not p.exists():
            sys.exit(f"無い: {p}（先に py -3.11 tools/twin-tree.py sync）")

    plate = imread(root / "tools/twin/plates/twin_A_plate.jpg")
    imwrite(assets / "posdrv_stainA_left_mask.png", frame_mask(LEFT_FACE))
    imwrite(assets / "posdrv_stainA_right_mask.png", frame_mask(RIGHT_FACE))
    imwrite(assets / "posdrv_stainA_peek_mask.png", frame_mask(LEFT_PEEK))
    imwrite(assets / "posdrv_stainA_right.png", make_right_face(assets, plate))

    spec = json.loads(spec_p.read_text(encoding="utf-8"))
    spec["cues"].update({
        "posdrv_stain_left": {"source": "twin_stainA_idle.png", "mask": "posdrv_stainA_left_mask.png"},
        "posdrv_stain_left_bulge": {"source": "twin_stainA_bulge.png", "mask": "posdrv_stainA_left_mask.png"},
        "posdrv_stain_left_peek": {"source": "twin_stainA_doll.png", "mask": "posdrv_stainA_peek_mask.png"},
        "posdrv_stain_right": {"source": "posdrv_stainA_right.png", "mask": "posdrv_stainA_right_mask.png"},
    })
    # 旧い cue は同じ素材の名前を取り合うので、台本に残っていても当てはめから外す
    for cid in ("cue_stain_A", "stain_A_press_bulge", "stain_A_done_doll"):
        spec["cues"].pop(cid, None)
    spec_p.write_text(json.dumps(spec, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")

    raw = show_p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    show = json.loads(raw.decode("utf-8-sig"))
    patch_show(show, a.chase)
    data = json.dumps(show, ensure_ascii=False).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    show_p.write_bytes(data)
    man = json.loads(man_p.read_text(encoding="utf-8-sig"))
    man["configSha256"] = hashlib.sha256(data).hexdigest()
    man_p.write_text(json.dumps(man, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print(f"[variant] 当てた: {root}（2 周目 A{' と 2 周目 C' if a.chase else ''}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
