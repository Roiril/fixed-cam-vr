"""配信スマホに出す「カーテンの輪郭」の補助線を、ツインのカメラ姿勢から作る（Blender 不要・数秒）。

    py -3.11 tools/twin/curtain_guide.py                     # JSON を書く（既定は配信アプリの assets）
    py -3.11 tools/twin/curtain_guide.py --check <フォルダ>   # 保存した無人写真へ重ねた画も書く

カメラの姿勢は rig.json（保存した無人写真から plate_fit.py で解いた値）。カーテンの形は twin_scene.py の
build_booth と同じ（L の 2 辺。角から 0.012m・端の 0.004m 手前まで、床から竿の 3cm 下まで）。
rig.json が写真に合っている（check_rig.py の RMS）限り、輪郭は「本番の素材を作ったときの据え方」を指す。

出力は 640x480 の配信画像に対する 0..1 の座標の折れ線。配信アプリは映像の矩形（GuideSpec.fitCenter）へ引く。
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import check_rig as cr  # noqa: E402  (cam_axes / project を共有する。投影の式を 2 つにしない)

W, H = 640.0, 480.0
FRAME_TOP = 1.853          # twin_common.FRAME_TOP（上桟の高さ）
CLOTH_TOP = FRAME_TOP - 0.03  # twin_scene.build_booth の cloth_h
CLOTH_BOTTOM = 0.0
INSET_CORNER = 0.012       # 角の側の引き（build_booth）
INSET_END = 0.004          # 端の側の引き
NEAR = 0.05                # これより手前（カメラの後ろ側）は引かない [m]
STEP_M = 0.03              # 辺を刻む間隔。直線でも分けておくと画の外への出入りで切れ目が正しく出る
DEFAULT_OUT = ROOT.parent.parent / "Mobile" / "fixed-cam-streamer" / "app" / "src" / "main" / "assets" / "curtain_guide.json"


def curtain_edges(show: dict) -> list[tuple[str, np.ndarray, np.ndarray]]:
    """カーテン 2 枚の外周（上・下・角の縦・端の縦）。座標は Unity の (x, y, z)。"""
    edges = []
    for w in show["layout"]["room"]["walls"]:
        a = np.array([w["x1"], w["z1"]], float)
        b = np.array([w["x2"], w["z2"]], float)
        t = (b - a) / np.linalg.norm(b - a)
        p, q = a + t * INSET_CORNER, b - t * INSET_END

        def pt(xz, y):
            return np.array([xz[0], y, xz[1]], float)

        edges += [
            (f"{w['id']}:top", pt(p, CLOTH_TOP), pt(q, CLOTH_TOP)),
            (f"{w['id']}:hem", pt(p, CLOTH_BOTTOM), pt(q, CLOTH_BOTTOM)),
            (f"{w['id']}:end", pt(q, CLOTH_BOTTOM), pt(q, CLOTH_TOP)),
            (f"{w['id']}:corner", pt(p, CLOTH_BOTTOM), pt(p, CLOTH_TOP)),
        ]
    return edges


def depth(cam, p) -> float:
    o, _, _, f = cr.cam_axes(cam)
    return float((p - o) @ f)


def project_edge(cam, a, b) -> list[list[list[float]]]:
    """1 辺を刻んで投影する。カメラの後ろへ回った所で折れ線を切る。座標は 0..1。"""
    n = max(2, int(math.ceil(np.linalg.norm(b - a) / STEP_M)) + 1)
    lines, cur = [], []
    for s in np.linspace(0.0, 1.0, n):
        p = a + s * (b - a)
        if depth(cam, p) < NEAR:
            if len(cur) > 1:
                lines.append(cur)
            cur = []
            continue
        uv = cr.project(cam, p, W, H)
        cur.append([round(float(uv[0]) / W, 5), round(float(uv[1]) / H, 5)])
    if len(cur) > 1:
        lines.append(cur)
    return lines


def visible(line) -> bool:
    return any(0.0 <= u <= 1.0 and 0.0 <= v <= 1.0 for u, v in line)


def build(show: dict, rig: dict) -> dict:
    out = {
        "_source": "fixed-cam-vr tools/twin/curtain_guide.py が rig.json と show.json の壁から作る。手で直さない",
        "srcW": int(W),
        "srcH": int(H),
        "cameras": {},
    }
    for cam in rig["cameras"]:
        lines = []
        for _, a, b in curtain_edges(show):
            lines += [ln for ln in project_edge(cam, a, b) if visible(ln)]
        out["cameras"][cam["id"]] = {"lines": lines}
    return out


def draw_check(guide: dict, out_dir: Path) -> list[Path]:
    """保存した無人写真へ輪郭を重ねる（写真の幕の縁に線が乗っていれば正しい）。"""
    from PIL import Image, ImageDraw

    out_dir.mkdir(parents=True, exist_ok=True)
    paths = []
    for cid, g in guide["cameras"].items():
        im = Image.open(_plate_path(cid)).convert("RGB")
        if im.size != (int(W), int(H)):
            raise SystemExit(f"{cid} の写真が {im.size}。640x480 でないと比べられない")
        d = ImageDraw.Draw(im)
        for ln in g["lines"]:
            d.line([(u * W, v * H) for u, v in ln], fill=(80, 255, 230), width=2)
        d.rectangle([0, 0, 260, 22], fill=(0, 0, 0))
        d.text((6, 4), f"{cid}  curtain guide  lines={len(g['lines'])}", fill=(255, 255, 255))
        p = out_dir / f"curtain_guide_{cid}.png"
        im.save(p)
        paths.append(p)
    return paths


def _plate_path(cid: str) -> Path:
    name = cr.OBS["plates"][cid]
    local = HERE / "plates" / name
    return local if local.exists() else ROOT / "Assets" / "StreamingAssets" / "show" / "assets" / name


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--check", type=Path, help="無人写真へ重ねた画を書くフォルダ")
    a = ap.parse_args()
    guide = build(cr.SHOW, cr.RIG)
    for cid, g in guide["cameras"].items():
        if not g["lines"]:
            print(f"✗ {cid}: 画に入る輪郭が 1 本も無い（rig.json の向きを疑う）")
            return 1
    a.out.parent.mkdir(parents=True, exist_ok=True)
    tmp = a.out.with_suffix(".tmp")
    tmp.write_text(json.dumps(guide, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    json.loads(tmp.read_text(encoding="utf-8"))
    tmp.replace(a.out)
    print(f"書いた: {a.out}  " + "  ".join(f"{c}={len(g['lines'])}本" for c, g in guide["cameras"].items()))
    if a.check:
        for p in draw_check(guide, a.check):
            print(f"重ねた画: {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
