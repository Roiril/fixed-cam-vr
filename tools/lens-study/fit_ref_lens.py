#!/usr/bin/env python3
"""Solve the lens of a reference still from its straight edges (0307 research).

World-straight edges project onto great circles of the view sphere. For each lens model the
fit looks for the focal length, the centre and the pixel aspect that make every edge chain lie
on one plane through the camera. A second, independent estimate of f comes from the Manhattan
assumption: every edge of a box-shaped room must be parallel to one of three orthogonal axes.

Usage:
    py -3.11 tools/lens-study/fit_ref_lens.py --image cam1.jpg --out <dir>
    py -3.11 tools/lens-study/fit_ref_lens.py --selftest
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import cv2
import numpy as np

# theta -> r/f. Each is monotonic on the range used for the lookup.
MODELS = {
    "equidistant": lambda t: t,
    "equisolid": lambda t: 2 * np.sin(t / 2),
    "stereographic": lambda t: 2 * np.tan(t / 2),
    "orthographic": lambda t: np.sin(t),
}
THETA_LIMIT = {
    "equidistant": np.radians(135),
    "equisolid": np.radians(135),
    "stereographic": np.radians(135),
    "orthographic": np.radians(89.9),
}
PERSPECTIVE = "perspective"


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


def rays(points: np.ndarray, model: str, f: float, cx: float, cy: float, aspect: float) -> np.ndarray:
    x = (points[:, 0] - cx) * aspect
    y = points[:, 1] - cy
    r = np.hypot(x, y)
    if model == PERSPECTIVE:
        v = np.stack([x, y, np.full_like(x, f)], axis=1)
        return v / np.linalg.norm(v, axis=1, keepdims=True)
    theta_axis = np.linspace(0, THETA_LIMIT[model], 3000)
    radius_axis = MODELS[model](theta_axis) * f
    theta = np.interp(r, radius_axis, theta_axis)
    safe = np.maximum(r, 1e-9)
    return np.stack([np.sin(theta) * x / safe, np.sin(theta) * y / safe, np.cos(theta)], axis=1)


def project(v, model, f, cx, cy, aspect):
    theta = np.arccos(np.clip(v[:, 2], -1, 1))
    phi = np.arctan2(v[:, 1], v[:, 0])
    if model == PERSPECTIVE:
        rad = f * np.tan(np.minimum(theta, np.radians(89.5)))
    else:
        rad = MODELS[model](theta) * f
    return np.stack([rad * np.cos(phi) / aspect + cx, rad * np.sin(phi) + cy], axis=1)


def chain_normals(chains, params, model):
    f, cx, cy, aspect = params
    normals, residuals = [], []
    for chain in chains:
        v = rays(chain, model, f, cx, cy, aspect)
        _, _, vt = np.linalg.svd(v, full_matrices=False)
        n = vt[-1]
        normals.append(n)
        # Measure in image pixels: slide each ray onto the plane and project it back.
        # An angular residual scaled by f is biased, because a short f shrinks it for free.
        foot = v - np.outer(v @ n, n)
        foot /= np.maximum(np.linalg.norm(foot, axis=1, keepdims=True), 1e-9)
        delta = project(foot, model, f, cx, cy, aspect) - chain
        residuals.append(float(np.sqrt(np.mean(np.sum(delta**2, axis=1)))))
    return np.array(normals), np.array(residuals)


def cost(params, chains, weights, model, keep, size):
    f, cx, cy, aspect = params
    w, h = size
    # A centre far outside the frame turns every ray parallel and fakes a perfect fit.
    if f < 40 or f > 20000 or aspect < 0.6 or aspect > 1.6:
        return 1e9
    if abs(cx - w / 2) > w * 0.2 or abs(cy - h / 2) > h * 0.2:
        return 1e9
    _, res = chain_normals(chains, params, model)
    order = np.argsort(res)[: int(len(res) * keep)]
    return float(np.sqrt(np.sum(weights[order] * res[order] ** 2) / np.sum(weights[order])))


def nelder_mead(fn, x0, steps, iters=500):
    n = len(x0)
    simplex = [np.array(x0, float)] + [np.array(x0, float) + np.eye(n)[i] * steps[i] for i in range(n)]
    values = [fn(s) for s in simplex]
    for _ in range(iters):
        order = np.argsort(values)
        simplex = [simplex[i] for i in order]
        values = [values[i] for i in order]
        centroid = np.mean(simplex[:-1], axis=0)
        reflected = centroid + (centroid - simplex[-1])
        fr = fn(reflected)
        if fr < values[0]:
            expanded = centroid + 2 * (centroid - simplex[-1])
            fe = fn(expanded)
            simplex[-1], values[-1] = (expanded, fe) if fe < fr else (reflected, fr)
        elif fr < values[-2]:
            simplex[-1], values[-1] = reflected, fr
        else:
            contracted = centroid + 0.5 * (simplex[-1] - centroid)
            fc = fn(contracted)
            if fc < values[-1]:
                simplex[-1], values[-1] = contracted, fc
            else:
                simplex = [simplex[0] + 0.5 * (s - simplex[0]) for s in simplex]
                values = [fn(s) for s in simplex]
    best = int(np.argmin(values))
    return simplex[best], values[best]


def fit_model(chains, w, h, model, free_aspect=True, keep=0.8):
    lengths = np.array([np.linalg.norm(c[-1] - c[0]) for c in chains])
    weights = lengths / lengths.sum()
    best = (None, 1e18)
    starts = [200.0, 300.0, 450.0, 800.0] if model != PERSPECTIVE else [400.0, 1200.0, 4000.0]
    for f0 in starts:
        x0 = [f0, w / 2, h / 2, 1.0]
        steps = [f0 * 0.2, 25, 25, 0.05 if free_aspect else 0.0]

        def fn(x):
            params = (x[0], x[1], x[2], x[3] if free_aspect else 1.0)
            return cost(params, chains, weights, model, keep, (w, h))

        x, value = nelder_mead(fn, x0, steps)
        if value < best[1]:
            best = (x, value)
    x, value = best
    return {"f": float(x[0]), "cx": float(x[1]), "cy": float(x[2]), "aspect": float(x[3] if free_aspect else 1.0), "rms": float(value)}


def manhattan_score(normals, rng, samples=2500, trim=0.8):
    """Median-like angular error (deg) of edges to the best orthogonal triad."""
    n = len(normals)
    best = (1e9, None)
    for _ in range(samples):
        i, j, k = rng.choice(n, 3, replace=False)
        v1 = np.cross(normals[i], normals[j])
        if np.linalg.norm(v1) < 0.05:
            continue
        v1 /= np.linalg.norm(v1)
        v2 = np.cross(v1, normals[k])
        if np.linalg.norm(v2) < 0.05:
            continue
        v2 /= np.linalg.norm(v2)
        v3 = np.cross(v1, v2)
        d = np.abs(normals @ np.stack([v1, v2, v3], axis=1))
        err = np.degrees(np.arcsin(np.clip(d.min(axis=1), 0, 1)))
        err = np.sort(err)[: int(n * trim)]
        score = float(np.sqrt(np.mean(err**2)))
        if score < best[0]:
            best = (score, (v1, v2, v3))
    return best


def manhattan_sweep(chains, fit, model, fs):
    rng = np.random.default_rng(7)
    rows = []
    for f in fs:
        normals, _ = chain_normals(chains, (f, fit["cx"], fit["cy"], fit["aspect"]), model)
        score, _ = manhattan_score(normals, rng)
        rows.append({"f": float(f), "errDeg": score})
    return rows


def field_angles(fit, model, w, h):
    def theta(px, py):
        x = (px - fit["cx"]) * fit["aspect"]
        y = py - fit["cy"]
        r = float(np.hypot(x, y))
        axis = np.linspace(0, THETA_LIMIT.get(model, np.radians(89.99)), 4000)
        rad = MODELS[model](axis) * fit["f"] if model != PERSPECTIVE else np.tan(np.minimum(axis, np.radians(89.9))) * fit["f"]
        return float(np.degrees(np.interp(r, rad, axis)))

    return {
        "left": theta(0, h / 2),
        "right": theta(w, h / 2),
        "top": theta(w / 2, 0),
        "bottom": theta(w / 2, h),
        "cornerTL": theta(0, 0),
        "cornerTR": theta(w, 0),
        "cornerBL": theta(0, h),
        "cornerBR": theta(w, h),
    }


def rectify(im, fit, model, out_w=1400, out_h=1000, f_out=320.0):
    xs, ys = np.meshgrid(np.arange(out_w, dtype=np.float32), np.arange(out_h, dtype=np.float32))
    x = (xs - out_w / 2) / f_out
    y = (ys - out_h / 2) / f_out
    theta = np.arctan(np.hypot(x, y))
    rho = MODELS[model](theta) * fit["f"]
    r = np.maximum(np.hypot(x, y), 1e-9)
    mx = (rho * x / r / fit["aspect"] + fit["cx"]).astype(np.float32)
    my = (rho * y / r + fit["cy"]).astype(np.float32)
    return cv2.remap(im, mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT, borderValue=(32, 0, 32))


def selftest():
    """Both directions of the instrument: a known fisheye must come back, a pinhole must not look like one."""
    rng = np.random.default_rng(3)
    w, h = 735, 490
    true_f, true_cx, true_cy = 230.0, 372.0, 251.0
    lines = []
    for _ in range(40):
        n = rng.normal(size=3)
        n /= np.linalg.norm(n)
        t = np.cross(n, [0.3, 0.2, 0.9])
        t /= np.linalg.norm(t)
        b = np.cross(n, t)
        a0 = rng.uniform(0, 2 * np.pi)
        lines.append([np.cos(a) * t + np.sin(a) * b for a in np.linspace(a0, a0 + 0.8, 25)])

    def synth(model):
        chains = []
        for line in lines:
            v = np.array(line)
            v = v[v[:, 2] > 0.05]
            if len(v) < 12:
                continue
            if model == PERSPECTIVE:
                p = v[:, :2] / v[:, 2:] * 260.0
                p = p + [true_cx, true_cy]
            else:
                theta = np.arccos(np.clip(v[:, 2], -1, 1))
                phi = np.arctan2(v[:, 1], v[:, 0])
                rad = MODELS[model](theta) * true_f
                p = np.stack([rad * np.cos(phi) + true_cx, rad * np.sin(phi) + true_cy], axis=1)
            keep = (p[:, 0] > 0) & (p[:, 0] < w) & (p[:, 1] > 0) & (p[:, 1] < h)
            p = p[keep]
            if len(p) >= 12:
                chains.append(p + rng.normal(scale=0.3, size=p.shape))
        return chains

    fish = synth("equidistant")
    fit = fit_model(fish, w, h, "equidistant")
    pin = fit_model(fish, w, h, PERSPECTIVE)
    assert abs(fit["f"] - true_f) / true_f < 0.03, fit
    assert abs(fit["cx"] - true_cx) < 8 and abs(fit["cy"] - true_cy) < 8, fit
    assert pin["rms"] > 4 * fit["rms"], (pin, fit)

    straight = synth(PERSPECTIVE)
    pin2 = fit_model(straight, w, h, PERSPECTIVE)
    fish2 = fit_model(straight, w, h, "equidistant")
    assert pin2["rms"] < 0.6, pin2
    # A pinhole scene fitted by a fisheye must drift to a long focal length, i.e. a narrow field.
    assert fish2["f"] > 800, fish2
    print("selftest ok", {"fishF": round(fit["f"], 1), "fishRms": round(fit["rms"], 3), "pinOnFishRms": round(pin["rms"], 3), "pinF": round(fish2["f"], 1)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image")
    parser.add_argument("--chains", help="JSON list of [x, y] chains. Defaults to <out>/ref_chains.json")
    parser.add_argument("--out")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()
    if args.selftest:
        selftest()
        return

    out = Path(args.out)
    im = read_image(args.image)
    if im is None:
        raise SystemExit(f"cannot read {args.image}")
    h, w = im.shape[:2]
    chains_path = Path(args.chains) if args.chains else out / "ref_chains.json"
    chains = [np.array(c, float) for c in json.loads(chains_path.read_text(encoding="utf-8"))]
    chains = [c for c in chains if np.linalg.norm(c[-1] - c[0]) > 55]
    print("chains used", len(chains))

    results = {}
    for model in [PERSPECTIVE, *MODELS]:
        fit = fit_model(chains, w, h, model)
        fit["angles"] = field_angles(fit, model, w, h)
        results[model] = fit
        print(model, {k: round(v, 3) for k, v in fit.items() if k != "angles"}, "corners", {k: round(v, 1) for k, v in fit["angles"].items()})

    fisheyes = {k: v for k, v in results.items() if k != PERSPECTIVE}
    best_model = min(fisheyes, key=lambda k: fisheyes[k]["rms"])
    best = results[best_model]
    sweep = manhattan_sweep(chains, best, best_model, np.arange(150, 601, 25))
    best_f = min(sweep, key=lambda r: r["errDeg"])
    print("manhattan sweep", [(r["f"], round(r["errDeg"], 2)) for r in sweep])
    print("manhattan best f", best_f, "vs straightness f", round(best["f"], 1))

    report = {"image": str(args.image), "size": [w, h], "fits": results, "best": best_model, "manhattan": {"sweep": sweep, "best": best_f}}
    (out / "lens-fit.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
    write_image(str(out / "ref_rectified.png"), rectify(im, best, best_model))
    print("wrote", out / "lens-fit.json")


if __name__ == "__main__":
    main()
