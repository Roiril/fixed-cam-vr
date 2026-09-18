#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""嘘の一文（3 周目 A）の憑依の出し方（canon/LEDGER.md 0230）を、Unity が焼いた連番から動画と画素検査へ。

    .\\tools\\unity.ps1 menu raw:FixedCamVr.Streaming.EditorTools.CommsPreview.RunPossession
    py -3.11 tools/render-comms-possession-preview.py

入力は Logs/comms-takeover-20260914/latest-render.txt が指す render-<日時>/<ja|en|fr>/（連番・frames.tsv）。
出力は同じ場所へ possession.mp4（塗り替わりの頭の乱れの音つき）と evidence.json。

⚠ 絵は Unity が描いたものをそのまま並べる（CPU の模写ではない）。音の発火は Unity が記録した frames.tsv の sfx 列。
⚠ 判定は「効果の実在」— 打鍵 0 / 全文が出た瞬間の絵と塗り替わり切った絵が違う / 前線が上から降りた
  （塗り替わりの途中で本文の上 1/3 は変わり下 1/3 は変わらない）/ 赤い「異常なし」が読ませる段だけ在る。
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
OUTPUT = ROOT / "Logs/comms-takeover-20260914"
FPS = 30
GLITCH_GAIN = 0.8   # CurseSweepAudioCue.Gain


def wav_read(path: Path):
    with wave.open(str(path), "rb") as f:
        assert f.getsampwidth() == 2, path
        sr, channels = f.getframerate(), f.getnchannels()
        a = np.frombuffer(f.readframes(f.getnframes()), dtype="<i2").astype(np.float32) / 32768
    a = a.reshape(-1, channels)
    if channels == 1:
        a = np.repeat(a, 2, axis=1)
    return a, sr


def load(path: Path) -> np.ndarray:
    return np.asarray(Image.open(path).convert("RGB")).astype(np.int16)


def ivory_mask(img: np.ndarray) -> np.ndarray:
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return (r > 140) & (g > 130) & (b > 115) & (r >= b)


def red_mask(img: np.ndarray) -> np.ndarray:
    """嘘の「異常なし」の赤（184, 48, 40）。"""
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return (r > 120) & (g < 90) & (b < 90) & (r > g + 60)


def bbox(mask: np.ndarray, pad: int = 0) -> dict:
    ys, xs = np.where(mask)
    assert len(xs), "領域が見つからない"
    return {"x0": int(xs.min()) - pad, "x1": int(xs.max()) + 1 + pad,
            "y0": int(ys.min()) - pad, "y1": int(ys.max()) + 1 + pad}


def crop(img: np.ndarray, r: dict) -> np.ndarray:
    return img[int(r["y0"]):int(r["y1"]), int(r["x0"]):int(r["x1"])]


def render_language(folder: Path, glitch_index: int) -> dict:
    with (folder / "frames.tsv").open(encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f, delimiter="\t"))
    assert rows and [int(r["frame"]) for r in rows] == list(range(len(rows)))
    required = {"frame", "phase", "sweep", "curse", "face", "cx", "red", "glyph", "panel_alpha",
                "hit", "sfx", "drawn", "shown", "total", "visible", "text", "tear", "torn"}
    assert required <= rows[0].keys(), f"frames.tsv の列が足りない: {sorted(required - rows[0].keys())}"
    frames = [folder / f"f{i:04d}.png" for i in range(len(rows))]
    assert all(p.is_file() for p in frames), "連番が欠けている"

    phases = [r["phase"] for r in rows]
    shown = [r for r in rows if r["phase"] == "Shown"]
    sweep = [r for r in rows if r["phase"] == "Sweep"]
    cursed = [r for r in rows if r["phase"] == "Cursed"]
    assert shown and sweep and cursed, "Shown → Sweep → Cursed が揃っていない"
    order = [p for i, p in enumerate(phases) if i == 0 or phases[i - 1] != p]
    # 面が畳まれ切ると段は Off へ戻る（尾まで焼くと最後に付く）。
    if len(order) > 1 and order[-1] == "Off":
        order = order[:-1]
    assert order == ["Off", "Shown", "Sweep", "Cursed"] or order == ["Shown", "Sweep", "Cursed"], f"段の順が違う: {order}"
    assert sum(int(r["hit"]) for r in rows) == 0, "打鍵が鳴った（一気に出るので 0 のはず）"
    assert len({r["text"] for r in rows}) == 1, "文面が途中で替わった"
    total = int(rows[0]["total"])
    visible = int(rows[0]["visible"])
    lit_shown = [r for r in shown if float(r["glyph"]) >= 0.99]
    assert lit_shown, "全文が出切った読ませる段が無い"
    assert all(int(r["shown"]) == total for r in lit_shown), "読ませる段で全文が出ていない"
    assert all(int(r["cx"]) == 0 and float(r["curse"]) == 0 for r in lit_shown), "読ませる段で字が切れた／斑が乗った"
    red_expected = int(lit_shown[0]["red"])
    assert red_expected > 0 and all(int(r["red"]) == red_expected for r in lit_shown), "赤い「異常なし」が読ませる段で揃っていない"
    # ⚠ 引いている最中は文字の濃さが 0 へ落ちるので、切られた字の数え上げも 0 になる（画に出ている字だけを数えるため）。
    #   判定は**まだ点いているコマ**に絞る。
    lit_cursed = [r for r in cursed if float(r["glyph"]) > 0.5]
    assert lit_cursed, "塗り替わり切って点いたままのコマが無い"
    assert all(int(r["cx"]) == visible and int(r["red"]) == 0 and int(r["drawn"]) == 0 for r in lit_cursed), \
        "塗り替わり切った後に切れていない字／赤い字が残っている"
    sfx_frames = [int(r["frame"]) for r in rows if int(r["sfx"]) > 0]
    assert sfx_frames == [int(sweep[0]["frame"])], f"乱れの音は前線が降り始めたコマに 1 発のはず: {sfx_frames}"
    sweep_sec = len(sweep) / FPS
    assert abs(sweep_sec - 0.45) <= 2 / FPS, f"塗り替わりが {sweep_sec:.2f} 秒（0.45 のはず）"
    sweeps = [float(r["sweep"]) for r in sweep]
    assert all(b >= a for a, b in zip(sweeps, sweeps[1:])), "前線が戻った"

    # ---- 画素 ----
    f_shown = int(lit_shown[len(lit_shown) // 2]["frame"])
    f_mid = int(sweep[len(sweep) // 2]["frame"])
    f_cursed = int(lit_cursed[min(6, len(lit_cursed) - 1)]["frame"])   # 乱れの尾（0.12 秒）が引いた後
    img_shown, img_mid, img_cursed = load(frames[f_shown]), load(frames[f_mid]), load(frames[f_cursed])
    dark = img_shown.max(axis=2) <= 11
    plate = bbox(dark)
    ink = ivory_mask(img_shown) | red_mask(img_shown)
    split = plate["x0"] + (plate["x1"] - plate["x0"]) // 4
    face_mask = ink.copy(); face_mask[:, split:] = False
    text_mask = ink.copy(); text_mask[:, :split] = False
    face, text = bbox(face_mask, 4), bbox(text_mask, 4)
    h = text["y1"] - text["y0"]
    top_third = {**text, "y1": text["y0"] + h // 3}
    bottom_third = {**text, "y0": text["y1"] - h // 3}
    d_text = float(np.abs(crop(img_cursed, text) - crop(img_shown, text)).mean())
    d_face = float(np.abs(crop(img_cursed, face) - crop(img_shown, face)).mean())
    d_plate = float(np.abs(crop(img_cursed, plate) - crop(img_shown, plate)).mean())
    d_mid_top = float(np.abs(crop(img_mid, top_third) - crop(img_shown, top_third)).mean())
    d_mid_bottom = float(np.abs(crop(img_mid, bottom_third) - crop(img_shown, bottom_third)).mean())
    red_shown = int(red_mask(crop(img_shown, text)).sum())
    red_cursed = int(red_mask(crop(img_cursed, text)).sum())
    ring = np.zeros(img_shown.shape[:2], bool)
    ring[max(0, plate["y0"] - 40):plate["y1"] + 40, max(0, plate["x0"] - 40):plate["x1"] + 40] = True
    ring[plate["y0"]:plate["y1"], plate["x0"]:plate["x1"]] = False
    ring_changed = int(((np.abs(img_cursed - img_shown).mean(axis=2) > 2.0) & ring).sum())
    assert d_text > 1.0, f"塗り替わっても本文の画素が変わっていない（{d_text:.2f}）"
    assert d_face > 3.0, f"塗り替わっても顔が人形になっていない（{d_face:.2f}）"
    assert d_plate > 1.0, f"塗り替わっても地が変わっていない（{d_plate:.2f}）"
    assert ring_changed >= 30, f"矩形の外に毛羽立ち・糸くずがほぼ無い（{ring_changed}）"
    # 前線の向きは矩形の外の環で見る（乱れは中身を動かすが枠は動かさない）。
    mid_y = (plate["y0"] + plate["y1"]) // 2
    changed_mid = (np.abs(img_mid - img_shown).mean(axis=2) > 2.0) & ring
    ring_mid_top, ring_mid_bottom = int(changed_mid[:mid_y].sum()), int(changed_mid[mid_y:].sum())
    # ⚠ 本文は 1 行なので「上 1/3 の差」は前線の証拠にならない（帯の境目が字の上端をかすめるだけのことがある）。
    #   向きは環（枠の外の毛羽立ち）で決める。d_mid_top は記録だけ。
    assert ring_mid_top >= 30 and ring_mid_bottom <= max(10, int(0.15 * ring_mid_top)), \
        f"前線が上から降りていない（途中の画: 環の変化 上 {ring_mid_top} 下 {ring_mid_bottom} / 上 1/3 の差 {d_mid_top:.2f}）"
    # ---- 乱れ（0231）----
    tear_col = [float(r["tear"]) for r in rows]
    f_start, f_end = int(sweep[0]["frame"]), int(cursed[0]["frame"])
    assert max(tear_col[int(r["frame"])] for r in shown) == 0.0, "読ませているあいだに乱れが立った"
    assert tear_col[f_mid] >= 0.5, f"降りている最中の乱れが弱い（{tear_col[f_mid]:.2f}）"
    motion_below = [float(np.abs(crop(load(frames[i + 1]), bottom_third) - crop(load(frames[i]), bottom_third)).mean())
                    for i in range(f_start, f_mid)]
    motion_below_mean = float(np.mean(motion_below)) if motion_below else 0.0
    assert motion_below_mean >= 0.3, f"前線の下側が動いていない（下 1/3 のコマ差 平均 {motion_below_mean:.3f}）"
    f_settle = f_end + int(np.ceil(0.12 * FPS)) + 1
    settle = [float(np.abs(crop(load(frames[i + 1]), plate) - crop(load(frames[i]), plate)).mean())
              for i in range(f_settle, min(f_settle + 6, len(rows) - 1))]
    settle_max = max(settle) if settle else 0.0
    assert settle_max <= 0.3 and tear_col[min(f_settle, len(rows) - 1)] == 0.0, \
        f"尾が引いた後も動いている（コマ差 {settle_max:.3f} / 乱れ {tear_col[min(f_settle, len(rows) - 1)]:.2f}）"
    assert red_shown > 30, f"読ませる段に赤い「異常なし」が無い（{red_shown} 画素）"
    assert red_cursed < red_shown * 0.1, f"塗り替わった後も赤が残っている（{red_cursed} / {red_shown}）"

    # ---- 音（Unity が記録した sfx 列だけ）----
    sounds = ROOT / "Assets/Resources/Sound"
    clip, sr = wav_read(sounds / f"sfx_glitch_{glitch_index % 3 + 1}.wav")
    samples = int(len(rows) / FPS * sr) + sr
    out = np.zeros((samples, 2), np.float32)
    for f in sfx_frames:
        start = int((f / FPS + 0.035) * sr)
        count = min(len(clip), samples - start)
        out[start:start + count] += clip[:count] * GLITCH_GAIN
    assert np.abs(out).max() <= 1, "動画の音が飽和する"
    audio = folder / "sweep.wav"
    with wave.open(str(audio), "wb") as f:
        f.setnchannels(2); f.setsampwidth(2); f.setframerate(sr)
        f.writeframes(np.round(out * 32767).astype("<i2").tobytes())
    video = folder / "possession.mp4"
    subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error",
                    "-framerate", str(FPS), "-i", str(folder / "f%04d.png"), "-i", str(audio),
                    "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac",
                    "-b:a", "160k", "-shortest", "-movflags", "+faststart", str(video)], check=True)
    result = dict(language=folder.name, frames=len(rows), seconds=len(rows) / FPS,
                  keystrokes=0, total_chars=total, visible_chars=visible, red_chars=red_expected,
                  shown_seconds=len(shown) / FPS, sweep_seconds=sweep_sec, cursed_seconds=len(cursed) / FPS,
                  sweep_start_sec=int(sweep[0]["frame"]) / FPS, sweep_sfx_frames=sfx_frames,
                  pixel_delta_text=d_text, pixel_delta_face=d_face, pixel_delta_plate=d_plate,
                  ring_changed_pixels=ring_changed,
                  mid_sweep_top_third_delta=d_mid_top, mid_sweep_bottom_third_delta=d_mid_bottom,
                  ring_changed_mid_top=ring_mid_top, ring_changed_mid_bottom=ring_mid_bottom,
                  tear_mid=tear_col[f_mid], motion_below_front_mean=motion_below_mean,
                  settle_frame=f_settle, settle_delta_max=settle_max,
                  red_pixels_shown=red_shown, red_pixels_cursed=red_cursed,
                  stills={"shown": str(folder / "shown.png"), "mid": str(folder / "sweep-mid.png"),
                          "cursed": str(folder / "cursed.png")},
                  video=str(video),
                  audio_note="Unity が記録した乱れの音 1 発だけ（35ms の DSP 遅延）。打鍵は無い。劇伴・立体音響は無し。")
    (folder / "evidence.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def main() -> int:
    folder = Path((OUTPUT / "latest-render.txt").read_text(encoding="utf-8").strip()).resolve()
    assert folder.is_relative_to(OUTPUT.resolve())
    results = [render_language(folder / lang, i) for i, lang in enumerate(("ja", "en", "fr"))]
    (folder / "evidence.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    print("OK: 打鍵 0 / 全文が出て読める / 上から降りる前線で塗り替わる / 赤い「異常なし」は読ませる段だけ")
    return 0


if __name__ == "__main__":
    sys.exit(main())
