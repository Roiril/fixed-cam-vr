"""Package an ImageGen still mockup; preserve the adopted screen aperture and provenance.

This is format/compositing preparation of a generated still, not a reusable live filter.
"""
from pathlib import Path
import argparse
import base64
import hashlib
import json
import os
import shutil
import sys

import cv2
import numpy as np
from PIL import Image, ImageOps

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study")
BASE = ROOT / "still/round-11/base-B.png"
UNDERLAY = ROOT / "still/round-23/B-standard-on-black.png"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def atomic_text(path, text):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8", newline="\n")
    os.replace(temporary, path)


def png(image, path):
    temporary = path.with_name(path.name + ".tmp.png")
    image.save(temporary)
    os.replace(temporary, path)


def geometry_diagnostic(source, generated, alpha):
    orb = cv2.ORB_create(nfeatures=5000, fastThreshold=9)
    mask = cv2.erode((alpha >= 250).astype(np.uint8) * 255, np.ones((61, 61), np.uint8))
    a = cv2.GaussianBlur(np.array(source.convert("L")), (0, 0), 1)
    b = cv2.GaussianBlur(np.array(generated.convert("L")), (0, 0), 1)
    kp_a, da = orb.detectAndCompute(a, mask)
    kp_b, db = orb.detectAndCompute(b, mask)
    record = {"method": "ORB on blurred interior; diagnostic only, not a geometry verdict",
              "features": [len(kp_a), len(kp_b)], "geometry_correction_applied": False}
    if da is None or db is None:
        return record
    pairs = cv2.BFMatcher(cv2.NORM_HAMMING).knnMatch(da, db, k=2)
    good = [m for pair in pairs if len(pair) == 2 for m, n in [pair] if m.distance < .75 * n.distance]
    record["ratio_matches"] = len(good)
    if len(good) < 8:
        return record
    pa = np.float32([kp_a[m.queryIdx].pt for m in good])
    pb = np.float32([kp_b[m.trainIdx].pt for m in good])
    matrix, inliers = cv2.estimateAffinePartial2D(pa, pb, method=cv2.RANSAC, ransacReprojThreshold=5)
    if matrix is not None:
        keep = inliers[:, 0].astype(bool)
        record.update({"affine_diagnostic": matrix.tolist(), "inliers": int(keep.sum()),
                       "matched_point_displacement_median_px": float(np.median(np.linalg.norm(pa[keep] - pb[keep], axis=1))),
                       "inlier_input_bbox": [*pa[keep].min(0).tolist(), *pa[keep].max(0).tolist()]})
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--generated", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--register", action="store_true", help="Undo a small global affine drift using the fixed underlay; no local geometry edits")
    args = parser.parse_args()
    out = args.out
    out.mkdir(parents=True, exist_ok=True)
    if (out / "mockup-on-black.png").exists():
        raise FileExistsError("Keep judged mockups immutable; choose a new round directory")
    prompt = out / "prompt.txt"
    if not prompt.is_file():
        raise FileNotFoundError("Save the exact ImageGen prompt in the round directory first")
    raw_copy = out / "generated-raw.png"
    if raw_copy.exists():
        if sha(raw_copy) != sha(args.generated):
            raise ValueError("Existing generated source differs")
    else:
        shutil.copyfile(args.generated, raw_copy)
    original = Image.open(BASE).convert("RGBA")
    generated = Image.open(raw_copy).convert("RGB")
    ratio_error = abs((generated.width / generated.height) / (original.width / original.height) - 1)
    if ratio_error > .002:
        raise ValueError(f"Generated aspect ratio changed: {ratio_error}")
    fitted = ImageOps.contain(generated, original.size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", original.size, (18, 18, 18))
    canvas.paste(fitted, ((original.width - fitted.width) // 2, (original.height - fitted.height) // 2))
    registration = {"applied": False}
    if args.register:
        raw_geometry = geometry_diagnostic(Image.open(UNDERLAY), canvas, np.array(original)[:, :, 3])
        if raw_geometry.get("inliers", 0) < 200:
            raise ValueError("Insufficient support for global format registration")
        matrix = np.array(raw_geometry["affine_diagnostic"], dtype=np.float64)
        scale = float(np.hypot(matrix[0, 0], matrix[1, 0]))
        if not .94 <= scale <= 1.06 or max(abs(matrix[:, 2])) > 40:
            raise ValueError("Generated drift exceeds limited registration scope")
        inverse = cv2.invertAffineTransform(matrix)
        canvas = Image.fromarray(cv2.warpAffine(np.array(canvas), inverse, original.size,
                                              flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT,
                                              borderValue=(18, 18, 18)))
        registration = {"applied": True, "method": "single global inverse similarity affine, estimated from blurred underlay ORB inliers; no local warp or feature replacement",
                        "source_diagnostic": raw_geometry, "inverse_matrix": inverse.tolist()}
    png(canvas, out / "fitted-generated.png")
    base = np.array(original)
    rgb = np.array(canvas).astype(float)
    distance = cv2.distanceTransform((base[:, :, 3] > 0).astype(np.uint8), cv2.DIST_L2, 5)
    u = np.clip((distance - 2) / 18, 0, 1)
    fade = u * u * (3 - 2 * u)
    result = base.copy()
    result[:, :, :3] = np.floor(base[:, :, :3] + (rgb - base[:, :, :3]) * fade[:, :, None] + .5).astype(np.uint8)
    hidden = base[:, :, 3] == 0
    boundary = (base[:, :, 3] > 0) & (distance <= 2)
    assert np.array_equal(result[:, :, 3], base[:, :, 3])
    assert np.array_equal(result[hidden], base[hidden])
    assert np.array_equal(result[boundary], base[boundary])
    final = Image.fromarray(result)
    png(final, out / "mockup.png")
    black = Image.new("RGBA", original.size, (18, 18, 18, 255))
    black.alpha_composite(final)
    png(black.convert("RGB"), out / "mockup-on-black.png")
    baseline_black = Image.new("RGBA", original.size, (18, 18, 18, 255))
    baseline_black.alpha_composite(original)
    images = {name: "data:image/png;base64," + base64.b64encode(path.read_bytes()).decode()
              for name, path in {"base": ROOT / "still-mockup/base-B-on-black.png", "filter": UNDERLAY,
                                 "mockup": out / "mockup-on-black.png"}.items()}
    css = Path("C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css").read_text(encoding="utf-8")
    preview = '''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>画像編集による静止画の見た目試作</title><style>CSS
img{display:block;width:100%;height:auto}.image{max-width:1280px}select{font:inherit;padding:8px;color:var(--ink);background:var(--surface)}</style><div class="wrap"><h1>静止画の見た目試作</h1><p>画像生成AIで追加加工の見た目を編集した静止画です。実行できるフィルタ版とは分けて保存しています。</p><select id="choice" aria-label="表示する画像"><option value="mockup">画像編集による見た目試作</option><option value="filter">実行できる追加加工（独立比較83%）</option><option value="base">現行の描画</option></select></div><div class="image"><img id="image" alt="追加加工の比較"></div><script>const images=IMAGES,s=document.getElementById('choice');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>'''
    atomic_text(out / "preview.html", preview.replace("CSS", css).replace("IMAGES", json.dumps(images)))
    manifest = {"kind": "image_edit_mockup", "mode": "built-in image_gen editing",
                "is_reusable_filter": False, "executable_filter_highest_similarity": 83,
                "geometry_precheck_required_before_similarity": True,
                "prompt_sha256": sha(prompt), "generated_raw_size": list(generated.size),
                "output_size": list(original.size), "fitted_size": list(fitted.size), "aspect_ratio_relative_error": ratio_error,
                "format_preparation": "isotropic fit; no crop; original alpha/hidden RGB/inner 2px restored; 2..20px boundary blend",
                "global_format_registration": registration,
                "alpha_exact": True, "hidden_RGB_exact": True, "boundary_pixels_exact": int(boundary.sum()),
                "reference": {"path": str(ROOT / "sources/Screenshot_20261101-004612.png"), "sha256": sha(ROOT / "sources/Screenshot_20261101-004612.png")},
                "base": {"path": str(BASE), "sha256": sha(BASE)},
                "filter_underlay": {"path": str(UNDERLAY), "sha256": sha(UNDERLAY), "similarity": 83},
                "generated_source": {"path": str(args.generated), "copied_sha256": sha(raw_copy)},
                "selected_candidate_path": str(out / "mockup-on-black.png"),
                "candidate_sha256": sha(out / "mockup-on-black.png"),
                "geometry_diagnostic": geometry_diagnostic(Image.open(UNDERLAY), canvas, base[:, :, 3]),
                "source_sha256": sha(Path(__file__))}
    atomic_text(out / "manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
