"""2 周目 A の右の幕を、動画生成（Flow）の版で本番の素材にする（LEDGER 0389 の C）。当日の写真へ合わせ直すのもこれ。

    py -3.11 tools/gen-plate/stain3/build_flow_right.py                        # 台帳の写真で static-inputs へ書く
    py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate <写真>         # 当日の無人写真へ合わせ直す
    py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate <写真> --dry   # 確認の画だけ（書かない）
    py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate <写真> --flow-inputs   # 生成し直すときの首尾フレーム
    py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate <写真> --from-flow <Flow の mp4> --dry
                                                       # 生成し直した動画で組む（確かめてから --dry を外す）

入力は tools/gen-plate/stain3/flow/flow_v2_plain_to_grin_4x3.mp4（Flow 360p の 4:3 部分を 640x480 にしたもの。
作り方は README「動画生成で試す」）。Flow は部屋ごと描き直すので、最初のコマ（何も無い幕）を無人写真へ 1 回だけ
位置合わせし、その写し方と色の合わせ方を全部のコマへ同じに掛ける。手すりと顔の範囲は動画を作った写真
（BASE_PLATE）の座標で決めてあるので、BASE_PLATE から指定の写真への写し方で動かす。

合わせられないとき（突き合わせの点が MIN_POINTS 未満か、残差の中央値が MAX_RESIDUAL_PX を超える）は
何も書かずに終了コード 3 で止まる。カメラの位置か幕の並びが変わったということなので、動画を生成し直す
（--flow-inputs が首尾フレームを出す。README「当日に作り直す」）。

0389 の C（何も無い → しみへディゾルブ → 顔が加速して出る → 人形が覗く）を台本の段へ割る:
  ratio   しみが出きったコマ（元 2.0 秒）÷ 最初のコマ。段 1 で掛け算にして 0.25 秒で入れる（ディゾルブの代わり。
          生の映像に掛けるので、幕の前を通る体は消えない）
  pop     元 2.0〜2.9 秒を 3 倍・2.9〜4.0 秒を 1.6 倍・4.0〜5.2 秒を等速。最後のコマを 3 枚足す。段 2
  watch   pop の最後のコマ（顔と覗く人形）。段 3
  face    pop の最後のコマの顔だけ（手すりより上）。段 4 の CG の人形がついてくる間

素材の名前は写真の名前から付ける（stain_A_right_flow_*_<写真の日時>.*）。いま台本が使う版は
static-inputs/stain_A_right_flow.json（POINTER）が持ち、tools/show-apply-stain-right.py と卓の「合わせ直す」が読む。
POINTER の source はその版を組んだ動画・動画を作った写真・最後のフレーム・しみの秒。次に合わせ直すときは
そこから組む（生成し直した動画で組んだら、以後の合わせ直しもその動画を使う）。
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_right_burst as rb  # noqa: E402

ROOT = rb.ROOT
W, H, FPS = rb.W, rb.H, rb.FPS
HERE = Path(__file__).resolve().parent
SRC = HERE / "flow/flow_v2_plain_to_grin_4x3.mp4"
END_FRAME = HERE / "flow/end_grin_A_20261115_125052.png"   # Flow に渡した最後のフレーム（案 1 の顔と人形）
CAPTURES = ROOT / "tools/web-compositor/captures"
BASE_PLATE = CAPTURES / "plate_A_20261115_125052.jpg"      # 動画を作った写真
OUT = ROOT / "tools/web-compositor/static-inputs"
POINTER = OUT / "stain_A_right_flow.json"
WORK = ROOT / "Logs/gen-plate/stain2A_right_video_20261115/build"
MIN_POINTS = 150          # 2026-11-16 の元の写真で 290 / 418 点
MAX_RESIDUAL_PX = 1.0     # 同じく 0.37px


def tag_of(plate: Path) -> str:
    stem = plate.stem
    return stem[len("plate_A_"):] if stem.startswith("plate_A_") else stem


def names_of(tag: str) -> dict:
    return dict(ratio=f"stain_A_right_flow_ratio_{tag}.png", ratio_mask=f"stain_A_right_flow_ratio_mask_{tag}.png",
                pop=f"stain_A_right_flow_pop_{tag}.mp4", mask=f"stain_A_right_flow_mask_{tag}.png",
                watch=f"stain_A_right_flow_watch_{tag}.png", face=f"stain_A_right_flow_face_{tag}.png",
                face_mask=f"stain_A_right_flow_face_mask_{tag}.png", meta=f"stain_A_right_flow_pop_{tag}.json")


def current() -> dict:
    """いま台本が使う版（POINTER）。無ければ元の写真の版。"""
    try:
        return json.loads(POINTER.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"tag": tag_of(BASE_PLATE), "plate": BASE_PLATE.name, "names": names_of(tag_of(BASE_PLATE))}


def source_of(version: dict) -> dict:
    """版を組んだ動画・動画を作った写真・最後のフレーム・しみの秒。POINTER に無ければ元の動画（0389 の C）。"""
    src = version.get("source") if isinstance(version.get("source"), dict) else {}

    def path(key, default):
        return ROOT / src[key] if isinstance(src.get(key), str) and src[key] else default
    return {"video": path("video", SRC), "base": path("base", BASE_PLATE), "end": path("end", END_FRAME),
            "stainAt": float(src.get("stainAt", STAIN_AT))}


def crop_flow(raw: Path, out: Path) -> Path:
    """Flow が返した 16:9 の動画から、首尾フレームに置いた 4:3 の部分（左右の黒帯の内側）を 640x480 で取り出す。"""
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
                            "-of", "csv=p=0", str(raw)], capture_output=True, text=True, check=True).stdout.strip()
    w, h = (int(v) for v in probe.split(",")[:2])
    cw = round(h * 4 / 3)
    if abs(w / h - 16 / 9) > 0.02 or cw > w:
        raise SystemExit(f"Flow の動画が 16:9 ではない（{w}x{h}）。首尾フレームは video_inputs.to_16x9 の形で渡す")
    out.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(raw), "-vf",
                    f"crop={cw}:{h}:{(w - cw) // 2}:0,scale={W}:{H}:flags=lanczos,fps={FPS}",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", "-an", str(out)], check=True)
    return out


STAIN_AT = 2.0                      # しみが出きって、まだ膨らんでいないコマ（元の秒）
REST = [(2.0, 2.9, 3.0), (2.9, 4.0, 1.6), (4.0, 5.2, 1.0)]   # (元の始め, 終わり, 速さ)。retime_flow.py の C と同じ
HOLD_FRAMES = 3
RIGHT_BOX = (280, 0, W, H)          # Flow が変えてよい所（右の幕と、その縁の裏の人形）。左の幕は使わない
RAIL = rb.SETS["real"]["rail"]
FACE_BOX = (280, 0, 522, 245)     # 顔としみ（手すりの右端の支柱より左）。人形と右の支柱を入れない


def read_frames(path: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.uint8).reshape(-1, H, W, 3).astype(np.float32)


def reg_maps(a: np.ndarray, ref: np.ndarray, deg: int = 3):
    """rb.register と同じ突き合わせで、ref の座標 → a の座標の写し方（remap の表）を返す。全コマに同じ表を掛ける。"""
    rg, ag = ref.mean(2), a.mean(2)
    pts = []
    size, step = 40, 24
    for y0 in list(range(4, H - size - 4, step)) + [H - size - 4]:
        for x0 in list(range(4, W - size - 4, step)) + [W - size - 4]:
            rx = (min(16, x0), min(16, W - x0 - size))
            ry = (min(44, y0), min(44, H - y0 - size))
            if min(*rx, *ry) < 3:
                continue
            m = rb._match(rg, ag, x0, y0, size, rx, ry)
            if m and m[2] > 0.8:
                pts.append((x0 + size / 2, y0 + size / 2, m[0], m[1]))
    p = np.array(pts, np.float64)
    X = rb._poly_terms(p[:, 0], p[:, 1], deg)
    keep = np.ones(len(p), bool)
    for _ in range(6):
        cx, *_ = np.linalg.lstsq(X[keep], p[keep, 2], rcond=None)
        cy, *_ = np.linalg.lstsq(X[keep], p[keep, 3], rcond=None)
        err = np.hypot(X @ cx - p[:, 2], X @ cy - p[:, 3])
        keep = err < max(1.0, 2.5 * np.median(err[keep]))
    gy, gx = np.mgrid[0:H, 0:W].astype(np.float64)
    T = rb._poly_terms(gx, gy, deg)
    mx = (gx + T @ cx).astype(np.float32)
    my = (gy + T @ cy).astype(np.float32)
    info = {"points": int(keep.sum()), "of": len(p), "residualPx": round(float(np.median(err[keep])), 2),
            "maxShiftPx": round(float(np.hypot(mx - gx, my - gy).max()), 1)}
    return mx, my, info


def passes(info: dict) -> bool:
    return info["points"] >= MIN_POINTS and info["residualPx"] <= MAX_RESIDUAL_PX


def load_640(path: Path) -> np.ndarray:
    a = rb.load(path)
    if a.shape[:2] != (H, W):
        a = np.asarray(Image.fromarray(a.astype(np.uint8)).resize((W, H), Image.LANCZOS), dtype=np.float32)
    return a


def main() -> int:
    import cv2
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--plate", default=None, help="合わせる無人写真（既定はいま台本が使う版の写真）")
    ap.add_argument("--dry", action="store_true", help="static-inputs へ書かない")
    ap.add_argument("--flow-inputs", action="store_true", help="生成し直すときの首尾フレーム（16:9）を書いて終わる")
    ap.add_argument("--from-flow", default=None,
                    help="生成し直した Flow の動画（16:9）。4:3 を切り出して flow/ に置き、--plate の写真を動画を作った写真として組む")
    ap.add_argument("--stain-at", type=float, default=None,
                    help="しみが出きってまだ膨らんでいないコマの秒（生成し直した動画で変わる。段 2 の区切りも同じだけずらす）")
    a = ap.parse_args()
    WORK.mkdir(parents=True, exist_ok=True)
    plate_path = Path(a.plate) if a.plate else CAPTURES / current()["plate"]
    if not plate_path.exists() and (CAPTURES / plate_path.name).exists():
        plate_path = CAPTURES / plate_path.name
    if not plate_path.exists():
        print(f"写真が無い: {plate_path}")
        return 2
    plate = load_640(plate_path)
    tag = tag_of(plate_path)
    print(f"写真: {plate_path.name}（名前の札 {tag}）")
    source = source_of(current())
    if a.from_flow:
        # 生成し直した動画: 最初のフレームはこの写真、最後のフレームは --flow-inputs が書いた end（無ければ描き直した版）
        inputs = WORK / f"flow_inputs_{tag}"
        end = inputs / "end.png"
        source = {"video": crop_flow(Path(a.from_flow), HERE / f"flow/flow_{tag}_4x3.mp4"), "base": plate_path,
                  "end": end if end.exists() else source["end"], "stainAt": source["stainAt"]}
        print(f"生成し直した動画: {source['video'].relative_to(ROOT).as_posix()}")
    if a.stain_at is not None:
        source["stainAt"] = a.stain_at
    stain_at = source["stainAt"]
    rest = [(s0 - STAIN_AT + stain_at, s1 - STAIN_AT + stain_at, sp) for s0, s1, sp in REST]
    print(f"組む動画: {source['video'].relative_to(ROOT).as_posix()}（動画を作った写真 {source['base'].name}・しみ {stain_at}s）")

    def remap(img, mx_, my_):
        return cv2.remap(np.ascontiguousarray(img), mx_, my_, cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)

    # 動画を作った写真 → 指定の写真。手すりと範囲の箱を動かす
    bx, by, binfo = reg_maps(load_640(source["base"]), plate)
    print(f"元の写真との位置合わせ: {binfo['points']}/{binfo['of']} 点・残差の中央値 {binfo['residualPx']}px・最大のずれ {binfo['maxShiftPx']}px")

    if a.flow_inputs:
        import video_inputs as vi
        out = WORK / f"flow_inputs_{tag}"
        out.mkdir(parents=True, exist_ok=True)
        start = Image.fromarray(plate.astype(np.uint8))
        start.save(out / "start.png")
        vi.to_16x9(start).save(out / "start_16x9.png")
        if passes(binfo):
            end = Image.fromarray(np.clip(remap(load_640(source["end"]), bx, by), 0, 255).astype(np.uint8))
            end.save(out / "end.png")
            vi.to_16x9(end).save(out / "end_16x9.png")
            print(f"首尾フレーム: {out.relative_to(ROOT).as_posix()}（start_16x9.png → end_16x9.png）")
        else:
            print(f"首尾フレーム: start だけ書いた（{out.relative_to(ROOT).as_posix()}）。元の写真と合わないので、"
                  "最後のフレームは Codex で描き直す（README「当日に作り直す」）")
        return 0

    src = read_frames(source["video"])
    mx, my, info = reg_maps(src[0], plate)
    print(f"位置合わせ: {info['points']}/{info['of']} 点・残差の中央値 {info['residualPx']}px・最大のずれ {info['maxShiftPx']}px")
    if not (passes(info) and passes(binfo)):
        print(f"合わせられない（点 {MIN_POINTS} 以上・残差 {MAX_RESIDUAL_PX}px 以下が要る）。何も書いていない。"
              "カメラの位置か幕の並びが変わっている。動画を生成し直す（--flow-inputs）")
        return 3

    def warp(f):
        return remap(f, mx, my)

    def base_mask(m):
        return np.clip(remap(m, bx, by), 0, 1)

    f0 = warp(src[0])
    d = np.abs(f0 - plate).mean(2)
    calm = d < np.percentile(d, 55)
    kc = [np.polyfit(f0[..., c][calm], plate[..., c][calm], 1) for c in range(3)]

    def grade(f):
        f = f.copy()
        for c, (k, o) in enumerate(kc):
            f[..., c] = np.clip(f[..., c] * k + o, 0, 255)
        return f

    def at(sec):
        return grade(warp(src[min(len(src) - 1, int(round(sec * FPS)))]))

    F0 = grade(f0)
    resid = float(np.abs(F0 - plate).mean())
    print(f"最初のコマと無人写真の差（平均）: {resid:.2f} / 255")

    # 段 2 のコマ（元の時刻を速さで詰める。コマは近い方を取る。混ぜない）
    times = []
    for s0, s1, sp in rest:
        n = int(round((s1 - s0) / sp * FPS))
        times += [s0 + i * sp / FPS for i in range(n)]
    pop = [at(t) for t in times]
    pop += [pop[-1]] * HOLD_FRAMES

    box = base_mask(rb.box_mask(RIGHT_BOX, 0))
    above = base_mask(rb.above_rail(RAIL))
    face_box = base_mask(rb.box_mask(FACE_BOX, 3))
    # 合成範囲: 段 2 のどのコマでも最初のコマから変わった所
    ch = np.zeros((H, W), np.float32)
    for f in pop:
        ch = np.maximum(ch, rb.soft_mask(f, F0, (0, 0, W, H), 0.03, 0.08, close=6))
    region = rb.blur(rb.grow(ch * box, 6), 4)

    # 段 1 のしみ: 比（linear）。1 を超える所は 1（掛け算は暗くするだけ）
    Fs = at(stain_at)
    ms = rb.soft_mask(Fs, F0, (0, 0, W, H), 0.02, 0.06, close=4) * box * above
    ms = rb.blur(rb.grow(ms, 2), 1.5) * face_box
    ratio = np.clip(rb.lin(Fs) / np.maximum(rb.lin(F0), 1e-3), 0, 1)
    ratio = np.ones_like(ratio) * (1 - ms[..., None]) + ratio * ms[..., None]

    # 段 4 の顔だけ: 最後のコマの、手すりより上
    last = pop[-1]
    mf = rb.soft_mask(last, F0, (0, 0, W, H), 0.03, 0.08, close=6) * box * above
    mf = rb.blur(rb.grow(mf, 4), 3) * face_box

    def comp(f, m):
        return np.clip(plate * (1 - m[..., None]) + f * m[..., None], 0, 255).astype(np.uint8)

    frames = [comp(f, region) for f in pop]
    watch = frames[-1]
    face = comp(last, mf)

    NAMES = names_of(tag)
    out = WORK if a.dry else OUT
    tmp = WORK / "frames"
    tmp.mkdir(exist_ok=True)
    for p in tmp.glob("*.png"):
        p.unlink()
    for i, f in enumerate(frames):
        Image.fromarray(f).save(tmp / f"f{i:04d}.png")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", str(tmp / "f%04d.png"),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-g", str(FPS), "-movflags", "+faststart",
                    str(out / NAMES["pop"])], check=True)
    Image.fromarray(watch).save(out / NAMES["watch"])
    Image.fromarray(face).save(out / NAMES["face"])
    Image.fromarray(rb.srgb(ratio).astype(np.uint8)).save(out / NAMES["ratio"])

    def save_mask(m, name):
        Image.fromarray(np.clip(rb.to_frame(m) * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / name)
    save_mask(region, NAMES["mask"])
    save_mask(rb.blur(rb.grow(ms, 4), 3), NAMES["ratio_mask"])
    save_mask(mf, NAMES["face_mask"])

    meta = {"durationSec": round(len(frames) / FPS, 3), "fps": FPS, "plate": plate_path.name, "tag": tag,
            "register": info, "registerBase": binfo, "firstFrameDiff": round(resid, 2),
            "source": source["video"].relative_to(ROOT).as_posix(), "rest": rest, "stainAt": stain_at}
    (out / NAMES["meta"]).write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    if not a.dry:
        tmp_p = POINTER.with_suffix(".json.tmp")
        rel = {k: Path(v).resolve().relative_to(ROOT).as_posix() for k, v in source.items() if k != "stainAt"}
        tmp_p.write_text(json.dumps({"tag": tag, "plate": plate_path.name, "durationSec": meta["durationSec"],
                                     "names": NAMES, "source": {**rel, "stainAt": stain_at}},
                                    ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp_p, POINTER)
    print(json.dumps(meta, ensure_ascii=False))

    # 確認の画: 無人写真・段 1（無人写真 × 比）・段 2 の 5 コマ・段 3・段 4・マスク 3 枚
    s1 = rb.srgb(rb.lin(plate) * ratio)
    picks = [0, 3, 7, 12, len(frames) // 2, len(frames) - 1]
    tiles = [("plate", plate), ("段1 しみ（掛け算）", s1)] + [(f"段2 {i / FPS:.2f}s", frames[i]) for i in picks] + \
            [("段3 watch", watch), ("段4 face", face),
             ("mask pop / ratio / face", np.stack([region, ms, mf], 2) * 255)]
    cols = 4
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new("RGB", (W * cols, (H + 30) * rows), (20, 20, 20))
    dr = ImageDraw.Draw(sheet)
    for k, (label, arr) in enumerate(tiles):
        x, y = (k % cols) * W, (k // cols) * (H + 30)
        sheet.paste(Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)), (x, y + 30))
        dr.text((x + 8, y + 8), label, fill=(255, 255, 255))
    sheet.save(WORK / "sheet.png")
    print("確認の画:", (WORK / "sheet.png").relative_to(ROOT).as_posix())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
