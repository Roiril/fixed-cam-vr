#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""Unity が焼いた現行の通信面を 30fps 動画にし、画素と状態を検査する。

    .\tools\unity.ps1 menu comms-revision
    py -3.11 tools/render-comms-possession-preview.py
    py -3.11 tools/render-comms-possession-preview.py --render Logs/comms-revision-20260920

入力は ``CommsRevisionPreview`` の出力。旧 ``--render <path>`` もそのまま使える。
各言語の ``fNNNN.png`` と ``frames.csv`` から ``possession.mp4`` と
``evidence.json`` を作る。Unity の絵を CPU で模写しない。
"""
from __future__ import annotations

import argparse
import csv
import json
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
DEFAULT_RENDER = ROOT / "Logs/comms-readable-20260921"
FPS = 30
LANGUAGES = ("ja", "en", "fr")


def ffmpeg_exe() -> str:
    """PATH を優先し、無ければ imageio-ffmpeg の同梱版を使う。"""
    found = shutil.which("ffmpeg")
    if found:
        return found
    try:
        import imageio_ffmpeg
    except ImportError as exc:
        raise SystemExit("ffmpeg が無い。PATH へ追加するか imageio-ffmpeg を入れてください") from exc
    return imageio_ffmpeg.get_ffmpeg_exe()


def load(path: Path) -> np.ndarray:
    if not path.is_file():
        raise AssertionError(f"必要な画像が無い: {path}")
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.int16)


def red_mask(image: np.ndarray) -> np.ndarray:
    """通信面の赤文字。実描画の (255, 120, 86) を含み、暗い赤茶の地は除く。"""
    red, green, blue = image[..., 0], image[..., 1], image[..., 2]
    return ((red >= 170) & (green <= 145) & (blue <= 125)
            & (red >= green + 65) & (red >= blue + 90))


def mean_delta(a: np.ndarray, b: np.ndarray, mask: np.ndarray | None = None) -> float:
    delta = np.abs(a.astype(np.float32) - b.astype(np.float32)).mean(axis=2)
    values = delta if mask is None else delta[mask]
    return float(values.mean()) if values.size else 0.0


def foreground_pixels(image: np.ndarray) -> int:
    """四隅の背景色から外れた画素数。"""
    corners = np.concatenate((image[:12, :12], image[:12, -12:], image[-12:, :12], image[-12:, -12:]))
    background = np.median(corners.reshape(-1, 3), axis=0)
    return int((np.abs(image - background).max(axis=2) > 3).sum())


def read_rows(folder: Path) -> list[dict[str, str]]:
    path = folder / "frames.csv"
    if not path.is_file():
        raise AssertionError(f"frames.csv が無い: {path}")
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    required = {"frame", "sec", "stage", "sweep", "lie", "glyph", "error"}
    if not rows or not required <= rows[0].keys():
        missing = required - (rows[0].keys() if rows else set())
        raise AssertionError(f"frames.csv の列が足りない: {sorted(missing)}")
    frames = [int(row["frame"]) for row in rows]
    if frames != list(range(len(rows))):
        raise AssertionError(f"フレーム番号が連続していない: {frames[:4]}...{frames[-4:]}")
    return rows


def verify_wipe(truth: np.ndarray, wipe: np.ndarray, doll: np.ndarray) -> dict[str, object]:
    final_delta = np.abs(doll.astype(np.float32) - truth.astype(np.float32)).mean(axis=2)
    changed = final_delta > 6.0
    changed_pixels = int(changed.sum())
    if changed_pixels < 500:
        raise AssertionError(f"人形への最終変化が少なすぎる: {changed_pixels} px")

    ys, xs = np.where(changed)
    x0, x1 = int(xs.min()), int(xs.max()) + 1
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    width = x1 - x0
    x_left_end = x0 + width // 3
    x_right_start = x0 + width * 2 // 3
    x_grid = np.indices(changed.shape)[1]
    left = changed.copy()
    left &= (x_grid >= x0) & (x_grid < x_left_end)
    right = changed.copy()
    right &= (x_grid >= x_right_start) & (x_grid < x1)
    if int(left.sum()) < 100 or int(right.sum()) < 100:
        raise AssertionError("左右の塗り替え判定に必要な変化画素が無い")

    left_to_truth = mean_delta(wipe, truth, left)
    left_to_doll = mean_delta(wipe, doll, left)
    right_to_truth = mean_delta(wipe, truth, right)
    right_to_doll = mean_delta(wipe, doll, right)
    if not left_to_doll < left_to_truth:
        raise AssertionError(
            f"途中画の左側が人形に塗り替わっていない: doll={left_to_doll:.2f} truth={left_to_truth:.2f}")
    if not right_to_truth < right_to_doll:
        raise AssertionError(
            f"途中画の右側がまだ真実の面ではない: truth={right_to_truth:.2f} doll={right_to_doll:.2f}")
    return {
        "changed_pixels": changed_pixels,
        "change_bbox": {"x0": x0, "x1": x1, "y0": y0, "y1": y1},
        "left_to_truth_delta": left_to_truth,
        "left_to_doll_delta": left_to_doll,
        "right_to_truth_delta": right_to_truth,
        "right_to_doll_delta": right_to_doll,
    }


def calibrate_direction_guard(truth: np.ndarray, doll: np.ndarray,
                              change_bbox: dict[str, int]) -> bool:
    """実画素で右から左の反転対照を作り、判定が必ず落とすことを確かめる。"""
    reverse = truth.copy()
    split = (change_bbox["x0"] + change_bbox["x1"]) // 2
    reverse[:, split:change_bbox["x1"]] = doll[:, split:change_bbox["x1"]]
    try:
        verify_wipe(truth, reverse, doll)
    except AssertionError:
        return True
    raise AssertionError("左→右判定が、右→左の反転対照も通している")


def make_video(folder: Path, frame_count: int, ffmpeg: str) -> Path:
    frames = [folder / f"f{i:04d}.png" for i in range(frame_count)]
    missing = [path.name for path in frames if not path.is_file()]
    if missing:
        raise AssertionError(f"連番画像が欠けている: {missing[:8]}")
    video = folder / "possession.mp4"
    subprocess.run([
        ffmpeg, "-y", "-v", "error", "-framerate", str(FPS),
        "-i", str(folder / "f%04d.png"), "-frames:v", str(frame_count),
        "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p",
        "-r", str(FPS), "-movflags", "+faststart", str(video),
    ], check=True)
    return video


def verify_language(folder: Path, ffmpeg: str, before: Path | None = None) -> dict[str, object]:
    rows = read_rows(folder)
    truth = load(folder / "truth.png")
    wipe = load(folder / "wipe.png")
    doll = load(folder / "doll.png")
    after = load(folder / "after-takeover.png")
    holding = load(folder / "holding.png")
    intro = load(folder / "intro-success.png")
    finished = load(folder / "finished.png")
    if len({image.shape for image in (truth, wipe, doll, after, holding, intro, finished)}) != 1:
        raise AssertionError("静止画のサイズが揃っていない")

    truth_red = int(red_mask(truth).sum())
    actual_red = max(int(red_mask(after).sum()), int(red_mask(doll).sum()))
    if truth_red != 0:
        raise AssertionError(f"真実の面に赤が出ている: {truth_red} px")
    if actual_red <= 0:
        raise AssertionError("乗っ取り後の実画素に赤文字が無い")
    holding_red = int(red_mask(holding).sum())
    if holding_red != 0:
        raise AssertionError(f"長押し中に前の赤い本文が戻っている: {holding_red} px")

    sweep = [float(row["sweep"]) for row in rows]
    active_sweep = [float(row["sweep"]) for row in rows if row["stage"] != "Off"]
    if any(current + 1e-6 < previous for previous, current in zip(active_sweep, active_sweep[1:])):
        raise AssertionError("塗り替えの前線が戻っている")
    if max(sweep) < 0.999:
        raise AssertionError("人形へ最後まで塗り替わっていない")
    if rows[-1]["stage"] != "Off" or int(rows[-1]["error"]) != 0:
        raise AssertionError("面か本体エラーが最後まで残っている")
    if not any(int(row["error"]) for row in rows[:-1]):
        raise AssertionError("乗っ取り中に本体エラーが表示されていない")
    last_panel_frame = max(index for index, row in enumerate(rows) if row["stage"] != "Off")
    last_error_frame = max(index for index, row in enumerate(rows) if int(row["error"]))
    if last_panel_frame - last_error_frame != FPS:
        raise AssertionError(
            f"エラー消去後に人形だけ1秒残っていない: panel={last_panel_frame} error={last_error_frame}")
    doll_only = load(folder / "doll-only.png")
    if not np.array_equal(doll_only, doll):
        raise AssertionError("人形だけの残留画像が静止した完成文と一致しない")

    empty_pixels = foreground_pixels(finished)
    if empty_pixels > 32:
        raise AssertionError(f"最終フレームが空ではない: {empty_pixels} px")
    intro_pixels = foreground_pixels(intro)
    if intro_pixels < 100:
        raise AssertionError("導入成功の面が描画されていない")

    direction = verify_wipe(truth, wipe, doll)
    reverse_rejected = calibrate_direction_guard(truth, doll, direction["change_bbox"])
    story = verify_story(folder, rows) if "blockFailed" in rows[0] else None
    intrusion_motion = verify_intrusion_motion(rows) if "shake" in rows[0] else None
    if intrusion_motion is not None:
        # 時計がずれた警告と、乱れが描画側へ届かない対照を確実に拒否する。
        for field, wrong in (("caption", "失敗"), ("tear", "0")):
            control = [{**row, field: wrong} for row in rows]
            try:
                verify_intrusion_motion(control)
            except (AssertionError, StopIteration):
                pass
            else:
                raise AssertionError(f"侵食の検査が不正な対照を受け入れた: {field}")
        intrusion_motion["wrong_caption_and_missing_glitch_controls_rejected"] = True
    video = make_video(folder, len(rows), ffmpeg)
    result: dict[str, object] = {
        "language": folder.name,
        "frames": len(rows),
        "fps": FPS,
        "seconds": len(rows) / FPS,
        "red_pixels_actual": actual_red,
        "red_pixels_truth": truth_red,
        "red_pixels_holding": holding_red,
        "finished_foreground_pixels": empty_pixels,
        "intro_foreground_pixels": intro_pixels,
        "sweep_max": max(sweep),
        "error_frames": sum(int(row["error"]) for row in rows),
        "last_panel_frame": last_panel_frame,
        "last_error_frame": last_error_frame,
        "doll_only_sec": (last_panel_frame - last_error_frame) / FPS,
        "wipe": direction,
        "reverse_direction_control_rejected": reverse_rejected,
        "story": story,
        "intrusion_motion": intrusion_motion,
        "stills": {
            "truth": str(folder / "truth.png"),
            "wipe": str(folder / "wipe.png"),
            "doll": str(folder / "doll.png"),
            "after_takeover": str(folder / "after-takeover.png"),
            "holding": str(folder / "holding.png"),
            "intro_success": str(folder / "intro-success.png"),
            "finished": str(folder / "finished.png"),
        },
        "video": str(video),
    }
    if before is not None:
        result["readability"] = verify_readability(folder, before)
    (folder / "evidence.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def verify_intrusion_motion(rows: list[dict[str, str]]) -> dict[str, object]:
    captions = ["WARNING", "不正アクセス", "接続元不明", "遮断を執行", "失敗"]
    for step in range(len(captions)):
        caption = " | ".join(captions[:step + 1])
        first = next(i for i, row in enumerate(rows) if row["caption"] == caption)
        if abs(first / FPS - step) > 1 / FPS + .001:
            raise AssertionError(f"警告の出現時刻が不正: {caption} at {first / FPS}")
        if step < 4 and sum(row["caption"] == caption for row in rows) != FPS:
            raise AssertionError(f"次の警告までの追加間隔が1秒ではない: {caption}")
    for row in rows:
        expected_count = min(5, int(float(row["sec"]) + .0001) + 1) if int(row["error"]) else 0
        expected = " | ".join(captions[:expected_count])
        if row["caption"] != expected:
            raise AssertionError(f"累積した警告が消えた/順序が違う: {row['sec']}")
    bursts = 0
    previous = False
    max_position = max_angle = 0.0
    for row in rows:
        sweep = float(row["sweep"])
        moving = float(row["shake"]) > 0
        tear = float(row["tear"]) > .001
        displacement = max(abs(float(row["shakeX"])), abs(float(row["shakeY"])))
        angle = float(row["shakeAngle"])
        max_position = max(max_position, displacement)
        max_angle = max(max_angle, angle)
        if not .001 < sweep < .999 and (moving or tear or displacement > 1e-6 or angle > 1e-4):
            raise AssertionError("読取中か完成文に揺れが残っている")
        if tear and not previous:
            bursts += 1
        previous = tear
    if bursts != 4 or not 0 < max_position <= .006 or not 0 < max_angle <= .6:
        raise AssertionError(f"侵食の乱れが設定外: {bursts}, {max_position}, {max_angle}")
    return {"warning_steps": 5, "caption_add_interval_sec": 1, "captions_accumulate": True,
            "glitch_bursts": bursts, "max_axis_displacement_m": max_position,
            "max_angle_deg": max_angle, "read_and_final_static": True}


def verify_story(folder: Path, rows: list[dict[str, str]]) -> dict[str, object]:
    """侵入→検知→遮断失敗→改ざん→消去。表示時計だけでなく実画素も確認する。"""
    intrusion = load(folder / "intrusion.png")
    failed = load(folder / "block-failed.png")
    if int(red_mask(intrusion).sum()) < 100:
        raise AssertionError("侵入の先行表示に赤い警告の実画素が無い")
    if foreground_pixels(failed) < 100:
        raise AssertionError("遮断失敗のコマが描画されていない")
    first_panel = next(i for i, r in enumerate(rows) if float(r["panel"]) > .01)
    first_fail = next(i for i, r in enumerate(rows) if int(r["blockFailed"]))
    first_sweep = next(i for i, r in enumerate(rows) if float(r["sweep"]) > .001)
    full_doll = next(i for i, r in enumerate(rows) if float(r["sweep"]) >= .999)
    cut = next(i for i, r in enumerate(rows) if r["stage"] == "Out")
    if first_panel / FPS < .89:
        raise AssertionError("不正アクセスの前段より早くスイの面が出た")
    if not first_panel < first_fail < first_sweep < full_doll < cut:
        raise AssertionError("検知/遮断失敗/改ざん/消去の順序が不正")
    readable = "readFocus" in rows[0]
    fail_min, fail_max = (.54, .67) if readable else (.25, .45)
    hold_min, hold_max = (1.34, 1.40) if readable else (.44, .50)
    if not fail_min <= (first_sweep - first_fail) / FPS <= fail_max:
        raise AssertionError("遮断失敗から改ざんまでの間隔が不正")
    if not hold_min <= (cut - full_doll) / FPS <= hold_max:
        raise AssertionError("人形の完成表示の読取時間が設定からずれている")
    if (len(rows) - 1 - cut) != FPS:
        raise AssertionError("人形だけが残る時間が1秒からずれている")
    for row in rows[cut:-1]:
        if int(row["error"]) or float(row["glyph"]) != 1 or float(row["sweep"]) != 1:
            raise AssertionError("人形だけの残留中にエラーか消灯が混ざっている")
    for r in rows[:first_panel]:
        if float(r["panel"]) > .01 or int(r["lie"]):
            raise AssertionError("侵入の前段で人形かスイの面が出ている")
    for r in rows[first_sweep:cut]:
        if abs(float(r["errorOpacity"]) - .35) > .002:
            raise AssertionError("改ざん中の警告が読み取り用の濃さを維持していない")
    return {"intrusion_red_pixels": int(red_mask(intrusion).sum()),
            "panel_at_sec": first_panel / FPS, "failure_at_sec": first_fail / FPS,
            "sweep_at_sec": first_sweep / FPS, "doll_at_sec": full_doll / FPS,
            "cut_at_sec": cut / FPS, "off_at_sec": (len(rows) - 1) / FPS}


def verify_readability(folder: Path, before: Path) -> dict[str, object]:
    """同じ実描画の有/無を比較し、文字の背後へ漏れた警告の強さを測る。"""
    metrics = {}
    for phase in ("truth", "doll"):
        previous = load(before / f"{phase}.png")
        current = load(folder / f"{phase}.png")
        old_composite = load(before / f"{phase}-with-error.png")
        new_composite = load(folder / f"{phase}-with-error.png")
        # 顔と文字の実画素の外接矩形。画像全体の明るさではなく、読む場所で測る。
        ink = previous.max(axis=2) > 100
        yy, xx = np.where(ink)
        if not len(xx):
            raise AssertionError("読取領域の実画素が無い")
        roi = (slice(max(0, yy.min()-4), yy.max()+5), slice(max(0, xx.min()-4), xx.max()+5))
        old_leak = float(np.abs(old_composite[roi].astype(float) - previous[roi]).mean())
        new_leak = float(np.abs(new_composite[roi].astype(float) - current[roi]).mean())
        if old_leak < 1 or new_leak >= old_leak * .45:
            raise AssertionError(f"{phase}: 読む場所の背景干渉が下がっていない ({old_leak:.2f} -> {new_leak:.2f})")
        metrics[phase + "_background_leak_before"] = old_leak
        metrics[phase + "_background_leak_after"] = new_leak
    for name in ("intro-success", "after-takeover", "holding", "cancel-closed", "intrusion"):
        if not np.array_equal(load(before / f"{name}.png"), load(folder / f"{name}.png")):
            raise AssertionError(f"対象外の表示が変わった: {name}")
    metrics["unchanged_controls"] = 5
    return metrics


def language_folders(render: Path) -> list[Path]:
    if (render / "frames.csv").is_file():
        return [render]
    folders = [render / language for language in LANGUAGES]
    missing = [str(folder) for folder in folders if not (folder / "frames.csv").is_file()]
    if missing:
        raise AssertionError(f"言語別の出力が無い: {missing}")
    return folders


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--render", type=Path, default=DEFAULT_RENDER,
                        help="CommsRevisionPreview の出力 root または言語フォルダ")
    parser.add_argument("--before", type=Path, help="読みやすさ調整前の3言語実描画 root")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    render = args.render.expanduser()
    if not render.is_absolute():
        render = (ROOT / render).resolve()
    else:
        render = render.resolve()
    ffmpeg = ffmpeg_exe()
    folders = language_folders(render)
    results = [verify_language(folder, ffmpeg, args.before / folder.name if args.before else None)
               for folder in folders]
    if len(folders) > 1:
        (render / "evidence.json").write_text(
            json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    print("OK: 30fps / 赤文字 / 真実は赤なし / 左→右の塗り替え / 警告累積 / 人形だけ1秒残留")
    return 0


if __name__ == "__main__":
    sys.exit(main())
