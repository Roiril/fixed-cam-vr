"""ツインの部屋で作り直した合成素材を、本番の cue のファイル名へ当てはめて並べる。

    py -3.11 tools/twin/make_assets.py [--out Logs/twin-run/assets]

tools/twin/assets.json の cue id ごとに、show.json の今の sourceUrl / maskUrl / revealUrl / pressMaskUrl のファイル名で
tools/twin/assets/ の素材をコピーする。Editor は FIXEDCAM_ASSET_OVERRIDE_DIR にこのフォルダが
指定されているときだけ、同じ名前のファイルを焼き込みの代わりに読む（ShowAssetResolver.TryOverride）。

映像（source）には、その cue のカメラの階調（feed_tone.py。束の映像と同じ表）を掛ける。マスクには掛けない。
掛け算の cue（blend=multiply）の source は比の画なので、階調を掛けずに置く。順の画と press マスクも同じ。
比の画・順の画・press マスクは tools/gen-plate/mask-recipes/bleed-twin-*.json から bleed_maps.py で作る。

⚠ 同じファイルを 2 つの cue が別の中身で使っていると、どちらかが負ける。そのときは止まる。
⚠ 本番の素材は 1 つも書き換えない。出力先は git 管理外。
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import feed_tone  # noqa: E402
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"


def file_of(url: str) -> str:
    return url.rsplit("/", 1)[-1] if url else ""


def tone(src: Path, dst: Path, cam: str) -> None:
    """素材へ、その台の階調を掛けて置く。静止画は同じ形式で、動画はコマごとに掛けて焼き直す。"""
    if src.suffix.lower() in (".png", ".jpg", ".jpeg"):
        img = feed_tone.apply(Image.open(src), cam)
        if dst.suffix.lower() == ".png":
            img.save(dst)
        else:
            img.convert("RGB").save(dst, quality=95)
        return
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                            "stream=width,height,r_frame_rate", "-of", "csv=p=0", str(src)],
                           capture_output=True, text=True, check=True).stdout.strip().split(",")
    w, h, rate = int(probe[0]), int(probe[1]), probe[2]
    dec = subprocess.Popen(["ffmpeg", "-v", "error", "-i", str(src), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                           stdout=subprocess.PIPE)
    enc = subprocess.Popen(["ffmpeg", "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{w}x{h}",
                            "-framerate", rate, "-i", "-", "-c:v", "libx264", "-crf", "16", "-preset", "medium",
                            "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(dst)], stdin=subprocess.PIPE)
    assert dec.stdout is not None and enc.stdin is not None
    n = w * h * 3
    frames = 0
    while True:
        buf = dec.stdout.read(n)
        if len(buf) < n:
            break
        enc.stdin.write(feed_tone.apply(Image.frombytes("RGB", (w, h), buf), cam).tobytes())
        frames += 1
    enc.stdin.close()
    if dec.wait() != 0 or enc.wait() != 0 or frames == 0:
        raise SystemExit(f"動画へ階調を掛けられない: {src}（{frames} コマ）")


def build(out: Path) -> dict[str, str]:
    spec = json.loads((HERE / "assets.json").read_text(encoding="utf-8"))["cues"]
    cues = {c.get("id"): c for c in json.loads(SHOW.read_text(encoding="utf-8-sig"))["cues"]}
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    placed: dict[str, Path] = {}
    for cid, roles in spec.items():
        cue = cues.get(cid)
        if cue is None:
            raise SystemExit(f"show.json に cue {cid} が無い（assets.json を直す）")
        for role, key in (("source", "sourceUrl"), ("mask", "maskUrl"), ("reveal", "revealUrl"), ("pressMask", "pressMaskUrl")):
            if role not in roles:
                continue
            name = file_of(cue.get(key, ""))
            if not name:
                raise SystemExit(f"cue {cid} に {key} が無い")
            src = (HERE / "assets" / roles[role]).resolve()
            if not src.is_file():
                raise SystemExit(f"素材が無い: {src}")
            if name in placed and placed[name] != src:
                raise SystemExit(f"{name} を 2 つの素材が取り合っている: {placed[name]} / {src}")
            placed[name] = src
            cam = cue.get("camera", "")
            data = role != "source" or cue.get("blend") == "multiply"
            if data or cam not in ("A", "B", "C"):
                shutil.copy2(src, out / name)
            else:
                tone(src, out / name, cam)
    return {k: str(v.relative_to(ROOT)) for k, v in placed.items()}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "Logs" / "twin-run" / "assets"))
    a = ap.parse_args()
    placed = build(Path(a.out))
    (Path(a.out) / "assets_map.json").write_text(json.dumps(placed, ensure_ascii=False, indent=1), encoding="utf-8")
    for k, v in placed.items():
        print(f"  {k} <- {v}")
    print(f"{len(placed)} 本 → {a.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
