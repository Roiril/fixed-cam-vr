#!/usr/bin/env python3
"""Extract long edge chains from a reference still for fit_ref_lens.py, plus a numbered check image."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import cv2
import numpy as np


def split_at_corners(points, step=12, min_len=45):
    pieces = []
    start = 0
    for i in range(step, len(points) - step, 4):
        before = points[i] - points[i - step]
        after = points[i + step] - points[i]
        cos_turn = before @ after / (np.linalg.norm(before) * np.linalg.norm(after) + 1e-9)
        if cos_turn < 0.88:
            if i - start > min_len:
                pieces.append(points[start : i - 4])
            start = i + 4
    if len(points) - start > min_len:
        pieces.append(points[start:])
    return pieces


def extract(gray):
    edges = cv2.Canny(cv2.GaussianBlur(gray, (0, 0), 1.0), 40, 110)
    contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_NONE)
    chains = []
    for contour in contours:
        points = contour[:, 0, :].astype(np.float64)
        if len(points) < 60:
            continue
        # A closed contour walks both sides of the edge: keep one side.
        if np.linalg.norm(points[0] - points[-1]) < 3:
            points = points[: len(points) // 2]
        for piece in split_at_corners(points):
            chord = np.linalg.norm(piece[-1] - piece[0])
            if chord > 55 and chord / len(piece) > 0.65:
                chains.append(piece[::3].tolist())
    return chains


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    image = cv2.imdecode(np.fromfile(args.image, np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise SystemExit(f"cannot read {args.image}")
    chains = extract(cv2.cvtColor(image, cv2.COLOR_BGR2GRAY))
    (out / "ref_chains.json").write_text(json.dumps(chains), encoding="utf-8")

    check = cv2.resize(image, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
    rng = np.random.default_rng(1)
    for i, chain in enumerate(chains):
        color = tuple(int(x) for x in rng.integers(60, 255, 3))
        points = (np.array(chain) * 2).astype(np.int32)
        cv2.polylines(check, [points], False, color, 2)
        middle = tuple(int(v) for v in points[len(points) // 2])
        cv2.putText(check, str(i), middle, cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
    ok, buffer = cv2.imencode(".png", check)
    if not ok:
        raise OSError("cannot encode the check image")
    buffer.tofile(str(out / "ref_chains.png"))
    print("chains", len(chains))


if __name__ == "__main__":
    main()
