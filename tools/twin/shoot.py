"""撮って仕上げるまでの入口。py -3.11 tools/twin/shoot.py --tag v1 --shots hero,wide --w 1920 --spp 512 [--person 1]
EXR は Logs/twin-cycles/exr、仕上げた PNG は Drive の renders/twin-cycles/<時刻>_<tag>/。
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
ROOT = HERE.parents[1]
BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
RENDERS = Path(r"G:\マイドライブ\shubie\renders\twin-cycles")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    args = sys.argv[1:]
    p = subprocess.run([BLENDER, "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(HERE / "twin_render.py"), "--", "still", *args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    lines = [l for l in p.stdout.splitlines() if l.startswith("[twin]") or "Error" in l or "Traceback" in l or l.startswith("  File")]
    print("\n".join(lines))
    if p.returncode != 0:
        print(p.stdout[-3000:])
        print(p.stderr[-3000:])
        return p.returncode
    exr = next(l.split(" ", 2)[2] for l in lines if l.startswith("[twin] EXR"))
    exr = Path(exr)
    manifest = json.loads((exr / "manifest.json").read_text(encoding="utf-8"))
    import twin_post

    out = RENDERS / exr.name
    out.mkdir(parents=True, exist_ok=True)
    for name, look in manifest.items():
        hdr = twin_post.read_exr(str(exr / f"{name}.exr"))
        bgr = twin_post.process(hdr, look)
        twin_post.write_image(str(out / f"{name}.png"), bgr)
        if name in "ABC" and len(name) == 1:  # 次の撮影でスマホの画面に映す
            feeds = ROOT / "Logs" / "twin-cycles" / "feeds"
            feeds.mkdir(parents=True, exist_ok=True)
            twin_post.write_image(str(feeds / f"{name}.jpg"), bgr)
    print("OUT", out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
