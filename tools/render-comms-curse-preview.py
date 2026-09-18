#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""連絡の面の呪い（canon/LEDGER.md 0229 / 0230）を、Unity が焼いた連番から動画と画素検査へ。

    .\\tools\\unity.ps1 menu comms-preview -Set motion=1
    py -3.11 tools/render-comms-curse-preview.py

入力は Assets/Screenshots/comms-preview/motion/（lv000 / lv025 / lv075 / lv100 の連番・type.tsv・curse.tsv・
geometry.json・summary.tsv）。出力は同じ場所へ lvXXX/curse.mp4（打鍵音・乱れの音つき）と evidence.json。

⚠ 絵は Unity が描いたものをそのまま並べる（CPU の模写ではない）。打鍵と乱れの音は Unity が記録した
  type.tsv / curse.tsv の hit / sfx 列。
⚠ 判定は「効果の実在」— 0.25 は 開いた直後は通常の面と同じ画素か / 1.2 秒で地と顔と本文が変わったか /
  地の矩形の外に煙のにじみが出たか（0229）。0.75 と 1 は 全文が出た直後は通常の面と同じか / 塗り替わりの途中で
  本文の上 1/3 だけが変わっているか / 塗り替わり切ったら地と顔と本文が変わったか（0230）。
  **良し悪しは判定しない**（reference/why.md）。
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


def make_video(folder: Path, fps: int, sounds: Path, sfx_index: int) -> dict:
    taps = read_tsv(folder / "type.tsv")
    curse_rows = read_tsv(folder / "curse.tsv")
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
    # 塗り替わりの頭の乱れの音（0230）。Unity が記録した sfx 列のコマに 1 発（3 種は走行の順で回る）。
    sfx_frames = [int(r["frame"]) for r in curse_rows if int(r.get("sfx") or 0) > 0]
    for k, f in enumerate(sfx_frames):
        clip, rate = wav_read(sounds / f"sfx_glitch_{(sfx_index + k) % 3 + 1}.wav")
        assert rate == sr
        start = int((f / fps + 0.035) * sr)
        count = min(len(clip), samples - start)
        if count > 0:
            out[start:start + count] += clip[:count] * 0.8
    peak = float(np.abs(out).max()) if (n or sfx_frames) else 0.0
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
    return {"frames": len(frames), "seconds": len(frames) / fps, "keystrokes": n,
            "sweep_sfx_frames": sfx_frames, "video": str(video)}


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
    # 前の版（0230・乱れ無し）の焼きと比べるとき: --before <motion dir>
    before_dir = Path(sys.argv[sys.argv.index("--before") + 1]) if "--before" in sys.argv else None
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
    sfx_index = 0
    for level in LEVELS:
        folder = MOTION / level
        video = make_video(folder, fps, sounds, sfx_index)
        sfx_index += len(video["sweep_sfx_frames"])
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
            "cx_1_2s": int(s["cx_1_2s"]), "cx_max": int(s["cx_max"]), "cx_typed_end": cx_end, "curse_typed_end": curse_end,
            "pixel_delta_open_vs_lv000": d_open,
            "pixel_delta_1_2s_plate": d_late_plate, "pixel_delta_1_2s_face": d_late_face,
            "pixel_delta_1_2s_text": d_late_text,
            "ring_changed_pixels_1_2s": ring_changed, "ring_dark_pixels_1_2s": ring_dark,
            "ivory_pixels_text_1_2s": ink_late, "ivory_pixels_text_1_2s_lv000": ink_base,
        }
        lv = float(level[2:]) / 100
        possessed = int(s.get("possessed") or 0) == 1
        if possessed:
            # ---- 憑依の出し方（0230）: 一気に出た直後は通常の面 / 途中は上だけ / 塗り替わり切ったら全部 ----
            base_typed = load(MOTION / "lv000" / f"f{typed_end0:04d}.png")
            f_shown = int(s["shown_frame"]) + 1
            f_start, f_end = int(s["sweep_start_frame"]), int(s["sweep_end_frame"])
            assert 0 < f_start < f_end, f"{level}: 塗り替わりの縁が無い（{f_start}〜{f_end}）"
            f_mid = (f_start + f_end) // 2
            img_shown = load(folder / f"f{f_shown:04d}.png")
            img_mid = load(folder / f"f{f_mid:04d}.png")
            img_done = load(folder / f"f{min(f_end + 2, len(rows) - 1):04d}.png")
            h = text["y1"] - text["y0"]
            top_third = {**text, "y1": text["y0"] + h // 3}
            bottom_third = {**text, "y0": text["y1"] - h // 3}
            d_shown_text = float(np.abs(crop(img_shown, text) - crop(base_typed, text)).mean())
            d_shown_face = float(np.abs(crop(img_shown, face) - crop(base_typed, face)).mean())
            d_shown_plate = float(np.abs(crop(img_shown, plate) - crop(base_typed, plate)).mean())
            d_mid_top = float(np.abs(crop(img_mid, top_third) - crop(base_typed, top_third)).mean())
            d_mid_bottom = float(np.abs(crop(img_mid, bottom_third) - crop(base_typed, bottom_third)).mean())
            # 前線の向きは**矩形の外の環**で見る（0231 の乱れは矩形の中身を動かすが、枠は動かさない）。
            # 途中の画で、環の上半分だけが変わっていれば前線は上から降りている。
            mid_y = (plate["y0"] + plate["y1"]) // 2
            changed_mid = (np.abs(img_mid - base_typed).mean(axis=2) > 2.0) & ring
            ring_mid_top = int(changed_mid[:mid_y].sum())
            ring_mid_bottom = int(changed_mid[mid_y:].sum())
            # ---- 乱れ（0231）----
            tear_col = [float(r.get("tear") or 0.0) for r in rows]
            torn_col = [int(r.get("torn") or 0) for r in rows]
            tear_shown_max = max(tear_col[i] for i in range(len(rows)) if rows[i]["phase"] == "Shown")
            tear_mid = tear_col[f_mid]
            # 前線がまだ上半分に居るあいだ（f_start〜f_mid）、本文の下 1/3 がコマごとに動くか
            motion_below = [float(np.abs(crop(load(folder / f"f{i + 1:04d}.png"), bottom_third)
                                        - crop(load(folder / f"f{i:04d}.png"), bottom_third)).mean())
                            for i in range(f_start, f_mid)]
            motion_below_mean = float(np.mean(motion_below)) if motion_below else 0.0
            # 尾: 抜けてから 0.12 秒 ＋ 1 コマの後は、地の矩形がコマごとに動かない
            f_settle = f_end + int(np.ceil(0.12 * fps)) + 1
            settle = [float(np.abs(crop(load(folder / f"f{i + 1:04d}.png"), plate)
                                   - crop(load(folder / f"f{i:04d}.png"), plate)).mean())
                      for i in range(f_settle, min(f_settle + 6, len(rows) - 1))]
            settle_max = max(settle) if settle else 0.0
            tear_settle = tear_col[min(f_settle, len(rows) - 1)]
            # 明るさ: 地の矩形の最大画素と最大平均（砂を混ぜていなければ前の版を超えない）
            lum_max = max(float(crop(load(folder / f"f{i:04d}.png"), plate).max()) for i in range(0, len(rows), 2))
            lum_mean_max = max(float(crop(load(folder / f"f{i:04d}.png"), plate).mean()) for i in range(0, len(rows), 2))
            before = {}
            if before_dir is not None and (before_dir / level / "curse.tsv").is_file():
                b_rows = read_tsv(before_dir / level / "curse.tsv")
                b_lum_max = max(float(crop(load(before_dir / level / f"f{i:04d}.png"), plate).max()) for i in range(0, len(b_rows), 2))
                b_lum_mean = max(float(crop(load(before_dir / level / f"f{i:04d}.png"), plate).mean()) for i in range(0, len(b_rows), 2))
                b_motion = [float(np.abs(crop(load(before_dir / level / f"f{i + 1:04d}.png"), bottom_third)
                                         - crop(load(before_dir / level / f"f{i:04d}.png"), bottom_third)).mean())
                            for i in range(f_start, f_mid) if (before_dir / level / f"f{i + 1:04d}.png").is_file()]
                before = {"lum_max": b_lum_max, "lum_mean_max": b_lum_mean,
                          "motion_below_front_mean": float(np.mean(b_motion)) if b_motion else 0.0}
            d_done_text = float(np.abs(crop(img_done, text) - crop(base_typed, text)).mean())
            d_done_face = float(np.abs(crop(img_done, face) - crop(base_typed, face)).mean())
            d_done_plate = float(np.abs(crop(img_done, plate) - crop(base_typed, plate)).mean())
            ring_done = int(((np.abs(img_done - base_typed).mean(axis=2) > 2.0) & ring).sum())
            results[level].update({
                "possessed": True, "shown_frame": f_shown, "sweep_start_frame": f_start, "sweep_end_frame": f_end,
                "sweep_seconds": (f_end - f_start) / fps, "sweep_sfx": int(s.get("sweep_sfx") or 0),
                "pixel_delta_shown_text": d_shown_text, "pixel_delta_shown_face": d_shown_face,
                "pixel_delta_shown_plate": d_shown_plate,
                "pixel_delta_mid_top_third": d_mid_top, "pixel_delta_mid_bottom_third": d_mid_bottom,
                "pixel_delta_done_text": d_done_text, "pixel_delta_done_face": d_done_face,
                "pixel_delta_done_plate": d_done_plate, "ring_changed_pixels_done": ring_done,
                "ring_changed_mid_top": ring_mid_top, "ring_changed_mid_bottom": ring_mid_bottom,
                "tear_shown_max": tear_shown_max, "tear_mid": tear_mid, "torn_bands_max": max(torn_col),
                "motion_below_front_mean": motion_below_mean, "motion_below_front": motion_below,
                "settle_frame": f_settle, "settle_delta_max": settle_max, "tear_at_settle": tear_settle,
                "lum_max": lum_max, "lum_mean_max": lum_mean_max, "before": before,
            })
            if d_open > 1.0:
                failures.append(f"{level}: 開いた直後（0.1 秒）が通常の面と違う（画素差 {d_open:.2f}/255）")
            if video["keystrokes"] != 0:
                failures.append(f"{level}: 打鍵が {video['keystrokes']} 発（一気に出るので 0 のはず）")
            if d_shown_text > 1.0 or d_shown_face > 1.0 or d_shown_plate > 1.0:
                failures.append(f"{level}: 一気に出た直後が通常の面と違う（本文 {d_shown_text:.2f} / 顔 {d_shown_face:.2f} / 地 {d_shown_plate:.2f}）")
            if not (d_mid_top > 2.0 and ring_mid_top >= 30 and ring_mid_bottom <= max(10, int(0.15 * ring_mid_top))):
                failures.append(f"{level}: 前線が上から降りていない（途中の画: 上 1/3 の差 {d_mid_top:.2f} / 環の変化 上 {ring_mid_top} 下 {ring_mid_bottom}）")
            # ---- 乱れ（0231）----
            if tear_shown_max > 0.0:
                failures.append(f"{level}: 読ませているあいだに乱れが立った（{tear_shown_max:.2f}）")
            if tear_mid < 0.5:
                failures.append(f"{level}: 降りている最中の乱れが弱い（{tear_mid:.2f}・頭打ち 0.6 のはず）")
            if motion_below_mean < 0.3:
                failures.append(f"{level}: 前線の下側が動いていない（下 1/3 のコマ差 平均 {motion_below_mean:.3f}・乱れが効いていない）")
            if settle_max > 0.3 or tear_settle > 0.0:
                failures.append(f"{level}: 尾が引いた後も動いている（コマ差 {settle_max:.3f} / 乱れ {tear_settle:.2f}）")
            if before:
                if lum_max > before["lum_max"] + 2 or lum_mean_max > before["lum_mean_max"] * 1.05 + 0.5:
                    failures.append(f"{level}: 前の版より明るい（最大 {lum_max:.0f} → 前 {before['lum_max']:.0f} / 平均の最大 {lum_mean_max:.1f} → 前 {before['lum_mean_max']:.1f}）")
            if d_done_text < 0.5 or d_done_face < 3.0 or d_done_plate < 1.0:
                failures.append(f"{level}: 塗り替わり切っても変わっていない（本文 {d_done_text:.2f} / 顔 {d_done_face:.2f} / 地 {d_done_plate:.2f}）")
            if ring_done < 30:
                failures.append(f"{level}: 塗り替わり切っても矩形の外に毛羽立ち・糸くずがほぼ無い（変わった画素 {ring_done}）")
            if len(video["sweep_sfx_frames"]) != 1 or video["sweep_sfx_frames"][0] != f_start:
                failures.append(f"{level}: 乱れの音が前線の頭に 1 発ではない（{video['sweep_sfx_frames']} / 頭 {f_start}）")
        elif lv > 0:
            if any(float(r.get("tear") or 0.0) > 0.0 for r in rows):
                failures.append(f"{level}: 打つ出し方なのに乱れが立った")
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
    results["_geometry"] = geometry
    results["_frames"] = {"open": f_open, "late": f_late}
    results["_failures"] = failures
    (MOTION / "evidence.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    if failures:
        print("FAIL:\n  " + "\n  ".join(failures))
        return 1
    print("OK: 0.25 は出た初めは通常 → 1.2 秒で地・顔・本文が変わる / 0.75 と 1 は一気に出て → 上から降りて → 全面が変わる")
    return 0


if __name__ == "__main__":
    sys.exit(main())
