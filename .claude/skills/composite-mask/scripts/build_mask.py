#!/usr/bin/env python3
"""Build deterministic composite masks from a versioned JSON recipe."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import os
from pathlib import Path
import sys
import tempfile
from typing import Any, Iterator

import cv2
import numpy as np
from PIL import Image, UnidentifiedImageError


ALGORITHM_VERSION = "composite-mask-v1"
OUTPUT_NAMES = (
    "source-alpha.png",
    "mask.png",
    "core.png",
    "difference.png",
    "report.json",
)
ROOT_KEYS = {
    "version", "mode", "reference", "sources", "transform", "domain",
    "protect", "seeds", "core_polygons", "difference", "margin", "feather",
    "frame_size", "allow_domain_contact",
}
TRANSFORM_KEYS = {"crop", "size"}
DIFFERENCE_KEYS = {"kind", "low", "high", "blur", "min_area", "close"}


class RecipeError(ValueError):
    """The recipe or one of its inputs is invalid."""


def _reject_duplicate_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise RecipeError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise RecipeError(f"non-finite JSON number: {value}")


def _load_json(data: bytes) -> dict[str, Any]:
    try:
        value = json.loads(
            data.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_pairs,
            parse_constant=_reject_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RecipeError(f"invalid UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise RecipeError("recipe root must be an object")
    return value


def _unknown_keys(value: dict[str, Any], allowed: set[str], label: str) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        raise RecipeError(f"unknown {label} key(s): {', '.join(unknown)}")


def _is_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _number(value: Any, label: str, minimum: float, maximum: float | None = None) -> float:
    if not _is_number(value) or not math.isfinite(float(value)):
        raise RecipeError(f"{label} must be a finite number")
    number = float(value)
    if number < minimum or (maximum is not None and number > maximum):
        upper = f"..{maximum:g}" if maximum is not None else " or greater"
        raise RecipeError(f"{label} must be {minimum:g}{upper}")
    return number


def _integer(value: Any, label: str, minimum: int) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        raise RecipeError(f"{label} must be an integer >= {minimum}")
    return value


def _int_pair(value: Any, label: str) -> tuple[int, int]:
    if not isinstance(value, list) or len(value) != 2:
        raise RecipeError(f"{label} must contain [width, height]")
    return (_integer(value[0], f"{label}[0]", 1), _integer(value[1], f"{label}[1]", 1))


def _crop(value: Any) -> tuple[int, int, int, int]:
    if not isinstance(value, list) or len(value) != 4:
        raise RecipeError("transform.crop must contain [x0, y0, x1, y1]")
    points = tuple(_integer(v, f"transform.crop[{i}]", 0) for i, v in enumerate(value))
    if points[2] <= points[0] or points[3] <= points[1]:
        raise RecipeError("transform.crop must have positive width and height")
    return points


def _path_value(value: Any, label: str, recipe_dir: Path) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value:
        raise RecipeError(f"{label} must be a non-empty path string")
    path = Path(value)
    if not path.is_absolute():
        path = recipe_dir / path
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise RecipeError(f"{label} does not resolve to an existing file: {value}") from exc
    if not resolved.is_file():
        raise RecipeError(f"{label} is not a file: {value}")
    return resolved


def _alias_key(path: Path) -> str:
    return os.path.normcase(str(path.resolve(strict=False)))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _load_reference(path: Path) -> np.ndarray:
    try:
        with Image.open(path) as image:
            image.load()
            if getattr(image, "n_frames", 1) != 1:
                raise RecipeError("reference must be a single-frame image")
            result = np.asarray(image.convert("RGB"), dtype=np.uint8)
    except (UnidentifiedImageError, OSError) as exc:
        raise RecipeError(f"reference is not a readable image: {path}") from exc
    if result.ndim != 3 or result.shape[2] != 3 or result.shape[0] < 1 or result.shape[1] < 1:
        raise RecipeError(f"reference has invalid dimensions: {path}")
    return result


def _try_load_image(path: Path) -> np.ndarray | None:
    try:
        with Image.open(path) as image:
            image.load()
            if getattr(image, "n_frames", 1) != 1:
                return None
            return np.asarray(image.convert("RGB"), dtype=np.uint8)
    except (UnidentifiedImageError, OSError):
        return None


def _transform_frame(
    frame: np.ndarray,
    transform: dict[str, Any],
    expected_size: tuple[int, int],
    label: str,
) -> np.ndarray:
    if frame.ndim != 3 or frame.shape[2] != 3:
        raise RecipeError(f"{label} decoded to a non-RGB frame")
    crop = transform.get("crop")
    if crop is not None:
        x0, y0, x1, y1 = crop
        height, width = frame.shape[:2]
        if x1 > width or y1 > height:
            raise RecipeError(
                f"transform.crop {crop} exceeds {label} dimensions {width}x{height}"
            )
        frame = frame[y0:y1, x0:x1]
    size = transform.get("size")
    if size is not None and (frame.shape[1], frame.shape[0]) != size:
        interpolation = (
            cv2.INTER_AREA
            if size[0] < frame.shape[1] or size[1] < frame.shape[0]
            else cv2.INTER_LINEAR
        )
        frame = cv2.resize(frame, size, interpolation=interpolation)
    actual = (frame.shape[1], frame.shape[0])
    if actual != expected_size:
        raise RecipeError(
            f"{label} is {actual[0]}x{actual[1]} after transform; "
            f"reference is {expected_size[0]}x{expected_size[1]}"
        )
    return np.ascontiguousarray(frame, dtype=np.uint8)


def _polygon_list(value: Any, label: str, width: int, height: int) -> list[np.ndarray]:
    if value is None:
        return []
    if not isinstance(value, list):
        raise RecipeError(f"{label} must be a list of polygons")
    polygons: list[np.ndarray] = []
    for polygon_index, polygon in enumerate(value):
        if not isinstance(polygon, list) or len(polygon) < 3:
            raise RecipeError(f"{label}[{polygon_index}] must have at least three points")
        points: list[tuple[float, float]] = []
        for point_index, point in enumerate(polygon):
            if not isinstance(point, list) or len(point) != 2:
                raise RecipeError(
                    f"{label}[{polygon_index}][{point_index}] must contain [x, y]"
                )
            x = _number(point[0], f"{label}[{polygon_index}][{point_index}][0]", 0, width - 1)
            y = _number(point[1], f"{label}[{polygon_index}][{point_index}][1]", 0, height - 1)
            points.append((x, y))
        area = abs(float(cv2.contourArea(np.asarray(points, dtype=np.float32))))
        if area <= 0:
            raise RecipeError(f"{label}[{polygon_index}] has zero area")
        polygons.append(np.rint(np.asarray(points)).astype(np.int32))
    return polygons


def _polygon_mask(polygons: list[np.ndarray], shape: tuple[int, int]) -> np.ndarray:
    mask = np.zeros(shape, dtype=np.uint8)
    if polygons:
        cv2.fillPoly(mask, polygons, 1, lineType=cv2.LINE_8)
    return mask.astype(bool)


def _domain_boundary(domain: np.ndarray) -> np.ndarray:
    boundary = np.zeros_like(domain)
    boundary[:, 1:] |= domain[:, 1:] & ~domain[:, :-1]
    boundary[:, :-1] |= domain[:, :-1] & ~domain[:, 1:]
    boundary[1:, :] |= domain[1:, :] & ~domain[:-1, :]
    boundary[:-1, :] |= domain[:-1, :] & ~domain[1:, :]
    return boundary


def _contain_rect(sw: int, sh: int, fw: int, fh: int) -> tuple[int, int, int, int]:
    frame_aspect = fw / fh
    source_aspect = sw / sh
    if source_aspect > frame_aspect:
        width, height = fw, max(1, round(fw / source_aspect))
    else:
        width, height = max(1, round(fh * source_aspect)), fh
    return ((fw - width) // 2, (fh - height) // 2, width, height)


def _smoothstep(value: np.ndarray) -> np.ndarray:
    value = np.clip(value, 0.0, 1.0)
    return value * value * (3.0 - 2.0 * value)


def _difference_alpha(core: np.ndarray, margin: float, feather: float) -> np.ndarray:
    if not np.any(core):
        return np.zeros(core.shape, dtype=np.float32)
    alpha = core.astype(np.float32)
    outside_distance = cv2.distanceTransform(
        (~core).astype(np.uint8), cv2.DIST_L2, cv2.DIST_MASK_PRECISE
    )
    if feather > 0:
        transition = 1.0 - _smoothstep((outside_distance - margin) / feather)
        alpha = np.where(core, 1.0, np.where(outside_distance <= margin, 1.0, transition))
    elif margin > 0:
        alpha = np.where(core | (outside_distance <= margin), 1.0, 0.0)
    return np.clip(alpha, 0.0, 1.0).astype(np.float32)


def _surface_alpha(surface: np.ndarray, feather: float) -> np.ndarray:
    if not np.any(surface):
        return np.zeros(surface.shape, dtype=np.float32)
    if feather <= 0:
        return surface.astype(np.float32)
    # OpenCV does not pad the image with zero here. A surface that continues through
    # an image edge therefore stays opaque at that edge, as a cropped wall should.
    inside_distance = cv2.distanceTransform(
        surface.astype(np.uint8), cv2.DIST_L2, cv2.DIST_MASK_PRECISE
    )
    alpha = _smoothstep(np.maximum(inside_distance - 1.0, 0.0) / feather)
    return np.where(surface, alpha, 0.0).astype(np.float32)


def _select_core(
    difference: np.ndarray,
    domain: np.ndarray,
    seeds: np.ndarray,
    config: dict[str, Any],
) -> np.ndarray:
    low = (difference >= config["low"]) & domain
    if config["close"] > 0:
        radius = config["close"]
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (radius * 2 + 1, radius * 2 + 1))
        low = cv2.morphologyEx(low.astype(np.uint8), cv2.MORPH_CLOSE, kernel).astype(bool)
        low &= domain
    high_or_seed = ((difference >= config["high"]) | seeds) & domain
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        low.astype(np.uint8), connectivity=8
    )
    core = np.zeros(low.shape, dtype=bool)
    for component in range(1, count):
        area = int(stats[component, cv2.CC_STAT_AREA])
        component_mask = labels == component
        if area >= config["min_area"] and np.any(component_mask & high_or_seed):
            core |= component_mask
    return core


def _png_bytes(array: np.ndarray, rgb: bool = False) -> bytes:
    if rgb:
        image = Image.fromarray(array, mode="RGB")
    else:
        image = Image.fromarray(array, mode="L")
    stream = io.BytesIO()
    image.save(stream, format="PNG", compress_level=9)
    return stream.getvalue()


def _atomic_write(path: Path, data: bytes) -> None:
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, path)
    except BaseException:
        try:
            temporary_path.unlink(missing_ok=True)
        finally:
            raise


def _parse_recipe(recipe: dict[str, Any], recipe_dir: Path) -> dict[str, Any]:
    _unknown_keys(recipe, ROOT_KEYS, "recipe")
    if recipe.get("version") != 1 or isinstance(recipe.get("version"), bool):
        raise RecipeError("version must be 1")
    mode = recipe.get("mode")
    if mode not in {"difference", "region", "surface"}:
        raise RecipeError("mode must be 'difference', 'region', or 'surface'")
    reference = _path_value(recipe.get("reference"), "reference", recipe_dir)
    source_values = recipe.get("sources")
    if not isinstance(source_values, list) or not source_values:
        raise RecipeError("sources must be a non-empty list")
    sources = [_path_value(value, f"sources[{i}]", recipe_dir) for i, value in enumerate(source_values)]

    transform_value = recipe.get("transform", {})
    if not isinstance(transform_value, dict):
        raise RecipeError("transform must be an object")
    _unknown_keys(transform_value, TRANSFORM_KEYS, "transform")
    transform: dict[str, Any] = {}
    if "crop" in transform_value:
        transform["crop"] = _crop(transform_value["crop"])
    if "size" in transform_value:
        transform["size"] = _int_pair(transform_value["size"], "transform.size")

    difference_value = recipe.get("difference", {})
    if not isinstance(difference_value, dict):
        raise RecipeError("difference must be an object")
    _unknown_keys(difference_value, DIFFERENCE_KEYS, "difference")
    kind = difference_value.get("kind", "absolute")
    if kind not in {"darken", "absolute"}:
        raise RecipeError("difference.kind must be 'darken' or 'absolute'")
    difference = {
        "kind": kind,
        "low": _number(difference_value.get("low", 8), "difference.low", 0, 255),
        "high": _number(difference_value.get("high", 20), "difference.high", 0, 255),
        "blur": _number(difference_value.get("blur", 0.7), "difference.blur", 0),
        "min_area": _integer(difference_value.get("min_area", 8), "difference.min_area", 1),
        "close": _integer(difference_value.get("close", 1), "difference.close", 0),
    }
    if difference["high"] < difference["low"]:
        raise RecipeError("difference.high must be >= difference.low")

    allow_contact = recipe.get("allow_domain_contact", False)
    if not isinstance(allow_contact, bool):
        raise RecipeError("allow_domain_contact must be a boolean")
    return {
        "mode": mode,
        "reference": reference,
        "sources": sources,
        "transform": transform,
        "difference": difference,
        "margin": _number(recipe.get("margin", 3), "margin", 0),
        "feather": _number(recipe.get("feather", 8), "feather", 0),
        "frame_size": _int_pair(recipe.get("frame_size", [640, 360]), "frame_size"),
        "allow_domain_contact": allow_contact,
        "polygon_values": {
            key: recipe.get(key, []) for key in ("domain", "protect", "seeds", "core_polygons")
        },
    }


def build_mask(recipe_path: str | os.PathLike[str], out_dir: str | os.PathLike[str]) -> dict[str, Any]:
    """Build all outputs and return the report dictionary."""
    cv2.setNumThreads(1)
    recipe_file = Path(recipe_path).resolve(strict=True)
    recipe_bytes = recipe_file.read_bytes()
    raw_recipe = _load_json(recipe_bytes)
    config = _parse_recipe(raw_recipe, recipe_file.parent)

    output_dir = Path(out_dir).resolve(strict=False)
    if output_dir.exists() and not output_dir.is_dir():
        raise RecipeError(f"out-dir exists and is not a directory: {output_dir}")
    input_paths = [recipe_file, config["reference"], *config["sources"]]
    input_aliases = {_alias_key(path) for path in input_paths}
    for output_name in OUTPUT_NAMES:
        output_path = output_dir / output_name
        if _alias_key(output_path) in input_aliases:
            raise RecipeError(f"output collides with an input: {output_path}")

    reference = _load_reference(config["reference"])
    height, width = reference.shape[:2]
    polygons = {
        key: _polygon_list(value, key, width, height)
        for key, value in config["polygon_values"].items()
    }
    shape = (height, width)
    domain = _polygon_mask(polygons["domain"], shape) if polygons["domain"] else np.ones(shape, bool)
    protect = _polygon_mask(polygons["protect"], shape)
    seeds = _polygon_mask(polygons["seeds"], shape)
    manual_core = _polygon_mask(polygons["core_polygons"], shape)

    reference_float = reference.astype(np.float32)
    maximum_difference = np.zeros(shape, dtype=np.float32)
    frame_count = 0
    source_reports: list[dict[str, Any]] = []
    expected_size = (width, height)
    for source_index, source_path in enumerate(config["sources"]):
        source_report: dict[str, Any] = {
            "path": str(source_path),
            "sha256": _sha256(source_path),
        }
        still = _try_load_image(source_path)
        if still is not None:
            frames: Iterator[np.ndarray] = iter((still,))
            source_report["type"] = "image"
            source_report["fps"] = None
        else:
            capture = cv2.VideoCapture(str(source_path))
            if not capture.isOpened():
                raise RecipeError(f"source is neither a readable image nor video: {source_path}")
            fps = float(capture.get(cv2.CAP_PROP_FPS))
            source_report["type"] = "video"
            source_report["fps"] = fps if math.isfinite(fps) and fps > 0 else None

            def video_frames(cap: cv2.VideoCapture = capture) -> Iterator[np.ndarray]:
                try:
                    while True:
                        ok, bgr = cap.read()
                        if not ok:
                            break
                        yield cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
                finally:
                    cap.release()

            frames = video_frames()

        source_frame_count = 0
        for source_frame_count, frame in enumerate(frames, start=1):
            transformed = _transform_frame(
                frame,
                config["transform"],
                expected_size,
                f"sources[{source_index}] frame {source_frame_count}",
            ).astype(np.float32)
            if config["mode"] == "difference":
                if config["difference"]["kind"] == "darken":
                    frame_difference = np.maximum(reference_float - transformed, 0.0).max(axis=2)
                else:
                    frame_difference = np.abs(reference_float - transformed).max(axis=2)
                np.maximum(maximum_difference, frame_difference, out=maximum_difference)
        if source_frame_count == 0:
            raise RecipeError(f"source contains no decodable frames: {source_path}")
        source_report["frame_count"] = source_frame_count
        source_reports.append(source_report)
        frame_count += source_frame_count

    difference_config = config["difference"]
    if config["mode"] == "difference" and difference_config["blur"] > 0:
        maximum_difference = cv2.GaussianBlur(
            maximum_difference,
            (0, 0),
            sigmaX=difference_config["blur"],
            sigmaY=difference_config["blur"],
            borderType=cv2.BORDER_REPLICATE,
        )
    maximum_difference = np.clip(maximum_difference, 0.0, 255.0).astype(np.float32)

    if config["mode"] == "difference":
        core = _select_core(maximum_difference, domain, seeds, difference_config) | manual_core
        core &= ~protect
        alpha = _difference_alpha(core, config["margin"], config["feather"])
    elif config["mode"] == "region":
        if not polygons["core_polygons"]:
            raise RecipeError("region mode requires at least one core_polygons polygon")
        core = manual_core & ~protect
        alpha = _difference_alpha(core, config["margin"], config["feather"])
    else:
        if not polygons["core_polygons"]:
            raise RecipeError("surface mode requires at least one core_polygons polygon")
        core = manual_core & ~protect
        alpha = _surface_alpha(core, config["feather"])
    alpha[protect] = 0.0

    contact = (
        core & _domain_boundary(domain)
        if config["mode"] == "difference"
        else np.zeros(shape, dtype=bool)
    )
    contact_pixels = int(np.count_nonzero(contact))
    if contact_pixels and not config["allow_domain_contact"]:
        contact_y, contact_x = np.nonzero(contact)
        bounds = (
            int(contact_x.min()), int(contact_y.min()),
            int(contact_x.max()), int(contact_y.max()),
        )
        samples = [
            [int(x_value), int(y_value)]
            for y_value, x_value in zip(contact_y[:8], contact_x[:8])
        ]
        raise RecipeError(
            f"accepted core touches the internal domain boundary at {contact_pixels} pixel(s); "
            f"bounds=[{bounds[0]},{bounds[1]}]-[{bounds[2]},{bounds[3]}], "
            f"samples={samples}; expand domain or set allow_domain_contact=true"
        )

    source_alpha = np.clip(np.rint(alpha * 255.0), 0, 255).astype(np.uint8)
    core_image = core.astype(np.uint8) * 255
    difference_image = np.clip(np.rint(maximum_difference), 0, 255).astype(np.uint8)
    frame_width, frame_height = config["frame_size"]
    x, y, contained_width, contained_height = _contain_rect(
        width, height, frame_width, frame_height
    )
    contained = cv2.resize(
        alpha,
        (contained_width, contained_height),
        interpolation=cv2.INTER_LINEAR,
    )
    frame_alpha = np.zeros((frame_height, frame_width), dtype=np.float32)
    frame_alpha[y:y + contained_height, x:x + contained_width] = contained
    frame_luma = np.clip(np.rint(frame_alpha * 255.0), 0, 255).astype(np.uint8)
    frame_rgb = np.repeat(frame_luma[:, :, None], 3, axis=2)

    output_bytes = {
        "source-alpha.png": _png_bytes(source_alpha),
        "mask.png": _png_bytes(frame_rgb, rgb=True),
        "core.png": _png_bytes(core_image),
    }
    if config["mode"] == "difference":
        output_bytes["difference.png"] = _png_bytes(difference_image)
    mask_sha256 = hashlib.sha256(output_bytes["mask.png"]).hexdigest()
    core_pixels = int(np.count_nonzero(core))
    opaque_core_pixels = int(np.count_nonzero(core & (alpha >= 0.99)))
    report: dict[str, Any] = {
        "algorithm_version": ALGORITHM_VERSION,
        "recipe_sha256": hashlib.sha256(recipe_bytes).hexdigest(),
        "inputs": [
            {"role": "reference", "path": str(config["reference"]), "sha256": _sha256(config["reference"])},
            *[
                {"role": "source", **source_report}
                for source_report in source_reports
            ],
        ],
        "frame_count": frame_count,
        "dimensions": {
            "source": [width, height],
            "frame": [frame_width, frame_height],
            "contained_rect": [x, y, contained_width, contained_height],
        },
        "video_fps": [
            {"path": item["path"], "fps": item["fps"]}
            for item in source_reports if item["type"] == "video"
        ],
        "core_coverage": (opaque_core_pixels / core_pixels) if core_pixels else 1.0,
        "domain_contact_pixels": contact_pixels,
        "areas": {
            "source_pixels": width * height,
            "core_pixels": core_pixels,
            "opaque_pixels": int(np.count_nonzero(alpha >= 0.99)),
            "nonzero_pixels": int(np.count_nonzero(alpha > 0.0)),
            "alpha_sum": float(alpha.sum(dtype=np.float64)),
        },
        "mask_sha256": mask_sha256,
    }
    report_bytes = (json.dumps(report, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")

    output_dir.mkdir(parents=True, exist_ok=True)
    for name, data in output_bytes.items():
        _atomic_write(output_dir / name, data)
    stale_difference = output_dir / "difference.png"
    if config["mode"] != "difference" and stale_difference.exists():
        stale_difference.unlink()
    _atomic_write(output_dir / "report.json", report_bytes)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recipe", required=True, help="version 1 recipe JSON")
    parser.add_argument("--out-dir", required=True, help="output directory")
    arguments = parser.parse_args(argv)
    try:
        report = build_mask(arguments.recipe, arguments.out_dir)
    except (RecipeError, FileNotFoundError, OSError) as exc:
        print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 2
    print(json.dumps({"ok": True, "report": report}, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
