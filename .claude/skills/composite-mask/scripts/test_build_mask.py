#!/usr/bin/env python3
"""Behavior tests for build_mask.py."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

import cv2
import numpy as np
from PIL import Image

import build_mask


class BuildMaskTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def save_rgb(self, name: str, pixels: np.ndarray) -> Path:
        path = self.root / name
        Image.fromarray(pixels.astype(np.uint8), mode="RGB").save(path)
        return path

    def write_recipe(self, recipe: dict, name: str = "recipe.json") -> Path:
        path = self.root / name
        path.write_text(json.dumps(recipe, sort_keys=True), encoding="utf-8")
        return path

    def base_recipe(self, source: str, **updates: object) -> dict:
        recipe = {
            "version": 1,
            "mode": "difference",
            "reference": "reference.png",
            "sources": [source],
            "difference": {
                "kind": "absolute",
                "low": 5,
                "high": 10,
                "blur": 0,
                "min_area": 1,
                "close": 0,
            },
            "margin": 0,
            "feather": 0,
            "frame_size": [64, 36],
        }
        recipe.update(updates)
        return recipe

    def load_luma(self, output: Path, name: str) -> np.ndarray:
        return np.asarray(Image.open(output / name).convert("L"))

    def test_identical_static_input_allows_zero_core(self) -> None:
        reference = np.full((48, 64, 3), 120, np.uint8)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", reference)

        report = build_mask.build_mask(
            self.write_recipe(self.base_recipe("source.png")), self.root / "out"
        )

        self.assertEqual(0, report["areas"]["core_pixels"])
        self.assertEqual(0, int(self.load_luma(self.root / "out", "source-alpha.png").sum()))
        self.assertEqual(1, report["frame_count"])

    def test_thin_stain_keeps_every_core_pixel_opaque(self) -> None:
        reference = np.full((48, 64, 3), 200, np.uint8)
        source = reference.copy()
        source[7:42, 31, :] = 130
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", source)
        recipe = self.base_recipe("source.png", margin=2, feather=6)

        report = build_mask.build_mask(self.write_recipe(recipe), self.root / "out")
        core = self.load_luma(self.root / "out", "core.png") > 0
        alpha = self.load_luma(self.root / "out", "source-alpha.png")

        self.assertEqual(35, int(core.sum()))
        self.assertTrue(np.all(alpha[core] == 255))
        self.assertEqual(1.0, report["core_coverage"])

    def test_two_disconnected_components_are_both_kept(self) -> None:
        reference = np.full((48, 64, 3), 180, np.uint8)
        source = reference.copy()
        source[8:13, 7:12, :] = 30
        source[31:37, 49:56, :] = 30
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", source)

        build_mask.build_mask(
            self.write_recipe(self.base_recipe("source.png")), self.root / "out"
        )
        core = self.load_luma(self.root / "out", "core.png")

        self.assertEqual(255, int(core[10, 9]))
        self.assertEqual(255, int(core[33, 52]))
        self.assertEqual(67, int(np.count_nonzero(core)))

    def test_video_union_includes_transient_moving_positions(self) -> None:
        reference = np.full((48, 64, 3), 220, np.uint8)
        self.save_rgb("reference.png", reference)
        video_path = self.root / "moving.avi"
        writer = cv2.VideoWriter(
            str(video_path), cv2.VideoWriter_fourcc(*"FFV1"), 12.0, (64, 48)
        )
        self.assertTrue(writer.isOpened(), "FFV1 VideoWriter is unavailable")
        frames = []
        for x in (None, 8, 45, None):
            frame = reference.copy()
            if x is not None:
                frame[20:25, x:x + 5, :] = 40
            frames.append(frame)
            writer.write(cv2.cvtColor(frame, cv2.COLOR_RGB2BGR))
        writer.release()

        report = build_mask.build_mask(
            self.write_recipe(self.base_recipe("moving.avi")), self.root / "out"
        )
        core = self.load_luma(self.root / "out", "core.png")

        self.assertEqual(255, int(core[22, 10]))
        self.assertEqual(255, int(core[22, 47]))
        self.assertEqual(4, report["frame_count"])
        self.assertAlmostEqual(12.0, report["video_fps"][0]["fps"], places=2)

    def test_domain_contact_fails_but_interior_component_passes(self) -> None:
        reference = np.full((48, 64, 3), 200, np.uint8)
        touching = reference.copy()
        touching[18:24, 27:34, :] = 0
        interior = reference.copy()
        interior[18:24, 17:23, :] = 0
        self.save_rgb("reference.png", reference)
        self.save_rgb("touching.png", touching)
        self.save_rgb("interior.png", interior)
        domain = [[[10, 10], [30, 10], [30, 35], [10, 35]]]

        touching_recipe = self.base_recipe("touching.png", domain=domain, feather=5)
        with self.assertRaisesRegex(build_mask.RecipeError, "internal domain boundary"):
            build_mask.build_mask(self.write_recipe(touching_recipe, "touch.json"), self.root / "bad")
        self.assertFalse((self.root / "bad").exists())

        interior_recipe = self.base_recipe("interior.png", domain=domain, feather=5)
        report = build_mask.build_mask(
            self.write_recipe(interior_recipe, "interior.json"), self.root / "good"
        )
        self.assertEqual(0, report["domain_contact_pixels"])
        alpha = self.load_luma(self.root / "good", "source-alpha.png")
        self.assertGreater(int(alpha[20, 24]), 0, "feather must extend beyond the selected core")

    def test_four_by_three_source_has_exact_black_side_bars(self) -> None:
        reference = np.full((48, 64, 3), 255, np.uint8)
        source = np.zeros_like(reference)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", source)

        build_mask.build_mask(
            self.write_recipe(self.base_recipe("source.png")), self.root / "out"
        )
        mask = np.asarray(Image.open(self.root / "out" / "mask.png"))

        self.assertEqual((36, 64, 3), mask.shape)
        self.assertTrue(np.all(mask[:, :8, :] == 0))
        self.assertTrue(np.all(mask[:, 56:, :] == 0))
        self.assertTrue(np.all(mask[:, 8:56, :] == 255))

    def test_surface_is_solid_and_does_not_fade_at_image_edges(self) -> None:
        reference = np.full((48, 64, 3), 100, np.uint8)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", reference)
        recipe = self.base_recipe(
            "source.png",
            mode="surface",
            core_polygons=[[[0, 0], [44, 0], [44, 36], [0, 36]]],
            feather=5,
            margin=99,
        )

        build_mask.build_mask(self.write_recipe(recipe), self.root / "out")
        alpha = self.load_luma(self.root / "out", "source-alpha.png")

        self.assertEqual(255, int(alpha[0, 0]))
        self.assertEqual(255, int(alpha[12, 12]))
        self.assertGreater(int(alpha[18, 42]), 0)
        self.assertEqual(0, int(alpha[18, 45]))
        self.assertFalse((self.root / "out" / "difference.png").exists())

    def test_region_keeps_thin_branch_core_opaque_and_expands_outward(self) -> None:
        reference = np.full((48, 64, 3), 140, np.uint8)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", reference)
        thin_branch = [[[9, 24], [47, 25], [47, 27], [9, 26]]]
        recipe = self.base_recipe(
            "source.png",
            mode="region",
            core_polygons=thin_branch,
            margin=2,
            feather=5,
        )

        report = build_mask.build_mask(self.write_recipe(recipe), self.root / "out")
        core = self.load_luma(self.root / "out", "core.png") > 0
        alpha = self.load_luma(self.root / "out", "source-alpha.png")

        self.assertGreater(int(core.sum()), 0)
        self.assertTrue(np.all(alpha[core] == 255))
        self.assertGreater(int(alpha[23, 25]), 0)
        self.assertEqual(1.0, report["core_coverage"])
        self.assertEqual(1, report["frame_count"])
        self.assertFalse((self.root / "out" / "difference.png").exists())

    def test_region_rejects_empty_core_polygons(self) -> None:
        reference = np.full((48, 64, 3), 140, np.uint8)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", reference)
        recipe = self.base_recipe("source.png", mode="region", core_polygons=[])

        with self.assertRaisesRegex(build_mask.RecipeError, "region mode requires"):
            build_mask.build_mask(self.write_recipe(recipe), self.root / "out")
        self.assertFalse((self.root / "out").exists())

    def test_same_input_produces_identical_hashes(self) -> None:
        rng = np.random.default_rng(104729)
        reference = rng.integers(80, 230, size=(48, 64, 3), dtype=np.uint8)
        source = reference.copy()
        source[12:29, 20:39, :] = np.maximum(source[12:29, 20:39, :] - 40, 0)
        self.save_rgb("reference.png", reference)
        self.save_rgb("source.png", source)
        recipe = self.write_recipe(self.base_recipe("source.png", margin=3, feather=7))

        first = build_mask.build_mask(recipe, self.root / "first")
        second = build_mask.build_mask(recipe, self.root / "second")

        self.assertEqual(first["mask_sha256"], second["mask_sha256"])
        self.assertEqual(
            (self.root / "first" / "mask.png").read_bytes(),
            (self.root / "second" / "mask.png").read_bytes(),
        )
        self.assertEqual(
            (self.root / "first" / "source-alpha.png").read_bytes(),
            (self.root / "second" / "source-alpha.png").read_bytes(),
        )


if __name__ == "__main__":
    unittest.main()
