"""Freeze the predeclared 2x2 illumination experiment. No missing-file fallback."""
from pathlib import Path
import json
import shutil
import sys
from PIL import Image
from verify import OUT, ROOT, sha, assert_rendered, verify

sys.stdout.reconfigure(encoding="utf-8")
verify()
manifest = json.loads((OUT / "manifest.json").read_text(encoding="utf-8-sig"))
rows = manifest["ablations"]
assert len(rows) == 12
assert {r["file"] for r in rows} == {f"ablation/{c}_v{v}_f{f}.png" for c in "ABC" for v in range(2) for f in range(2)}
for camera in "ABC":
    group = [r for r in rows if Path(r["file"]).name.startswith(camera)]
    assert len({r["invariantHash"] for r in group}) == 1
    assert len({r["sourceHash"] for r in group}) == 1
for row in rows:
    path = OUT / row["file"]
    assert path.is_file() and sha(path) == row["sha256"]
    assert sha(Path(row["source"])) == row["sourceHash"]
    with Image.open(path) as im:
        assert im.size == (1280, 1030)
        assert_rendered(im)
assert sha(OUT / "ablation/A_v1_f1.png") == sha(OUT / "round-27/02.png"), "baseline changed"
destination = OUT / "ablation-study-01"
assert not destination.exists(), "frozen study already exists"
destination.mkdir()
for row in rows:
    folder = destination / Path(row["file"]).stem
    folder.mkdir()
    shutil.copyfile(ROOT / "Assets/Resources/CrtReference/CaseColor.jpg", folder / "01.jpg")
    shutil.copyfile(OUT / row["file"], folder / "02.png")
    (folder / "parameters.json").write_text(json.dumps(row, ensure_ascii=False, indent=2), encoding="utf-8")
shutil.copyfile(OUT / "manifest.json", destination / "manifest.json")
shutil.copyfile(OUT / "round-27/judgment.json", destination / "A_v1_f1/judgment.json")
print(json.dumps({"frozen": 12, "invariant_parameters_verified": True, "baseline_identical_round_27": True}))
