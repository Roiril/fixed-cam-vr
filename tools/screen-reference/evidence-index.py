"""Read the CRT diagnostic manifest variants without guessing their shape.

Every published row is checked against its real screenshot and source hash.
Labels come from the actual file; missing captures never have a substitute.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_evidence(directory):
    directory = Path(directory).resolve()
    manifest_path = directory / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    rows = []
    for collection in ("captures", "photos", "ablations"):
        records = manifest.get(collection, [])
        if not isinstance(records, list):
            raise ValueError(f"{manifest_path}: {collection} must be an array")
        expanded = []
        for record in records:
            raw = record.get("capture", record)
            if isinstance(raw, dict) and "file" not in raw:
                roles = ("singlepass", "control", "variant")
                if not all(role in raw and role + "Hash" in raw for role in roles):
                    raise ValueError(f"{manifest_path}: unknown capture schema: {list(raw)}")
                for role in roles:
                    expanded.append({"capture": {**raw, "file": raw[role],
                                                  "sha256": raw[role + "Hash"]},
                                     "image_role": role})
            else:
                expanded.append(record)
        for record in expanded:
            capture = record.get("capture", record)
            if not isinstance(capture, dict):
                raise ValueError(f"{manifest_path}: invalid capture object")
            filename, expected = capture.get("file"), capture.get("sha256")
            if not isinstance(filename, str) or not filename:
                raise ValueError(f"{manifest_path}: capture has no file")
            if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected):
                raise ValueError(f"{manifest_path}: missing screenshot SHA256 for {filename}")
            image = (directory / filename).resolve()
            if not image.is_relative_to(directory) or not image.is_file():
                raise FileNotFoundError(image)
            if digest(image) != expected.lower():
                raise ValueError(f"Screenshot hash mismatch: {image}")
            source, source_hash = capture.get("source"), capture.get("sourceHash")
            if not isinstance(source, str) or not isinstance(source_hash, str):
                raise ValueError(f"{manifest_path}: missing source evidence for {filename}")
            source_path = Path(source)
            if not source_path.is_file() or digest(source_path) != source_hash.lower():
                raise ValueError(f"Source file/hash mismatch: {source_path}")
            # Wrapper values differ by experiment: mode, lut, condition, glare,
            # lodIncrement. Preserve the real values rather than infer enums.
            metadata = {key: value for key, value in record.items()
                        if key not in ("capture", "parameters")
                        and isinstance(value, (str, int, float, bool))}
            rows.append({"file": filename, "path": image.as_posix(),
                         "sha256": expected.lower(), "plate": capture.get("plate"),
                         "source": source_path.as_posix(), "sourceHash": source_hash.lower(),
                         "collection": collection, "metadata": metadata})
    if not rows:
        raise ValueError(f"{manifest_path}: no screenshot records")
    if len({row["file"] for row in rows}) != len(rows):
        raise ValueError(f"{manifest_path}: duplicate screenshot labels")
    return {"directory": directory.as_posix(), "manifest_sha256": digest(manifest_path),
            "count": len(rows), "rows": rows}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", nargs="+")
    args = parser.parse_args()
    print(json.dumps([read_evidence(path) for path in args.directories], ensure_ascii=False))
