"""Verify explicitly selected study artifacts; visual similarity remains a separate result."""
from pathlib import Path
import argparse
import ast
import hashlib
import json
import os
import subprocess
import sys

import cv2
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def verify_file(row, key="path"):
    path = Path(row[key])
    assert path.is_file(), str(path)
    assert digest(path) == row["sha256"], f"Hash changed: {path}"
    if "bytes" in row:
        assert path.stat().st_size == row["bytes"], f"Size changed: {path}"
    return path


def main():
    parser = argparse.ArgumentParser(__doc__)
    for name in ("output", "still-image", "still-view", "base-image", "still-manifest", "motion-dir", "spatial-verification"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--audit", action="append", type=Path, required=True)
    parser.add_argument("--still-rgba-hash-key", default="output_sha256")
    parser.add_argument("--manual-review-delivery", action="store_true",
                        help="User requested stopping iterations and reviewing the current artifacts")
    args = parser.parse_args()
    assert args.output.name == "revision-02"
    report = read(args.output / "report-manifest.json")
    assert digest(report["report"]) == report["report_sha256"]
    report_qa = read(args.output / "report-check.json")
    assert report_qa["pass"] is True and not report_qa["pageErrors"]
    assert report_qa["report_sha256"] == report["report_sha256"]
    used = report["used_source_hashes"]
    for path, row in used.items():
        verify_file({"path": path, **row})

    base = np.array(Image.open(args.base_image).convert("RGBA"))
    actual = np.array(Image.open(args.still_image).convert("RGBA"))
    assert base.shape == actual.shape == (1030, 1280, 4)
    visible = base[:, :, 3] > 0
    inner = visible & (cv2.distanceTransform(visible.astype(np.uint8), cv2.DIST_L2, 5) <= 2)
    assert int(inner.sum()) == 7394
    assert np.array_equal(actual[:, :, 3], base[:, :, 3])
    assert np.array_equal(actual[~visible], base[~visible])
    assert np.array_equal(actual[inner], base[inner])
    opaque = Image.new("RGBA", (1280, 1030), (0, 0, 0, 255))
    opaque.alpha_composite(Image.fromarray(actual))
    assert np.array_equal(np.array(opaque.convert("RGB")), np.array(Image.open(args.still_view).convert("RGB")))
    static = read(args.still_manifest)
    assert digest(args.still_image) == static[args.still_rgba_hash_key]
    assert digest(args.base_image) == static["input_sha256"]

    delivery = read(args.motion_dir / "delivery-manifest.json")
    for row in delivery["artifacts"]:
        verify_file(row)
    for row in delivery["sources"]:
        verify_file(row)
        verify_file(row, "snapshot")
    movie = args.motion_dir / "motion-revision.mp4"
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(movie)], check=True, capture_output=True, text=True, encoding="utf-8").stdout)
    videos = [s for s in probe["streams"] if s["codec_type"] == "video"]
    assert len(videos) == 1 and not any(s["codec_type"] == "audio" for s in probe["streams"])
    stream = videos[0]
    assert (stream["width"], stream["height"], stream["codec_name"], stream["r_frame_rate"], int(stream["nb_frames"])) == (1170, 540, "h264", "30/1", 469)

    spatial = read(args.spatial_verification)
    scores = {}
    expected = {args.still_view.resolve(): "still", movie.resolve(): "motion"}
    for path in args.audit:
        audit = read(path)
        target = Path(audit["selected_candidate_path"]).resolve()
        assert target in expected, f"Audit belongs to another candidate: {target}"
        assert digest(target) == audit["candidate_sha256"]
        value = float(audit["overall_similarity"])
        assert 0 <= value <= 100
        scores[expected[target]] = value
    assert "still" in scores
    if not args.manual_review_delivery:
        assert set(scores) == {"still", "motion"}

    syntax = {"python": 0, "javascript": 0, "json": 0}
    source_hashes = {}
    for source in sorted(HERE.rglob("*")):
        if not source.is_file() or "__pycache__" in source.parts:
            continue
        source_hashes[source.relative_to(HERE).as_posix()] = digest(source)
        if source.suffix == ".py":
            ast.parse(source.read_text(encoding="utf-8-sig"), filename=str(source))
            syntax["python"] += 1
        elif source.suffix in (".js", ".mjs", ".cjs"):
            subprocess.run(["node", "--check", str(source)], check=True, capture_output=True)
            syntax["javascript"] += 1
        elif source.suffix == ".json":
            read(source)
            syntax["json"] += 1

    old_root = args.output.parent
    old = read(old_root / "manifest.json")
    assert digest(old_root / old["report"]["path"]) == old["report"]["sha256"]
    for row in old["sources"]:
        assert digest(HERE.parent / row["path"]) == row["sha256"]
    result = {"technical_verification": "PASS", "production_integration": False,
              "report_sha256": report["report_sha256"], "report_inputs_verified": len(used),
              "report_browser_checks": len(report_qa["checks"]),
              "motion_artifacts_verified": len(delivery["artifacts"]), "motion_source_snapshots_verified": len(delivery["sources"]),
              "old_report_and_source_hashes_unchanged": len(old["sources"]),
              "still_alpha_hidden_and_inner7394_exact": True, "still_composite_exact": True,
              "movie": {"size": [1170, 540], "fps": 30, "frames": 469, "audio": False, "duration": float(probe["format"]["duration"])},
              "spatial_verification_file": str(args.spatial_verification), "spatial_verification_sha256": digest(args.spatial_verification),
              "spatial_metric_keys": list(spatial), "syntax": syntax, "source_hashes": source_hashes,
              "independent_subjective_scores": scores, "completion_threshold": 85,
              "requested_similarity_threshold_met": set(scores) == {"still", "motion"} and min(scores.values()) >= 85,
              "delivery_status": "current_artifacts_for_user_visual_review" if args.manual_review_delivery else "evaluated_artifacts",
              "further_iterations_stopped_at_user_request": args.manual_review_delivery,
              "visual_adoption_by_user": "not_judged"}
    tmp = args.output / "delivery-verification.tmp.json"
    tmp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    os.replace(tmp, args.output / "delivery-verification.json")
    print(json.dumps({k: v for k, v in result.items() if k != "source_hashes"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
