"""体験者が画面のどこに映るかを、ツインの固定カメラの動画から測る。

演出を「体験者の位置」で設計するための地図を作る。台ごとに、歩く道の進み（区間の中の 0〜1）に対して
代役の体が画のどこを占めるかを、無人写真との差分で出す。あわせて、台本の素材（マスクつき）が
出ている間に体と重なるかを数える。素材は体より手前に描かれるので、重なった所では体が素材に隠れる。

出すもの:
  stage_map.json   台ごとの、コマ・床の位置・進み・体の外接矩形（画の 0〜1）と、重なりの集計
  sweep_<台>.png   無人写真の上に、区間の間に体が通った所を進みの色で重ねた画
  strip_<台>.png   区間の頭から終わりまでのコマを並べた画（外接矩形と進みつき）
  bands_<台>.png   区間を 3 つの帯に分け、帯ごとに体が来うる所を赤く塗った画（下の数字は列ごとの空き %）
  audit.md         台本の素材ごとの、出ている帯・重なり始める進み・体が隠れる割合

使い方:
  py -3.11 tools/twin/stage_map.py --out "G:/マイドライブ/shubie/reports/stage-map/<名前>"
  py -3.11 tools/twin/stage_map.py --out <フォルダ> --show <別の show.json> --assets <素材のフォルダ> --check

入力はツインの動画（Drive renders/twin-cycles/game/forward/<台>.mp4 と <台>_plate.jpg）と
Logs/twin-run/pack/pack.json（コマごとの床の位置とカメラの姿勢）。測るのは順走の 1 周ぶん。
代役は大人 1 人・手すりから 0.16m の道。背の低い人・道を外れた人・逆走は測っていない。
"""
from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
GAME = Path("G:/マイドライブ/shubie/renders/twin-cycles/game/forward")
PACK = ROOT / "Logs/twin-run/pack/pack.json"
SHOW = ROOT / "Assets/StreamingAssets/show/show.json"
CAMS = ["A", "B", "C"]
ZONE_OF = {"A": 0, "B": 1, "C": 2}
W, H = 640, 480
BODY_H = 1.70      # 代役の背丈（m）
BODY_R = 0.22      # 肩の半分（m）
FADE_TAIL_SEC = 0.4


def imread(path, flags=cv2.IMREAD_COLOR):
    """cv2.imread は日本語のパス（Drive の「マイドライブ」）を開けないので、バイト列から読む。"""
    img = cv2.imdecode(np.fromfile(str(path), dtype=np.uint8), flags)
    if img is None:
        sys.exit(f"画像が読めない: {path}")
    return img


def imwrite(path, img) -> None:
    ok, buf = cv2.imencode(Path(path).suffix, img)
    if not ok:
        sys.exit(f"画像が書けない: {path}")
    buf.tofile(str(path))


def zone_at(x: float, z: float, grid: dict) -> int:
    cells, tile, cols, rows = grid["cells"], grid["tileM"], grid["cols"], grid["rows"]
    c = min(cols - 1, max(0, int((x + cols * tile / 2) / tile)))
    r = min(rows - 1, max(0, int((rows * tile / 2 - z) / tile)))
    return int(cells[r][c])


def read_frames(cam: str, tmp: Path) -> list:
    d = tmp / cam
    if not d.exists() or not any(d.iterdir()):
        d.mkdir(parents=True, exist_ok=True)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(GAME / f"{cam}.mp4"),
                        "-start_number", "0", str(d / "%04d.png")], check=True)
    return sorted(d.glob("*.png"))


def person_mask(frame: np.ndarray, plate: np.ndarray) -> np.ndarray:
    """無人写真との差分から体の形を取る。いちばん大きい塊と、その近くの塊（離れた腕や頭）を残す。"""
    a = cv2.GaussianBlur(frame, (5, 5), 0).astype(np.int16)
    b = cv2.GaussianBlur(plate, (5, 5), 0).astype(np.int16)
    diff = np.abs(a - b).max(axis=2).astype(np.uint8)
    m = (diff > 26).astype(np.uint8)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(m, 8)
    out = np.zeros_like(m)
    if n <= 1:
        return out
    big = max(range(1, n), key=lambda i: stats[i, cv2.CC_STAT_AREA])
    if stats[big, cv2.CC_STAT_AREA] < 250:
        return out
    bx, by, bw, bh = (int(stats[big, k]) for k in (cv2.CC_STAT_LEFT, cv2.CC_STAT_TOP, cv2.CC_STAT_WIDTH, cv2.CC_STAT_HEIGHT))
    for i in range(1, n):
        if stats[i, cv2.CC_STAT_AREA] < 120:
            continue
        x, y, w, h = (int(stats[i, k]) for k in (cv2.CC_STAT_LEFT, cv2.CC_STAT_TOP, cv2.CC_STAT_WIDTH, cv2.CC_STAT_HEIGHT))
        if x < bx + bw + 40 and x + w > bx - 40 and y < by + bh + 40 and y + h > by - 40:
            out[lab == i] = 1
    return out


def cam_projector(calib: dict):
    """show.json の calib と同じ形（位置・yaw・pitch・roll・fx）から、空間の点を画へ写す関数を作る。
    Unity の左手系（x 右・y 上・z 前）。"""
    yaw, pitch, roll = (math.radians(calib[k]) for k in ("yawDeg", "pitchDeg", "rollDeg"))
    cy, sy, cp, sp = math.cos(yaw), math.sin(yaw), math.cos(pitch), math.sin(pitch)
    fwd = np.array([sy * cp, sp, cy * cp])
    right = np.array([cy, 0.0, -sy])
    up = np.cross(fwd, right)
    cr, sr = math.cos(roll), math.sin(roll)
    right2, up2 = right * cr + up * sr, -right * sr + up * cr
    pos = np.array([calib["x"], calib["y"], calib["z"]])
    fx, fy, cx, cyp = calib["fxPx"], calib["fyPx"], calib["cxPx"], calib["cyPx"]

    def project(p):
        v = np.asarray(p, dtype=float) - pos
        zc = float(v @ fwd)
        if zc <= 0.05:
            return None
        return (cx + fx * float(v @ right2) / zc, cyp - fy * float(v @ up2) / zc)
    return project


def body_box(project, x: float, z: float):
    """床の位置に立つ体（円柱）の、画の中の外接矩形（px）。"""
    pts = []
    for h in (0.0, BODY_H * 0.5, BODY_H):
        for k in range(8):
            a = k * math.pi / 4
            p = project((x + BODY_R * math.cos(a), h, z + BODY_R * math.sin(a)))
            if p:
                pts.append(p)
    if len(pts) < 6:
        return None
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


def mask_in_source(path: Path):
    """枠空間（16:9）のマスクを、4:3 の映像の座標へ切り出す。白い所が素材の出る所。"""
    if not path.exists():
        return None
    m = imread(path, cv2.IMREAD_GRAYSCALE)
    fh, fw = m.shape
    src_w = int(round(fh * W / H))
    x0 = (fw - src_w) // 2
    return cv2.resize(m[:, x0:x0 + src_w], (W, H), interpolation=cv2.INTER_LINEAR) > 96


def crossing_index(rows: list, line: dict) -> list:
    """歩く道が床の線を越えるコマの番号（越えるたびに 1 つ）。"""
    out = []
    ax, az, bx, bz = line["x1"], line["z1"], line["x2"], line["z2"]

    def side(x, z):
        return (bx - ax) * (z - az) - (bz - az) * (x - ax)

    for k in range(1, len(rows)):
        p, q = rows[k - 1], rows[k]
        s0, s1 = side(p["x"], p["z"]), side(q["x"], q["z"])
        if s0 == s1 or (s0 > 0) == (s1 > 0):
            continue
        t = s0 / (s0 - s1)
        ix, iz = p["x"] + (q["x"] - p["x"]) * t, p["z"] + (q["z"] - p["z"]) * t
        u = ((ix - ax) * (bx - ax) + (iz - az) * (bz - az)) / max(1e-9, (bx - ax) ** 2 + (bz - az) ** 2)
        if 0.0 <= u <= 1.0:
            out.append(k)
    return out


def audit_cues(cam: str, rows: list, occ_frames: list, show: dict, assets_dir: Path, fps: float) -> list:
    """出ている帯は台本から読む: 入りは区間の頭か線、終わりは線・秒・区間の終わり。消えぎわも数える。
    秒は束の歩く速さ（本番の走行の約 2 倍速い）で進みへ直すので、秒で終わる演出の帯は目安。"""
    cues = {c["id"]: c for c in show["cues"]}
    lines = {ln["id"]: ln for ln in show["layout"]["lines"]}
    zid = ZONE_OF[cam]
    n = len(rows)
    tail = int(round(FADE_TAIL_SEC * fps))
    out = []

    def first_cross(line_id, after):
        ln = lines.get(line_id)
        if not ln:
            return None
        return next((k for k in crossing_index(rows, ln) if k > after), None)

    for seg in show["timeline"]["segments"]:
        if int(seg.get("camera", 0)) != zid:
            continue
        for take in seg.get("takes", []):
            k = 0
            if take.get("at") == "line":
                k = first_cross(take.get("lineId", ""), -1)
                if k is None:
                    continue
            spans = {}
            take_k0, take_k1 = k, k
            for st in take.get("steps", []):
                kind = st.get("durKind", "sec")
                if kind == "sec":
                    k1 = min(n - 1, k + int(round(float(st.get("durSec", 0)) * fps)))
                elif kind in ("untilLine", "untilBeyond"):
                    c = first_cross(st.get("lineId", ""), k)
                    k1 = n - 1 if c is None else c
                else:
                    k1 = n - 1
                if st.get("source") in ("inherit", "live") and st.get("cueId"):
                    spans.setdefault(st["cueId"], []).append((k, min(n - 1, k1 + tail)))
                    take_k1 = max(take_k1, k1)
                k = k1
                if k >= n - 1:
                    break
            for key in ("markStartCueId", "markDoneCueId"):
                if take.get(key) and take_k1 > take_k0:
                    spans.setdefault(take[key], []).append((take_k0, take_k1))
            for cid, ranges in spans.items():
                cue = cues.get(cid)
                if not cue or not cue.get("maskUrl"):
                    continue
                mask = mask_in_source(assets_dir / Path(cue["maskUrl"]).name)
                if mask is None:
                    continue
                live = sorted({j for a, b in ranges for j in range(a, b + 1)})
                first, hit, peak = None, 0, 0.0
                for j in live:
                    body = np.unpackbits(occ_frames[j])[:W * H].reshape(H, W).astype(bool)
                    area = int(body.sum())
                    if area == 0:
                        continue
                    share = float((body & mask).sum()) / area
                    if share > 0.02:
                        hit += 1
                        peak = max(peak, share)
                        if first is None:
                            first = rows[j]["prog"]
                out.append({"take": take["id"], "cue": cid,
                            "life": [rows[live[0]]["prog"], rows[live[-1]]["prog"]] if live else None,
                            "firstProg": first, "frameShare": round(hit / max(1, len(live)), 3),
                            "peakBodyShare": round(peak, 3), "maskAreaShare": round(float(mask.mean()), 3)})
    return out


def turbo(t: float):
    c = cv2.applyColorMap(np.array([[int(255 * min(1, max(0, t)))]], np.uint8), cv2.COLORMAP_TURBO)[0, 0]
    return int(c[0]), int(c[1]), int(c[2])


def put(img, text, org, scale=0.45, color=(255, 255, 255)):
    cv2.putText(img, text, org, cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), 3, cv2.LINE_AA)
    cv2.putText(img, text, org, cv2.FONT_HERSHEY_SIMPLEX, scale, color, 1, cv2.LINE_AA)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--tmp", default=str(ROOT / "Logs/stage-map/frames"))
    ap.add_argument("--margin", type=float, default=0.06, help="体の外に取る余白（画の幅に対する割合）")
    ap.add_argument("--show", default=str(SHOW), help="重なりを数える台本（既定は焼いた本番の show.json）")
    ap.add_argument("--assets", default=str(ROOT / "Logs/twin-run/assets"), help="台本の素材名で並べたツイン用素材のフォルダ")
    ap.add_argument("--check", action="store_true", help="体と重なる素材が 1 本でもあれば exit 1")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    tmp = Path(args.tmp)
    show_path = Path(args.show)
    for p in (GAME / "A.mp4", PACK, show_path):
        if not p.exists():
            sys.exit(f"入力が無い: {p}")

    pack = json.loads(PACK.read_text(encoding="utf-8"))
    xs, zs, fps = pack["xs"], pack["zs"], pack["fps"]
    calibs = {c["id"]: c["calib"] for c in json.loads(pack["calibOverride"])["cams"]}
    show = json.loads(show_path.read_text(encoding="utf-8-sig"))
    grid = show["layout"]["grid"]
    zones = [zone_at(x, z, grid) for x, z in zip(xs, zs)]
    move_end = pack["moveEnd"]
    grow = int(args.margin * W)
    kernel = np.ones((grow * 2 + 1, grow * 2 + 1), np.uint8)

    result = {"source": "twin forward", "fps": fps, "bodyH": BODY_H, "show": str(show_path), "cams": {}}
    audits = {}
    for cam in CAMS:
        frames = read_frames(cam, tmp)
        plate = imread(GAME / f"{cam}_plate.jpg")
        project = cam_projector(calibs[cam])
        idx = [i for i in range(min(len(frames), move_end + 1)) if zones[i] == ZONE_OF[cam]]
        if cam == "A":
            # A は 1 周の頭（歩き出し）と終わり（戻り）に分かれる。戻り → 歩き出しの順に繋いで区間 1 本にする
            half = len(xs) // 2
            idx = [i for i in idx if i >= half] + [i for i in idx if i < half]
        n = len(idx)
        rows, occ_frames = [], []
        heat = np.zeros((H, W), np.float32)
        band_occ = [np.zeros((H, W), np.uint8) for _ in range(3)]
        for k, i in enumerate(idx):
            prog = k / max(1, n - 1)
            m = person_mask(imread(frames[i]), plate)
            pb = body_box(project, xs[i], zs[i])
            if pb and cam == "C":
                # C は黒い服が暗い幕の前で差分に出ない（頭しか取れない）。床の位置から投影した矩形を足す
                x0, y0 = max(0, int(pb[0])), max(0, int(pb[1]))
                x1, y1 = min(W, int(pb[2]) + 1), min(H, int(pb[3]) + 1)
                if x1 > x0 and y1 > y0:
                    m[y0:y1, x0:x1] = 1
            occ_frames.append(np.packbits(m))
            ys_, xs_ = np.nonzero(m)
            box = [float(xs_.min()) / W, float(ys_.min()) / H, float(xs_.max()) / W, float(ys_.max()) / H] if len(xs_) else None
            rows.append({"frame": i, "x": round(xs[i], 3), "z": round(zs[i], 3), "prog": round(prog, 3),
                         "box": [round(v, 3) for v in box] if box else None})
            heat[m > 0] = np.maximum(heat[m > 0], 0.001 + prog)
            band_occ[min(2, int(prog * 3))] |= cv2.dilate(m, kernel)

        col = cv2.applyColorMap((np.clip(heat, 0, 1) * 255).astype(np.uint8), cv2.COLORMAP_TURBO)
        a = (heat > 0).astype(np.float32)[..., None] * 0.55
        sweep = (plate * (1 - a) + col * a).astype(np.uint8)
        for t in (0.0, 0.25, 0.5, 0.75, 1.0):
            r = min(rows, key=lambda r_: abs(r_["prog"] - t))
            if r["box"]:
                x0, y0, x1, y1 = r["box"]
                cv2.rectangle(sweep, (int(x0 * W), int(y0 * H)), (int(x1 * W), int(y1 * H)), turbo(t), 2)
                put(sweep, f"{int(t * 100)}%", (int(x0 * W) + 3, max(14, int(y0 * H) + 14)))
        imwrite(out / f"sweep_{cam}.png", sweep)

        tiles = []
        for t in np.linspace(0, 1, 8):
            r = rows[int(round(t * (n - 1)))]
            fr = imread(frames[r["frame"]])
            if r["box"]:
                x0, y0, x1, y1 = r["box"]
                cv2.rectangle(fr, (int(x0 * W), int(y0 * H)), (int(x1 * W), int(y1 * H)), (0, 255, 255), 1)
            put(fr, f"{int(r['prog'] * 100)}%  x={r['x']:+.2f} z={r['z']:+.2f}", (8, 20), 0.6)
            tiles.append(cv2.resize(fr, (480, 360)))
        imwrite(out / f"strip_{cam}.png", np.vstack([np.hstack(tiles[:4]), np.hstack(tiles[4:])]))

        tiles, free = [], []
        for b in range(3):
            img = plate.copy()
            occ = band_occ[b] > 0
            img[occ] = (img[occ] * 0.25).astype(np.uint8)
            img[occ, 2] = np.minimum(255, img[occ, 2].astype(np.int16) + 70).astype(np.uint8)
            cols_free = [float((~occ[:, int(W * q / 8):int(W * (q + 1) / 8)]).mean()) for q in range(8)]
            free.append([round(v, 2) for v in cols_free])
            for q in range(8):
                put(img, f"{int(cols_free[q] * 100)}", (int(W * q / 8) + 22, H - 10), 0.5,
                    (120, 255, 120) if cols_free[q] > 0.97 else (200, 200, 255))
            put(img, f"cam {cam}  band {b + 1}/3", (8, 18), 0.5)
            tiles.append(img)
        imwrite(out / f"bands_{cam}.png", np.hstack(tiles))

        table = []
        for t in np.linspace(0, 1, 11):
            r = rows[int(round(t * (n - 1)))]
            table.append({"prog": round(float(t), 1), "x": r["x"], "z": r["z"], "box": r["box"]})
        audits[cam] = audit_cues(cam, rows, occ_frames, show, Path(args.assets), fps)
        result["cams"][cam] = {"frames": n, "sec": round(n / fps, 2), "table": table,
                               "freeByBandAndColumn8": free, "audit": audits[cam], "rows": rows}
        print(f"[stage-map] {cam}: {n} コマ（{n / fps:.1f} 秒）")

    lines = ["# 台本の素材と体の重なり（ツインの順走 1 周）", "", f"台本: {show_path}", "",
             "| 台 | 演出 | cue | 出ている帯 | 重なり始める進み | 出ている間に重なるコマ | 体が隠れる割合の最大 |", "|---|---|---|---|---|---|---|"]
    for cam in CAMS:
        for a in audits[cam]:
            first = "重ならない" if a["firstProg"] is None else f"{int(a['firstProg'] * 100)}%"
            life = "-" if not a["life"] else f"{int(a['life'][0] * 100)}〜{int(a['life'][1] * 100)}%"
            lines.append(f"| {cam} | {a['take']} | {a['cue']} | {life} | {first} | {int(a['frameShare'] * 100)}% | {int(a['peakBodyShare'] * 100)}% |")
    (out / "audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    (out / "stage_map.json").write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    print(f"[stage-map] 出力: {out}")
    bad = [a for cam in CAMS for a in audits[cam] if a["firstProg"] is not None]
    if args.check and bad:
        sys.exit(f"[stage-map] 体と重なる素材が {len(bad)} 本ある")


if __name__ == "__main__":
    main()
