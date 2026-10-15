"""博士動画の顔（両目）の位置を毎コマ追い、JSON に書く。

目元に影を落とす焼き込み（shade-doctor-eyes.py）の入力になる。
口はリップシンクで動くので、追跡には鼻より上の点だけを使う。
各コマは 1 コマ目から直接追う（前のコマからの累積をしないので漂わない）。

  py -3.11 tools/visitor-ui/track-doctor-face.py            # 4 本を追う
  py -3.11 tools/visitor-ui/track-doctor-face.py --sheet    # 追跡の確認用コンタクトシートも出す
"""
import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
ASSET = ROOT / "tablet" / "app" / "src" / "main" / "assets" / "web" / "asset"
OUT = ROOT / "tools" / "visitor-ui" / "doctor-face-track.json"
SCENES = ["introduction", "subject", "report", "wear"]

# introduction の 1 コマ目で目で測った両目の中心（1280×720）。他の 3 本はこの周りの目の版で探す
REF_SCENE = "introduction"
REF_EYES = ((590.0, 270.0), (707.0, 270.0))
PATCH = (56, 32)  # 目の版の幅・高さ


def read_frames(path):
    cap = cv2.VideoCapture(str(path))
    frames = []
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frames.append(frame)
    cap.release()
    if not frames:
        raise SystemExit(f"読めない: {path}")
    return frames


def gray(frame):
    return cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)


def patch(img, center):
    w, h = PATCH
    x, y = int(round(center[0] - w / 2)), int(round(center[1] - h / 2))
    return img[y:y + h, x:x + w]


def locate_eyes(ref_gray, target_gray):
    """参照の目の版を、対象の 1 コマ目から探す。"""
    found = []
    for eye in REF_EYES:
        tpl = patch(ref_gray, eye)
        x0, y0 = int(eye[0] - 140), int(eye[1] - 110)
        roi = target_gray[y0:y0 + 220 + PATCH[1], x0:x0 + 280 + PATCH[0]]
        res = cv2.matchTemplate(roi, tpl, cv2.TM_CCOEFF_NORMED)
        _, score, _, loc = cv2.minMaxLoc(res)
        found.append(((x0 + loc[0] + PATCH[0] / 2, y0 + loc[1] + PATCH[1] / 2), float(score)))
    return found


def upper_face_points(g0, eyes):
    (lx, ly), (rx, ry) = eyes
    d = rx - lx
    mask = np.zeros_like(g0)
    cx, cy = (lx + rx) / 2, (ly + ry) / 2
    # 額から鼻先まで（口を含めない）
    x0, x1 = int(cx - 1.25 * d), int(cx + 1.25 * d)
    y0, y1 = int(cy - 0.95 * d), int(cy + 0.62 * d)
    mask[y0:y1, x0:x1] = 255
    pts = cv2.goodFeaturesToTrack(g0, maxCorners=220, qualityLevel=0.01, minDistance=6, mask=mask)
    if pts is None or len(pts) < 20:
        raise SystemExit("追跡点が足りない")
    return pts.astype(np.float32)


def track(scene, ref_gray):
    frames = read_frames(ASSET / f"{scene}-ja-v1.mp4")
    g0 = gray(frames[0])
    if scene == REF_SCENE:
        eyes0 = [REF_EYES[0], REF_EYES[1]]
        scores = [1.0, 1.0]
    else:
        loc = locate_eyes(ref_gray, g0)
        eyes0 = [p for p, _ in loc]
        scores = [s for _, s in loc]
    pts0 = upper_face_points(g0, eyes0)
    guess = pts0.copy()
    rows = []
    lk = dict(winSize=(31, 31), maxLevel=4,
              criteria=(cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT, 40, 0.01))
    e0 = np.array(eyes0, dtype=np.float64)
    for i, frame in enumerate(frames):
        g = gray(frame)
        nxt, st, err = cv2.calcOpticalFlowPyrLK(g0, g, pts0, guess.copy(),
                                                flags=cv2.OPTFLOW_USE_INITIAL_FLOW, **lk)
        good = st.reshape(-1) == 1
        m, inl = cv2.estimateAffinePartial2D(pts0[good], nxt[good], method=cv2.RANSAC,
                                             ransacReprojThreshold=2.0)
        if m is None:
            raise SystemExit(f"{scene} f{i}: 変換が求まらない")
        inliers = int(inl.sum())
        # 外れた点は変換で置き直して次の初期値にする
        proj = cv2.transform(pts0, m)
        guess = np.where(good[:, None, None], nxt, proj).astype(np.float32)
        eyes = cv2.transform(e0.reshape(-1, 1, 2), m).reshape(-1, 2)
        scale = float(np.hypot(m[0, 0], m[1, 0]))
        angle = float(np.degrees(np.arctan2(m[1, 0], m[0, 0])))
        rows.append({"eyes": [[round(float(x), 2), round(float(y), 2)] for x, y in eyes],
                     "scale": round(scale, 4), "angle": round(angle, 3),
                     "inliers": inliers, "tracked": int(good.sum())})
    return {"frames": len(frames), "eyes0": [[round(x, 2), round(y, 2)] for x, y in eyes0],
            "matchScore": [round(s, 3) for s in scores], "points": int(len(pts0)), "rows": rows}, frames


def smooth(rows, radius=2):
    """目の座標を前後 radius コマの平均にする（追跡の微振動を消す）。"""
    arr = np.array([r["eyes"] for r in rows], dtype=np.float64)
    out = arr.copy()
    for i in range(len(arr)):
        a, b = max(0, i - radius), min(len(arr), i + radius + 1)
        out[i] = arr[a:b].mean(axis=0)
    for r, e in zip(rows, out):
        r["eyesSmooth"] = [[round(float(x), 2), round(float(y), 2)] for x, y in e]


def contact_sheet(scene, frames, data, path):
    n = len(frames)
    picks = np.linspace(0, n - 1, 12).round().astype(int)
    tiles = []
    for i in picks:
        f = frames[i].copy()
        for x, y in data["rows"][i]["eyesSmooth"]:
            cv2.circle(f, (int(round(x)), int(round(y))), 4, (0, 255, 0), -1)
            cv2.circle(f, (int(round(x)), int(round(y))), 24, (0, 255, 0), 1)
        crop = f[130:430, 450:850]
        cv2.putText(crop, f"{scene} f{i}", (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
        tiles.append(crop)
    rows = [np.hstack(tiles[k:k + 4]) for k in range(0, 12, 4)]
    cv2.imwrite(str(path), np.vstack(rows))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", type=Path, default=None, help="コンタクトシートの出力フォルダ")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    ref_gray = gray(read_frames(ASSET / f"{REF_SCENE}-ja-v1.mp4")[0])
    result = {"source": "tablet/app/src/main/assets/web/asset/<scene>-ja-v1.mp4", "scenes": {}}
    for scene in SCENES:
        data, frames = track(scene, ref_gray)
        smooth(data["rows"])
        inl = [r["inliers"] for r in data["rows"]]
        dist = [np.hypot(*(np.subtract(*r["eyesSmooth"]))) for r in data["rows"]]
        print(f"{scene}: {data['frames']} コマ / 版の一致 {data['matchScore']} / 点 {data['points']} / "
              f"一致点 最小 {min(inl)} / 目の間隔 {min(dist):.1f}〜{max(dist):.1f}px")
        result["scenes"][scene] = data
        if args.sheet:
            args.sheet.mkdir(parents=True, exist_ok=True)
            contact_sheet(scene, frames, data, args.sheet / f"track-{scene}.jpg")
    tmp = OUT.with_suffix(".tmp")
    tmp.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    json.loads(tmp.read_text(encoding="utf-8"))
    tmp.replace(OUT)
    print(f"書いた: {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
