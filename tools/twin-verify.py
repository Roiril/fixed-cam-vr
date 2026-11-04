"""HMD を被らず、Quest も配信スマホも使わずに、本番の体験を最初から最後まで通して確かめる入口。

    py -3.11 tools/twin-verify.py                 # 走行 → 判定 → 目線の動画（10 分ほど）
    py -3.11 tools/twin-verify.py --follow        # 後ろ上からの動画も撮る（5 分増える。部屋が黒いので画面しか映らない）
    py -3.11 tools/twin-verify.py --no-video      # 判定だけ（速い）
    py -3.11 tools/twin-verify.py --nomark        # 報告しない走行（人形 End）
    py -3.11 tools/twin-verify.py --relief        # ホラー軽減を選んだ走行

本番の Main シーンを Editor の Play で流す。入力は生の左 X だけ、カメラはデジタルツインの動画（tools/twin/make_pack.py）。
走った結果は体験の記録に残し、同じ記録を unity.ps1 replay で撮る。

出力は Drive の renders/twin-verify/<時刻>/（report.md・verdict.json・events.json・eye.mp4・follow.mp4）。
見る面は tools/twin-game/verify.html（preview_start twin-game の /verify.html）。

⚠ 実機の代わりではない。圧縮・遅延・両眼・ディスプレイ・パススルーは入らない。音はここでは聴けても実機の聞こえではない。
⚠ PASS は人形が実写に合っていることを意味しない。既定は APK と同じ本番の値で、ツインの画には合わない（report の頭に差を出す）。
   人形を画に合わせて見るなら --calib twin。カメラ A の配置によっては別れの人形の立ち位置が解けず、結末は中断になる。
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

ROOT = Path(__file__).resolve().parents[1]
PACK = ROOT / "Logs" / "twin-run" / "pack"
DRIVE = Path(r"G:\マイドライブ\shubie\renders\twin-verify")
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"
PS = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools" / "unity.ps1")]
XP_LINE = re.compile(r"\[XP\]\s+t=([\d.]+)\s+ev=(\S+)\s*(.*)$")
VERDICT_LINE = re.compile(r"^- (❌|⚠|✅) (.*)$")


def run(cmd: list[str], what: str) -> int:
    print(f"== {what}", flush=True)
    t0 = time.time()
    rc = subprocess.run(cmd, cwd=ROOT).returncode
    print(f"   {what}: exit={rc} {time.time() - t0:.0f}s", flush=True)
    return rc


def set_arg(pairs: dict[str, str]) -> str:
    return ",".join(f"{k}={v}" for k, v in pairs.items())


def read_verdicts(report: Path) -> list[dict]:
    level = {"❌": "FAIL", "⚠": "WARN", "✅": "OK"}
    out = []
    for line in report.read_text(encoding="utf-8").splitlines():
        m = VERDICT_LINE.match(line)
        if m:
            out.append({"level": level[m.group(1)], "text": m.group(2)})
        elif line.strip() == "---":
            break
    return out


def read_events(log: Path) -> list[dict]:
    """録画の頭（ev=xprec state=start）を 0 秒にした出来事。動画の時刻と揃う。"""
    rows = []
    t0 = None
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        m = XP_LINE.search(line)
        if not m:
            continue
        t, ev, rest = float(m.group(1)), m.group(2), m.group(3)
        if ev == "xprec" and "state=start" in rest and t0 is None:
            t0 = t
        rows.append((t, ev, rest))
    if t0 is None:
        t0 = rows[0][0] if rows else 0.0
    # 毎秒の標本は面に出さない（時間軸が埋まる）。
    skip = {"sum", "effects", "xprec", "fps", "stream", "config"}
    return [{"t": round(t - t0, 2), "ev": ev, "text": rest[:200]} for t, ev, rest in rows if ev not in skip]


def twinlap_check(events: list[dict]) -> tuple[str, str]:
    """3 周目の録画カットの頭に、録った周の印が映ったか（3-A は 2 周目・3-B/C は 1 周目）。

    録画は区間を出たあとも少し回るので、カットの後ろには次の周の印が映ってよい。見るのは最初の 1 つと、
    その次の周より先へ進んでいないこと。
    """
    want = {0: 2, 1: 1, 2: 1}
    shown_rec: dict[int, list[int]] = {}
    seen = False
    for e in events:
        if e["ev"] != "twinlap":
            continue
        seen = True
        kv = dict(p.split("=", 1) for p in e["text"].split() if "=" in p)
        seg = kv.get("seg", "-1/-1").split("/")
        if seg[0] == "3" and kv.get("src") == "rec":
            shown_rec.setdefault(int(seg[1]), []).append(int(kv["shown"]))
    if not seen:
        return "WARN", "周の印を 1 度も読めなかった（どの周の映像が映ったか確かめていない）"
    bad = [f"3-{'ABC'[c]} に {v} 周目" for c, v in shown_rec.items()
           if v[0] != want.get(c) or max(v) > want.get(c, 0) + 1]
    if bad:
        return "FAIL", "3 周目の録画が想定の周ではない: " + " / ".join(bad)
    if len(shown_rec) < 3:
        return "WARN", f"3 周目で録画の印を読めた区間が {sorted(shown_rec)} だけ"
    return "OK", "3 周目の録画は 3-A が 2 周目・3-B と 3-C が 1 周目だった（画に焼いた印で確認）"


def ending_checks(log: Path, calib: str, nomark: bool) -> list[tuple[str, str]]:
    """走行そのものの照合。頼んだ calib で走ったか・報告したのに帰還で終わったか・別れの立ち位置が解けたか。"""
    text = log.read_text(encoding="utf-8", errors="replace")
    out = []
    used = re.findall(r"ev=config .*?calib=(\w+)", text)
    if not used or used[-1] != calib:
        out.append(("FAIL", f"頼んだ calib={calib} で走っていない（ログは {used[-1] if used else '無し'}）"))
    endings = re.findall(r"ev=outro stage=Report .*?ending=(\w+)", text)
    ending = endings[-1] if endings else "-"
    want = "Trapped" if nomark else "Released"
    unsolved = re.findall(r"\[EndingFarewell\] placement unavailable (.*)", text)
    if ending == want:
        out.append(("OK", f"結末は想定どおり {ending}"))
    else:
        out.append(("FAIL", f"結末が {ending}（想定は {want}）"))
    if unsolved and not nomark:
        out.append(("FAIL", f"カメラ A のこの配置では別れの人形の立ち位置が解けない（{unsolved[-1].strip()}）。"
                            "人形が出ないまま待ち続け、報告していても帰還で終わらない"))
    return out


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-video", action="store_true")
    ap.add_argument("--no-eye", action="store_true")
    ap.add_argument("--follow", action="store_true", help="後ろ上からの動画も撮る")
    ap.add_argument("--nomark", action="store_true", help="報告しない（人形 End）")
    ap.add_argument("--relief", action="store_true", help="ホラー軽減")
    ap.add_argument("--speed", type=float, default=0.25)
    ap.add_argument("--dwell", type=float, default=6.0)
    ap.add_argument("--keep-rec", action="store_true", help="体験の記録（JPEG 込みで数百 MB）を消さない")
    ap.add_argument("--calib", choices=("twin", "show"), default="show",
                    help="人形の姿勢。show は APK と同じ本番の show.json（結末まで通る。人形は画と合わない）、twin はツインの配置（人形は画に合う）")
    ap.add_argument("--fov", type=float, default=52.0, help="目線の動画の縦の画角（度）。実機より狭く切って画面を大きく映す")
    ap.add_argument("--tag", default="")
    a = ap.parse_args()

    stamp = time.strftime("%Y%m%d_%H%M%S") + (f"_{a.tag}" if a.tag else "")
    work = ROOT / "Logs" / "twin-run" / stamp
    work.mkdir(parents=True, exist_ok=True)
    dest = DRIVE / stamp

    if run(["py", "-3.11", str(ROOT / "tools" / "twin" / "make_pack.py"), "--out", str(PACK)], "映像の束") != 0:
        return 2

    args = {"pack": str(PACK), "out": str(work), "speed": str(a.speed), "dwell": str(a.dwell)}
    if a.nomark:
        args["nomark"] = "1"
    if a.relief:
        args["relief"] = "1"
    if a.calib == "show":
        args["calib"] = "show"
    rc_run = run(PS + ["twin-run", "-Set", set_arg(args)], "走行")
    run_json = work / "twin_run.json"
    run_info = json.loads(run_json.read_text(encoding="utf-8")) if run_json.exists() else {"result": "走行の結果が無い"}

    log = work / "run_xp.log"
    report = work / "report.md"
    verdicts: list[dict] = []
    events: list[dict] = []
    if log.exists():
        run(["py", "-3.11", str(ROOT / "tools" / "analyze-xp-log.py"), str(log), "--show", str(SHOW),
             "--out", str(report), "--pc"], "判定")
        if report.exists():
            verdicts = read_verdicts(report)
        events = read_events(log)
        level, text = twinlap_check(events)
        verdicts.insert(0, {"level": level, "text": text})
        for level, text in reversed(ending_checks(log, a.calib, a.nomark)):
            verdicts.insert(0, {"level": level, "text": text})

    pack = json.loads((PACK / "pack.json").read_text(encoding="utf-8"))
    gaps = pack.get("calibGapPx", {})
    # 再生で撮る。
    rec = run_info.get("recDir", "")
    videos = {}
    replay_compare = ""
    if not a.no_video and rc_run == 0 and rec:
        for view, off in (("eye", a.no_eye), ("follow", not a.follow)):
            if off:
                continue
            target = work / f"{view}.mp4"
            sub = work / f"replay_{view}"
            sub.mkdir(exist_ok=True)
            target = sub / f"{view}.mp4"
            if run(PS + ["replay", "-Set", set_arg({"rec": rec, "view": view, "out": str(target), **({"fov": str(a.fov)} if view == "eye" else {})})], f"撮影 {view}") == 0 and target.exists():
                videos[view] = target
            elif not target.exists():
                verdicts.insert(0, {"level": "FAIL", "text": f"{view} の動画を撮れなかった"})
        replay_log = next((work / f"replay_{v}" / "replay_xp.log" for v in ("eye", "follow") if (work / f"replay_{v}" / "replay_xp.log").exists()), None)
        if replay_log:
            p = subprocess.run(["py", "-3.11", "-X", "utf8", str(ROOT / "tools" / "quest-xprec.py"), "compare", rec, str(replay_log)],
                               cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
            replay_compare = p.stdout
            (work / "compare.txt").write_text(p.stdout + p.stderr, encoding="utf-8")

    n = {lv: sum(1 for v in verdicts if v["level"] == lv) for lv in ("FAIL", "WARN", "OK")}
    ok = run_info.get("result") == "ok" and n["FAIL"] == 0
    summary = {
        "stamp": stamp, "pass": ok, "counts": n, "run": run_info, "calibGapPx": gaps,
        "videos": {k: f"{k}.mp4" for k in videos},
        "compare": replay_compare.splitlines()[:2],
        "verdicts": verdicts, "args": {"speed": a.speed, "dwell": a.dwell, "nomark": a.nomark, "relief": a.relief, "calib": a.calib},
    }

    calib_note = ("人形の姿勢はツインの配置へ差し替えて走った" if a.calib == "twin" else "人形の姿勢は本番の show.json のまま走った（ツインの映像とは合わない）")
    head = [
        f"# ツイン走行 {stamp}",
        "",
        f"走行: **{run_info.get('result')}**（{run_info.get('sec')} 秒・周 {run_info.get('lap')}・報告 {run_info.get('marks')}・例外 {run_info.get('exceptions')}）",
        f"判定: FAIL {n['FAIL']} / WARN {n['WARN']} / OK {n['OK']}",
        "",
        f"⚠ {calib_note}。本番の calib とツインの配置の差（頭と足の投影・中央値 px）: {gaps}。**PASS は本番の calib で合っていることを意味しない。**",
        f"⚠ 映像は頭の位置から引いている。道とのずれ最大 {run_info.get('feedMaxErrM')}m。道から外れた刻み {run_info.get('feedOffRouteTicks')}（人の居ない写真を配った）。立ち止まると人は歩きの 1 コマで止まる。",
        "",
    ]
    final_report = work / "summary.md"
    body = report.read_text(encoding="utf-8") if report.exists() else "（判定を作れなかった）"
    final_report.write_text("\n".join(head) + "\n" + body, encoding="utf-8", newline="\n")

    # Drive へ移す。
    dest.mkdir(parents=True, exist_ok=True)
    (dest / "verdict.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    (dest / "events.json").write_text(json.dumps(events, ensure_ascii=False), encoding="utf-8")
    shutil.copy2(final_report, dest / "report.md")
    for f in ("run_xp.log", "twin_run.json", "compare.txt"):
        if (work / f).exists():
            shutil.copy2(work / f, dest / f)
    for view, path in videos.items():
        shutil.copy2(path, dest / f"{view}.mp4")
    (DRIVE / "latest.json").write_text(json.dumps({"stamp": stamp}), encoding="utf-8")

    # C: を食うものは残さない。
    if videos:
        shutil.rmtree(work, ignore_errors=True)
    if rec and not a.keep_rec and Path(rec).is_dir():
        shutil.rmtree(rec, ignore_errors=True)

    print(f"{'PASS' if ok else 'NOT PASS'} FAIL={n['FAIL']} WARN={n['WARN']} OK={n['OK']}")
    for v in verdicts:
        if v["level"] != "OK":
            print(f"  {v['level']}: {v['text']}")
    print(f"→ {dest}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
