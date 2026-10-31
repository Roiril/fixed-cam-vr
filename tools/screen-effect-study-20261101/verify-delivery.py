"""Verify the selected portable report, its provenance hashes and owned-source syntax."""
from pathlib import Path
import argparse
import ast
import hashlib
import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ROOT = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, default=ROOT / "manifest.json")
    args = parser.parse_args()
    record = json.loads(args.manifest.read_text(encoding="utf-8"))
    checks = []
    for item in record["files"]:
        source = Path(item["path"])
        if digest(source) != item["sha256"] or source.stat().st_size != item["bytes"]:
            raise ValueError(f"Selected input or artifact changed: {source}")
        checks.append(str(source))
    for item in record["sources"]:
        source = HERE / item["path"]
        if digest(source) != item["sha256"]:
            raise ValueError(f"Owned source changed after report generation: {source}")
    report = ROOT / record["report"]["path"]
    if digest(report) != record["report"]["sha256"]:
        raise ValueError("Portable report differs from its manifest")
    syntax = {"python": 0, "javascript": 0, "json": 0}
    for source in sorted(HERE.rglob("*")):
        if not source.is_file() or "__pycache__" in source.parts:
            continue
        if source.suffix == ".py":
            ast.parse(source.read_text(encoding="utf-8-sig"), filename=str(source))
            syntax["python"] += 1
        elif source.suffix in (".js", ".mjs", ".cjs"):
            subprocess.run(["node", "--check", str(source)], check=True, capture_output=True)
            syntax["javascript"] += 1
        elif source.suffix == ".json":
            json.loads(source.read_text(encoding="utf-8-sig"))
            syntax["json"] += 1
    layout_name = "report-progress-layout-check.json" if record["draft"] else "report-layout-check.json"
    layout = json.loads((ROOT / layout_name).read_text(encoding="utf-8"))
    if layout[0].get("report_sha256") != digest(report):
        raise ValueError("Layout verification belongs to another report revision")
    result = {"manifest": str(args.manifest), "manifest_sha256": digest(args.manifest),
              "report_sha256": digest(report), "selected_file_hashes_verified": len(checks),
              "owned_source_hashes_verified": len(record["sources"]), "syntax": syntax,
              "complete": record["complete"], "scores": record["selected_scores"],
              "layout_check": layout_name}
    target = ROOT / ("delivery-progress-check.json" if record["draft"] else "delivery-check.json")
    temporary = target.with_suffix(".tmp.json")
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    os.replace(temporary, target)
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
