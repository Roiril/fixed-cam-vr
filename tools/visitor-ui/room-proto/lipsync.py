"""博士の口の動画（DomoAI の Talking Avatar）を作り、試作の時刻表に並べて 1 本の顔の動画にする。

    py -3.11 tools/visitor-ui/room-proto/lipsync.py plan  <作業フォルダ>   # 声を無音の位置で切る（切り方は layers/lipsync-plan.json）
    py -3.11 tools/visitor-ui/room-proto/lipsync.py send  <作業フォルダ>   # 切った声を全部 DomoAI へ投げて取り出す（済んだものは飛ばす）
    py -3.11 tools/visitor-ui/room-proto/lipsync.py track <作業フォルダ>   # 取り出した動画を並べて layers/face-ja.mp4 を作る

前提: `npm install -g @domoai/cli` と `domoai login --start` / `--finish`（トークンは CLI が持つ。ここでは扱わない）。
入力の画像は `../doctor-centered-v1.png`（部屋の絵の顔と同じ写真）。プロンプトは `../lipsync-kit.md` の英文。

枠の値段は 5 秒 15・10 秒 45・20 秒 90 クレジット（2026-11-12）。5 秒の枠が秒あたり最安なので、
声を 4.7 秒以下に切る。出来る動画の長さは声の長さと同じ（枠の長さではない）・1280×720・25fps。
切れ目は無音の真ん中に置き、つなぎ目は前後 0.1 秒を重ねて溶かす（どの本も元の写真の姿勢で始まり、ほぼ同じ姿勢で終わる）。
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
ASSET = ROOT / "tablet/app/src/main/assets/web/asset"
IMAGE = HERE.parent / "doctor-centered-v1.png"
KIT = HERE.parent / "lipsync-kit.md"
PLAN = HERE / "layers" / "lipsync-plan.json"
TRACK = HERE / "layers" / "face-ja.mp4"
META = HERE / "frames" / "meta" / "f00000.jpg"
SCENES = ["introduction", "subject", "report", "wear"]
TIERS = [(4.7, 5, 15), (9.6, 10, 45), (19.6, 20, 90)]   # 声の長さの上限・枠（秒）・クレジット
W, H, FPS = 1280, 720, 25
BLEND = 0.10
SETTLE = 0.35


def voice_file(scene: str) -> Path:
    briefing = json.loads((ASSET / "briefing-v1.json").read_text(encoding="utf-8"))
    name = next(s["audio"]["ja"] for s in briefing["scenes"] if s["id"] == scene)
    return ASSET / name


def duration(path: Path) -> float:
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
                       capture_output=True, text=True, check=True)
    return float(json.loads(r.stdout)["format"]["duration"])


def silences(path: Path, d: float = 0.12) -> list[tuple[float, float]]:
    r = subprocess.run(["ffmpeg", "-hide_banner", "-i", str(path), "-af", f"silencedetect=noise=-38dB:d={d}", "-f", "null", "-"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    s = [float(v) for v in re.findall(r"silence_start: ([0-9.]+)", r.stderr)]
    e = [float(v) for v in re.findall(r"silence_end: ([0-9.]+)", r.stderr)]
    return list(zip(s, e))


def cmd_plan(work: Path, only: str | None = None) -> None:
    """場面ごとに、無音の真ん中で切って値段が最小になる分け方を出す（同じ値段なら本数の少ない方）。
    only を渡すと、今の計画のその場面だけを、作り直した声で切り直す（ほかの場面の口の動画はそのまま使う）。
    そのときの本の名前は声のファイル名を頭に付ける（前の版の本と取り違えない）。"""
    if only:
        if not PLAN.exists():
            sys.exit(f"{PLAN} が無い。場面だけの切り直しは、今ある計画の上でしかできない")
        plan = json.loads(PLAN.read_text(encoding="utf-8"))
        if plan["voice"][only] == voice_file(only).name:
            sys.exit(f"{only}: 声は計画と同じ（{voice_file(only).name}）。切り直す理由が無い")
    elif PLAN.exists():
        sys.exit(f"{PLAN} は既にある。今ある口の動画はこの切り方で作ってある。声を作り直したときだけ消してから回す")
    else:
        plan = {"voice": {}, "segments": {}}
    total = 0
    (work / "audio").mkdir(parents=True, exist_ok=True)
    for scene in ([only] if only else SCENES):
        src = voice_file(scene)
        prefix = src.stem if only else scene
        dur = duration(src)
        sil = silences(src)
        first = sil[0][1] if sil and sil[0][0] < 0.05 else 0.0
        last = sil[-1][0] if sil and sil[-1][1] > dur - 0.05 else dur
        cuts = [first] + [(a + b) / 2 for a, b in sil if a > first + 0.01 and b < last - 0.01] + [last]
        best = [(0, 0)] + [(10 ** 9, 0)] * (len(cuts) - 1)
        prev = [-1] * len(cuts)
        for j in range(1, len(cuts)):
            for i in range(j):
                tier = next((t for t in TIERS if cuts[j] - cuts[i] <= t[0]), None)
                if tier is None:
                    continue
                cand = (best[i][0] + tier[2], best[i][1] + 1)
                if cand < best[j]:
                    best[j], prev[j] = cand, i
        segs, j = [], len(cuts) - 1
        while j > 0:
            segs.append((round(cuts[prev[j]], 3), round(cuts[j], 3)))
            j = prev[j]
        segs.reverse()
        plan["voice"][scene] = src.name
        plan["segments"][scene] = [{"name": f"{prefix}_{k:02d}", "from": a, "to": b,
                                    "frame": next(t[1] for t in TIERS if b - a <= t[0])} for k, (a, b) in enumerate(segs)]
        for seg in plan["segments"][scene]:
            out = work / "audio" / f"{seg['name']}.mp3"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(src), "-af",
                            f"atrim=start={seg['from']:.3f}:end={seg['to']:.3f},asetpts=PTS-STARTPTS",
                            "-ac", "1", "-ar", "48000", "-c:a", "libmp3lame", "-b:a", "192k", str(out)], check=True)
        cost = best[-1][0]
        total += cost
        print(f"{scene}: {len(segs)} 本・{cost} クレジット")
    PLAN.write_text(json.dumps(plan, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"計 {total} クレジット → {PLAN}")


def domo(*args: str) -> dict:
    exe = shutil.which("domoai.cmd") or shutil.which("domoai")
    if not exe:
        sys.exit("domoai が無い（npm install -g @domoai/cli）")
    r = subprocess.run([exe, *args], capture_output=True, text=True, encoding="utf-8")
    try:
        return json.loads(r.stdout)
    except json.JSONDecodeError:
        sys.exit(f"domoai の出力が読めない: {r.stdout[:300]} {r.stderr[:300]}")


def cmd_send(work: Path) -> None:
    plan = json.loads(PLAN.read_text(encoding="utf-8"))
    prompt = re.search(r"```text\n(.*?)\n```", KIT.read_text(encoding="utf-8"), re.S).group(1)
    posts_path = work / "posts.json"
    posts = json.loads(posts_path.read_text(encoding="utf-8")) if posts_path.exists() else {}
    (work / "out").mkdir(parents=True, exist_ok=True)
    todo = [seg for scene in SCENES for seg in plan["segments"][scene]
            if seg["name"] not in posts and not (work / "out" / f"{seg['name']}.mp4").exists()]
    pending = {n: p for n, p in posts.items() if not (work / "out" / f"{n}.mp4").exists()}
    while todo or pending:
        # 同時に積める本数には上限がある（queue_full）。当たったら、積んだ分が終わるのを待ってから投げ直す
        while todo:
            seg = todo[0]
            name = seg["name"]
            common = ["--duration", str(seg["frame"]), "--orientation", "LANDSCAPE", "--disable-watermark", "true",
                      "--prompt", prompt, "--image-urls", str(IMAGE), "--audio-urls", str(work / "audio" / f"{name}.mp3")]
            dry = domo("talking-avatar", "--dry-run", *common)["data"]
            if not dry.get("ready"):
                sys.exit(f"{name}: 計画が通らない {json.dumps(dry.get('validation'), ensure_ascii=False)}")
            made = domo("talking-avatar", "--confirm", "--plan-digest", dry["plan_digest"], *common)
            if not made.get("ok"):
                reason = ((made.get("error") or {}).get("interpretation") or {}).get("reason")
                if reason == "queue_full" and pending:
                    break
                sys.exit(f"{name}: 投げられない {json.dumps(made, ensure_ascii=False)[:400]}")
            todo.pop(0)
            posts[name] = pending[name] = made["data"]["post_id"]
            posts_path.write_text(json.dumps(posts, indent=1), encoding="utf-8")
            print(f"投げた {name}（{dry['price']['credits']} クレジット）", flush=True)
        if not pending:
            continue
        time.sleep(30)
        for name, post in list(pending.items()):
            d = domo("status", "--post-id", post)["data"]
            if d["outcome"] == "in_progress":
                continue
            pending.pop(name)
            if d["outcome"] != "succeeded" or not d.get("generated_assets"):
                print(f"失敗 {name} {d['outcome']} {d.get('finish_reason')} 返金={d.get('refunded')}", flush=True)
                continue
            # ⚠ Python の urllib は既定の User-Agent で 403 になる。curl で取る
            rc = subprocess.run(["curl", "-sSL", "-f", "-A", "Mozilla/5.0", "-o", str(work / "out" / f"{name}.mp4"),
                                 d["generated_assets"][0]["url"]]).returncode
            print(("取り出した " if rc == 0 else f"curl {rc} ") + name, flush=True)
    print("済み")


def read_frames(path: Path) -> np.ndarray:
    r = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(path), "-vf", f"scale={W}:{H},fps={FPS}",
                        "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True, check=True)
    return np.frombuffer(r.stdout, np.uint8).reshape(-1, H, W, 3)


def cmd_track(work: Path) -> None:
    """試作の時刻表（frames/meta。RoomProto.postMeta が書く）の上に、口の動画を声の位置へ置く。声の無い所は元の写真。"""
    plan = json.loads(PLAN.read_text(encoding="utf-8"))
    meta = json.loads(META.read_text(encoding="utf-8"))
    starts: dict[str, float] = {}
    for c in meta["cues"]:
        starts.setdefault(c["scene"], c["s"] / 1000)
    still = np.asarray(Image.open(IMAGE).convert("RGB").resize((W, H), Image.LANCZOS), np.uint8)
    spans = []    # (始まり, 終わり, ファイル)。全体の時刻（秒）
    for scene in SCENES:
        if plan["voice"][scene] != voice_file(scene).name:
            sys.exit(f"{scene}: 声が作り直されている（{plan['voice'][scene]} → {voice_file(scene).name}）。plan からやり直す")
        for seg in plan["segments"][scene]:
            clip = work / "out" / f"{seg['name']}.mp4"
            if not clip.exists():
                sys.exit(f"口の動画が無い: {clip}")
            spans.append((starts[scene] + seg["from"], starts[scene] + seg["to"], clip))
    total = meta["total"] / 1000
    cache: dict[Path, np.ndarray] = {}
    still_gray = still.astype(np.float32).mean(2)
    lengths = {path: int(round((b - a) * FPS)) + 1 for a, b, path in spans}

    def clip_frames(path: Path) -> np.ndarray:
        if path not in cache:
            for old in list(cache)[:-1]:
                cache.pop(old)
            cache[path] = settle(read_frames(path)[:lengths[path]])
        return cache[path]

    def settle(fr: np.ndarray) -> np.ndarray:
        """どの本も元の写真の姿勢で始まるが、終わりは上下に最大 18 画素ずれる。
        終わりの SETTLE 秒（無音で口が閉じている）で頭を元の位置へ滑らかに戻し、つなぎ目で跳ねないようにする。"""
        dx, dy = offset(fr[-1])
        count = min(int(round(SETTLE * FPS)), len(fr) // 3)
        if (dx == 0 and dy == 0) or count < 2:
            return fr
        out = fr.copy()
        for j in range(count):
            i = len(fr) - count + j
            w = j / (count - 1)
            w = w * w * (3 - 2 * w)
            img = Image.fromarray(fr[i])
            out[i] = np.asarray(img.transform(img.size, Image.AFFINE, (1, 0, dx * w, 0, 1, dy * w), Image.BILINEAR))
        return out

    def offset(frame: np.ndarray) -> tuple[int, int]:
        """鼻から顎のまわりで、元の写真に対するずれ（画素）。粗く 2 画素刻みで探してから 1 画素で詰める。"""
        x0, y0, x1, y1 = 430, 200, 850, 520
        g = frame.astype(np.float32).mean(2)
        ref = still_gray[y0:y1, x0:x1]
        def err(dx: int, dy: int) -> float:
            return float(np.abs(ref - g[y0 + dy:y1 + dy, x0 + dx:x1 + dx]).mean())
        best = min(((err(dx, dy), dx, dy) for dy in range(-24, 25, 2) for dx in range(-16, 17, 2)))
        best = min(((err(dx, dy), dx, dy) for dy in range(best[2] - 1, best[2] + 2) for dx in range(best[1] - 1, best[1] + 2)))
        return best[1], best[2]

    def source_at(t: float, k: int | None) -> np.ndarray:
        if k is None:
            return still
        a, b, path = spans[k]
        fr = clip_frames(path)
        i = int(round((t - a) * FPS))
        return fr[max(0, min(len(fr) - 1, i))]

    def span_at(t: float) -> int | None:
        return next((k for k, (a, b, _) in enumerate(spans) if a <= t < b), None)

    # 境目: 口の動画どうしのつなぎ目と、写真との出入り
    edges = sorted({round(a, 4) for a, _, _ in spans} | {round(b, 4) for _, b, _ in spans})
    TRACK.parent.mkdir(parents=True, exist_ok=True)
    enc = subprocess.Popen(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                            "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-crf", "21", "-preset", "slow",
                            "-g", "25", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(TRACK)], stdin=subprocess.PIPE)
    n = int(round(total * FPS))
    for f in range(n):
        t = f / FPS
        edge = next((e for e in edges if abs(t - e) < BLEND), None)
        if edge is None:
            frame = source_at(t, span_at(t))
        else:
            left = source_at(edge - 1e-3 if t >= edge else t, span_at(edge - 1e-3))    # 境の後は左の最後のコマで止める
            right = source_at(edge if t < edge else t, span_at(edge))                  # 境の前は右の最初のコマで待つ
            w = (t - (edge - BLEND)) / (2 * BLEND)
            frame = (left.astype(np.float32) * (1 - w) + right.astype(np.float32) * w).astype(np.uint8)
        enc.stdin.write(frame.tobytes())
    enc.stdin.close()
    if enc.wait() != 0:
        sys.exit("書き出しに失敗")
    print(f"{TRACK}  {n} コマ・{TRACK.stat().st_size / 1e6:.1f}MB・口の動画 {len(spans)} 本")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("step", choices=["plan", "send", "track"])
    ap.add_argument("work", type=Path, help="切った声と取り出した動画を置くフォルダ（リポジトリの外でよい）")
    ap.add_argument("--scene", choices=SCENES, help="plan だけ。声を作り直したこの場面だけを、今の計画の上で切り直す")
    args = ap.parse_args()
    if args.scene and args.step != "plan":
        sys.exit("--scene は plan にだけ付ける")
    if args.step == "plan":
        cmd_plan(args.work, args.scene)
    else:
        {"send": cmd_send, "track": cmd_track}[args.step](args.work)


if __name__ == "__main__":
    main()
