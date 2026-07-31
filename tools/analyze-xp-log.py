#!/usr/bin/env python3
"""実機 logcat の [XP] 行を読んで「体験が著作どおりに起きたか」を判定する。

使い方:
    python tools/analyze-xp-log.py <logcat.txt> [--show tools/web-compositor/show.json]
                                   [--out reports/xp-YYYYMMDD.md]

判定の考え方:
  - **show.json が期待値の正**。著作された演出・周回数・録画設定と、実機で起きたことを突き合わせる。
  - 「起きなかったこと」を最重視する。演出が黙って消える・録画が飛ぶのがこの系の代表的な壊れ方で、
    ログに何も出ないので、期待値の側から引き算しないと気づけない。
  - **「状態が進んだ」ではなく「効果が出た」を見る。** 2026-07-31、この解析が「FAIL ゼロ・演出 7 本
    すべて OK」と判定した走行の画を録ったら、導入演出が 1 段も画に出ていなかった。段の遷移は完璧に
    進んでいて、「その段で画に何かが実際に出たか」を 1 つも観測していなかった。以後
    `veil` / `pt` / `bg` / `wire` / `ovl` / `cg` / `bgm` / `font` / `ev=step` を判定に入れる。
  - 映像の質（受信 fps・砂嵐・遅延・表示 fps）は分布で出す。平均だけだと一過性の破綻が消える。

⚠ **観測項目は C# の ShowTelemetryHost と対で直す**（片方だけだと沈黙して食い違う）。
   新しいキーが無い古いログでも例外を出さずに動くこと（すべて存在チェックで囲む）。

レポートは UTF-8 のファイルへ書く（Windows 端末は cp932 で日本語表示が壊れるため）。
標準出力へは ASCII の要約だけ出す。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import defaultdict

XP = re.compile(r"\[XP\]\s+(.*)$")
# 体験に関わる既存タグ（テレメトリ以外の一次情報）。理由まで書いてあるのはこちら。
OTHER_TAGS = re.compile(
    r"\[(TakeRunner|ScreenOverlay|SegmentRecorder|CameraStream|MJPEG|ShowRun|Intro|"
    r"LapCounter|CueScheduler|ShowControl|Discovery|HmdLife|ShowCgLayer|XPWalk|CourseFrame)\]\s*(.*)$"
)


# logcat の 1 行。`07-31 16:49:58.057 W/Unity   (10831): 本文`（`-v time` 以外の形でも拾えるよう search）
LOGCAT_LINE = re.compile(r"\b([VDIWEF])/([\w.\-]+)\s*\(\s*\d+\s*\):\s?(.*)$")
# Unity がスタックトレースとして吐く行。`Namespace.Class:Method(引数)` の形で、本文ではない。
STACK_FRAME = re.compile(r"^[\w.`<>+\[\]]+:[\w.`<>+\[\]]+\s*\(.*\)\s*$")
# `[./Runtime/Camera/Camera.cpp line 169299656]` / `(Filename: X Line: 12)` もスタックの付属物。
STACK_NOISE = re.compile(r"^(\[.*line \d+\]|\(Filename:.*\))\s*$")
JP_WARN = re.compile(r"見つかりません|失敗|出ません|出ない")
# 同じ本文が複数のレベルで出たときに残す方の順位（大きい方が重い）。
LEVEL_RANK = {"V": 0, "D": 0, "I": 0, "W": 1, "E": 2, "F": 3}

# 毎フレーム出るが害の無いもの。**なぜ無害かを書く**（書かないと次の人が消せない）。
HARMLESS = (
    # Meta の OpenXR ランタイムは遮蔽メッシュを返さない。URP は無ければ描かないだけで、絵に影響しない。
    "Failed to get occlusion mesh",
    # 上と同じ経路（可視矩形の算出）。同じ理由で毎フレーム出る。
    "line loop data",
)


def parse_kv(body: str) -> dict:
    out = {}
    for tok in body.split():
        if "=" in tok:
            k, _, v = tok.partition("=")
            out[k] = v
    return out


def load_warnings(path: str):
    """実機ログの警告・エラーを数え上げる。

    `.claude/memory/onsite_experience_test.md` は「走行後は必ず grep しろ」と書いているのに、
    判定はこれを見ていなかった（人が手で grep する前提のまま）。2026-07-31 に見つけた 3 件は
    すべてここに出ていたのに、[XP] だけ見ていて気づけなかった。

    返すのは {本文: (レベル, 件数, 日本語か)}。スタックトレースの行は本文ではないので落とす
    （1 つの警告に 10 行以上ぶら下がり、数えると原因が埋もれる）。

    **日本語かどうかを持つ**のは、日本語の警告がこの codebase の人間が「これが起きたら困る」と
    思って書いたものだから。engine 由来の英語警告は数で勝つが、直すべきは前者のことが多い
    （実測: `[IntroVeil] シェーダが見つかりません` 1 行が、MJPEG の再接続 9 行に埋もれていた）。
    """
    hits = {}
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            m = LOGCAT_LINE.search(raw)
            if not m:
                continue
            level, tag, msg = m.group(1), m.group(2), m.group(3).rstrip()
            if tag != "Unity" or not msg:
                continue
            jp = bool(JP_WARN.search(msg))
            if level not in ("W", "E", "F") and not jp:
                continue
            if STACK_FRAME.match(msg) or STACK_NOISE.match(msg):
                continue
            # 1 語だけの行はスタックの切れ端（`System.Threa` 等）。本文なら空白か [タグ] を持つ。
            if " " not in msg and not msg.startswith("["):
                continue
            if any(h in msg for h in HARMLESS):
                continue
            key = msg[:200]
            prev = hits.get(key)
            # 同じ本文が W と E の両方で出たら、重い方を残す。
            worst = level if prev is None or LEVEL_RANK.get(level, 0) > LEVEL_RANK.get(prev[0], 0) \
                else prev[0]
            hits[key] = (worst, (prev[1] if prev else 0) + 1, jp)
    return hits


def load_events(path: str):
    """[XP] イベントと、参考にする既存タグ行を返す。"""
    events, others = [], []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            m = XP.search(raw)
            if m:
                body = m.group(1)
                head, *cams = body.split("|")
                ev = parse_kv(head)
                ev["_cams"] = [parse_kv(c) for c in cams]
                # "|c0 con=1 ..." の c0 はキー無しトークンなので拾い直す
                for c, raw_c in zip(ev["_cams"], cams):
                    first = raw_c.split()[0] if raw_c.split() else ""
                    c["_idx"] = first[1:] if first.startswith("c") else "?"
                events.append(ev)
                continue
            m2 = OTHER_TAGS.search(raw)
            if m2:
                others.append((m2.group(1), m2.group(2).rstrip()))
    return events, others


def fnum(d: dict, key: str, default=None):
    try:
        return float(d[key])
    except (KeyError, ValueError, TypeError):
        return default


# ev=step の理由トークン → 日本語（C# の TakeRunner.StepSkip* と対）。
STEP_SKIP_REASONS = {
    "camrange": "live のカメラが registry の範囲外",
    "noasset": "素材（clip / still）が解決できない",
    "norec": "端末内録画が無い",
}


def effect_samples(events, key: str, t_from: float = None, t_to: float = None):
    """ev=intro / ev=sum が持つ「効果の実在」キーの値列を、時間窓で切って返す。

    このキーを出していないビルドのログでは空リストになる（＝判定を黙って飛ばす）。
    値は文字列のまま返す — `-`（そもそもシーンに居ない）と `0`（居るが出ていない）を
    区別しないと、未配線を「壊れている」と誤診する。
    """
    out = []
    for e in events:
        if e.get("ev") not in ("intro", "sum") or key not in e:
            continue
        t = fnum(e, "t", 0.0)
        if t_from is not None and t < t_from:
            continue
        if t_to is not None and t > t_to:
            continue
        out.append(e[key])
    return out


def expected_from_show(show: dict):
    """show.json から期待値を作る。"""
    exp = {}
    run = show.get("run") or {}
    exp["totalLaps"] = run.get("totalLaps") or 3
    exp["introEnabled"] = bool(run.get("introEnabled", True))
    exp["introMinSec"] = run.get("introMinSec", 20)
    exp["introStartLineId"] = (run.get("intro") or {}).get("startLineId") or ""
    exp["targetSec"] = run.get("targetSec", 180)
    exp["order"] = ((show.get("layout") or {}).get("course") or {}).get("order") or []

    # host が空のカメラは接続しないのが正しい（show.json で未設定＝現場に置いていない）。
    # 判定に混ぜると「受信 0fps」で常に FAIL が出て、本物の不具合が埋もれる。
    exp["activeCams"] = {i for i, c in enumerate(show.get("cameras") or [])
                         if (c.get("host") or "").strip()}

    rec = show.get("record") or {}
    exp["recEnabled"] = bool(rec.get("enabled"))
    exp["recLaps"] = rec.get("laps") or []

    takes = []
    for seg in ((show.get("timeline") or {}).get("segments") or []):
        for t in (seg.get("takes") or []):
            # 開始規則は take 直下にある（C# の ShowTakeDef と同じフラット形）。
            # `start` という入れ子は存在しない — そこを見ると全部 None になり、
            # 「著作されていない」と誤読する。
            takes.append({
                "id": t.get("id"),
                "lap": seg.get("lap"),
                "camera": seg.get("camera"),
                "at": t.get("at") or "enter",
                "offsetSec": t.get("offsetSec") or 0,
                "ifMissed": t.get("ifMissed") or "fireOnExit",
                "lineId": t.get("lineId"),
                "wait": t.get("wait") or "segment",
                "steps": t.get("steps") or [],
            })
    exp["takes"] = takes

    # 3 周目の録画カットが必要とする (周, カメラ)。ここが録れていないと実機は黙ってカットを飛ばす。
    needed = set()
    for t in takes:
        for s in t["steps"]:
            if s.get("source") == "rec":
                needed.add((s.get("recLap") or 1, s.get("camera")))
    exp["recNeeded"] = needed
    exp["config"] = config_from_show(show)
    return exp


def config_from_show(show: dict) -> dict:
    """実機の `ShowControlClient.DescribeConfig()` と**同じ項目**を PC の show.json から作る。

    実機が読むのは端末キャッシュ (persistentDataPath/show_config.json) で、これは APK の焼き込みより
    優先される。だから「ビルドし直した = 設定も新しい」は成り立たない。実測 (2026-07-31) では 2 台の
    Quest のキャッシュで `run.intro.startLineId` が食い違い、導入の始まり方が機ごとに違っていた。
    しかも `timeline.rev` は両方 21 で一致していたので、rev では検出できない。

    設定がずれたまま解析すると「著作した演出が出なかった」と報告されるが、原因はコードではなく設定。
    **項目を足すときは C# の DescribeConfig と対で直すこと**（片方だけだと沈黙して食い違う）。
    既定値も C# 側 (ShowRunDef / ShowIntroDef / ShowRecordDef のフィールド初期値) に合わせてある
    — JsonUtility はキーが無ければ既定値で埋めるので、PC 側で「キーが無い = None」にすると偽の差分が出る。
    """
    tl = show.get("timeline") or {}
    segs = tl.get("segments") or []
    layout = show.get("layout") or {}

    has_run = isinstance(show.get("run"), dict)
    run = show.get("run") or {}
    has_intro = isinstance(run.get("intro"), dict)
    intro = run.get("intro") or {}

    # C#: (_run?.introEnabled ?? true) && (intro == null || intro.enabled)
    intro_on = bool(run.get("introEnabled", True)) and (not has_intro or bool(intro.get("enabled", True)))
    start_line = (intro.get("startLineId") or "").strip() if has_intro else ""

    rec = show.get("record") if isinstance(show.get("record"), dict) else None

    return {
        "tlrev": tl.get("rev", -1),
        "segs": len(segs),
        "takes": sum(len(s.get("takes") or []) for s in segs),
        "cues": len(show.get("cues") or []),
        "cams": len(show.get("cameras") or []),
        "lines": len(layout.get("lines") or []),
        "laps": run.get("totalLaps", 3) if has_run else -1,
        "intro": 1 if intro_on else 0,
        "startLine": start_line or "-",
        "rec": 1 if (rec or {}).get("enabled") else 0,
    }


def analyze(events, others, exp, warns=None):
    rep = []          # レポート行
    verdicts = []     # (level, text) level: OK / WARN / FAIL

    def w(line=""):
        rep.append(line)

    def verdict(level, text):
        verdicts.append((level, text))

    def emit_warnings():
        # [XP] は「こちらが観測しようと決めたもの」しか出さない。実機が自分から言っていることは
        # ここにしか出ない。2026-07-31 の 3 件（シェーダの剥がれ・パススルー未初期化・背景が不透明）は
        # すべてこの節に出ていたのに、人が手で grep する前提だったので誰も見ていなかった。
        w("## 実機ログの警告")
        if warns is None:
            w("  （収集していない）")
        elif not warns:
            w("  警告・エラーは 1 件も無い")
            verdict("OK", "実機ログに警告・エラーが無い")
        else:
            errs = {k: v for k, v in warns.items() if v[0] in ("E", "F")}
            jp = {k: v for k, v in warns.items() if k not in errs and v[2]}
            rest = {k: v for k, v in warns.items() if k not in errs and k not in jp}

            # 重い順 → 日本語優先 → 件数順。数で勝つ engine 由来のノイズを上に置かない。
            def rank(kv):
                return (-LEVEL_RANK.get(kv[1][0], 0), 0 if kv[1][2] else 1, -kv[1][1], kv[0])

            for msg, (lv, n, _jp) in sorted(warns.items(), key=rank):
                mark = {"E": "❌", "F": "❌", "W": "⚠"}.get(lv, "・")
                w(f"  {mark} ×{n}  {msg}")

            def name(d, limit=3):
                # 件数の多い順に名指しする。1 種だけ出すと、頻発する無害なものが本命を隠す
                # （実測: 描画の警告 28 行がパススルー初期化失敗 1 行を押しのけた）。
                top = [m for m, _ in sorted(d.items(), key=lambda kv: -kv[1][1])][:limit]
                return " / ".join(m[:90] for m in top)

            if errs:
                verdict("FAIL", f"実機ログにエラーが {sum(v[1] for v in errs.values())} 行"
                                f"（{len(errs)} 種）: " + name(errs))
            if jp:
                verdict("WARN", f"実機が「見つかりません・失敗・出ません」と言っている"
                                f"（{len(jp)} 種）: " + name(jp))
            if rest:
                verdict("WARN", f"実機ログにほかの警告が {sum(v[1] for v in rest.values())} 行"
                                f"（{len(rest)} 種）— 最多: " + name(rest, 1))
        w()

    sums = [e for e in events if e.get("ev") == "sum"]
    if not events:
        verdict("FAIL", "[XP] 行が 1 つも無い。Development ビルドか、テレメトリの起動を確認する")
        emit_warnings()
        return rep, verdicts

    t_end = fnum(events[-1], "t", 0.0)
    # 体験の終わり。Finished 以降は「その区間に居続けた」のではなく「終わって黒に覆われていた」ので、
    # 滞在の計算はここで打ち切る。打ち切らないと最後の区間が観測時間ぶん膨らみ（実測で 182.7 秒）、
    # 演出のはみ出し判定も卓へ出す実測滞在も、まとめて嘘になる。
    _fin = [fnum(e, "t", 0.0) for e in events if e.get("ev") == "phase" and e.get("v") == "Finished"]
    t_show_end = _fin[0] if _fin else t_end
    w(f"観測時間: {t_end:.0f} 秒 / [XP] 行 {len(events)} 本（うちサマリ {len(sums)}）")
    w()

    # ---------------- 実機が使った設定 ----------------
    # ここが show.json と違えば、以下の演出・周回・録画の判定は**全部あてにならない**。
    # 実機は端末キャッシュを焼き込みより優先して読むので、APK を焼き直しても設定は変わらない。
    w("## 実機が使った設定")
    cfgs = [e for e in events if e.get("ev") == "config"]
    if not cfgs:
        w("  ev=config が無い（この計装より前のビルド）")
        verdict("WARN", "実機がどの設定で走ったか分からない — 以下の演出判定は show.json と"
                        "食い違っている可能性がある")
    else:
        last = cfgs[-1]
        src = last.get("src", "?")
        w(f"  出所: {src}  (live=卓が配った / cache=端末に残っていた / baked=APK 焼き込み)"
          f"  rev={last.get('rev')}")
        diffs = []
        for key, want in (exp.get("config") or {}).items():
            got = last.get(key)
            if got is None:
                continue  # 実機が出していない項目（古いビルド）は責めない
            if str(got) != str(want):
                diffs.append((key, got, want))
        if diffs:
            for k, got, want in diffs:
                w(f"  ! {k}: 実機={got} / show.json={want}")
            verdict("FAIL", "実機の設定が PC の show.json と違う（"
                    + ", ".join(f"{k} {g}!={wv}" for k, g, wv in diffs)
                    + "） — 端末キャッシュを消して配り直す: "
                      "python tools/quest-fleet.py reset-config <serial>")
        else:
            verdict("OK", f"実機は show.json と同じ設定で走った（出所 {src}）")
    w()

    # ---------------- 体験の骨格 ----------------
    w("## 体験の骨格")
    phases = [e for e in events if e.get("ev") == "phase"]
    for p in phases:
        w(f"  t={fnum(p,'t',0):7.1f}  相={p.get('v')}  lap={p.get('lap','-')}  経過={p.get('elapsed','-')}")
    seen = [p.get("v") for p in phases]
    if exp["introEnabled"] and "Intro" not in seen:
        verdict("WARN", "導入相 (Intro) の記録が無い")
    if "Run" not in seen:
        verdict("FAIL", "本編 (Run) に入っていない — 導入が終わっていない")
    else:
        intro_end = next((fnum(p, "t", 0) for p in phases if p.get("v") == "Run"), None)
        if intro_end is not None:
            verdict("OK", f"導入 → 本編へ遷移した（t={intro_end:.0f}s）")
    if "Finished" in seen:
        fin = next(fnum(p, "t", 0) for p in phases if p.get("v") == "Finished")
        verdict("OK", f"体験が終了した（t={fin:.0f}s）")
        if fin > exp["targetSec"] * 1.3:
            verdict("WARN", f"目安 {exp['targetSec']:.0f}s に対し {fin:.0f}s かかった")
    else:
        verdict("FAIL", f"体験が終了していない（{exp['totalLaps']} 周ぶん歩いても Finished が来ていない）")
    w()

    # ---------------- 周回と区間 ----------------
    w("## 周回と区間（ショーの時計）")
    segs = [e for e in events if e.get("ev") == "seg"]
    order = exp["order"]
    seg_seq = []
    for s in segs:
        # ⚠ `fnum(...) or -1` と書くと **カメラ 0 が falsy なので -1 に化ける**。
        # 順路の先頭カメラが常に 0 なので、この 1 文字で判定が全部ずれる。
        lap = int(fnum(s, "lap", -1))
        cam = int(fnum(s, "cam", -1))
        seg_seq.append((fnum(s, "t", 0), lap, cam))
    for t, lap, cam in seg_seq:
        w(f"  t={t:7.1f}  {lap} 周目 / カメラ {cam}")
    laps_seen = sorted({l for _, l, _ in seg_seq if l > 0})
    w(f"  観測した周: {laps_seen}")
    if not laps_seen:
        verdict("FAIL", "区間の進入が 1 度も記録されていない（歩行かゾーン判定が効いていない）")
    else:
        # 周回は進行ポインタ方式で、order[0] へ戻った時点で lap が上がる。だから 3 周を走り切ると
        # 「4 周目に入った」記録が必ず出る。これを素で出すと「3 周のはずが 4 周した」と読めてしまう。
        if max(laps_seen) < exp["totalLaps"]:
            verdict("FAIL", f"{exp['totalLaps']} 周のはずが {max(laps_seen)} 周までしか進んでいない")
        elif max(laps_seen) > exp["totalLaps"]:
            verdict("OK", f"{exp['totalLaps']} 周を完走した"
                          f"（{max(laps_seen)} 周目のスタート区間に入った時点で終了）")
        else:
            verdict("OK", f"{max(laps_seen)} 周まで進んだ")
    # 順路どおりか
    if order:
        bad = []
        for i in range(1, len(seg_seq)):
            prev_cam, cam = seg_seq[i - 1][2], seg_seq[i][2]
            if prev_cam not in order or cam not in order:
                continue
            want = order[(order.index(prev_cam) + 1) % len(order)]
            if cam != want:
                bad.append((seg_seq[i][0], prev_cam, cam, want))
        if bad:
            verdict("WARN", f"順路どおりでない進入が {len(bad)} 回（例: t={bad[0][0]:.0f}s "
                            f"カメラ{bad[0][1]}→{bad[0][2]}、順路では {bad[0][3]}）")

    # 区間ごとの実測滞在
    w()
    w("### 区間ごとの実測滞在")
    dwell = {}
    for i, (t, lap, cam) in enumerate(seg_seq):
        end = seg_seq[i + 1][0] if i + 1 < len(seg_seq) else t_show_end
        if end <= t:
            continue   # 体験が終わった後に入った区間（滞在ゼロ）
        dwell[(lap, cam)] = dwell.get((lap, cam), 0.0) + (end - t)
    for (lap, cam), sec in sorted(dwell.items()):
        w(f"  {lap} 周目 カメラ {cam}: {sec:.1f} 秒")
    w()

    # ---------------- 演出 ----------------
    w("## 演出（著作 vs 実機）")
    takes_ev = [e for e in events if e.get("ev") == "take"]
    began = {}
    for e in takes_ev:
        if e.get("st") == "begin":
            began.setdefault(e.get("id"), []).append(fnum(e, "t", 0))
    ended = defaultdict(list)
    for e in takes_ev:
        if e.get("st") == "end":
            ended[e.get("id")].append(fnum(e, "t", 0))

    for t in exp["takes"]:
        tid = t["id"]
        starts = began.get(tid, [])
        seg_key = (t["lap"], t["camera"])
        stayed = dwell.get(seg_key)
        kinds = "+".join(s.get("source", "?") for s in t["steps"])
        if starts:
            dur = ""
            if ended.get(tid):
                dur = f" / 尺 {ended[tid][0] - starts[0]:.1f}s"
            # 「区間進入からの差」だけを見ると誤診する。著作の開始規則と突き合わせる。
            seg_t = next((s for s, lap, cam in seg_seq
                          if lap == t["lap"] and cam == t["camera"]), None)
            how = ""
            if seg_t is not None:
                delay = starts[0] - seg_t
                if t["at"] == "line":
                    how = (f" / ライン {t['lineId']} を通って +{delay:.1f}s"
                           if delay < 1.0 else
                           f" / 進入 +{delay:.1f}s（ラインを通らず離脱時に決着＝ifMissed どおり）")
                else:
                    want = float(t["offsetSec"] or 0)
                    how = (f" / 進入 +{delay:.1f}s（著作 +{want:.1f}s）"
                           + ("" if abs(delay - want) <= 1.5 else " ⚠ ずれている"))
            w(f"  ✅ {tid} ({t['lap']}周 cam{t['camera']} {t['at']}) 出た t={starts[0]:.0f}s{dur}{how} [{kinds}]")
        else:
            reason = ""
            if stayed is None:
                reason = " — その区間に一度も入っていない"
            elif t["at"] == "line":
                reason = f" — ライン {t['lineId']} を通っていない可能性（滞在 {stayed:.0f}s）"
            else:
                reason = f" — 区間には {stayed:.0f}s 居たのに出ていない"
            w(f"  ❌ {tid} ({t['lap']}周 cam{t['camera']} {t['at']}) 出ていない{reason} [{kinds}]")
            verdict("FAIL", f"演出 {tid} が出なかった{reason}")
    if exp["takes"] and all(began.get(t["id"]) for t in exp["takes"]):
        verdict("OK", f"著作された演出 {len(exp['takes'])} 本すべてが出た")

    # 演出が区間の滞在に収まったか。はみ出す＝体験者が次の場所へ移った後も画面を握り続ける。
    # 設計上は許される（復帰先は「いま居るゾーン」を再計算する）が、著作の意図とはずれるので出す。
    w()
    w("### 演出の尺と区間の滞在")
    for t in exp["takes"]:
        tid = t["id"]
        if not began.get(tid) or not ended.get(tid):
            continue
        start, end = began[tid][0], ended[tid][0]
        dur = end - start
        # この演出が属する区間の終わり（次の seg の時刻）
        seg_start = next((s for s, lap, cam in seg_seq
                          if lap == t["lap"] and cam == t["camera"]), None)
        if seg_start is None:
            continue
        seg_end = next((s for s, _, _ in seg_seq if s > seg_start), t_show_end)
        over = end - seg_end
        stay = seg_end - seg_start
        # ⚠ 離脱の瞬間に決着した演出（at=exit / ifMissed=fireOnExit）は、**契約上そもそも区間を出てから走る**。
        #    これを「滞在をはみ出した」と呼ぶと、仕様どおりの動作を毎回 WARN で報告することになる
        #    （実際に前セッションが同じ行を読んで「2 周目 A の演出が壊れている」と誤診した）。
        #    著作の調整が要るのは「区間の中で始まったのに終わりきらなかった」場合だけ。
        if start >= seg_end - 0.05:
            w(f"  ✅ {tid}: 尺 {dur:.1f}s — 離脱の瞬間に決着（区間外で走るのが契約）")
        elif over > 0.3:
            w(f"  ⚠ {tid}: 尺 {dur:.1f}s / 滞在 {stay:.1f}s — {over:.1f}s はみ出した")
            verdict("WARN", f"演出 {tid} が区間の滞在を {over:.1f}s 超えた"
                            f"（尺 {dur:.1f}s / 滞在 {stay:.1f}s）— 速く歩く体験者では途中で場所が変わる")
        else:
            w(f"  ✅ {tid}: 尺 {dur:.1f}s / 滞在 {stay:.1f}s（余裕 {-over:.1f}s）")

    drops = [ln for tag, ln in others if tag == "TakeRunner" and ("出ないまま" in ln or "drop" in ln.lower())]
    if drops:
        w()
        w("### 演出が捨てられた記録")
        for d in drops[:20]:
            w(f"  {d}")
        verdict("WARN", f"演出の drop が {len(drops)} 件（TakeRunner の警告）")
    w()

    # ---------------- 端末内録画 ----------------
    w("## 端末内録画（3 周目の素材）")
    rec_ev = [e for e in events if e.get("ev") == "rec"]
    rec_lines = [ln for tag, ln in others if tag == "SegmentRecorder"]
    for ln in rec_lines[:40]:
        w(f"  {ln}")
    if not exp["recEnabled"]:
        w("  （show.json で録画は無効）")
    else:
        # 区間の切れ目は同一フレームで stop→start になることがあり、テレメトリのポーリングでは
        # 1 回に見える。区間数はレコーダ自身のログ（区間ごとに 1 行）を正とする。
        started = [ln for ln in rec_lines if "録画開始" in ln]
        polled = [e for e in rec_ev if e.get("v") == "start"]
        w(f"  録画した区間 {len(started)} / 期待する区間 {sorted(exp['recNeeded'])}")
        if not started and not polled:
            verdict("FAIL", "録画が 1 度も始まっていない — 3 周目の録画カットは実機で黙って飛ぶ")
        else:
            verdict("OK", f"録画が {max(len(started), len(polled))} 区間で走った")
        # 実際に録れた区間はレコーダのログにしか出ないので、そちらを頼りに突き合わせる
        for lap, cam in sorted(exp["recNeeded"]):
            hit = any(f"L{lap}C{cam}" in ln for ln in rec_lines)
            if not hit:
                verdict("WARN", f"{lap} 周目 カメラ {cam} の録画が確認できない"
                                f"（3 周目のこのカットは飛ぶ可能性）")
    w()

    # ---------------- 映像の安定性 ----------------
    w("## 映像の安定性")
    per_cam = defaultdict(lambda: {"rx": [], "tx": [], "jit": [], "dec": [], "age": [], "drop": 0, "off": 0, "n": 0})
    for s in sums:
        for c in s["_cams"]:
            idx = c.get("_idx", "?")
            d = per_cam[idx]
            d["n"] += 1
            for k in ("rx", "tx", "jit", "dec", "age"):
                v = fnum(c, k)
                if v is not None:
                    d[k].append(v)
            dv = fnum(c, "drop", 0)
            if dv:
                d["drop"] = max(d["drop"], dv)
            if c.get("con") == "0":
                d["off"] += 1

    def stats(vals):
        if not vals:
            return "-"
        vs = sorted(vals)
        return (f"平均 {sum(vs)/len(vs):5.1f} / 最小 {vs[0]:5.1f} / "
                f"下位5% {vs[max(0,int(len(vs)*0.05))]:5.1f} / 最大 {vs[-1]:5.1f}")

    for idx in sorted(per_cam):
        d = per_cam[idx]
        try:
            active = int(idx) in exp["activeCams"]
        except ValueError:
            active = True
        if not active:
            w(f"  カメラ {idx}: show.json で host 未設定（接続しないのが正しい）")
            continue
        w(f"  カメラ {idx}:")
        w(f"    受信 fps  {stats(d['rx'])}")
        w(f"    配信 fps  {stats(d['tx'])}")
        w(f"    到着の揺らぎ ms {stats(d['jit'])}")
        w(f"    展開 ms   {stats(d['dec'])}")
        w(f"    配信側の鮮度 ms {stats(d['age'])}")
        w(f"    取りこぼし累計 {d['drop']:.0f} / 未接続だったサマリ {d['off']}/{d['n']}")
        if d["rx"]:
            lowest = min(d["rx"])
            avg = sum(d["rx"]) / len(d["rx"])
            if avg < 12:
                verdict("FAIL", f"カメラ {idx} の受信 fps が平均 {avg:.1f} — 体験に耐えない")
            elif lowest < 5:
                verdict("WARN", f"カメラ {idx} の受信 fps が一時 {lowest:.1f} まで落ちた")
            else:
                verdict("OK", f"カメラ {idx} の受信 fps 平均 {avg:.1f}（最小 {lowest:.1f}）")
        if d["off"]:
            verdict("WARN", f"カメラ {idx} が {d['off']}/{d['n']} 回のサマリで未接続だった")

    recon = [ln for tag, ln in others if tag == "CameraStream" and ("reconnect" in ln or "detected" in ln)]
    if recon:
        w()
        w("### 再接続")
        for ln in recon[:25]:
            w(f"  {ln}")
        verdict("WARN" if len(recon) > 2 else "OK", f"MJPEG の再接続が {len(recon)} 回")
    w()

    # ---------------- 砂嵐 ----------------
    w("## 砂嵐（信号ロスト）")
    storms = [e for e in events if e.get("ev") == "storm"]
    spans, open_t = [], None
    for e in storms:
        if e.get("v") == "on":
            open_t = (fnum(e, "t", 0), e.get("cam"))
        elif open_t is not None:
            spans.append((open_t[0], fnum(e, "t", 0) - open_t[0], open_t[1]))
            open_t = None
    if open_t is not None:
        spans.append((open_t[0], t_end - open_t[0], open_t[1]))
    for t, dur, cam in spans:
        w(f"  t={t:7.1f}  {dur:5.1f} 秒  カメラ {cam}")
    last_pct = fnum(sums[-1], "stormPct", 0.0) if sums else 0.0
    weak_pct = fnum(sums[-1], "weakPct", 0.0) if sums else 0.0
    w(f"  強い砂嵐の累計 {last_pct:.1f}% / 弱い砂嵐（トラッキング明け） {weak_pct:.1f}%")
    w(f"  発生回数 {len(spans)}")
    if last_pct >= 10:
        verdict("FAIL", f"体験時間の {last_pct:.1f}% が砂嵐 — 多すぎる")
    elif last_pct >= 3:
        verdict("WARN", f"体験時間の {last_pct:.1f}% が砂嵐（{len(spans)} 回）")
    else:
        verdict("OK", f"砂嵐は体験時間の {last_pct:.1f}%（{len(spans)} 回）")
    long_spans = [s for s in spans if s[1] >= 2.0]
    if long_spans:
        verdict("WARN", f"2 秒以上続いた砂嵐が {len(long_spans)} 回（最長 {max(s[1] for s in long_spans):.1f}s）")
    w()

    # ---------------- 表示 fps ----------------
    w("## 表示（VR の快適性）")
    fps = [fnum(s, "fps") for s in sums if fnum(s, "fps") is not None]
    worst = [fnum(s, "worst") for s in sums if fnum(s, "worst") is not None]
    if fps:
        w(f"  アプリ fps {stats(fps)}")
        w(f"  最悪フレーム時間 ms {stats(worst)}")
        avg = sum(fps) / len(fps)
        if avg < 60:
            verdict("FAIL", f"表示 fps が平均 {avg:.1f} — VR として破綻している")
        elif avg < 80:
            verdict("WARN", f"表示 fps が平均 {avg:.1f}（90 を目標）")
        else:
            verdict("OK", f"表示 fps 平均 {avg:.1f}")
        if worst and max(worst) > 100:
            verdict("WARN", f"最悪フレーム時間が {max(worst):.0f}ms — 引っかかりが体感される")
    w()

    # ---------------- 導入演出 ----------------
    w("## 導入演出")
    line_id = exp.get("introStartLineId") or ""
    w(f"  開始の合図: {'通過ライン ' + line_id if line_id else '開始位置の円'}")
    for tag, ln in others:
        if tag == "XPWalk" and ("開始ライン" in ln or "開始位置へ" in ln):
            w(f"  {ln}")
    intro = [e for e in events if e.get("ev") == "intro"]
    for e in intro:
        w(f"  t={fnum(e,'t',0):7.1f}  段={e.get('stage')} pass={e.get('pass')} live={e.get('live')} frame={e.get('frame')}")
    stages = [e.get("stage") for e in intro]
    if exp["introEnabled"]:
        if not intro:
            verdict("FAIL", "導入演出の段が 1 つも記録されていない（IntroDirector 未配線か enabled=false）")
        else:
            for want in ("Real", "Degrade", "Structure", "Frame", "Swap"):
                if want not in stages:
                    verdict("WARN", f"導入演出の段 {want} が出ていない")
            if "Swap" in stages:
                verdict("OK", "導入演出が最後の段（Swap）まで進んだ")
    w()

    # ---------------- 効果の実在 ----------------
    # 「段が進んだ」「演出が走った」は、画・音に何かが出たことを意味しない。
    # 2026-07-31 に FAIL ゼロ・演出 7 本 OK と判定した走行の画を録ったら、導入演出が 1 段も
    # 出ていなかった（パススルー未初期化 / シェーダのビルド剥がれ / カメラ背景が不透明）。
    # ここは「その結果、画・音に何かが出たか」だけを見る節。
    w("## 効果の実在（画・音に出たか）")
    any_effect_key = False

    # -- 起動時に焼かれている前提（コードを直しても走行のたびには変わらない層）
    boot = next((e for e in events if e.get("ev") == "boot"), None)
    if boot and "bg" in boot:
        any_effect_key = True
        bg = str(boot["bg"])
        try:
            clear_n, total_n = (int(x) for x in bg.split("/", 1))
        except ValueError:
            clear_n = total_n = -1
        # ⚠ **期待するのは「不透明」**。パススルーの穴を開けるのは覆い (IntroVeil) の仕事で、
        #    カメラの背景ではない。背景を透明にすると `Blend Zero SrcAlpha`（乗算）が alpha を
        #    1 へ戻せなくなり、**段 4 で枠の外を黒く閉じる演出が原理的に出ない**
        #    （2026-07-31 に一度 a=0 にして同日戻した。契約は .claude/rules/meta-xr.md）。
        w(f"  カメラ背景が透明なもの: {bg}（透明/全体・**0 が正常**）")
        if total_n > 0 and clear_n > 0:
            verdict("FAIL", f"カメラ背景が透明なカメラが {clear_n} 台ある（{bg}）— "
                            "枠の外を黒く閉じる演出が原理的に出ない（乗算ブレンドは alpha を戻せない）。"
                            "MainDemoSceneSetup は (0,0,0,1) を焼くので Setup Main Demo Scene を再実行する")
        elif total_n > 0:
            verdict("OK", f"カメラ背景は全て不透明（{bg}）— 覆いが穴を開ける方式で正しい")
    if boot and "font" in boot:
        any_effect_key = True
        font_ok = str(boot["font"]) == "1"
        w(f"  日本語フォント: {'解決できた' if font_ok else '解決できていない'}")
        if not font_ok:
            verdict("WARN", "日本語フォントが解決できていない（HMD 内の文字が豆腐になる）— "
                            "Tools/FixedCamVr/Setup/Generate Japanese HUD Font を再実行する")

    # -- 導入演出（覆い・パススルー・構造の線）
    # 段 Real 以降 → 本編へ入るまでを窓にする。それ以前は「まだ出さないのが正しい」。
    real_t = next((fnum(e, "t", 0.0) for e in intro if e.get("stage") == "Real"), None)
    run_t = next((fnum(p, "t", 0.0) for p in phases if p.get("v") == "Run"), None)

    built = effect_samples(events, "veilBuilt")
    if built:
        any_effect_key = True
        if "0" in built:
            verdict("FAIL", "導入の覆いを組めていない（IntroVeil の Shader.Find が null）— "
                            "シェーダがビルドから剥がれた疑い。GraphicsSettings の "
                            "m_AlwaysIncludedShaders に FixedCamVr/IntroVeil を入れる")
        elif "1" in built:
            w("  導入の覆い: 実体を組めている")

    if exp["introEnabled"] and real_t is not None:
        veil = effect_samples(events, "veil", t_from=real_t, t_to=run_t)
        if veil:
            any_effect_key = True
            w(f"  導入の覆いが描画された標本: {veil.count('1')}/{len(veil)}")
            if "1" not in veil and "-" not in veil:
                verdict("FAIL", "導入の覆いが一度も描画されていない（段は進んでいるのに画には何も出ていない）"
                                "— シェーダがビルドで剥がれた疑い")
            elif "1" in veil:
                verdict("OK", f"導入の覆いが描画された（{veil.count('1')}/{len(veil)} 標本）")

        pt = effect_samples(events, "pt", t_from=real_t, t_to=run_t)
        if pt:
            any_effect_key = True
            w(f"  パススルーが有効だった標本: {pt.count('1')}/{len(pt)}")
            if "1" not in pt and "-" not in pt:
                verdict("FAIL", "パススルーがアプリから有効化できていない（導入の主役は現実の映像なので"
                                "演出が丸ごと死ぬ）— OculusProjectConfig の _insightPassthroughSupport を "
                                "1 (Supported) にする")
            elif "1" in pt:
                verdict("OK", "パススルーがアプリから有効化できていた")

    if "Structure" in stages:
        wires = [v for v in effect_samples(events, "wire") if v not in ("", "-")]
        nums = []
        for v in wires:
            try:
                nums.append(int(v))
            except ValueError:
                pass
        if nums:
            any_effect_key = True
            w(f"  構造の線: 最大 {max(nums)} 本")
            if max(nums) == 0:
                verdict("WARN", "段 Structure に達したのに構造の線が 1 本も引けていない — "
                                "位置合わせの現地検証（線が実物に重なるか）ができない。"
                                "卓の 🧱 部屋 で壁を引くか、較正パネルで床の実寸を入れる")
            else:
                verdict("OK", f"構造の線が {max(nums)} 本引けた")

    # -- 演出のカット（画面を取ったか / 飛ばされたか）
    steps_ev = [e for e in events if e.get("ev") == "step"]
    if steps_ev:
        any_effect_key = True
        w()
        w("### 演出のカット（画面を取ったか）")
        by_take = defaultdict(lambda: {"played": 0, "skipped": 0, "why": defaultdict(int)})
        skipped_why = defaultdict(int)
        for e in steps_ev:
            d = by_take[e.get("take", "?")]
            if e.get("played") == "1":
                d["played"] += 1
            else:
                d["skipped"] += 1
                why = e.get("why", "?")
                d["why"][why] += 1
                skipped_why[why] += 1
        for tid in sorted(by_take):
            d = by_take[tid]
            detail = ""
            if d["why"]:
                detail = "（" + " / ".join(
                    f"{STEP_SKIP_REASONS.get(k, k)} {n}" for k, n in sorted(d["why"].items())) + "）"
            mark = "✅" if d["played"] else "❌"
            w(f"  {mark} {tid}: 出たカット {d['played']} / 飛ばしたカット {d['skipped']}{detail}")
            if d["played"] == 0:
                verdict("FAIL", f"演出 {tid} は走ったが画面に何も出していない"
                                f"（全 {d['skipped']} カットが飛んだ）{detail}")
        for why, n in sorted(skipped_why.items()):
            verdict("WARN", f"飛ばされたカットが {n} 件: {STEP_SKIP_REASONS.get(why, why)}")
        if not skipped_why:
            verdict("OK", f"演出のカットは全て画面を取った（{len(steps_ev)} カット）")

    # -- 端末内録画が 0 バイトで閉じていないか
    zero_rec = [e for e in rec_ev if e.get("v") == "stop" and fnum(e, "bytes") == 0]
    if any("bytes" in e for e in rec_ev):
        any_effect_key = True
        if zero_rec:
            w()
            w("### 0 バイトで閉じた録画")
            for e in zero_rec:
                w(f"  t={fnum(e,'t',0):7.1f}  {e.get('lap','?')} 周目 / カメラ {e.get('cam','?')}")
                verdict("FAIL", f"録画が 0 バイトで閉じた（{e.get('lap','?')} 周目 カメラ {e.get('cam','?')}）"
                                "— この区間を指す録画カットは実機で黙って飛ぶ")

    # -- BGM（クリップ取得に失敗すると BgmDirector は無音のまま黙って戻る）
    bgm_vals = [e["bgm"] for e in sums if "bgm" in e]
    bgm_ev = [e for e in events if e.get("ev") == "bgm"]
    if bgm_vals:
        any_effect_key = True
        played = bgm_vals.count("1")
        w()
        w(f"  BGM が鳴っていた標本: {played}/{len(bgm_vals)}")
        tracks = sorted({e.get("trk", "-") for e in bgm_ev if e.get("play") == "1"})
        if tracks:
            w(f"  鳴ったトラック: {', '.join(tracks)}")
        if played == 0 and not tracks:
            verdict("WARN", "BGM が体験中に一度も鳴っていない（クリップの取得に失敗している可能性）")
        elif played:
            verdict("OK", f"BGM が鳴っていた（{played}/{len(bgm_vals)} 標本）")

    if not any_effect_key:
        w("  効果の実在を出すキーが 1 つも無い（この計装より前のビルドのログ）")
        verdict("WARN", "「効果の実在」を観測していないビルドのログ — 段が進んだことしか分からない。"
                        "画が出たかは tools/quest-record.py で確かめる")
    w()

    # ---------------- 実機ログの警告 ----------------
    emit_warnings()

    # ---------------- 位置合わせ ----------------
    reg = [s for s in sums if "reg" in s]
    if reg and reg[-1].get("reg") == "0":
        verdict("FAIL", "位置合わせが未登録 — 開始位置の判定と CG が働かない")
    w()

    return rep, verdicts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--show", default="tools/web-compositor/show.json")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    events, others = load_events(args.log)
    warns = load_warnings(args.log)
    show = {}
    if os.path.exists(args.show):
        with open(args.show, "r", encoding="utf-8") as fh:
            show = json.load(fh)
    exp = expected_from_show(show)

    rep, verdicts = analyze(events, others, exp, warns)

    out = args.out or os.path.splitext(args.log)[0] + "-report.md"
    order = {"FAIL": 0, "WARN": 1, "OK": 2}
    verdicts.sort(key=lambda v: order.get(v[0], 3))
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# 実機テスト所見 — {os.path.basename(args.log)}\n\n")
        fh.write("## 判定\n\n")
        for lv, tx in verdicts:
            mark = {"FAIL": "❌", "WARN": "⚠", "OK": "✅"}.get(lv, "・")
            fh.write(f"- {mark} {tx}\n")
        fh.write("\n---\n\n")
        fh.write("\n".join(rep))
        fh.write("\n")

    n_fail = sum(1 for lv, _ in verdicts if lv == "FAIL")
    n_warn = sum(1 for lv, _ in verdicts if lv == "WARN")
    n_ok = sum(1 for lv, _ in verdicts if lv == "OK")
    print(f"events={len(events)} other={len(others)} logwarn={len(warns)} "
          f"FAIL={n_fail} WARN={n_warn} OK={n_ok}")
    print(f"report -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
