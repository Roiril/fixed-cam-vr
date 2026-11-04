"""Extract only the preview walk and embedded Remy textures; no dependencies."""
import hashlib
import struct
import sys
import zipfile
import zlib
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parents[3]
out = Path(__file__).resolve().parent


def read_fbx(data):
    wide = struct.unpack_from("<I", data, 23)[0] >= 7500
    fmt, size = ("<QQQ", 25) if wide else ("<III", 13)

    def prop(pos):
        kind = chr(data[pos])
        pos += 1
        fmts = {"Y": "h", "C": "?", "I": "i", "F": "f", "D": "d", "L": "q"}
        if kind in fmts:
            f = "<" + fmts[kind]
            return struct.unpack_from(f, data, pos)[0], pos + struct.calcsize(f)
        if kind in "SR":
            count = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            value = data[pos:pos + count]
            return (value.decode("utf-8", "replace") if kind == "S" else value), pos + count
        count, compressed, length = struct.unpack_from("<III", data, pos)
        pos += 12
        value = data[pos:pos + length]
        if compressed:
            value = zlib.decompress(value)
        code = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b", "c": "b"}[kind]
        return struct.unpack("<" + str(count) + code, value), pos + length

    def node(pos):
        end, count, _ = struct.unpack_from(fmt, data, pos)
        length = data[pos + size - 1]
        if not end:
            return None, pos + size
        name = data[pos + size:pos + size + length].decode()
        pos += size + length
        values, children = [], []
        for _ in range(count):
            value, pos = prop(pos)
            values.append(value)
        while pos < end - size:
            child, pos = node(pos)
            if child:
                children.append(child)
        return {"n": name, "v": values, "c": children}, end

    pos, nodes = 27, []
    while pos < len(data):
        value, pos = node(pos)
        if value is None:
            break
        nodes.append(value)
    return nodes


if __name__ == "__main__":
    nodes = read_fbx((root / "Assets/ThirdParty/Mixamo/Remy.fbx").read_bytes())
    objects = next(n for n in nodes if n["n"] == "Objects")["c"]
    for video in (n for n in objects if n["n"] == "Video"):
        children = {n["n"]: n["v"] for n in video["c"]}
        content = (children.get("Content") or [b""])[0]
        filename = children.get("Filename", [""])[0].replace("\\", "/").split("/")[-1]
        if content and (filename.endswith("_Diffuse.png") or filename.endswith("_Opacity.png")):
            assert content.startswith(b"\x89PNG\r\n\x1a\n"), filename
            (out / filename).write_bytes(content)
            print(filename, len(content), hashlib.sha256(content).hexdigest())
    with zipfile.ZipFile(Path("G:/マイドライブ/shubie/archive/cold/projects/Models.zip")) as archive:
        walk = archive.read("Models/Walking.fbx")
    (out / "Walking.fbx").write_bytes(walk)
    (out / "WalkingBind.fbx").write_bytes(walk)
    print("Walking.fbx", len(walk), hashlib.sha256(walk).hexdigest())
    print("WalkingBind.fbx", len(walk), hashlib.sha256(walk).hexdigest())
