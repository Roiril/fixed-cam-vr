"""写真に合わせて作った素材を、別の部屋の写真へ射影で写し替える（生成しない）。

会場でカメラの位置が研究室と変わり、動画の位置合わせ（build_flow_right.py）が断られたときの道具。
幕や壁を平面とみなし、旧い写真と新しい写真で手で取った 4 点の射影で素材を写す。

- ratio       : 掛け算の比の画（640x480・外は 255）。射影で写し、外は 255
- frame       : 枠空間 16:9 のマスク・順の画（640x360。4:3 の画は x=80..560 に収まる）。外は fill（既定 0）
- replace_img : 差し替えの画（旧い写真の上に異変を描いたもの）。旧い写真との比を新しい写真に掛ける
- replace_mp4 : 差し替えの動画。各コマに replace_img と同じことをする（H.264・yuv420p・元の fps）

差し替えでは、旧い写真と同じ画素（差が小さい所）は新しい写真のまま残し、異変の所だけ比を掛ける。
明るさが会場の幕に合うので、旧い部屋の色が乗らない。

使い方:
  py -3.11 tools/gen-plate/replant.py tools/gen-plate/replant/<設定>.json [--only <名前>] [--dry]
--dry は確認の画（Drive の screenshots/onsite_<日付>/replant_<設定>.jpg）だけ書く。
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
WC = os.path.join(ROOT, "tools", "web-compositor")

# 4:3 の画（640x480）→ 枠空間 16:9（640x360）
S = np.array([[0.75, 0, 80], [0, 0.75, 0], [0, 0, 1]], dtype=np.float64)
S_INV = np.linalg.inv(S)


def imread(path, flags=cv2.IMREAD_COLOR):
    data = np.fromfile(path, dtype=np.uint8)
    im = cv2.imdecode(data, flags)
    if im is None:
        raise SystemExit(f"読めない: {path}")
    return im


def imwrite(path, im):
    ext = os.path.splitext(path)[1]
    ok, buf = cv2.imencode(ext, im)
    if not ok:
        raise SystemExit(f"書けない: {path}")
    tmp = path + ".tmp" + ext
    buf.tofile(tmp)
    os.replace(tmp, path)


def wc(rel):
    return os.path.join(WC, rel.lstrip("/").replace("/", os.sep))


def homography(spec):
    return cv2.getPerspectiveTransform(np.float32(spec["src"]), np.float32(spec["dst"])).astype(np.float64)


def warp_img(im, H, fill):
    return cv2.warpPerspective(im, H, (640, 480), flags=cv2.INTER_LINEAR,
                               borderMode=cv2.BORDER_CONSTANT, borderValue=(fill, fill, fill))


def warp_frame(im, H, fill):
    Hf = S @ H @ S_INV
    return cv2.warpPerspective(im, Hf, (im.shape[1], im.shape[0]), flags=cv2.INTER_LINEAR,
                               borderMode=cv2.BORDER_CONSTANT, borderValue=(fill, fill, fill))


class Transfer:
    """旧い写真の上の異変を、新しい写真へ比で移す。"""

    def __init__(self, old_plate, new_plate, H, thresh):
        self.H = H
        self.new = new_plate.astype(np.float32)
        self.old = old_plate.astype(np.float32)
        self.thresh = thresh
        # 写した先で、旧い写真が無い所（枠の外）
        ones = np.full(old_plate.shape[:2], 255, np.uint8)
        self.inside = cv2.warpPerspective(ones, H, (640, 480)).astype(np.float32) / 255.0

    def apply(self, frame):
        f = frame.astype(np.float32)
        diff = np.abs(f - self.old).max(axis=2)
        diff = cv2.GaussianBlur(diff, (0, 0), 1.5)
        lo, hi = self.thresh
        a = np.clip((diff - lo) / (hi - lo), 0, 1)
        # 生成のコマは旧い写真と 1〜2px ずれていることがあり、柱や手すりの縁が細い線として残る。
        # 細い線は開く演算で消し、人形や顔のような塊だけを残す
        a = cv2.morphologyEx(a, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
        a = cv2.dilate(a, np.ones((3, 3), np.uint8))
        a = cv2.GaussianBlur(a, (0, 0), 1.0)
        ratio = (f + 8.0) / (self.old + 8.0)
        ratio = np.clip(ratio, 0.0, 2.5)
        wr = cv2.warpPerspective(ratio, self.H, (640, 480), flags=cv2.INTER_LINEAR,
                                 borderMode=cv2.BORDER_CONSTANT, borderValue=(1, 1, 1))
        wa = cv2.warpPerspective(a, self.H, (640, 480), flags=cv2.INTER_LINEAR) * self.inside
        out = self.new * (1 - wa[..., None]) + np.clip(self.new * wr, 0, 255) * wa[..., None]
        return np.clip(out + 0.5, 0, 255).astype(np.uint8)


def read_video(path):
    cap = cv2.VideoCapture(path)
    fps = cap.get(cv2.CAP_PROP_FPS)
    frames = []
    while True:
        ok, fr = cap.read()
        if not ok:
            break
        frames.append(fr)
    cap.release()
    if not frames:
        raise SystemExit(f"動画が読めない: {path}")
    return frames, fps


def write_video(path, frames, fps):
    with tempfile.TemporaryDirectory() as td:
        for i, fr in enumerate(frames):
            cv2.imwrite(os.path.join(td, f"f{i:05d}.png"), fr)
        tmp = path + ".tmp.mp4"
        cmd = ["ffmpeg", "-loglevel", "error", "-y", "-framerate", f"{fps:.6f}",
               "-i", os.path.join(td, "f%05d.png"), "-c:v", "libx264", "-pix_fmt", "yuv420p",
               "-crf", "16", "-movflags", "+faststart", tmp]
        subprocess.run(cmd, check=True)
        os.replace(tmp, path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("config")
    ap.add_argument("--only", default="")
    ap.add_argument("--dry", action="store_true")
    args = ap.parse_args()
    cfg = json.load(open(args.config, encoding="utf-8"))
    homs = {k: homography(v) for k, v in cfg["homographies"].items()}
    plates = {}

    def plate(name):
        if name not in plates:
            plates[name] = imread(wc("captures/" + name))
        return plates[name]

    sheet_rows = []
    written = []
    for item in cfg["items"]:
        if args.only and item["name"] != args.only:
            continue
        hspec = cfg["homographies"][item["homography"]]
        H = homs[item["homography"]]
        src, dst, kind = wc(item["src"]), wc(item["dst"]), item["kind"]
        if kind == "ratio":
            out = warp_img(imread(src), H, 255)
            preview = cv2.multiply(plate(hspec["new"]).astype(np.float32), out.astype(np.float32) / 255.0)
            preview = np.clip(preview, 0, 255).astype(np.uint8)
        elif kind == "frame":
            out = warp_frame(imread(src), H, int(item.get("fill", 0)))
            preview = cv2.resize(out, (640, 360))
            preview = cv2.copyMakeBorder(preview, 0, 120, 0, 0, cv2.BORDER_CONSTANT, value=(40, 40, 40))
        elif kind in ("replace_img", "replace_mp4"):
            tr = Transfer(plate(hspec["old"]), plate(hspec["new"]), H, item.get("thresh", [10, 30]))
            if kind == "replace_img":
                out = tr.apply(imread(src))
                preview = out
            else:
                frames, fps = read_video(src)
                outs = [tr.apply(fr) for fr in frames]
                preview = outs[len(outs) * 3 // 4]
                out = (outs, fps)
        else:
            raise SystemExit(f"知らない種類: {kind}")
        label = f"{item['name']} ({kind})"
        p = preview.copy()
        cv2.putText(p, label, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 4)
        cv2.putText(p, label, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 1)
        sheet_rows.append(p)
        if not args.dry:
            if kind == "replace_mp4":
                write_video(dst, *out)
            else:
                imwrite(dst, out)
            written.append(os.path.relpath(dst, ROOT))
    # 2 列に並べる
    if sheet_rows:
        if len(sheet_rows) % 2:
            sheet_rows.append(np.zeros_like(sheet_rows[0]))
        grid = np.vstack([np.hstack(sheet_rows[i:i + 2]) for i in range(0, len(sheet_rows), 2)])
        sheet = os.path.join(cfg["sheetDir"], f"replant_{os.path.splitext(os.path.basename(args.config))[0]}.jpg")
        os.makedirs(cfg["sheetDir"], exist_ok=True)
        imwrite(sheet, grid)
        print("確認の画:", sheet)
    for w in written:
        print("書いた:", w)


if __name__ == "__main__":
    main()
