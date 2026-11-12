"""位置で動く演出の版を、検証用の木へ当てる（本番の台本と APK には入らない）。

設計は reports/2026-11-12_position-driven-redesign.html。相手は 1 体で、体験者の位置で振る舞いを変える:

  2 周目 A  遠いときは左の幕の顔のシミ（締まったマスク）。角の線まで来ると幕へ沈む。
            背の線を越えて離れると、背丈のある人形が、体験者の歩いた跡をついてくる（cgMode=trail）
  2 周目 B  小さい人形が跡の上を寄り続ける。届いた線で、画面ごと別の場所へ飛ぶ（いまの演出を線で起こす）
  2 周目 C  人形の目で見たカットを、秒ではなく床の線で 1 本ずつ進める。途中は音なし。通路の 6 割で追いつく

使い方（検証用の木を sync した後）:
  py -3.11 tools/twin-tree.py sync
  py -3.11 tools/twin/variant_posdriven.py --root ../fcv-twin-tree
  py -3.11 tools/twin-tree.py run -- --fast --calib twin --tag posdrv-after

やること: 木の tools/twin/assets/ へ締まったマスクを作る → 木の assets.json と焼いた show.json へ
cue・線・人形・take を足す → manifest.json の configSha256 を合わせる。こちらの木（メイン）には書かない。
--only で場面を絞れる（a,b,c）。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
MAIN = HERE.parents[1]
W, H = 640, 480          # 映像（4:3）
FW, FH = 640, 360        # マスクの枠（16:9）

# 画の中の所（映像の 0〜1）。ツインの無人写真 A で、幕のひだとポールと手すりに合わせて決めた
LEFT_FACE = (0.215, 0.0, 0.435, 0.445)    # 左の幕の、ポールと角のあいだ。下の辺は手すりの上の縁
LEFT_PEEK = (0.125, 0.14, 0.315, 0.96)    # 左の幕の外の縁（人形が半身で覗く）

TALL_DOLL = "doll_tall"                   # 背丈のある市松人形（0330 の「人の背丈の市松人形」と同じ見立て）


def imwrite(p: Path, img) -> None:
    ok, buf = cv2.imencode(p.suffix, img)
    if not ok:
        sys.exit(f"書けない: {p}")
    buf.tofile(str(p))


def frame_mask(box, feather_px: int = 9) -> np.ndarray:
    """映像の矩形を、枠空間（16:9）のマスクにする。縁は内側へぼかす（外へはみ出さない）。"""
    x0, y0, x1, y1 = box
    src_w = FH * W / H                     # 枠の中で映像が占める幅（px）
    off = (FW - src_w) / 2
    m = np.zeros((FH, FW), np.float32)
    px0, px1 = int(round(off + x0 * src_w)), int(round(off + x1 * src_w))
    py0, py1 = int(round(y0 * FH)), int(round(y1 * FH))
    inset = feather_px
    m[py0 + (inset if y0 > 0 else 0):py1 - inset, px0 + inset:px1 - inset] = 1.0
    k = feather_px * 2 + 1
    m = cv2.GaussianBlur(m, (k, k), feather_px / 2)
    return (np.clip(m, 0, 1) * 255).astype(np.uint8)


def step(source: str, **kw) -> dict:
    """焼いた台本と同じ既定（-1 は「素材の値を継ぐ」）で 1 カットを作る。"""
    d = {"source": source, "camera": -1, "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
         "trimStartSec": -1, "trimEndSec": -1, "durKind": "sec", "lineId": "", "durSec": 0,
         "transition": "cut", "cg": "", "cgMode": "follow", "switchSfx": False}
    d.update(kw)
    return d


def trail(actor: str, gap: float, hop: float, hide: float = 0.9, **kw) -> dict:
    """体験者の歩いた跡を、道のり gap 後ろについてくる人形（hop ごとに飛び飛びに立つ。hide より近いと居ない）。"""
    return step("inherit", cg=actor, cgMode="trail", trailGapM=gap, trailStepM=hop, trailHideM=hide, **kw)


def patch_show(show: dict, only: set) -> None:
    def sa(name: str) -> str:
        return "sa://assets/" + name

    def cue(cid, name, cam, src, mask, **kw):
        c = {"id": cid, "name": name, "camera": cam, "maskUrl": sa(mask) if mask else "", "sourceUrl": sa(src),
             "strength": 1, "loop": False, "fadeIn": 0, "fadeOut": 0}
        c.update(kw)
        return c

    def line(lid, cam, x1, z1, x2, z2, label):
        return {"id": lid, "camera": cam, "x1": x1, "z1": z1, "x2": x2, "z2": z2, "dir": "both", "label": label, "mirrorOf": ""}

    old = {c["id"]: c for c in show["cues"]}
    idle = old["cue_stain_A"]["sourceUrl"].rsplit("/", 1)[-1]
    bulge = old["stain_A_press_bulge"]["sourceUrl"].rsplit("/", 1)[-1]
    doll = old["stain_A_done_doll"]["sourceUrl"].rsplit("/", 1)[-1]
    show["cues"] = [c for c in show["cues"] if not c["id"].startswith("posdrv_")] + [
        cue("posdrv_stain_left", "顔のシミ（左の幕・締まった形）", "A", idle, "posdrv_stainA_left_mask.png", fadeOut=0.4),
        cue("posdrv_stain_left_bulge", "盛り上がり（左の幕・締まった形）", "A", bulge, "posdrv_stainA_left_mask.png"),
        cue("posdrv_stain_left_peek", "人形が覗く（左の幕の外の縁）", "A", doll, "posdrv_stainA_peek_mask.png"),
    ]

    base = next(a for a in show["actors"] if a["id"] == "doll")
    show["actors"] = [a for a in show["actors"] if a["id"] != TALL_DOLL] + [
        dict(base, id=TALL_DOLL, name="背丈のある人形（跡をついてくる）", heightM=1.1)]

    show["layout"]["lines"] = [ln for ln in show["layout"]["lines"] if not ln["id"].startswith("posdrv_")] + [
        line("posdrv_A_behind", 0, -0.5, -0.20, 0.0, -0.20, "A 背の線（左の幕の前へ出た所）"),
        line("posdrv_B_reach", 1, -0.9, 0.25, -0.5, 0.25, "B 届く線（通路の 6 割）"),
        line("posdrv_C_1", 2, -0.27, 0.42, -0.27, 0.95, "C 15%"),
        line("posdrv_C_2", 2, -0.03, 0.42, -0.03, 0.95, "C 30%"),
        line("posdrv_C_3", 2, 0.17, 0.42, 0.17, 0.95, "C 45%"),
        line("posdrv_C_catch", 2, 0.38, 0.42, 0.38, 0.95, "C 60%（追いつき）"),
    ]

    for seg in show["timeline"]["segments"]:
        lap, cam = int(seg.get("lap", 0)), int(seg.get("camera", 0))
        if lap != 2:
            continue
        if cam == 0 and "a" in only:
            seg["takes"] = [
                {"id": "L2C0#0", "name": "2周目A 顔のシミ（左の幕。角の線で幕へ沈む）", "at": "enter", "ifMissed": "skip",
                 "policy": "yield", "wait": "segment", "once": True, "dismissible": True,
                 "markStartCueId": "posdrv_stain_left_bulge", "markDoneCueId": "posdrv_stain_left_peek", "markDoneSec": 0.4,
                 "steps": [step("inherit", camera=0, cueId="posdrv_stain_left", durKind="untilBeyond",
                                lineId="line_center_A", durSec=20, fadeOutSec=0.4)]},
                {"id": "L2C0#1", "name": "2周目A 離れると、歩いた跡を人形がついてくる", "at": "line", "lineId": "posdrv_A_behind",
                 "ifMissed": "skip", "policy": "yield", "wait": "segment", "once": True, "dismissible": True,
                 "steps": [trail(TALL_DOLL, 1.5, 0.45, camera=0, durKind="untilZoneChange")]},
            ]
        if cam == 1 and "b" in only:
            takes = seg["takes"]
            if len(takes) != 1 or takes[0]["id"] != "L2C1#0":
                sys.exit("2 周目 B の台本が想定と違う（L2C1#0 が 1 本のはず）")
            t = takes[0]
            t["name"] = "2周目B 跡を寄ってくる → 届いた線で別の場所へ飛ぶ"
            t["policy"] = "yield"
            t["ifMissed"] = "skip"
            # 床置きのカメラのすぐ前に立つので、背丈のある人形だと着物の裾しか映らない（20261112_232756_posdrv-trail）。
            # B では小さい人形（0.4m）を使う。床の高さから見上げる画角に全身が入る
            t["steps"] = [trail("doll", 0.8, 0.25, 0.4, camera=1, durKind="untilBeyond", lineId="posdrv_B_reach", durSec=20)] + t["steps"]
        if cam == 2 and "c" in only:
            pre = next((t for t in seg["takes"] if t["id"] == "L2C2#0"), None)
            if pre is None:
                sys.exit("2 周目 C の台本に L2C2#0 が無い")
            for st in pre["steps"]:
                if st.get("source") == "live":
                    st["camera"] = 2       # C の区間に 0.6 秒だけ B が映るのを直す（NOW.md の食い違い 11）

            def pov(n, sec, **kw):
                return step("clip", camera=2, cueId=f"pov_{n}", durSec=sec, transition="glitch", **kw)

            def walk(until):
                # 「越えた瞬間」ではなく「向こう側に居るか」で終える。動画のカットの最中に越えた線も数える
                return step("live", camera=2, durKind="untilBeyond", lineId=until, durSec=10, transition="glitch")

            seg["takes"] = [pre, {
                "id": "L2C2#1", "name": "2周目C 追いつき（歩くたびに近づく。途中は音なし）", "at": "line", "lineId": "posdrv_C_1",
                "ifMissed": "skip", "policy": "yield", "wait": "segment", "once": True,
                "steps": [pov(1, 0.6), walk("posdrv_C_2"), pov(2, 0.6), walk("posdrv_C_3"), pov(3, 0.6), walk("posdrv_C_catch"),
                          pov(4, 1.4, dollCall=True, switchSfx=True),
                          step("live", camera=2, durKind="untilZoneChange", transition="glitch", hasBgm=True,
                               bgm={"action": "play", "trackId": "bgm_LostPlace2", "loop": True, "fadeInSec": 3.0, "fadeOutSec": 3.0})]}]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="当てる木（検証用の木）")
    ap.add_argument("--only", default="a,b,c", help="当てる場面（2 周目の a,b,c）")
    a = ap.parse_args()
    only = {s.strip().lower() for s in a.only.split(",") if s.strip()}
    if not only <= {"a", "b", "c"}:
        sys.exit("--only は a,b,c から選ぶ")
    root = Path(a.root).resolve()
    if root == MAIN.resolve():
        sys.exit("メインの木には当てない（本番の台本を書き換えない）。検証用の木を指す")
    show_p = root / "Assets/StreamingAssets/show/show.json"
    man_p = root / "Assets/StreamingAssets/show/manifest.json"
    assets = root / "tools/twin/assets"
    spec_p = root / "tools/twin/assets.json"
    for p in (show_p, man_p, spec_p, assets / "twin_stainA_idle.png"):
        if not p.exists():
            sys.exit(f"無い: {p}（先に py -3.11 tools/twin-tree.py sync）")

    imwrite(assets / "posdrv_stainA_left_mask.png", frame_mask(LEFT_FACE))
    imwrite(assets / "posdrv_stainA_peek_mask.png", frame_mask(LEFT_PEEK))

    spec = json.loads(spec_p.read_text(encoding="utf-8"))
    spec["cues"].update({
        "posdrv_stain_left": {"source": "twin_stainA_idle.png", "mask": "posdrv_stainA_left_mask.png"},
        "posdrv_stain_left_bulge": {"source": "twin_stainA_bulge.png", "mask": "posdrv_stainA_left_mask.png"},
        "posdrv_stain_left_peek": {"source": "twin_stainA_doll.png", "mask": "posdrv_stainA_peek_mask.png"},
    })
    spec_p.write_text(json.dumps(spec, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")

    raw = show_p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    show = json.loads(raw.decode("utf-8-sig"))
    patch_show(show, only)
    data = json.dumps(show, ensure_ascii=False).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    show_p.write_bytes(data)
    man = json.loads(man_p.read_text(encoding="utf-8-sig"))
    man["configSha256"] = hashlib.sha256(data).hexdigest()
    man_p.write_text(json.dumps(man, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print(f"[variant] 当てた: {root}（2 周目 {'・'.join(sorted(only)).upper()}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
