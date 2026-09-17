#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""連絡の面の呪い（canon/LEDGER.md 0229）を、Unity が焼いた連番から動画と画素検査へ。

    .\\tools\\unity.ps1 menu comms-preview -Set motion=1
    py -3.11 tools/render-comms-curse-preview.py

入力は Assets/Screenshots/comms-preview/motion/（lv000 / lv025 / lv075 / lv100 の連番・type.tsv・curse.tsv・
geometry.json・summary.tsv）。出力は同じ場所へ lvXXX/curse.mp4（打鍵音つき）と evidence.json。

⚠ 絵は Unity が描いたものをそのまま並べる（CPU の模写ではない）。打鍵は Unity が記録した type.tsv。
⚠ 判定は「効果の実在」— 開いた直後は通常の面と同じ画素か / 1.2 秒で地と顔と本文が変わったか /
  地の矩形の外に煙のにじみが出たか。**良し悪しは判定しない**（reference/why.md）。
"""
from __future__ import annotations

import csv
import json
import subprocess
import sys
import wave
from pathlib import Path

import imageio_ffmpeg
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
MOTION = ROOT / "Assets/Screenshots/comms-preview/motion"
LEVELS = ["lv000", "lv025", "lv075", "lv100"]


def wav_read(path: Path):
    with wave.open(str(path), "rb") as f:
        assert f.getsampwidth() == 2, path
        sr, channels = f.getframerate(), f.getnchannels()
        a = np.frombuffer(f.readframes(f.getnframes()), dtype="<i2").astype(np.float32) / 32768
    a = a.reshape(-1, channels)
    if channels == 1:
        a = np.repeat(a, 2, axis=1)
    return a, sr


def read_tsv(path: Path):
    with path.open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f, delimiter="\t"))


def load(path: Path) -> np.ndarray:
    return np.asarray(Image.open(path).convert("RGB")).astype(np.int16)


def crop(img: np.ndarray, r: dict) -> np.ndarray:
    return img[int(r["y0"]):int(r["y1"]), int(r["x0"]):int(r["x1"])]


def ivory_mask(img: np.ndarray) -> np.ndarray:
    """象牙の墨（文字と走り書き）: 明るく、赤みが青みより少し強い画素。"""
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return (r > 140) & (g > 130) & (b > 115) & (r >= b)


def make_video(folder: Path, fps: int, sounds: Path) -> dict:
    taps = read_tsv(folder / "type.tsv")
    frames = [folder / f"f{i:04d}.png" for i in range(len(taps))]
    assert all(p.is_file() for p in frames), f"{folder}: 連番が欠けている"
    clips = [wav_read(sounds / f"sfx_type_{i}.wav") for i in range(1, 9)]
    sr = clips[0][1]
    samples = int(len(frames) / fps * sr) + sr
    out = np.zeros((samples, 2), np.float32)
    n = 0
    for row in taps:
        for _ in range(int(row["hit"])):
            clip = clips[n % len(clips)][0]
            start = int((int(row["frame"]) / fps + 0.035) * sr)
            count = min(len(clip), samples - start)
            if count > 0:
                out[start:start + count] += clip[:count]
            n += 1
    peak = float(np.abs(out).max()) if n else 0.0
    if peak > 1:
        out /= peak
    audio = folder / "type.wav"
    with wave.open(str(audio), "wb") as f:
        f.setnchannels(2)
        f.setsampwidth(2)
        f.setframerate(sr)
        f.writeframes(np.round(out * 32767).astype("<i2").tobytes())
    video = folder / "curse.mp4"
    subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error",
                    "-framerate", str(fps), "-i", str(folder / "f%04d.png"), "-i", str(audio),
                    "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac",
                    "-b:a", "160k", "-shortest", "-movflags", "+faststart", str(video)], check=True)
    return {"frames": len(frames), "seconds": len(frames) / fps, "keystrokes": n, "video": str(video)}


def bbox(mask: np.ndarray, pad: int = 0) -> dict:
    ys, xs = np.where(mask)
    assert len(xs), "領域が見つからない"
    return {"x0": int(xs.min()) - pad, "x1": int(xs.max()) + 1 + pad,
            "y0": int(ys.min()) - pad, "y1": int(ys.max()) + 1 + pad}


def measure_geometry(base_typed: np.ndarray) -> dict:
    """通常の面（侵食度 0・打ち終わり）の画素から矩形を測る。

    ⚠ Unity 側の投影計算（geometry.json）は使わない — 畳んだ後の quad を測って外れたことがある。
    地は背景（14,13,13）より暗い（11,10,9）ので「全チャンネル ≤ 11」で、顔と本文は象牙の墨で取る。
    """
    dark = base_typed.max(axis=2) <= 11
    plate = bbox(dark)
    ink = ivory_mask(base_typed)
    # 顔と本文は縦罫線（顔の右）で分かれる。地の左端から 1/4 より左が顔。
    split = plate["x0"] + (plate["x1"] - plate["x0"]) // 4
    face_mask = ink.copy()
    face_mask[:, split:] = False
    text_mask = ink.copy()
    text_mask[:, :split] = False
    return {"plate": plate, "face": bbox(face_mask, 4), "text": bbox(text_mask, 4)}


def main() -> int:
    geometry = json.loads((MOTION / "geometry.json").read_text(encoding="utf-8"))
    fps = int(geometry["fps"])
    summary = {r["level"]: r for r in read_tsv(MOTION / "summary.tsv")}
    sounds = ROOT / "Assets/Resources/Sound"
    f_open = 3                          # 0.10 秒（開いた直後）
    f_late = int(round(1.2 * fps))      # 1.20 秒（1 秒で重なった後）
    base_open = load(MOTION / "lv000" / f"f{f_open:04d}.png")
    base_late = load(MOTION / "lv000" / f"f{f_late:04d}.png")
    rows0 = read_tsv(MOTION / "lv000" / "curse.tsv")
    typed_end0 = next(int(r["frame"]) for r in rows0 if r["stage"] == "Hold")
    measured = measure_geometry(load(MOTION / "lv000" / f"f{typed_end0:04d}.png"))
    plate, face, text = measured["plate"], measured["face"], measured["text"]
    geometry["measured"] = measured
    # 地の矩形の外・40 px の環（毛羽立ち・煙のにじみ・糸くずが出る所）
    ring = np.zeros(base_late.shape[:2], bool)
    ring[max(0, plate["y0"] - 40):plate["y1"] + 40, max(0, plate["x0"] - 40):plate["x1"] + 40] = True
    ring[plate["y0"]:plate["y1"], plate["x0"]:plate["x1"]] = False
    background = float(base_late[5:40, 5:40].mean())

    results = {}
    failures = []
    for level in LEVELS:
        folder = MOTION / level
        video = make_video(folder, fps, sounds)
        rows = read_tsv(folder / "curse.tsv")
        img_open = load(folder / f"f{f_open:04d}.png")
        img_late = load(folder / f"f{f_late:04d}.png")
        d_open = float(np.abs(img_open - base_open).mean())
        d_late_plate = float(np.abs(crop(img_late, plate) - crop(base_late, plate)).mean())
        d_late_face = float(np.abs(crop(img_late, face) - crop(base_late, face)).mean())
        d_late_text = float(np.abs(crop(img_late, text) - crop(base_late, text)).mean())
        # 矩形の外で変わった画素（毛羽立ち・にじみ・糸くず）。暗い背景では暗くはならないので、差で数える。
        ring_changed = int(((np.abs(img_late - base_late).mean(axis=2) > 2.0) & ring).sum())
        ring_dark = int(((img_late.mean(axis=2) < background - 4) & ring).sum())
        ink_late = int(ivory_mask(crop(img_late, text)).sum())
        ink_base = int(ivory_mask(crop(base_late, text)).sum())
        # 打ち終わった所（読ませている段の頭）で切られた字の割合
        typed_end = next((int(r["frame"]) for r in rows if r["stage"] == "Hold"), None)
        cx_end = int(rows[typed_end]["cx"]) if typed_end is not None else -1
        curse_end = float(rows[typed_end]["curse"]) if typed_end is not None else -1
        s = summary[f"{float(level[2:]) / 100:.2f}"]
        results[level] = {
            **video,
            "curse_open": float(s["curse_open"]), "curse_1_2s": float(s["curse_1_2s"]),
            "target": float(s["target"]), "ramp_frame": int(s["ramp_frame"]),
            "cx_1_2s": int(s["cx_1_2s"]), "cx_typed_end": cx_end, "curse_typed_end": curse_end,
            "pixel_delta_open_vs_lv000": d_open,
            "pixel_delta_1_2s_plate": d_late_plate, "pixel_delta_1_2s_face": d_late_face,
            "pixel_delta_1_2s_text": d_late_text,
            "ring_changed_pixels_1_2s": ring_changed, "ring_dark_pixels_1_2s": ring_dark,
            "ivory_pixels_text_1_2s": ink_late, "ivory_pixels_text_1_2s_lv000": ink_base,
        }
        lv = float(level[2:]) / 100
        if lv > 0:
            if d_open > 1.0:
                failures.append(f"{level}: 開いた直後（0.1 秒）が通常の面と違う（画素差 {d_open:.2f}/255）")
            if d_late_plate < 1.0:
                failures.append(f"{level}: 1.2 秒で地が 1 画素も変わっていない（{d_late_plate:.3f}/255）")
            if ring_changed < 30:
                failures.append(f"{level}: 地の矩形の外に毛羽立ち・糸くずがほぼ無い（変わった画素 {ring_changed}）")
            if d_late_text < 0.5:
                failures.append(f"{level}: 1.2 秒で本文が変わっていない（{d_late_text:.3f}/255）")
        else:
            if ring_changed > 0:
                failures.append(f"{level}: 侵食度 0 なのに矩形の外が変わっている（{ring_changed} 画素）")
    if results["lv100"]["pixel_delta_1_2s_face"] < 3.0:
        failures.append(f"lv100: 1.2 秒で顔が人形になっていない（顔の画素差 {results['lv100']['pixel_delta_1_2s_face']:.2f}/255）")
    results["_geometry"] = geometry
    results["_frames"] = {"open": f_open, "late": f_late}
    results["_failures"] = failures
    (MOTION / "evidence.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    if failures:
        print("FAIL:\n  " + "\n  ".join(failures))
        return 1
    print("OK: 出た初めは通常 / 1.2 秒で地・顔・本文が変わる / 矩形の外に毛羽立ち")
    return 0


if __name__ == "__main__":
    sys.exit(main())
