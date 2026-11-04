#!/usr/bin/env python3
"""Apply the lens solved from the reference to the camera C plate and lay the versions side by side.

Usage:
    py -3.11 tools/lens-study/lens_study.py --plate <jpg> --reference <jpg> --fit <lens-fit.json> \
        --twin Logs/twin-cycles/lens-study --out <dir>

Every version is a function from output pixels to plate pixels. A fisheye is defined by how far off
axis each output pixel looks. The plate is a pinhole with a known focal length, so that angle tells
which plate pixel to read.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

OUT_W, OUT_H = 960, 720
PLATE_W, PLATE_H = 640, 480
PLATE_F = 288.33  # tools/twin/rig.json _fit.fPx
CURRENT_K = 0.155  # CrtSignalProfile.LensWarp
FONT = "C:/Windows/Fonts/YuGothM.ttc"


def read_image(path):
    # cv2.imread and cv2.imwrite fail on non-ASCII Windows paths; imwrite fails silently.
    try:
        return cv2.imdecode(np.fromfile(str(path), np.uint8), cv2.IMREAD_COLOR)
    except OSError:
        return None


def write_image(path, img):
    ok, buf = cv2.imencode(Path(path).suffix, img)
    if not ok:
        raise OSError(f"cannot encode {path}")
    buf.tofile(str(path))


def equisolid_theta(rho):
    return 2 * np.arcsin(np.clip(rho / 2, 0, 1))


def out_grid():
    xs, ys = np.meshgrid(np.arange(OUT_W, dtype=np.float64) + 0.5, np.arange(OUT_H, dtype=np.float64) + 0.5)
    return xs - OUT_W / 2, ys - OUT_H / 2


def fisheye_map(f_out, f_plate):
    """Output equisolid lens with focal f_out, reading a pinhole plate. Both focals are in output pixels."""
    x, y = out_grid()
    r = np.hypot(x, y)
    rho = r / f_out
    theta = equisolid_theta(rho)
    r_u = f_plate * (PLATE_W / OUT_W) * np.tan(np.minimum(theta, np.radians(89.0)))
    scale = r_u / np.maximum(r, 1e-9)
    mx = x * scale + PLATE_W / 2
    my = y * scale + PLATE_H / 2
    valid = (rho < 2) & (theta < np.radians(89.0))
    mx = np.where(valid, mx, -1e4)
    my = np.where(valid, my, -1e4)
    return mx.astype(np.float32), my.astype(np.float32)


def current_shader_map(k):
    """ScreenComposite: sample = p / (1 + k * dot(p, p) / 2), p in +-1 over the 4:3 image."""
    x, y = out_grid()
    px, py = x / (OUT_W / 2), y / (OUT_H / 2)
    d = 1 + k * (px * px + py * py) / 2
    return ((px / d + 1) * PLATE_W / 2).astype(np.float32), ((py / d + 1) * PLATE_H / 2).astype(np.float32)


def plain_map():
    x, y = out_grid()
    return ((x / (OUT_W / 2) + 1) * PLATE_W / 2).astype(np.float32), ((y / (OUT_H / 2) + 1) * PLATE_H / 2).astype(np.float32)


def sample(img, mx, my, border=(0, 0, 0)):
    return cv2.remap(img, mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT, borderValue=border)


def inside(mx, my):
    return (mx >= 0) & (mx <= PLATE_W - 1) & (my >= 0) & (my <= PLATE_H - 1)


def grid_image():
    g = np.full((PLATE_H, PLATE_W, 3), 235, np.uint8)
    for x in range(0, PLATE_W + 1, 40):
        cv2.line(g, (min(x, PLATE_W - 1), 0), (min(x, PLATE_W - 1), PLATE_H - 1), (40, 40, 40), 1, cv2.LINE_AA)
    for y in range(0, PLATE_H + 1, 40):
        cv2.line(g, (0, min(y, PLATE_H - 1)), (PLATE_W - 1, min(y, PLATE_H - 1)), (40, 40, 40), 1, cv2.LINE_AA)
    return g


def local_scale(mx, my):
    """Output pixels per plate pixel (normalised so plain fit = 1), at centre and at the right edge middle."""
    plain = OUT_W / PLATE_W
    cy, cx = OUT_H // 2, OUT_W // 2
    dcx = (mx[cy, cx + 1] - mx[cy, cx - 1]) / 2
    centre = 1 / abs(dcx) / plain
    ex = OUT_W - 40
    de = (mx[cy, ex + 1] - mx[cy, ex - 1]) / 2
    edge = 1 / abs(de) / plain if abs(de) > 1e-6 else float("nan")
    return float(centre), float(edge)


def bow(mx, my):
    """How far a straight line bends, as a share of frame height.

    Take the line through the point a quarter of the way down the centre column that is horizontal
    in the source. Measure how far it has moved at the frame's right edge. + bends away from the
    centre at the middle (barrel, as in the reference). - is the opposite (pincushion).
    """
    y0 = OUT_H // 4
    target = my[y0, OUT_W // 2]
    column = my[:, OUT_W - 1].astype(np.float64)
    ok = column > -1e3
    i = int(np.argmin(np.where(ok, np.abs(column - target), 1e12)))
    return float((i - y0) / OUT_H)


def coverage(mx, my):
    return float(inside(mx, my).mean())


def pano_map(f_out, axis_fix):
    """Output equisolid lens reading the twin's full-sphere panorama."""
    x, y = out_grid()
    r = np.hypot(x, y)
    theta = equisolid_theta(r / f_out)
    s = np.sin(theta) / np.maximum(r, 1e-9)
    # Camera space: +x right, +y down in the image, +z forward.
    return axis_fix(x * s, y * s, np.cos(theta))


def equirect(dx, dy, dz, pano_w, pano_h):
    # Cycles' equirect puts the camera's view direction at the centre column and camera up at the top.
    lon = np.arctan2(dx, dz)
    lat = np.arcsin(np.clip(-dy, -1, 1))
    mx = (lon / (2 * np.pi) + 0.5) * pano_w
    my = (0.5 - lat / np.pi) * pano_h
    return mx.astype(np.float32), my.astype(np.float32)


def twin_exposure(hdr):
    """Exposure from one render, so the pinhole and the panorama get the same display transform."""
    lin = np.maximum(np.nan_to_num(hdr.astype(np.float32)), 0)
    return 0.9 / max(float(np.percentile(lin.mean(axis=2), 97)), 1e-4)


def finish_twin(hdr, exposure):
    """Display transform for the preview: exposure and gamma only."""
    lin = np.maximum(np.nan_to_num(hdr.astype(np.float32)), 0)
    img = np.clip(lin * exposure, 0, 1) ** (1 / 2.2)
    return (img[:, :, ::-1] * 255 + 0.5).astype(np.uint8)


def match_to_plate(img, reference, mask):
    """Match per-channel mean and spread inside the region both images show."""
    out = img.astype(np.float32)
    for c in range(3):
        a = out[:, :, c][mask]
        b = reference[:, :, c][mask].astype(np.float32)
        out[:, :, c] = (out[:, :, c] - a.mean()) * (b.std() / max(a.std(), 1e-3)) + b.mean()
    return np.clip(out, 0, 255).astype(np.uint8)


def feather(mask, width):
    dist = cv2.distanceTransform(mask.astype(np.uint8), cv2.DIST_L2, 5)
    return np.clip(dist / width, 0, 1)[:, :, None]


def label(img, lines, size=24):
    im = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    d = ImageDraw.Draw(im)
    font = ImageFont.truetype(FONT, size)
    y = 8
    for line in lines:
        box = d.textbbox((10, y), line, font=font)
        d.rectangle((box[0] - 5, box[1] - 4, box[2] + 5, box[3] + 4), fill=(0, 0, 0))
        d.text((10, y), line, font=font, fill=(255, 255, 255))
        y = box[3] + 8
    return cv2.cvtColor(np.array(im), cv2.COLOR_RGB2BGR)


def fit_to_tile(img, w, h):
    """Contain without changing the aspect; the rest stays neutral grey."""
    ih, iw = img.shape[:2]
    s = min(w / iw, h / ih)
    r = cv2.resize(img, (int(iw * s), int(ih * s)), interpolation=cv2.INTER_AREA)
    tile = np.full((h, w, 3), 70, np.uint8)
    oy, ox = (h - r.shape[0]) // 2, (w - r.shape[1]) // 2
    tile[oy : oy + r.shape[0], ox : ox + r.shape[1]] = r
    return tile


def sheet(tiles, cols, path, tile_w=960, tile_h=720, gap=10):
    rows = (len(tiles) + cols - 1) // cols
    canvas = np.full((rows * tile_h + (rows + 1) * gap, cols * tile_w + (cols + 1) * gap, 3), 24, np.uint8)
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        y, x = gap + r * (tile_h + gap), gap + c * (tile_w + gap)
        canvas[y : y + tile_h, x : x + tile_w] = t
    write_image(str(path), canvas)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--plate", required=True)
    ap.add_argument("--reference", required=True)
    ap.add_argument("--fit", required=True)
    ap.add_argument("--twin", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "versions").mkdir(exist_ok=True)

    plate = read_image(a.plate)
    reference = read_image(a.reference)
    if plate is None or reference is None:
        raise SystemExit("missing input image")
    assert plate.shape[:2] == (PLATE_H, PLATE_W), plate.shape
    report = json.loads(Path(a.fit).read_text(encoding="utf-8"))
    fit = report["fits"]["equisolid"]
    ref_h, ref_w = reference.shape[:2]
    ref_f_over_w = fit["f"] / ref_w
    # Same lens on the 960 wide output: the reference's focal length as a share of the frame width.
    f_ref = ref_f_over_w * OUT_W
    f_true = PLATE_F * OUT_W / PLATE_W

    def plate_edges(f_out):
        """Output radius (px) reached by the plate's right edge, top edge and corner."""
        return {n: float(2 * f_out * np.sin(np.arctan(u / PLATE_F) / 2)) for n, u in (("right", 320), ("top", 240), ("corner", 400))}

    rows = {}

    def version(key, title, mx, my, note=""):
        img = sample(plate, mx, my)
        centre, edge = local_scale(mx, my)
        rows[key] = {"title": title, "centreScale": round(centre, 3), "edgeScale": round(edge, 3), "bow": round(bow(mx, my), 4), "coverage": round(coverage(mx, my), 4), "note": note}
        write_image(str(out / "versions" / f"{key}.png"), img)
        write_image(str(out / "versions" / f"{key}_grid.png"), sample(grid_image(), mx, my))
        return img

    # Pinhole plate, no warp.
    mx, my = plain_map()
    v0 = version("0_plain", "そのまま", mx, my)
    # What the game does today.
    mx, my = current_shader_map(CURRENT_K)
    v1 = version("1_current", f"いまの本編 k={CURRENT_K}", mx, my)
    # Same lens, same central scale as the reference.
    mx, my = fisheye_map(f_ref, f_true)
    v2 = version("2_same_lens", "参照と同じレンズ・同じ倍率", mx, my)

    # Same lens model at the plate's true field, enlarged until something covers the frame.
    edges = plate_edges(f_true)  # radii at the true scale in output px, for f_out = f_true
    fit_width = (OUT_W / 2) / edges["right"]
    fit_corner = np.hypot(OUT_W / 2, OUT_H / 2) / edges["corner"]
    mx, my = fisheye_map(f_true * fit_width, f_true)
    v3 = version("3_fit_width", f"同じレンズ・幅を合わせる ×{fit_width:.2f}", mx, my)
    mx, my = fisheye_map(f_true * fit_corner, f_true)
    v4 = version("4_fit_corner", f"同じレンズ・角まで埋める ×{fit_corner:.2f}", mx, my)

    # Pretend the plate saw a wider field: the frame corner is theta_c off axis, plate corner at the frame corner.
    half_diag = float(np.hypot(OUT_W / 2, OUT_H / 2))
    fakes = {}
    for key, theta_c in (("5_wider_65", 65.0), ("6_wider_75", 75.0)):
        f_o = half_diag / (2 * np.sin(np.radians(theta_c) / 2))
        f_plate_fake = 400 / np.tan(np.radians(theta_c)) * OUT_W / PLATE_W
        mx, my = fisheye_map(f_o, f_plate_fake)
        fakes[key] = version(key, f"画角を偽る 角={theta_c:.0f}度", mx, my)

    # Twin: what a real lens with the reference's field would see from camera C.
    twin_dir = Path(a.twin)
    pinhole_hdr = np.load(twin_dir / "C_pinhole.npy")
    pano_hdr = np.load(twin_dir / "C_pano.npy")
    pano_h, pano_w = pano_hdr.shape[:2]
    exposure = twin_exposure(pinhole_hdr)
    pano8 = finish_twin(pano_hdr, exposure)
    pin8 = finish_twin(pinhole_hdr, exposure)

    def to_pano(dx, dy, dz):
        return equirect(dx, dy, dz, pano_w, pano_h)

    # Instrument check: the panorama resampled through the pinhole lens must land on the pinhole render.
    x, y = out_grid()
    n = np.sqrt(x * x + y * y + f_true * f_true)
    cmx, cmy = to_pano(x / n, y / n, f_true / n)
    resampled = cv2.remap(pano8, cmx, cmy, cv2.INTER_LINEAR, borderMode=cv2.BORDER_WRAP)
    a_gray = cv2.GaussianBlur(cv2.cvtColor(resampled, cv2.COLOR_BGR2GRAY), (0, 0), 2).astype(np.float32)
    b_gray = cv2.GaussianBlur(cv2.cvtColor(pin8, cv2.COLOR_BGR2GRAY), (0, 0), 2).astype(np.float32)
    # The right half differs on purpose: the panorama clips geometry nearer than 4 cm.
    half = OUT_W // 2
    corr_ok = float(np.corrcoef(a_gray[:, :half].ravel(), b_gray[:, :half].ravel())[0, 1])
    corr_flip = float(np.corrcoef(a_gray[::-1, :half].ravel(), b_gray[:, :half].ravel())[0, 1])
    print("axis check corr", round(corr_ok, 4), "negative control (rotated 180)", round(corr_flip, 4))
    if corr_ok < 0.9:
        raise SystemExit(f"panorama axis check failed: {corr_ok}")
    write_image(str(out / "axis-check.png"), np.hstack([resampled, pin8]))

    wmx, wmy = pano_map(f_ref, to_pano)
    twin_wide = cv2.remap(pano8, wmx, wmy, cv2.INTER_CUBIC, borderMode=cv2.BORDER_WRAP)
    # The twin's wall paint is not the real room's. Only its shapes count here, so drop its colour.
    twin_wide = cv2.cvtColor(cv2.cvtColor(twin_wide, cv2.COLOR_BGR2GRAY), cv2.COLOR_GRAY2BGR)
    pmx, pmy = fisheye_map(f_ref, f_true)
    mask = inside(pmx, pmy)
    plate_fish = sample(plate, pmx, pmy)
    twin_matched = match_to_plate(twin_wide, plate_fish, cv2.erode(mask.astype(np.uint8), np.ones((15, 15), np.uint8)).astype(bool))
    alpha = feather(mask, 26)
    v7 = (plate_fish * alpha + twin_matched * (1 - alpha)).astype(np.uint8)
    write_image(str(out / "versions" / "7_wide_plate_plus_twin.png"), v7)
    write_image(str(out / "versions" / "8_wide_twin_only.png"), twin_matched)
    rows["7_wide_plate_plus_twin"] = {"title": "実際に広く撮った場合（周辺はCG）", "centreScale": rows["2_same_lens"]["centreScale"], "coverage": 1.0, "realPixels": round(float(mask.mean()), 4)}

    # The part of the reference that sees the same field as the plate: same lens, same field.
    theta_right = np.arctan(320 / PLATE_F)
    theta_top = np.arctan(240 / PLATE_F)
    ref_half_w = 2 * fit["f"] * np.sin(theta_right / 2)
    ref_half_h = 2 * fit["f"] * np.sin(theta_top / 2)
    x0, x1 = int(fit["cx"] - ref_half_w), int(fit["cx"] + ref_half_w)
    y0, y1 = int(fit["cy"] - ref_half_h), int(fit["cy"] + ref_half_h)
    ref_crop = reference[max(y0, 0) : min(y1, ref_h), max(x0, 0) : min(x1, ref_w)]
    write_image(str(out / "versions" / "ref_same_field_crop.png"), ref_crop)

    # Reference placed with a box so its field can be compared to the plate's.
    ref_box = reference.copy()
    cv2.rectangle(ref_box, (x0, y0), (x1, y1), (60, 200, 255), 2, cv2.LINE_AA)

    # Lay out the sheet. Every tile is built from its real file; nothing substitutes for a missing input.
    tiles = [
        label(fit_to_tile(reference, OUT_W, OUT_H), ["参照 cam1", f"左右 {fit['angles']['left'] + fit['angles']['right']:.0f}度 / 対角 {fit['angles']['cornerTL'] + fit['angles']['cornerBR']:.0f}度 / 線の曲がり {rows['2_same_lens']['bow'] * 100:+.1f}%"]),
        label(v0, ["0 そのまま（Cの無人画像）", "左右 96度 / 対角 108度"]),
        label(v1, [f"1 いまの本編 k={CURRENT_K}", f"中央 ×{rows['1_current']['centreScale']:.2f} / 線の曲がり {rows['1_current']['bow'] * 100:+.1f}%（逆向き）"]),
        label(v2, ["2 参照と同じレンズ・同じ倍率", f"映る範囲 {rows['2_same_lens']['coverage'] * 100:.0f}% / 線の曲がり {rows['2_same_lens']['bow'] * 100:+.1f}%"]),
        label(v3, [f"3 同じレンズ・幅に合わせる", f"中央 ×{rows['3_fit_width']['centreScale']:.2f} / 映る範囲 {rows['3_fit_width']['coverage'] * 100:.0f}% / 線の曲がり {rows['3_fit_width']['bow'] * 100:+.1f}%"]),
        label(v4, [f"4 同じレンズ・角まで埋める", f"中央 ×{rows['4_fit_corner']['centreScale']:.2f} / 線の曲がり {rows['4_fit_corner']['bow'] * 100:+.1f}%"]),
        label(fakes["5_wider_65"], ["5 もっと広いレンズのふり 対角130度", f"中央 ×{rows['5_wider_65']['centreScale']:.2f} / 線の曲がり {rows['5_wider_65']['bow'] * 100:+.1f}%"]),
        label(fakes["6_wider_75"], ["6 もっと広いレンズのふり 対角150度", f"中央 ×{rows['6_wider_75']['centreScale']:.2f} / 線の曲がり {rows['6_wider_75']['bow'] * 100:+.1f}%"]),
        label(v7, ["7 本当に広いレンズで撮った場合", f"実写 {rows['7_wide_plate_plus_twin']['realPixels'] * 100:.0f}%・外側はツインのCG（灰色）"]),
    ]
    sheet(tiles, 3, out / "sheet.png")

    grid_tiles = []
    for key, name in (("0_plain", "0"), ("1_current", "1 いま"), ("3_fit_width", "3"), ("4_fit_corner", "4"), ("5_wider_65", "5"), ("6_wider_75", "6")):
        grid_tiles.append(label(read_image(str(out / "versions" / f"{key}_grid.png")), [name]))
    sheet(grid_tiles, 3, out / "grid-sheet.png")

    summary = {"refFit": {"f": fit["f"], "fOverWidth": ref_f_over_w, "angles": fit["angles"], "cx": fit["cx"], "cy": fit["cy"], "aspect": fit["aspect"]},
               "plate": {"fOverWidth": PLATE_F / PLATE_W, "edgesAtSameLens": plate_edges(f_ref)}, "fRefOut": f_ref, "fTrueOut": f_true, "axisCorr": corr_ok, "axisCorrFlip": corr_flip, "versions": rows}
    (out / "summary.json").write_text(json.dumps(summary, indent=1, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(summary, indent=1, ensure_ascii=False))


if __name__ == "__main__":
    main()
