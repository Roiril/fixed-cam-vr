"""Check actual Unity render output, including absence controls, without rewriting images."""
import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops

sys.stdout.reconfigure(encoding="utf-8")


def changed(a, b, box=None):
    diff = ImageChops.difference(a, b)
    if box:
        diff = diff.crop(box)
    channels = diff.split()
    maximum = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2])
    return sum(maximum.histogram()[13:])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    names = ["sample-0.00", "sample-0.35", "sample-0.80", "sample-1.60",
             "hero", "sample-5.90", "sample-7.00", "stopped", "parallax"]
    pictures = {name: Image.open(root / (name + ".png")).convert("RGB") for name in names}
    base, hero = pictures["sample-0.00"], pictures["hero"]
    w, h = base.size
    center = (int(w * .36), int(h * .31), int(w * .64), int(h * .70))
    left = (0, 0, int(w * .25), h)
    right = (int(w * .75), 0, w, h)
    metrics = {"size": [w, h], "calibration_identical": changed(base, base),
               "center_changed": changed(base, hero, center),
               "left_space_changed": changed(base, hero, left),
               "right_space_changed": changed(base, hero, right),
               "end_changed": changed(base, pictures["sample-7.00"]),
               "stop_changed": changed(base, pictures["stopped"]),
               "parallax_changed": changed(hero, pictures["parallax"])}
    metrics["timeline_changed_pixels"] = {name: changed(base, pictures[name]) for name in names[:6]}
    # Reject a renderer that silently produced the same blank view in every frame.
    def visible(m):
        return m["center_changed"] > 500 and m["left_space_changed"] > 100 and m["right_space_changed"] > 100
    null = dict(metrics, center_changed=0, left_space_changed=0, right_space_changed=0)
    assert not visible(null), "absence control failed"
    assert metrics["calibration_identical"] == 0, "identity control failed"
    assert visible(metrics), "screen and both spatial regions must actually draw"
    assert metrics["end_changed"] == 0 and metrics["stop_changed"] == 0, "effect must fully disappear"
    assert metrics["parallax_changed"] > 500, "camera motion must change spatial projection"
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
