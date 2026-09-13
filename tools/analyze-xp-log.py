#!/usr/bin/env python3
"""実機 logcat の [XP] 行を読んで「体験が著作どおりに起きたか」を判定する。

使い方:
    py -3.11 tools/analyze-xp-log.py <logcat.txt> [--show tools/web-compositor/show.json]
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

# 目の視界ジャックの終わり方（canon/LEDGER.md 0099）。done / cut が体験者 2 通りの分岐そのもの。
JACK_END_WHY = {
    "done": "写真が尽きた（足を止めた体験者 → 目も閉じる）",
    "cut": "区間を出た（歩き続ける体験者 → 目は流しきる）",
    "abort": "中止（ラン開始 / 卓の中止 / 位置合わせ）",
    "wd": "⚠ 安全網で打ち切り",
}


def fstr(v):
    """観測値を float にする。`-`（シーンに居ない）等は None で返す。"""
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def _pos(v, default):
    """0 以下・未指定はコード既定へ倒す（show.json の秒指定で C# と同じ判定をする）。"""
    f = fstr(v)
    return f if f is not None and f > 0 else default


# カメラ切替の変種の本数（`SwitchAudioCue.DefaultVariantCount` /
# `ingest-sounds.py` の `SWITCH_VARIANTS` の写し・`canon/LEDGER.md` 0112）。
# ⚠ **3 か所を対で直す。** 片方だけ増やすと「焼いたのに FAIL」か「減ったのに OK」になる。
SWITCH_VARIANTS_WANT = 6


def _sw_played(sw_n) -> int:
    """切替音の累計（数に読めなければ 0）。**変種の判定は 2 発以上鳴ってからでないと出せない。**"""
    if not sw_n:
        return 0
    try:
        return int(str(sw_n[-1]))
    except ValueError:
        return 0


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


def timed_samples(events, key: str):
    """<see cref="effect_samples"/> と同じだが **(時刻, 値) の組**で返す。

    区間ごとに切って見たいものに要る（例: 増えてよいのは 3 周目 C だけ、という判定）。
    ⚠ 数にならない値（`-` / `nc`）は落とす — 「居ない」を「0」と読むと未配線を壊れと誤診する。
    """
    out = []
    for e in events:
        if e.get("ev") not in ("intro", "sum") or key not in e:
            continue
        v = fnum(e, key)
        if v is None:
            continue
        out.append((fnum(e, "t", 0.0), v))
    return out


def expected_from_show(show: dict):
    """show.json から期待値を作る。"""
    exp = {}
    run = show.get("run") or {}
    exp["totalLaps"] = run.get("totalLaps") or 3
    exp["introEnabled"] = bool(run.get("introEnabled", True))
    exp["introMinSec"] = run.get("introMinSec", 20)
    exp["introStartLineId"] = (run.get("intro") or {}).get("startLineId") or ""
    # 段 3 の構造の線は既定 false（2026-08-01）。出す設定のときだけ「引けたか」を判定する。
    # 既定を true 側に取ると、線を出さない現行の著作で毎回 WARN が出て報告が信用されなくなる。
    # ⚠ `run.intro.showRoomWire` / `showCameraMarks` は 2026-08-13 に画へ出なくなった
    #    （線を出していた段 3「構造」を廃止した）。判定からも外してある。
    exp["targetSec"] = run.get("targetSec", 180)
    exp["order"] = ((show.get("layout") or {}).get("course") or {}).get("order") or []

    # host が空のカメラは接続しないのが正しい（show.json で未設定＝現場に置いていない）。
    # 判定に混ぜると「受信 0fps」で常に FAIL が出て、本物の不具合が埋もれる。
    exp["activeCams"] = {i for i, c in enumerate(show.get("cameras") or [])
                         if (c.get("host") or "").strip()}

    rec = show.get("record") or {}
    exp["recEnabled"] = bool(rec.get("enabled"))
    exp["recLaps"] = rec.get("laps") or []
    # 録画 1 本の尺 = 切り替えの tailSec 秒前 〜 postSec 秒後。0 以下・未指定はコード既定へ倒す
    # （C# ShowRecordDef.TailSec / PostSec・卓 record-model.js と同じ判定）。
    exp["recTailSec"] = _pos(rec.get("tailSec"), 3.0)
    exp["recPostSec"] = _pos(rec.get("postSec"), 2.0)
    # 録り始めの線（空 = 末尾方式）。C# の ShowRecordDef.startLineId と対。
    exp["recStartLineId"] = (rec.get("startLineId") or "").strip()

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
                # 体験者が異変を報告したら畳まれる演出か（canon/LEDGER.md 0050）。
                # 既定 false ＝ 消えない（JsonUtility の 0 埋めと同じ側へ倒してある）。
                "dismissible": bool(t.get("dismissible")),
                "steps": t.get("steps") or [],
            })
    exp["takes"] = takes
    # 終幕の合図が指す演出は、旗が立っていても実機が落とす（畳むと終幕が早撃ちされる）。
    exp["outroAnchorId"] = ((run.get("outro") or {}).get("afterTakeId") or "").strip()

    # 3 周目の録画カットが必要とする (周, カメラ)。ここが録れていないと実機は黙ってカットを飛ばす。
    needed = set()
    for t in takes:
        for s in t["steps"]:
            if s.get("source") == "rec":
                needed.add((s.get("recLap") or 1, s.get("camera")))
    exp["recNeeded"] = needed

    # 警告つきの切替音が鳴るカット（カットの switchSfx・canon/LEDGER.md 0106）。
    # ⚠ 音は録画に映らないので、期待値の出どころはここだけ。
    exp["switchSfxSteps"] = sum(1 for t in takes for s in t["steps"] if s.get("switchSfx"))

    # 人形の呼びかけが鳴るカット（カットの dollCall・canon/LEDGER.md 0109）。
    # ⚠ 同上。画にも動画にも差が出ないので、期待値の出どころはここだけ。
    exp["dollCallSteps"] = sum(1 for t in takes for s in t["steps"] if s.get("dollCall"))

    # カットが差し替える劇伴（カットの bgm・canon/LEDGER.md 0119）。
    # ⚠ **これも音なので録画に映らない。** `ev=bgm trk=` が唯一の証拠。
    #   演出の bgm（占有・終われば戻る）とは別物なので混ぜない — こちらは鳴りっぱなしになる。
    # 入れ替わりのノイズを使うカット（カットの transition:"swap"）。
    # ⚠ **本数を決め打ちにしない**（2026-08-23・0119 で 2 → 1 になった）。著作から数える。
    exp["swapSteps"] = sum(1 for t in takes for s in t["steps"]
                           if (s.get("transition") or "") == "swap")

    exp["stepBgmTracks"] = [
        s["bgm"].get("trackId", "")
        for t in takes for s in t["steps"]
        if s.get("hasBgm") and isinstance(s.get("bgm"), dict)
        and s["bgm"].get("action") == "play" and s["bgm"].get("trackId")
    ]

    # 録るべき区間（record.laps × course.order）。1 つでも欠けると、それを指す録画カットは
    # 実機で無言に飛ぶ。recNeeded（要求する側）と対で見ると「録り忘れ」と「使い忘れ」を切り分けられる。
    exp["recShould"] = ({(lap, cam) for lap in exp["recLaps"] for cam in exp["order"]}
                        if exp["recEnabled"] else set())

    exp["config"] = config_from_show(show)
    return exp


def is_segment_reachable(lap, camera, total_laps, order):
    """区間 (lap, camera) が体験中に踏まれうるか。

    ⚠ **同じ式が 3 箇所にある。** ここ / C# の `ShowRunReach.IsSegmentReachable` /
    卓の `run-model.mjs` の `isSegmentReachable`。片方だけ直すと沈黙して食い違うので、
    期待値を 3 者のテストにハードコードして突き合わせてある。

    周回は進行ポインタ方式で `order[0]` へ戻った時に上がるので、`lap = totalLaps + 1` の
    `order[0]`（＝帰りの A）は構造的に必ず踏む。体験はそこで終わる。
    """
    if lap is None or camera is None or lap < 1:
        return False
    if not total_laps or total_laps < 1:
        total_laps = 3
    if lap <= total_laps:
        return True
    if lap != total_laps + 1:
        return False
    if not order:
        return True          # 順路が未著作なら判定できない。到達可能側に倒す
    return camera == order[0]


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
        # 録る周。ここがずれていると録画カットが全部無言で飛ぶ（録画はラン中にしか作れないので、
        # 後から気づいても取り返せない）。C# の DescribeConfig と対。
        "recLaps": (",".join(str(l) for l in ((rec or {}).get("laps") or []))
                    if (rec or {}).get("enabled") and ((rec or {}).get("laps") or []) else "-"),
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
            # XR の描画が立ち上がるまでの数秒、URP が視錐台の外の点を投影しようとして大量に吐く。
            # 発生源は UniversalRenderPipeline → XRSystem → XRLayout（**Unity 内部**）で、
            # アプリのコードでは直せず、体験にも影響しない（実測 2026-08-02: 起動から 4.3 秒に閉じ、
            # 472 行）。ここを FAIL のままにすると毎回赤くなり、**本物のエラーが埋もれる**。
            # 無視はせず別枠で数える（後半に出るようになったら行数の変化で気づける）。
            boot_noise = {k: v for k, v in warns.items()
                          if v[0] in ("E", "F")
                          and ("out of view frustum" in k or "Runtime/Camera/Camera.cpp" in k)}
            errs = {k: v for k, v in warns.items() if v[0] in ("E", "F") and k not in boot_noise}
            jp = {k: v for k, v in warns.items() if k not in errs and k not in boot_noise and v[2]}
            rest = {k: v for k, v in warns.items()
                    if k not in errs and k not in jp and k not in boot_noise}

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
            if boot_noise:
                verdict("WARN", f"XR の起動時に URP が視錐台の外を投影した {sum(v[1] for v in boot_noise.values())} 行"
                                f"（Unity 内部・体験には影響しない）")
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

    # ⚠ **ログの先頭が落ちていないか先に見る。**
    # logcat はリングバッファなので、他の行がうるさいと走行の前半が押し流される。実測（2026-08-02）では
    # host が空のカメラへの /info 失敗が毎秒 1 行・計 359 行出て、`ev=boot` / `ev=config` / 導入の記録が
    # 丸ごと消えた。それを「起きなかった」と読むと **導入が動いていないと誤診する**
    # （visual-verification §8「撮れていないと出ていないを混同しない」と同じ罠）。
    truncated = not any(e.get("ev") == "boot" for e in events)
    if truncated:
        w()
        w("⚠ **ログの先頭が落ちている**（ev=boot が無い）。起動・設定・導入の判定は保留する。")
        w("   原因はたいてい logcat のリングバッファ溢れ。実機ログの警告の行数を見ること。")
        verdict("WARN", "ログの先頭が落ちている（ev=boot なし）— 導入と設定については"
                        "「記録が無い」だけで、起きなかった証拠にはならない")
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
                      "py -3.11 tools/quest-fleet.py reset-config <serial>")
        else:
            verdict("OK", f"実機は show.json と同じ設定で走った（出所 {src}）")
    w()

    # ---------------- 体験の骨格 ----------------
    w("## 体験の骨格")
    phases = [e for e in events if e.get("ev") == "phase"]
    for p in phases:
        w(f"  t={fnum(p,'t',0):7.1f}  相={p.get('v')}  lap={p.get('lap','-')}  経過={p.get('elapsed','-')}")
    seen = [p.get("v") for p in phases]
    # ログの先頭が落ちているときは「相の記録が無い」を欠落として扱わない（そこは観測できていない）。
    # 区間が進んでいる＝本編に入った証拠なので、そちらを見る。
    ran = truncated and any(e.get("ev") == "seg" for e in events)
    if exp["introEnabled"] and "Intro" not in seen and not truncated:
        verdict("WARN", "導入相 (Intro) の記録が無い")
    if "Run" not in seen and ran:
        verdict("WARN", "本編に入った記録が落ちている（区間は進んでいるので走ってはいる）")
    elif "Run" not in seen:
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
    # ⚠⚠ **周は 2 つある**（2026-08-17）。
    #   `lap`  = 区間の周（逆走で戻る）＝ 演出・録画・post / BGM の区間キー
    #   `plap` = 進行の周（単調増加）  ＝ 体験の終了判定
    # 「3 周走ったか」は plap で見る。lap で見ると、最後に引き返した体験者を
    # 「周回が足りない」と誤判定する。plap を出さない旧ログでは lap で代用する。
    seg_prog = []
    for s in segs:
        # ⚠ `fnum(...) or -1` と書くと **カメラ 0 が falsy なので -1 に化ける**。
        # 順路の先頭カメラが常に 0 なので、この 1 文字で判定が全部ずれる。
        lap = int(fnum(s, "lap", -1))
        cam = int(fnum(s, "cam", -1))
        plap = int(fnum(s, "plap", lap))
        seg_seq.append((fnum(s, "t", 0), lap, cam))
        seg_prog.append(plap)
    for (t, lap, cam), plap in zip(seg_seq, seg_prog):
        back = "  ← 引き返し（進行は %d 周目）" % plap if plap != lap else ""
        w(f"  t={t:7.1f}  {lap} 周目 / カメラ {cam}{back}")
    laps_seen = sorted({l for l in seg_prog if l > 0})
    w(f"  観測した周（進行）: {laps_seen}")
    if not laps_seen:
        verdict("FAIL", "区間の進入が 1 度も記録されていない（歩行かゾーン判定が効いていない）")
    else:
        # 周回は進行ポインタ方式で、order[0] へ戻った時点で lap が上がる。だから 3 周を走り切ると
        # 「4 周目に入った」記録が必ず出る。これを素で出すと「3 周のはずが 4 周した」と読めてしまう。
        if max(laps_seen) < exp["totalLaps"]:
            verdict("FAIL", f"{exp['totalLaps']} 周のはずが {max(laps_seen)} 周までしか進んでいない")
        elif max(laps_seen) > exp["totalLaps"]:
            verdict("OK", f"{exp['totalLaps']} 周を完走して帰りのスタート区間まで来た"
                          f"（観測 {max(laps_seen)} 周目 = 帰りの区間）")
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

    # ---- 引き返しと再演（2026-08-17）----
    # 体験者が後ろのカメラへ戻ると、区間キーは「前にそこに居たときの周」へ戻る。
    # そこで途中で切れていた演出は、報告していなければ頭から出し直す。
    #   ⚠ **引き返したこと**（lap ≠ plap）と **再演されたこと**（reN）は別物。
    #     片方だけ見ても「効いたか」は分からない — 引き返していないのに 0 なのは正常で、
    #     引き返したのに 0 なら（報告済みでない限り）機構が効いていない。
    back_steps = [(t, lap, cam, plap)
                  for (t, lap, cam), plap in zip(seg_seq, seg_prog) if plap != lap]
    replay_n = [int(v) for v in effect_samples(events, "reN") if str(v).lstrip("-").isdigit()]
    replays = max(replay_n) if replay_n else 0
    if back_steps or replays:
        w()
        w("### 引き返しと再演")
        for t, lap, cam, plap in back_steps:
            w(f"  t={t:7.1f}  カメラ {cam} へ引き返した → {lap} 周目の区間として扱う（進行は {plap} 周目）")
        w(f"  途中で切れた演出を出し直した回数: {replays}")
        if back_steps and replays == 0:
            verdict("OK", f"引き返しが {len(back_steps)} 回あったが再演は無し"
                          "（切れた演出が無いか、報告済みだった）")
        elif replays:
            verdict("OK", f"引き返しで演出を {replays} 回出し直した")

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
            # 引き返して出し直した演出は begin が複数出る（once でも決着していなければ再演する）。
            again = (f" / {len(starts)} 回出た（引き返しての再演: "
                     + " ".join(f"t={s:.0f}s" for s in starts[1:]) + "）") if len(starts) > 1 else ""
            w(f"  ✅ {tid} ({t['lap']}周 cam{t['camera']} {t['at']}) 出た t={starts[0]:.0f}s{dur}{how}{again} [{kinds}]")
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

    # ---------------- 動画カットの停滞 ----------------
    # カットの尺は発火時刻から数える（TakeRunnerLogic.StepEndTime）ので、動画の
    # ダウンロード + Prepare にかかった時間はそのまま「画に出ない時間」になる。
    # ev=step played=1 は「画面を取った」しか言わないので、ここでしか観測できない。
    # 短いカットを連続で差し替える演出（2 周目 C の接近）はこれで成否が決まる。
    clips = [e for e in events if e.get("ev") == "clip"]
    # 著作された動画カットの最短尺（この値を停滞が食い切ると画が前のカットのまま残る）。
    clip_durs = [float(s.get("durSec") or 0) for t in exp["takes"] for s in t["steps"]
                 if s.get("source") in ("clip", "plate") and float(s.get("durSec") or 0) > 0]
    if clips or clip_durs:
        w("## 動画カットの停滞（発火 → 画に出るまで）")
        if not clips:
            if clip_durs:
                w("  ⚠ 観測していないビルドのログ（ev=clip が無い）")
                verdict("WARN", "動画カットの停滞を観測していないビルド"
                                "（ev=clip キーが無い）— 連続カットの成否が判定できない")
        else:
            # ⚠ ev=clip の dl / prep / tot は **ミリ秒**、著作の durSec は **秒**。
            #    混ぜると割合が 1000 倍になる（実際に 56000% と出した）。比較は秒に揃える。
            tot_ms = sorted(fnum(e, "tot", 0) or 0 for e in clips)
            worst = max(clips, key=lambda e: fnum(e, "tot", 0) or 0)
            med_ms = tot_ms[len(tot_ms) // 2]
            max_ms = max(tot_ms)
            cached = sum(1 for e in clips if (fnum(e, "cache", 0) or 0) >= 1)
            w(f"  観測 {len(clips)} 回 / 中央値 {med_ms:.0f}ms / 最大 {max_ms:.0f}ms"
              f"（{worst.get('id')} — 落とす {fnum(worst, 'dl', 0):.0f}ms"
              f" + 用意 {fnum(worst, 'prep', 0):.0f}ms）/ キャッシュ済み {cached}/{len(clips)}")
            # 用意（Prepare）はファイルサイズによらずほぼ一定で、先読みでも消えない下限。
            prep_ms = sorted(fnum(e, "prep", 0) or 0 for e in clips)
            w(f"  用意だけで 中央値 {prep_ms[len(prep_ms) // 2]:.0f}ms / 最大 {max(prep_ms):.0f}ms"
              f" — 素材を先に落としても、ここは残る")
            if clip_durs:
                shortest = min(clip_durs)
                # 最短のカットに対して停滞が何割を食うか。1.0 を超えると画は 1 フレームも出ない。
                eaten = (max_ms / 1000.0) / shortest if shortest > 0 else 0
                w(f"  著作の最短カット {shortest:.2f}s に対し、最大の停滞は {eaten * 100:.0f}%")
                if eaten >= 1.0:
                    verdict("FAIL", f"動画カットの停滞（最大 {max_ms:.0f}ms）が"
                                    f"最短カット {shortest:.2f}s を食い切る"
                                    f" — そのカットは画に 1 フレームも出ない")
                elif eaten >= 0.3:
                    verdict("WARN", f"動画カットの停滞（最大 {max_ms:.0f}ms）が"
                                    f"最短カット {shortest:.2f}s の {eaten * 100:.0f}% を食う"
                                    f" — 著作した尺より短く見える")
                else:
                    verdict("OK", f"動画カットの停滞は最短カットの {eaten * 100:.0f}%"
                                  f"（最大 {max_ms:.0f}ms / {shortest:.2f}s）")
            # 初回だけ落として以後キャッシュに当たるなら、当日の 1 本目だけが遅い。
            first_dl = [fnum(e, "dl", 0) or 0 for e in clips if (fnum(e, "cache", 0) or 0) < 1]
            if first_dl and max(first_dl) > 300:
                w(f"  ⚠ 初回のダウンロードが最大 {max(first_dl):.0f}ms"
                  f" — 素材を先に落としておかないと 1 回目のカットだけ停滞する")
        w()

    drops = [ln for tag, ln in others if tag == "TakeRunner" and ("出ないまま" in ln or "drop" in ln.lower())]
    if drops:
        w()
        w("### 演出が捨てられた記録")
        for d in drops[:20]:
            w(f"  {d}")
        verdict("WARN", f"演出の drop が {len(drops)} 件（TakeRunner の警告）")
    w()

    # ---------------- 著作の到達可能性 ----------------
    # 体験中に踏まれない区間に置いた演出は絶対に出ない。実機は黙って落とすので、著作者には
    # 発見手段が無い（卓の本番前チェックも同じ判定を持つが、実機が使う設定は卓と食い違いうる）。
    unreachable = [t for t in exp["takes"]
                   if not is_segment_reachable(t["lap"], t["camera"], exp["totalLaps"], exp["order"])]
    if unreachable:
        w("## 踏まれない区間の演出")
        for t in unreachable:
            w(f"  {t['id']}（{t['lap']} 周目 カメラ {t['camera']}）")
        verdict("WARN", f"体験中に踏まれない区間に演出が {len(unreachable)} 本ある（出ません）"
                        f" — 走り切るのは {exp['totalLaps']} 周と帰りのスタート区間まで")
        w()

    # ---------------- 位置合わせ（床の高さ） ----------------
    w("## 位置合わせ")
    floor_ys = [fnum(s, "floorY") for s in sums if fnum(s, "floorY") is not None]
    head_ys = [fnum(s, "headY") for s in sums if fnum(s, "headY") is not None]
    regvs = [s.get("regv") for s in sums if s.get("regv") is not None]
    if not floor_ys and not head_ys:
        w("  （床の高さのテレメトリが無い — 旧ビルド）")
    else:
        if floor_ys:
            w(f"  床の高さ {floor_ys[-1]:+.2f}m")
        if head_ys:
            lo, hi = min(head_ys), max(head_ys)
            w(f"  頭の高さ（床から）{lo:.2f}〜{hi:.2f}m")
            # 立って歩く体験なので、頭は床から 1.2〜2.0m の間にあるはず。
            # 外れていれば床の基準がずれている（ワイヤーや人形が沈む / 浮く）。
            mid = sorted(head_ys)[len(head_ys) // 2]
            if mid < 1.2 or mid > 2.0:
                verdict("WARN", f"頭の高さが床から {mid:.2f}m — 床の基準がずれている疑い"
                                f"（位置合わせをやり直すと合う）")
        if regvs and all(int(v) < 2 for v in regvs):
            verdict("WARN", "床の高さを測っていない登録で走っている"
                            "（右トリガー長押しで登録し直すとワイヤーと人形の高さが合う）")
    w()

    # ---------------- 端末内録画（記録） ----------------
    w("## 端末内録画 — 録れたか")
    rec_ev = [e for e in events if e.get("ev") == "rec"]
    rec_lines = [ln for tag, ln in others if tag == "SegmentRecorder"]
    for ln in rec_lines[:40]:
        w(f"  {ln}")
    if not exp["recEnabled"]:
        w("  （show.json で録画は無効）")
    else:
        # ev=rec は 2026-08-14 からイベント駆動（追い録りで開始と終了が入れ子になり、
        # ポーリングでは閉じた区間が frames=0 に見えていた）。取り逃しの保険としてレコーダ自身の
        # ログ（区間ごとに 1 行）とも突き合わせる。
        started = [ln for ln in rec_lines if "録画開始" in ln]
        polled = [e for e in rec_ev if e.get("v") == "start"]
        w(f"  録画した区間 {len(started)} / 録る設定の区間 {sorted(exp['recShould'])}"
          f" / 演出が要求する区間 {sorted(exp['recNeeded'])}")
        w(f"  1 本の尺 = 切り替えの {exp['recTailSec']:.1f}s 前 〜 {exp['recPostSec']:.1f}s 後"
          f"（計 {exp['recTailSec'] + exp['recPostSec']:.1f}s。滞在が短ければ前側だけ縮む）")
        if not started and not polled:
            verdict("FAIL", "録画が 1 度も始まっていない — 録画カットは実機で黙って飛ぶ")
        else:
            verdict("OK", f"録画が {max(len(started), len(polled))} 区間で走った")

        # ---- 録り始めの線（`record.startLineId`・`canon/LEDGER.md` 0061）----
        # ⚠⚠ **枚数では区別できない。** 末尾方式でも枚数は出るので、「線を指したのに効いていない」は
        #    `start=` でしか分からない。効いていないと録画は「切り替えの直前 3 秒」＝
        #    **そのカメラに体験者が写っていない区間では無人の部屋だけ**になる（2026-08-16 実測）。
        want_line = exp.get("recStartLineId") or ""
        if want_line:
            stops = [e for e in rec_ev if e.get("v") == "stop" and "start" in e]
            if not stops:
                verdict("WARN", f"録り始めの線 '{want_line}' を指しているが観測（start=）が無い — "
                                "古い APK か ShowTelemetryHost 未更新")
            else:
                hit = [e for e in stops if str(e.get("start")) == "1"]
                if not hit:
                    verdict("FAIL", f"録り始めの線 '{want_line}' が 1 度も効いていない"
                                    f"（{len(stops)} 区間すべて末尾方式）— "
                                    "線の担当カメラと区間のカメラが違う / 線を踏んでいない / "
                                    "位置合わせが済んでいない のどれか")
                else:
                    verdict("OK", f"録り始めの線 '{want_line}' が {len(hit)}/{len(stops)} 区間で効いた")

        # 「録れた」の判定は**フレーム数**で行う。バイト数はヘッダだけの空ファイルでも 0 にならない。
        stops = [e for e in rec_ev if e.get("v") == "stop"]
        wrote = {}
        for e in stops:
            lap, cam = e.get("lap"), e.get("cam")
            if lap is None or cam is None:
                continue
            frames = e.get("frames")
            frames = int(frames) if frames is not None else -1
            key = (int(lap), int(cam))
            wrote[key] = max(wrote.get(key, -1), frames)
        for key, frames in sorted(wrote.items()):
            w(f"  L{key[0]}C{key[1]}: {frames} 枚")
            if frames == 0:
                verdict("FAIL", f"{key[0]} 周目 カメラ {key[1]} の録画が 0 枚 "
                                f"— この区間を指す録画カットは実機で黙って飛ぶ")

        # 演出が要求する区間が録れているか（ここが本丸。欠けたら 3 周目・帰りの A の画が出ない）。
        for lap, cam in sorted(exp["recNeeded"]):
            frames = wrote.get((lap, cam))
            if frames is None:
                hit = any(f"L{lap}C{cam}" in ln for ln in rec_lines)
                if not hit:
                    verdict("FAIL", f"{lap} 周目 カメラ {cam} を録っていない "
                                    f"— これを流す演出は出ない（record.laps に {lap} は入っているか）")
            elif frames <= 0:
                verdict("FAIL", f"{lap} 周目 カメラ {cam} の録画が空 — これを流す演出は出ない")

        # 録る設定なのに 1 度も走らなかった区間（体験者がそこを通らなかった / ゲートが閉じていた）。
        for lap, cam in sorted(exp["recShould"] - set(wrote.keys())):
            if any(f"L{lap}C{cam}" in ln for ln in rec_lines):
                continue
            verdict("WARN", f"{lap} 周目 カメラ {cam} は録る設定だが録画が走っていない")
    w()

    # ---------------- 端末内録画（再生） ----------------
    # 「録れた」と「画に出た」は別。ファイルを開けただけのカットは絵が 1 枚も出ないまま尺を消費し、
    # ログ上は演出が走ったように見える。暗い現場では目視で区別できないのでここで判定する。
    w("## 端末内録画 — 再生されたか")
    play = [e for e in events if e.get("ev") == "recplay"]
    # この計装（2026-08-02）より前のビルドは recplay も rec の frames も出さない。
    # 「観測していない」を「起きなかった」と読むと、直っているものを壊しに行くことになる。
    has_rec_telemetry = bool(play) or any("frames" in e for e in rec_ev if e.get("v") == "stop")
    if not exp["recNeeded"]:
        w("  （録画を流すカットは著作されていない）")
    elif not has_rec_telemetry:
        w("  （録画の再生を観測していないビルドのログ）")
        verdict("WARN", "録画の再生を観測していないビルドのログ — 「ファイルを開けた」までしか分からない")
    elif not play:
        verdict("FAIL", "録画カットが著作されているのに、録画が 1 度も再生されていない")
    else:
        opened = {}
        for e in play:
            lap, cam = e.get("lap"), e.get("cam")
            if lap is None or cam is None:
                continue
            key = (int(lap), int(cam))
            if e.get("v") == "open":
                opened.setdefault(key, {"frames": 0, "presented": -1, "luma": None})
                opened[key]["frames"] = int(e.get("frames") or 0)
            else:
                d = opened.setdefault(key, {"frames": 0, "presented": -1, "luma": None})
                d["presented"] = max(d["presented"], int(e.get("presented") or 0))
                d["failed"] = int(e.get("failed") or 0)
                d["luma"] = fnum(e, "luma")
        for key, d in sorted(opened.items()):
            luma = "" if d.get("luma") is None else f" 輝度 {d['luma']:.2f}"
            w(f"  L{key[0]}C{key[1]}: {d['frames']} 枚のうち {max(d['presented'], 0)} 枚を画に出した{luma}")
            if d["presented"] == 0:
                verdict("FAIL", f"{key[0]} 周目 カメラ {key[1]} の録画を開いたが 1 枚も画に出ていない")
            elif d.get("failed", 0) > 0:
                verdict("WARN", f"{key[0]} 周目 カメラ {key[1]} の録画で {d['failed']} 枚の読み出しに失敗")
            elif d.get("luma") is not None and d["luma"] < 0.02:
                verdict("WARN", f"{key[0]} 周目 カメラ {key[1]} の録画が真っ黒に近い"
                                f"（輝度 {d['luma']:.2f}）— 撮影時に映像が来ていたか")
        for lap, cam in sorted(exp["recNeeded"]):
            if (lap, cam) not in opened:
                verdict("FAIL", f"{lap} 周目 カメラ {cam} の録画が 1 度も再生されていない"
                                f"（カットが飛んだか、区間に到達していない）")
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
                if v is None:
                    continue
                # ⚠ `age` の **-1 は「まだ 1 枚も作っていない」の番兵**（配信側 /health）。
                #   平均へ混ぜると「鮮度が良い」方へ引っ張られて、止まっている端末を隠す。
                if k == "age" and v < 0:
                    continue
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

    # ---------------- 入れ替わりのノイズ ----------------
    # `canon/LEDGER.md` 0089。全画面の砂嵐で入れ替えるのをやめ、**映像の中の体験者だけ**を
    # 砂で覆って入れ替える。
    #
    # ⚠⚠ **「段が進んだ」だけを見ない。** 3 段（湧く → 縮む → 晴れる）が完走しても、
    #    人形がカメラの後ろに居れば矩形が書けず、砂は 1 画素も出ない。しかも全画面の砂嵐と違って
    #    「出ていない」が録画から読みにくい（人形は最後に普通に出るので、絵だけ見ると成立して見える）。
    #    だから **rect（矩形を書けたか）と cover（実際に砂が乗った割合）を判定に入れる**。
    w("## 入れ替わりのノイズ（人 ⇄ 人形）")
    swaps = [e for e in events if e.get("ev") == "swap"]
    begins = [e for e in swaps if e.get("st") == "begin"]
    ends = [e for e in swaps if e.get("st") == "end"]
    for e in swaps:
        w(f"  t={fnum(e, 't', 0):7.1f}  {e.get('st', '?'):5s} {e.get('dir', '?'):7s} "
          f"背丈 {fnum(e, 'h', 0):.2f}m 矩形 {e.get('rect', '?')} "
          f"差分マスク {e.get('mask', '?')} 人の代役 {e.get('vis', '?')}")
    # 画に出た側。`swap=<走ったか>/<被覆>/<背丈>/<矩形>/<累計>/<差分マスク>/
    #                <山の振幅>/<エネルギー>/<手の点>`
    # （6 つ目は 2026-08-21〜、7〜9 つ目は黒い波。旧ログは短いまま読める）
    cov_max, rect_ok, swap_n, mask_ok = 0.0, 0, 0, None
    crest_max, energy_max, hot_max = None, None, None
    for smp in sums:
        raw = smp.get("swap")
        if not raw or raw == "-":
            continue
        parts = raw.split("/")
        if len(parts) < 5:
            continue
        try:
            cov_max = max(cov_max, float(parts[1]))
            rect_ok = max(rect_ok, int(parts[3]))
            swap_n = max(swap_n, int(parts[4]))
            if len(parts) >= 6:
                mask_ok = max(mask_ok or 0, int(parts[5]))
            if len(parts) >= 9:
                crest_max = max(crest_max or 0.0, float(parts[6]))
                energy_max = max(energy_max or 0.0, float(parts[7]))
                hot_max = max(hot_max or 0, int(parts[8]))
        except ValueError:
            continue
    want_swaps = exp.get("swapSteps", 0)
    if not swaps and swap_n == 0:
        if want_swaps == 0:
            w("  著作に入れ替わりのカットが 1 つも無い（0119 で 4 周目 A だけになった）")
        else:
            w("  記録なし（この走行では入れ替わりのカットへ到達していない）")
    else:
        w(f"  走った回数 {swap_n} / 砂の被覆の最大 {cov_max:.2f} / 矩形を書けた {rect_ok}")
        dirs = [e.get("dir") for e in begins]
        if rect_ok == 0:
            verdict("FAIL", "入れ替わりの矩形を 1 度も書けていない（rect=0）— 砂は 1 画素も出ていない。"
                            "人形がカメラの後ろ / 位置合わせが未完了 / 人形の層が居ない")
        elif cov_max < 0.9:
            verdict("FAIL", f"砂が人型を覆い切っていない（被覆の最大 {cov_max:.2f}）— "
                            "体験者が隠れないまま画面が差し替わっている")
        elif len(ends) < len(begins):
            verdict("WARN", f"入れ替わりが途中で畳まれた（begin {len(begins)} / end {len(ends)}）")
        elif swap_n < want_swaps:
            verdict("WARN", f"入れ替わりが {swap_n} 回だけ（著作は {want_swaps} 回）— "
                            "走行が途中で終わったか、どれかのカットへ到達していない")
        elif want_swaps >= 2 and ("toDoll" not in dirs or "toHuman" not in dirs):
            # ⚠ 著作が 1 本しか無い走行でここを見ない（0119 で人 → 人形の入れ替わりは廃止され、
            #   残るのは 4 周目 A の人形 → 人だけ。見ると**正しい走行が毎回 WARN する**）。
            verdict("WARN", f"向きが片方だけ（{'/'.join(d or '?' for d in dirs)}）— "
                            "人 → 人形 と 人形 → 人 の両方が要る")
        else:
            verdict("OK", f"入れ替わりが {swap_n} 回、砂の被覆 {cov_max:.2f} まで乗った")
        # 覆いの形の出どころ（2026-08-21〜）。数値が緑でも、CG の形で覆っていたら
        # 映像の中の人と位置がずれうる（0095 の縮退経路）。走った回だけ判定する。
        if swaps or swap_n > 0:
            no_plate = [e for e in begins if e.get("mask") == "0"]
            if no_plate:
                verdict("WARN", f"無人プレートを掴めずに覆った回がある（{len(no_plate)} 回）— "
                                "覆いは CG の形＝映像の中の人とずれうる。"
                                "show.json の cues に plate_<カメラid> があるか・配信で届くかを見る")
            no_vis = [e for e in swaps if e.get("vis") == "0" and e.get("st") == "end"]
            if no_vis:
                verdict("WARN", f"人の代役（actors[] の visitor）へ替えられなかった回がある"
                                f"（{len(no_vis)} 回）— 覆いの形が人形の引き伸ばし＝人型に見えない")

        # 黒い波（`reports/2026-08-21_swap-wave-design.html` の設計 A〜F）。
        # ⚠⚠ **「段が進んだ」ではなく「効果が出た」を見る。** 山の振幅は**画へ書いた値**なので、
        #    走っているのに 0 なら uniform の配線が切れている（絵からは気づけない —
        #    従来の波だけの絵が普通に出るので、走行の PNG は正常に見える）。
        # ⚠ エネルギーと手の点は**立ち止まっている体験者では 0 が正しい**ので判定に使わない。
        #    手はコントローラを持っていると取れない（既定の運用では取れない方が普通）。
        ends_wave = [e for e in ends if e.get("crest") is not None]
        if crest_max is not None or ends_wave:
            peak = crest_max if crest_max is not None else 0.0
            for e in ends_wave:
                peak = max(peak, fnum(e, "crest", 0.0) or 0.0)
            w(f"  黒い波: 山の振幅の最大 {peak:.2f} / "
              f"エネルギーの最大 {energy_max if energy_max is not None else 0.0:.2f} / "
              f"手の点 {hot_max if hot_max is not None else 0}")
            if rect_ok == 1 and peak <= 0.0:
                verdict("FAIL", "黒い波が 1 度も画へ書かれていない（山の振幅が常に 0）— "
                                "段は進んでいるのに走る波・針・跳びが出ていない。"
                                "SwapMorphFx の uniform（_SwapWave / _SwapWave2）の配線を見る")
            elif (energy_max or 0.0) <= 0.0 and (hot_max or 0) == 0:
                w("  ⓘ 体の入力が 1 度も動いていない（立ち止まっていた / 体が取れていない）— "
                  "応答の層（E / F）はこの走行では効いていない。静かな呼吸の側の絵")
    w()

    # ---------------- 持続の覆い（3 周目 A の入り）----------------
    #
    # ⚠⚠ **`swap=` だけでは判定できない。** 包んでいるあいだ入れ替わりは Active だが段は進まない
    #    ので、`swap=` を素朴に読むと「被覆 1.00 のまま何秒も止まっている壊れた入れ替わり」に見える。
    #    `wrap=` は「包んだままか / 覆いの左端 / 立てた回数 / 矩形 / 差分マスク」の 5 つ組。
    # ⚠ 著作に `swapHold` が 1 つも無い走行ではこの節を出さない（起きようのないことを赤くしない）。
    hold_authored = [(t["id"], i, st) for t in exp["takes"]
                     for i, st in enumerate(t["steps"]) if st.get("swapHold")]
    wrap_lines = [e for e in events if e.get("ev") == "wrap"]
    wrap_n, wrap_minx, wrap_rect, wrap_mask = 0, None, None, None
    for smp in sums:
        raw = smp.get("wrap")
        if not raw or raw == "-":
            continue
        parts = raw.split("/")
        if len(parts) < 5:
            continue
        try:
            wrap_n = max(wrap_n, int(parts[2]))
            if int(parts[0]):      # 包んでいる最中のサンプルだけが左端・形の証拠になる
                wrap_minx = max(wrap_minx or 0.0, float(parts[1]))
                wrap_rect = max(wrap_rect or 0, int(parts[3]))
                wrap_mask = max(wrap_mask or 0, int(parts[4]))
        except ValueError:
            continue

    if hold_authored or wrap_n or wrap_lines:
        w("## 持続の覆い（3 周目 A の入り）")
        w(f"  著作 {len(hold_authored)} カット"
          + (f"（{', '.join(f'{tid}#{i}' for tid, i, _ in hold_authored)}）" if hold_authored else "")
          + f" / 立てた回数 {wrap_n}")
        for e in wrap_lines:
            w(f"  t={fnum(e, 't', 0):7.1f}  {e.get('st', '?'):3s} "
              + (f"左端 {e.get('minx', '?')} 矩形 {e.get('rect', '?')} 差分 {e.get('mask', '?')}"
                 if e.get("st") == "on" else f"理由 {e.get('why', '?')}"))
        if not hold_authored:
            verdict("WARN", f"著作に無いのに覆いが {wrap_n} 回立っている — "
                            "show.json の swapHold と実機のカットが食い違っている")
        elif wrap_n == 0 and not wrap_lines:
            if any(t["id"].startswith("L3C0") for t in exp["takes"]):
                verdict("WARN", "覆いが 1 度も立っていない — 3 周目 A へ到達していないか、"
                                "人の代役（actors[] の visitor）／カメラ A の較正が無くて立てられなかった"
                                "（実機ログの [SwapMorphFx] を見る）")
        else:
            authored_minx = max((st.get("swapMinX") or 0) for _t, _i, st in hold_authored) \
                if hold_authored else 0
            if wrap_rect == 0:
                verdict("FAIL", "覆いの矩形を 1 度も書けていない（rect=0）— 黒は 1 画素も出ていない。"
                                "人形の層が居ない / カメラ A の姿勢が未著作 / 位置合わせ未完了")
            elif authored_minx > 0.01 and (wrap_minx or 0) < 0.01:
                verdict("FAIL", f"覆いの左端が 0 のまま包んでいる（著作は {authored_minx:.2f}）— "
                                "**左半分の鏡映しの人物まで包んでいる**。"
                                "カットの swapMinX が実機へ届いていない")
            else:
                verdict("OK", f"覆いを {wrap_n} 回立てた（左端 {wrap_minx if wrap_minx is not None else 0:.2f}"
                              f" / 差分マスク {wrap_mask}）")
            if wrap_mask == 0:
                verdict("WARN", "無人プレートを掴めずに包んだ — 覆いは CG の形＝映像の中の人とずれうる。"
                                "show.json の cues に plate_A があるか・配信で届くかを見る")
            dropped = [e for e in wrap_lines if e.get("st") == "off" and e.get("why") == "drop"]
            if dropped:
                verdict("WARN", f"覆いが入れ替わりへ引き継がれずに畳まれた回がある（{len(dropped)} 回）— "
                                "凍結の次のカットが transition:\"swap\" になっているか、"
                                "演出が途中で中止されていないかを見る")
        w()

    # ---------------- 凍結から録画の頭まで ----------------
    #
    # ⚠⚠ 「凍結が消える地点より外から録画が始まる」（0102）を数値で見る唯一の場所。
    #    画からは「跳んだ」としか読めず、原因（線の対応 / 歩線の違い / 継ぎ目が裸）を分けられない。
    # 測るのは **凍結のカットが画面を取った時刻 → 録画が実際にテクスチャへ載り始めた時刻**。
    #    著作の尺（凍結 1.2 秒）＋ 覆いの引き継ぎ（1 フレーム）が期待値。
    freeze_steps = [(t["id"], i, st) for t in exp["takes"]
                    for i, st in enumerate(t["steps"]) if st.get("splitFreeze")]
    if freeze_steps:
        w("## 凍結から録画の頭まで")
        step_ev = [e for e in events if e.get("ev") == "step"]
        play_ev = [e for e in events if e.get("ev") == "recplay" and e.get("v") == "open"]
        for tid, idx, st in freeze_steps:
            fz = next((e for e in step_ev
                       if e.get("take") == tid and e.get("i") == str(idx)), None)
            if fz is None:
                w(f"  {tid}#{idx}: 凍結のカットへ到達していない")
                continue
            t0 = fnum(fz, "t", 0.0)
            nxt = next((e for e in play_ev if fnum(e, "t", 0.0) >= t0), None)
            want = (st.get("durSec") or 0) or 1.2
            if nxt is None:
                verdict("FAIL", f"{tid}#{idx}: 凍結の後に録画が 1 度も画へ載っていない — "
                                "3 周目の左半分は凍ったまま（録画が録れていない / recLap の指定違い）")
                continue
            gap = fnum(nxt, "t", 0.0) - t0
            w(f"  {tid}#{idx}: 凍結 t={t0:.2f} → 録画の頭 t={fnum(nxt, 't', 0):.2f}"
              f"（間隔 {gap:.2f}s / 著作 {want:.2f}s）"
              f" lap={nxt.get('lap')} cam={nxt.get('cam')}")
            if gap < want - 0.35:
                verdict("WARN", f"{tid}#{idx}: 凍結が著作より {want - gap:.2f}s 短い — "
                                "凍結の 1 枚を読ませる間が足りていない")
            elif gap > want + 0.8:
                verdict("WARN", f"{tid}#{idx}: 凍結から録画までが {gap:.2f}s（著作 {want:.2f}s）— "
                                "覆いの引き継ぎが成立せず乱れ遷移へ倒れた疑い"
                                "（ev=wrap の why=drop と実機ログの [TakeRunner] を見る）")
            else:
                verdict("OK", f"{tid}#{idx}: 凍結 {gap:.2f}s で録画へ渡った")
            # 録画の周が著作と合っているか（recLap 1 → 2 の変更が実機へ届いたか）。
            swap_step = next((t["steps"][idx + 1] for t in exp["takes"]
                              if t["id"] == tid and idx + 1 < len(t["steps"])), None)
            if swap_step and swap_step.get("source") == "rec":
                want_lap = swap_step.get("recLap") or 1
                got_lap = nxt.get("lap")
                if got_lap is not None and str(want_lap) != str(got_lap):
                    verdict("FAIL", f"{tid}#{idx}: 流れた録画が {got_lap} 周目（著作は {want_lap} 周目）— "
                                    "端末キャッシュが古い設定で走っている疑い")
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
        if not intro and truncated:
            w("  （ログの先頭が落ちているので導入は観測できていない）")
            verdict("WARN", "導入演出を観測できていない（ログの先頭が落ちた）— 出なかった証拠ではない")
        elif not intro:
            verdict("FAIL", "導入演出の段が 1 つも記録されていない（IntroDirector 未配線か enabled=false）")
        else:
            for want in ("Real", "Degrade", "Structure", "Frame", "Swap"):
                if want not in stages:
                    verdict("WARN", f"導入演出の段 {want} が出ていない")
            if "Swap" in stages:
                verdict("OK", "導入演出が最後の段（Swap）まで進んだ")
        # 段 1 以降に auth=0 の行があれば、人の A ではなくスタッフの ⏭ で始まったということ。
        forced = [e for e in intro if e.get("stage") not in ("Off", "Black") and str(e.get("auth")) == "0"]
        if forced:
            verdict("WARN", "導入が A の押下ではなく明示操作（⏭）で始まっている — "
                            "現場の運用（周回リセット → 引き渡し → A）を通っていない")
    w()

    # ---------------- タイトル画面 ----------------
    # ⚠ 2026-08-14 まで**タイトルの観測が 1 つも無かった**。体験の入口そのもので、
    #    しかも「実体を組めないと A が何もしない」経路があるのに、出たかどうかがログから
    #    分からなかった（`built=0` なら題字は一生出ない）。
    title = [e for e in events if e.get("ev") == "title"]
    if title:
        w("## タイトル画面")
        for e in title:
            w(f"  t={fnum(e,'t',0):7.1f}  段={e.get('stage')} built={e.get('built')} "
              f"veil={e.get('veil')} glyph={e.get('glyph')}")
        tstages = [e.get("stage") for e in title]
        if any(str(e.get("built")) == "0" for e in title):
            verdict("FAIL", "タイトルの実体を組めていない（built=0）— A を押しても何も起きず、"
                            "注意書きも出ない。シェーダの Always Included と Resources/Title の版を見る")
        else:
            # 黒（veil）が実際に立ったか。段が Wait でも veil=0 なら画は素通しのまま。
            # ⚠ **譲っている間（位置合わせ・ステータス表示）は veil=0 が正常**。除外しないと
            #    「起動 → 位置合わせ」の運用そのものを FAIL と言う（2026-08-14 の走行で出した）。
            waits = [e for e in title if e.get("stage") == "Wait" and str(e.get("yield")) != "1"]
            if waits and all(fnum(e, "veil", 0.0) < 0.9 for e in waits):
                verdict("FAIL", "タイトルの段は Wait なのに黒が立っていない（veil<0.9）— "
                                "引き渡し前に体験エリアが見えている")
            if "In" not in tstages and "Hold" not in tstages:
                verdict("WARN", "題字が 1 度も立っていない（A が押されていないか、押しても効いていない）")
            elif "Done" in tstages:
                verdict("OK", "タイトルが A で呼ばれて閉じ切った")
            if any(e.get("stage") in ("In", "Hold") and fnum(e, "glyph", 0.0) < 0.5 for e in title):
                verdict("WARN", "題字の段なのに文字の不透明度が低い（glyph<0.5）")
    elif exp["introEnabled"] and not truncated:
        verdict("WARN", "タイトルの段が 1 行も記録されていない（TitleScreen 未配線か、"
                        "シーンを焼き直していない）")
    w()

    # ---------------- 終幕 ----------------
    # ⚠⚠ 終幕は「装置が力尽きて報告を出す」だけの演出なので、**録画からは「暗い」としか読めない**
    #    （管が潰れて消えるのも、黒の中に文字が出るのも、暗い部屋の録画では判別が難しい）。
    #    画に出たかを見る手はここしかない:
    #      cl/clMax = 電源断が実際に画へ出たか（0 のままなら 1 画素も潰れていない・LEDGER 0111）
    #      pw       = 実際に材質へ書いた電力（nc なら書く先を掴めていない）
    #      repBuilt = 報告の面を組めたか（0 なら最後の 4 行が 1 文字も出ない）
    #      armed/cue = 著作した合図（run.outro.afterTakeId）が武装したか / そこから始まったか
    #    観測の出どころは C# の `ShowTelemetryHost`。**片方だけ直すと沈黙して食い違う。**
    outro = [e for e in events if e.get("ev") == "outro"]
    if outro:
        w("## 終幕（消えて、報告が出たか）")
        for e in outro:
            w(f"  t={fnum(e,'t',0):7.1f}  段={e.get('stage')} pw={e.get('pw')} cl={e.get('cl')} "
              f"rep={e.get('rep')} repBuilt={e.get('repBuilt')} "
              f"repChars={e.get('repChars')} repSfx={e.get('repSfx')} marks={e.get('marks')} "
              f"armed={e.get('armed')} cue={e.get('cue')}")
        ostages = [e.get("stage") for e in outro if e.get("stage") != "Off"]
        if not ostages:
            w("  終幕は 1 度も始まっていない（体験が終わる前に走行が切れたなら正常）")
        else:
            if any(str(e.get("pw")) == "nc" for e in outro):
                verdict("FAIL", "終幕がスクリーンの材質を掴めていない（pw=nc）— 電力を書けないので、"
                                "装置が死ぬ過程が 1 度も画に出ない")
            if any(str(e.get("repBuilt")) == "0" for e in outro):
                verdict("FAIL", "終幕の報告の面を組めていない（repBuilt=0）— 最後の 4 行が 1 文字も"
                                "出ない。日本語フォントの静的ベイクを確かめる（menu hud-font）")
            for want in ("Collapse", "Dark", "Report"):
                if want not in ostages:
                    verdict("WARN", f"終幕の段 {want} が出ていない")

            # -- 電源断が画に出たか（`canon/LEDGER.md` 0111）------------------------
            # ⚠⚠ **`ev=outro` の `cl` は必ず頭の値（≒0）**（段の縁でしか出ないため）。
            #    潰れ切ったかは `ev=sum` の `clMax`（走行全体の最大値）でしか取れない。
            # ⚠⚠ **これが無いと「潰れなかった」を誰も検出できない** — 段は正しく進み、
            #    pw も Dark 以降 0 になるので、書けていなくても他の判定は全部 PASS する
            #    （画は「0.9 秒ふつうに映ってから黒へ瞬断」になり、暗い現場では目視でも同じに見える）。
            cl_all = [v for v in effect_samples(events, "clMax") if str(v) not in ("-", "nc")]
            if any(str(e.get("cl")) == "nc" for e in outro):
                verdict("FAIL", "電源断を画へ書けていない（cl=nc）— スクリーンの材質を掴めていない")
            elif not cl_all:
                verdict("WARN", "電源断の観測（clMax）が出ていない — 古い APK か "
                                "ShowTelemetryHost 未更新（0111 より前のビルド）")
            else:
                cl_max = max(float(v) for v in cl_all)
                if cl_max >= 0.9:
                    verdict("OK", f"電源断が画に出た（管が潰れて消えた・clMax {cl_max:.2f}）")
                elif cl_max > 0.05:
                    verdict("FAIL", f"電源断が途中で止まっている（clMax {cl_max:.2f} < 0.9）— "
                                    "潰れ切る前に段が変わった。collapseSec と実測を突き合わせる")
                else:
                    verdict("FAIL", "電源断が 1 画素も画に出ていない（clMax≒0）— 進みは配っているのに "
                                    "_ScreenCollapse が効いていない。シェーダの焼き直しを確かめる")

            # 「段が進んだ」ではなく「画に出た」。Report 以降は電力 0 でなければ消えていない。
            # ⚠ Dark は入れない。段の遷移と電力の書き込みは同じフレームで、実行順は未定義なので、
            #   Dark へ入った 1 行だけ Collapse の値が載りうる（偽の FAIL になる）。
            after = [e for e in outro if e.get("stage") in ("Report", "Done")]
            if after and any(fnum(e, "pw", 1.0) > 0.05 for e in after):
                verdict("FAIL", "スクリーンが消え切っていない（Dark 以降で pw>0.05）")
            done = [e for e in outro if e.get("stage") == "Done"]
            if done and all(fnum(e, "rep", 0.0) > 0.95 for e in done):
                verdict("OK", "終幕が最後まで進み、報告が画に出た")
            elif done:
                verdict("FAIL", "終幕は終わったのに報告の不透明度が上がっていない（rep<0.95）— "
                                "黒の中で何も出ないまま体験が終わっている")
            if any(str(e.get("cue")) == "1" for e in outro):
                verdict("OK", "終幕は著作した合図（run.outro.afterTakeId）から始まった")
            elif any(str(e.get("armed")) == "1" for e in outro):
                verdict("WARN", "合図の演出は走ったが、そこから終幕へは入っていない"
                                "（endHoldMaxSec の安全網で終わった — 演出が自分から終わっていない）")

            # -- 報告は 1 字ずつ打たれ、1 字ごとに打鍵音が鳴る（`canon/LEDGER.md` 0063）。
            #    ⚠ **打ち切るのは段 Done より後**（打つ尺 > reportFadeSec）なので、
            #      到達の判定は段の行ではなく `ev=sum` の側でしか取れない。
            #    ⚠ 走行が報告の途中で切れれば届かないのが正常なので、そこは WARN に留める。
            want_chars = int(max((fnum(e, "repChars", 0) or 0) for e in outro)) if outro else 0
            shown_all = [v for v in effect_samples(events, "repShown") if str(v) != "-"]
            typed_all = [v for v in effect_samples(events, "repTypeN") if str(v) != "-"]
            if any(str(e.get("repSfx")) == "0" for e in outro):
                verdict("FAIL", "報告の打鍵の音源を掴めていない（repSfx=0）— 字は出るのに無音。"
                                "`py -3.11 tools/ingest-sounds.py --only sfx_type` を走らせたか")
            elif not typed_all or not shown_all:
                verdict("WARN", "報告の打鍵の観測（repTypeN / repShown）が出ていない — "
                                "古い APK か ShowTelemetryHost 未更新")
            elif want_chars <= 0:
                verdict("WARN", "報告の字数（repChars）が 0 — 文面を組めていない可能性")
            else:
                shown = max(int(v) for v in shown_all)
                typed = max(int(v) for v in typed_all if str(v) != "nc") if any(
                    str(v) != "nc" for v in typed_all) else -1
                w(f"  打鍵: 出た文字 {shown} / 鳴った {typed} / 全部で {want_chars}（改行を除く）")
                if shown == 0:
                    verdict("FAIL", "報告が 1 文字も打たれていない（repShown=0）— "
                                    "黒の中で何も出ないまま体験が終わっている")
                elif typed == 0:
                    verdict("FAIL", f"報告の字は出たのに打鍵が 1 発も鳴っていない"
                                    f"（repShown={shown} / repTypeN=0）")
                elif typed > want_chars:
                    verdict("FAIL", f"報告の打鍵が字数より多い（repTypeN={typed} / 字数 {want_chars}）— "
                                    "改行で鳴らしているか、1 フレームで複数発鳴らしている")
                elif shown < want_chars:
                    verdict("WARN", f"報告を打ち切る前に走行が切れた（{shown}/{want_chars} 文字）— "
                                    "走行を伸ばすか、体験の最後まで回す")
                else:
                    verdict("OK", f"報告が {want_chars} 文字ぶん打たれ、打鍵が {typed} 発鳴った")
        w()

    # ---------------- AIエージェントからの連絡 ----------------
    # 発火は 3 点（`canon/LEDGER.md` 0054）: ①導入が明けた直後 ②報告した瞬間（演出の有無で文面が
    # 変わる）③4 周目 A の締めで押さないまま 3 秒。
    # ⚠⚠ **②の分岐は「解除が通ったか」で決まる**（2026-08-17・`canon/LEDGER.md` 0082）。
    #    ここは `ev=mark` の `res=` と `ev=comms` の id を突き合わせて
    #    **食い違っていたら FAIL** にする（画を見ても絶対に気づけない壊れ方なので）。
    #    ⚠ 2026-08-17 まではキーが `take=`（演出が走っていたか）で、3 周目の入れ替わりに
    #      押しても「異変を排除しました」と返っていた ＝ 消えていないのに認めた顔をしていた。
    # 観測の出どころは C# の `ShowTelemetryHost`。**片方だけ直すと沈黙して食い違う。**
    comms = [e for e in events if e.get("ev") == "comms"]
    comms_built = effect_samples(events, "commsBuilt")
    if comms or comms_built:
        w("## AIエージェントからの連絡")
        for e in comms:
            w(f"  t={fnum(e,'t',0):7.1f}  {e.get('id')} n={e.get('n')} "
              f"built={e.get('built')} lap={e.get('lap')} chars={e.get('chars')} "
              f"sfx={e.get('sfx')} decay={e.get('decay')} wait={e.get('wait')}")

        if comms_built and all(str(v) == "0" for v in comms_built):
            verdict("FAIL", "連絡の面を組めていない（commsBuilt=0）— 1 通も出ない。"
                            "日本語フォント（menu hud-font）と menu scene を見る")
        elif not comms:
            verdict("WARN", "連絡が 1 通も届いていない（本編に入っていないか、CommsPanel が未配線）")
        else:
            ids = [e.get("id") for e in comms]
            # ⚠⚠ **2026-09-06 に①と①b を入れ替えた**（canon/LEDGER.md 0174・ユーザー指定
            #    「調査を開始してくださいと、異変を見つけたらボタンを長押ししてくださいの順番を逆に」）。
            #    ① = BeginHow（押し方）/ ①b = Begin（開始の合図）。**id の綴りは入れ替えていない**。
            if "BeginHow" in ids:
                verdict("OK", "導入が明けた直後に①「異変を見つけたら…」が届いた")
            else:
                verdict("FAIL", "①の連絡が届いていない — 本編に入った縁を見ていない"
                                "（CommsCueLogic.BeginDelaySec / runDirector の配線）")

            # ①b 開始の合図。**①を読ませ終わった縁**で間を置かず続く。
            # ⚠ ここが欠けると、体験者は押し方だけ読んで「始めてよい」を一度も言われないまま終わる。
            if "BeginHow" in ids and "Begin" not in ids:
                verdict("FAIL", "①b『調査を開始してください』が届いていない — "
                                "開始の合図が 1 度も画に出ていない"
                                "（CommsPanelLogic.DoneReading / panelDoneReading の配線）")

            # ⓪ タイトルの直後の 2 通（canon/LEDGER.md 0079）。**順序が意味を持つ** —
            #    名乗る前に指示が出ると、誰が喋っているのか分からないまま歩かされる。
            def _first(kind):
                return next((fnum(e, "t", 0.0) for e in comms if e.get("id") == kind), None)
            t_greet, t_walk = _first("Greeting"), _first("Walk")
            t_arrived, t_begin = _first("Arrived"), _first("Begin")
            t_how = _first("BeginHow")
            if t_greet is None and t_walk is None:
                verdict("WARN", "タイトル直後の⓪が 1 通も届いていない — 導入で連絡を出していない"
                                "（CommsPanel の intro 未配線 / 古い APK）")
            else:
                if t_greet is None:
                    verdict("FAIL", "⓪a 自己紹介が届いていない（体験の中で AI だと分かる唯一の所）")
                if t_walk is None:
                    verdict("FAIL", "⓪b 歩行の指示が届いていない — 矢印と円だけが出て、"
                                    "何をすればよいか画に出ていない")
                if t_greet is not None and t_walk is not None:
                    if t_greet < t_walk:
                        verdict("OK", f"⓪a 名乗り {t_greet:.1f}s → ⓪b 指示 {t_walk:.1f}s の順で届いた")
                    else:
                        verdict("FAIL", "⓪b の指示が⓪a の名乗りより先に出ている")
                if t_walk is not None and t_how is not None and t_how <= t_walk:
                    verdict("FAIL", "①が⓪b より先に出ている — 導入と本編の連絡が入れ替わっている")
            # ①b は①の後（0174 で中身が入れ替わった。順序が逆なら、
            # 押し方を知らないまま「調査を開始してください」を読むことになる）。
            if t_begin is not None and t_how is not None:
                gap = t_begin - t_how
                if gap <= 0:
                    verdict("FAIL", "①b『調査を開始してください』が①『異変を見つけたら…』より先に"
                                    "出ている — 押し方を知らないまま始めさせている（0174）")
                elif gap > 8.0:
                    verdict("WARN", f"①→①b が {gap:.1f}s 空いている — 同じ面のまま繋がっていない疑い"
                                    "（②③に割り込まれた回なら正常）")
                else:
                    verdict("OK", f"① {t_how:.1f}s → ①b {gap:.1f}s 後 の順で届いた")

            # ⓪c 演出の始まりの告知。**導入演出が始まったのと同じ縁**で出る（0079 の赤入れ 3）。
            # ⚠ 導入まで走らなかった走行では出ないのが正常なので、段 1 を踏んだときだけ判定する。
            t_real2 = next((fnum(e, "t", 0.0) for e in events
                            if e.get("ev") == "intro" and e.get("stage") == "Real"), None)
            if t_real2 is not None:
                if t_arrived is None:
                    verdict("FAIL", "⓪c『ポイントに到着しました』が届いていない — "
                                    "導入が段 0 を抜けた縁を CommsCueLogic が見ていない")
                elif abs(t_arrived - t_real2) > 1.0:
                    verdict("WARN", f"⓪c と演出の始まりが {abs(t_arrived - t_real2):.1f}s ずれている"
                                    "（同時に出る約束）")
                else:
                    verdict("OK", f"⓪c が演出の始まりと同時に届いた（t={t_arrived:.1f}s）")

            # ②の分岐が「解除が通ったか」と一致しているか。ev=mark と ev=comms を時刻で対にする。
            marks = [e for e in events if e.get("ev") == "mark"]
            answers = [e for e in comms if e.get("id") in ("MarkLogged", "MarkNothing")]
            mark_ok = True
            if len(answers) < len(marks):
                mark_ok = False
                verdict("FAIL", f"報告 {len(marks)} 回に対して②の返事が {len(answers)} 通しかない — "
                                "押しても返らない報告がある（装置が壊れて見える）")
            for m in marks:
                near = [a for a in answers if abs(fnum(a, "t", 0.0) - fnum(m, "t", 0.0)) < 1.0]
                if not near:
                    continue
                want = "MarkLogged" if str(m.get("res")) == "1" else "MarkNothing"
                if near[0].get("id") != want:
                    mark_ok = False
                    verdict("FAIL",
                            f"t={fnum(m,'t',0):.1f} の報告（解除 res={m.get('res')}）に対して "
                            f"{near[0].get('id')} が返っている — 期待は {want}。"
                            "ShowControlClient.LastMarkResolved と CommsCueLogic.markResolved が "
                            "食い違っている（画に出る意味が真逆になる）")
            if answers and mark_ok:
                verdict("OK", f"②の返事が報告 {len(answers)} 回すべてに返り、解除の可否と一致した")

            # ⚠⚠ **解除が 1 度も通らない台本は、ゲーム性が死んでいる**（`canon/LEDGER.md` 0082）。
            #    体験者は「押すと消える」を 1〜2 周目で学習してはじめて、3 周目の「消えない」が効く。
            #    dismissible を 1 つも立てていない show.json ではここが 0 になる。
            resolved = [m for m in marks if str(m.get("res")) == "1"]
            if marks and not resolved:
                verdict("WARN", f"報告 {len(marks)} 回すべてで解除が通っていない（res=0）— "
                                "台本の演出に dismissible が 1 つも立っていない疑い。"
                                "1〜2 周目に消せる異変が無いと、3 周目の「消えない」が伝わらない")

            # ③は 2 通（canon/LEDGER.md 0168）。③a「止まってください！」→ ③b「異常があなたを…」。
            # ⚠ ③a は**打鍵を鳴らさない**ので、chars=0 で届くのが正常（下の打鍵の合計に混ぜない）。
            #
            # ⚠⚠ **2026-09-06（0178）に時計が変わった。** ③は「報告待ちが立ってから 2 秒」ではなく
            #    **「締めのカットに入ってから 5 秒」**（`CommsCueLogic.HaltAfterClosingSec`）で出る。
            #    ＝ **押しても押さなくても③a →③b は必ず流れる。**
            #    ⇒ **③b が届いて③a が無い走行は FAIL**（順序が崩れている）。
            #    ⇒ **③a が届いて③b が無い走行も FAIL**（0168 の頃は「押したら正常」だったが、
            #      いまは押しても③b は続く。欠けていたら配線が壊れている）。
            t_halt = _first("Halt")
            waited = [e for e in comms if e.get("id") == "Prompt"]
            if t_halt is not None and waited:
                gap = fnum(waited[0], "t", 0.0) - t_halt
                if gap < 0:
                    verdict("FAIL", "③b が③a『止まってください！』より先に出ている")
                else:
                    verdict("OK", f"③a『止まってください！』{t_halt:.1f}s → ③b が {gap:.1f}s 後 の順で届いた")
            elif waited:
                verdict("FAIL", "③b は届いたのに③a『止まってください！』が届いていない（0168）— "
                                "CommsCueLogic の Halt を見ていない古い APK の疑い")
            elif t_halt is not None:
                verdict("FAIL", "③a は届いたのに③b『異常があなたを…』が届いていない（0178）— "
                                "報告で③b を止める古い実装の疑い。いまは押しても続く約束")
            elif any(str(e.get("wait")) == "1" for e in comms):
                verdict("FAIL", "締めのカットまで来たのに③が 1 通も届いていない（0178）— "
                                "③は締めに入ってからの時計で出るので、押した／押さないに関わらず出る")

            # ---- 打鍵音（`canon/LEDGER.md` 0056）----
            # ⚠⚠ **音は録画に映らない。** 字が 1 文字ずつ出る絵は PNG で確かめられるが、
            #    それに合わせて鳴っているかはこの数でしか分からない。
            # ⚠ 完全一致は求めない — 打ち終わる前に次の連絡が届くと（②は押すたび返る）
            #    残りの字は打たれないので、`typeN` は合計より少なくなるのが**正常**。
            #    見るのは「0 でないこと」と「合計を超えないこと」の 2 つ。
            want_hits = sum(int(e.get("chars") or 0) for e in comms)
            typed_all = [v for v in effect_samples(events, "typeN") if str(v) != "-"]
            typed = typed_all[-1] if typed_all else None
            if typed is None:
                verdict("WARN", "打鍵の観測（typeN）が出ていない — 古い APK か ShowTelemetryHost 未更新")
            elif str(typed) == "nc":
                verdict("FAIL", "打鍵の音源を掴めていない（typeN=nc）— 字は出るのに無音。"
                                "`py -3.11 tools/ingest-sounds.py --only sfx_type` と "
                                "`menu sound-import` を見る")
            else:
                n = int(typed)
                if n <= 0:
                    verdict("FAIL", f"連絡が {len(comms)} 通届いたのに打鍵が 1 発も鳴っていない"
                                    f"（typeN=0 / 字数の合計 {want_hits}）— "
                                    "CommsPanel が TypeAudioCue を掴めていない疑い")
                elif n > want_hits:
                    verdict("FAIL", f"打鍵が字数より多い（typeN={n} / 字数の合計 {want_hits}）— "
                                    "1 文字で 2 発以上鳴っている（Apply の増分の見方）")
                elif n < want_hits * 0.5:
                    verdict("WARN", f"打鍵が字数の半分以下（typeN={n} / 合計 {want_hits}）— "
                                    "打ち終わる前に次の連絡が届いて打ち切れていない")
                else:
                    verdict("OK", f"打鍵が {n} 発鳴った（字数の合計 {want_hits}・1 文字 1 発）")

            # ---- 周回の壊れ（`canon/LEDGER.md` 0068 / 0069）----
            # ⚠⚠ 見るのは「強さが動いた」ではなく **実際に化けた字の数（commsCx）**。
            #    2026-07-31 の「段は進んだのに画は空だった」と同じ型を避けるための観測。
            # ⚠ 0069 で壊れ方を作り直した（レイヤを貼る → 印字そのものが壊れる）。
            #    化けの組み合わせは刻みごとに変わるので、標本によって 0 が出るのは正常。
            #    だから「全標本が 0」でだけ落とす。
            cx = []
            for v in effect_samples(events, "commsCx"):
                try:
                    cx.append(int(v))
                except (TypeError, ValueError):
                    pass
            gl = []
            for v in effect_samples(events, "commsGl"):
                try:
                    gl.append(float(v))
                except (TypeError, ValueError):
                    pass
            # ⚠⚠ 地と縁（commsBg）。**0 なら文字と壊れだけが宙に浮く。**
            #    2026-08-17 まで実機がそうだった（`Unlit/Color` がビルドから剥がれていた）。
            #    Editor のプレビューでは必ず出るので、**この 1 ビットだけが唯一の手掛かり**。
            bg = [str(v) for v in effect_samples(events, "commsBg") if str(v) not in ("", "-")]
            if bg and all(v == "0" for v in bg):
                verdict("FAIL", "連絡の面の地と縁が出ていない（commsBg=0）— 文字と壊れだけが宙に浮く。"
                                "地のシェーダがビルドから剥がれている疑い"
                                "（CommsPanel.Build の Shader.Find）")
            elif bg:
                verdict("OK", "連絡の面の地と縁が出ている（commsBg=1）")

            # ⚠⚠ AIエージェントの顔（`canon/LEDGER.md` 0071 / 0073）。
            #    `<枠>/<版の枚数>/<濃さ>/<侵食>`。**4 つとも別の壊れ方**なので畳まず 1 つずつ見る。
            #    どれも Editor のプレビューでは必ず出るので、実機の手掛かりはここだけ。
            faces = [str(v) for v in effect_samples(events, "commsFace")
                     if str(v) not in ("", "-")]
            if faces:
                parts = [f.split("/") for f in faces if f.count("/") == 3]
                frame = [p[0] for p in parts]
                art = [p[1] for p in parts]
                lit, mix = [], []
                for p in parts:
                    try:
                        lit.append(float(p[2]))
                        mix.append(float(p[3]))
                    except ValueError:
                        pass
                if frame and all(v == "0" for v in frame):
                    verdict("FAIL", "AIエージェントの顔の枠を組めていない（commsFace の 1 つ目が 0）— "
                                    "FixedCamVr/CommsAvatar がビルドから剥がれている疑い"
                                    "（ProjectSettings の Always Included を見る）")
                elif art and all(v == "0" for v in art):
                    verdict("FAIL", "顔の版を掴めていない（commsFace の 2 つ目が 0）— 枠だけが出て中身が空。"
                                    "py -3.11 tools/make-comms-face.py で焼いてから "
                                    "Assets/Resources/Comms/ に在るか見る")
                elif lit and max(lit) <= 0.004:
                    verdict("FAIL", "顔が 1 度も画に出ていない（commsFace の 3 つ目が全標本 0）— "
                                    "枠も版も在るのに濃さが乗っていない（CommsPanel.ApplyAvatar）")
                else:
                    if art and max(int(v) for v in art) < 2:
                        # ⚠ 画は普通に出るので、**走行の絵を見ても気づけない**。
                        verdict("FAIL", "侵食の版（市松人形）を掴めていない（commsFace の 2 つ目が 1）— "
                                        "3 周目になっても顔がスイのまま変わらない。"
                                        "Assets/Resources/Comms/DollFace.png を焼く")
                    if lit:
                        verdict("OK", f"AIエージェントの顔が出ている（最大の濃さ {max(lit):.2f}）")
                    # ⚠⚠ 侵食は周回の壊れと同じ値のはず。届いていなければ顔だけ無事に見える。
                    if mix and max(mix) <= 0.01 and gl and max(gl) > 0.01:
                        verdict("FAIL", f"文字は壊れた（最大 {max(gl):.2f}）のに顔が侵食されていない"
                                        "（commsFace の 4 つ目が全標本 0）— "
                                        "CommsPanel.ApplyAvatar が _FaceMix を書けていない疑い")
                    elif mix and max(mix) > 0.01:
                        verdict("OK", f"顔が市松人形へ侵食された（最大 {max(mix):.2f}）")

            if gl and max(gl) <= 0.0:
                verdict("WARN", "連絡の面が最後まで壊れなかった（commsGl が全標本 0）— "
                                "周が進んでいないか、ShowRunDirector.ScreenDecay を読めていない")
            elif gl and cx and max(cx) <= 0:
                # 強さは上がったのに 1 字も化けていない ＝ 印字へ届いていない。
                verdict("FAIL", f"強さは上がった（最大 {max(gl):.2f}）のに字が 1 つも化けていない"
                                "（commsCx が全標本 0）— CommsPanel.ApplyCorruption が"
                                "文面へ届いていない疑い")
            elif gl:
                verdict("OK", f"連絡の面が周回とともに壊れた"
                              f"（強さ 最大 {max(gl):.2f} / 化けた字 最大 {max(cx) if cx else 0}）")

            # ③（4 周目 A の締め）は進み 1.0 ＝ 壊れが最大の状態で届くはず。
            # ⚠ 進みは 3 周目 A で 1.0 に着いて以後動かない（`ScreenDecayLogic`）ので、
            #    ここが 1 に届いていないなら周が進み切っていない（＝ 演出最大に到達していない）。
            for e in comms:
                if e.get("id") != "Prompt":
                    continue
                try:
                    d = float(e.get("decay"))
                except (TypeError, ValueError):
                    continue
                if d < 0.9:
                    verdict("WARN", f"③の催促が進み {d:.2f} で届いた — "
                                    "3 周目 A で最大に着いていない（周が足りないか走行が短い）")
        w()

    # ---------------- 位置合わせ（コントローラの操作モード） ----------------
    # ⚠ ここが無かったせいで「トリガー長押しが発火していないのか、発火しても画が変わらないのか」を
    #    実機ログから切り分けられなかった（2026-08-09）。卓の heartbeat にしか出ていなかった。
    #    `pt`（パススルーが実際に有効か）を併記するのは、モードが変わっただけで現実が出ていない
    #    ケース＝作業が成立しないケースを名指しするため。
    modes = [e for e in events if e.get("ev") == "ctrlmode"]
    if modes:
        w("## 位置合わせ（コントローラの操作モード）")
        for e in modes:
            w(f"  t={fnum(e,'t',0):7.1f}  モード={e.get('v')} pt={e.get('pt')}")
        reg = [e for e in modes if e.get("v") == "REG"]
        if reg:
            blind = [e for e in reg if str(e.get("pt")) != "1"]
            if blind:
                verdict("FAIL", f"位置合わせに入ったのにパススルーが有効でない回が {len(blind)} 件 — "
                                "現実が見えないので線を実物に重ねられない（PassthroughStyler の配線と "
                                "OculusProjectConfig の _insightPassthroughSupport を見る）")
            else:
                verdict("OK", f"位置合わせに {len(reg)} 回入り、いずれもパススルーが有効だった")
        w()

    # ---------------- コントローラの位置（手元の面が出るか）----------------
    # ⚠⚠ **繋がっていることと、位置が取れていることは別**（2026-08-16 実機で踏んだ）。
    #    カメラから見えていないコントローラは接続 true のまま姿勢が無効になり、`OVRCameraRig` は
    #    アンカーをトラッキング原点（床の中心）へ置く。**手元の面がそこへ出て「遠くに小さく」見える。**
    #    いまは位置が無効なら面を出さないので、**出ていない理由**がここにしか残らない。
    #    観測の出どころは C# の `ShowTelemetryHost`。**片方だけ直すと沈黙して食い違う。**
    ctrl_any = [k for k in ("ctrlL", "ctrlR")
                if any(str(v) != "-" for v in effect_samples(events, k))]
    if ctrl_any:
        w("## コントローラの位置（手元の面が出るか）")
    for side, key, what in (("左", "ctrlL", "報告の押し方"), ("右", "ctrlR", "操作早見表")):
        vals = [str(v) for v in effect_samples(events, key) if str(v) != "-"]
        if not vals:
            continue
        if not any(v.startswith("1") for v in vals):
            verdict("WARN", f"{side}コントローラが一度も繋がっていない（{key}）— "
                            f"{what}の面は出ない")
            continue
        conn = [v for v in vals if v.startswith("1")]
        lost = [v for v in conn if v.endswith("/0")]
        if len(lost) > len(conn) * 0.2:
            verdict("FAIL", f"{side}コントローラは繋がっているのに位置が取れていない回が "
                            f"{len(lost)}/{len(conn)}（{key}）— そのあいだ{what}の面は出ない。"
                            "伏せて置いていないか / 体の陰に入っていないかを見る")
        elif lost:
            verdict("WARN", f"{side}コントローラの位置が一時的に取れなかった回が "
                            f"{len(lost)}/{len(conn)}（{key}）— 短い遮蔽なら正常")
        else:
            verdict("OK", f"{side}コントローラは繋がっていて位置も取れていた（{key}）")
    if ctrl_any:
        w()

    # ---------------- 言語（体験者が読んでいた言語）----------------
    # ⚠⚠ **画にも音にも出ない。** 走行の PNG に写るのは「文字が出ている」ことだけで、
    #    それが日本語だったのか English だったのかは読めない（読めるのは絵を開いた人だけで、
    #    判定スクリプトは通してしまう）。観測の出どころは C# の `ShowTelemetryHost` の
    #    `lang` / `langN`。**片方だけ直すと沈黙して食い違う。**
    # ⚠ **2 つで 1 組**。`lang` だけだと「選ばれなかった（＝ 日本語のまま）」と
    #   「押しても切り替わらない（＝ 壊れている）」がどちらも `ja` で区別できない。
    lang_rows = [e for e in events if e.get("ev") == "sum" and "lang" in e]
    if lang_rows:
        w("## 言語（体験者が読んでいた言語）")
        final = str(lang_rows[-1].get("lang"))
        changes = max((int(fnum(e, "langN", 0) or 0) for e in lang_rows), default=0)
        w(f"  最後まで {final} / 切り替え {changes} 回")

        # ⚠⚠ **切り替えてよいのは体験前の注意書きが出ているあいだだけ**（相 Intro）。
        #    本編が始まってからも変わるなら、`TitleNotice.IsShowing` の門が漏れている ＝
        #    体験者が異変を報告しようと握り込むたびに文面の言語が変わる。
        #    画では「文字が出ている」ようにしか見えないので、ここでしか捕まらない。
        leaked = []
        prev = None
        for e in lang_rows:
            n = int(fnum(e, "langN", 0) or 0)
            if prev is not None and n > prev and str(e.get("phase", "")) not in ("", "Intro"):
                leaked.append((fnum(e, "t", 0.0), str(e.get("phase")), str(e.get("lang"))))
            prev = n
        if leaked:
            where = " / ".join(f"t={t:.1f} 相={ph}→{lg}" for t, ph, lg in leaked[:4])
            verdict("FAIL", f"注意書きが引っ込んだ後にも言語が変わった {len(leaked)} 回（{where}）— "
                            "左 X／Y の門（TitleNotice.IsShowing）が漏れている。"
                            "体験者が異変を報告するたびに文面の言語が変わる")
        elif changes > 0:
            verdict("OK", f"言語は注意書きの中だけで {changes} 回変わり、{final} で本編へ入った")
        else:
            # 自動走行（--walk）は誰も押さないので、これが普通。異常ではない。
            verdict("OK", f"言語は既定（{final}）のまま — 誰も切り替えていない")
        w()

    # ---------------- ホラー軽減モード（2026-09-05・canon/LEDGER.md 0154）----------------
    # ⚠⚠ **画にも録画にも 1 ビットも出ない。** 効くのは音だけで、音は録画に映らない。
    #    観測の出どころは C# の `ShowTelemetryHost` の `relief`（5 つ組）。
    #    **片方だけ直すと沈黙して食い違う。**
    #      `<入っているか>/<切り替えた回数>/<既存の音の倍率>/<陽気な曲の音量>/<曲の再生位置>`
    # ⚠ 自動走行（--walk）は誰も長押ししないので、**入っていないのが普通**。
    #    ここで見るのは「入っていないのに何かが起きていないか」と「入ったなら実際に効いたか」。
    relief_rows = [e for e in events if e.get("ev") == "sum" and "relief" in e]
    if relief_rows:
        w("## ホラー軽減モード（体験者が注意書きで長押しして選ぶ）")

        def _relief(e):
            f = str(e.get("relief", "")).split("/")
            f += ["-"] * (5 - len(f))
            return f

        def _f(s):
            try:
                return float(s)
            except (TypeError, ValueError):
                return None

        toggles = max((int(p[1]) for p in map(_relief, relief_rows) if p[1].isdigit()), default=0)
        on_rows = [e for e in relief_rows if _relief(e)[0] == "1"]
        off_rows = [e for e in relief_rows if _relief(e)[0] == "0"]
        w(f"  入っていた {len(on_rows)}/{len(relief_rows)} 標本 / 切り替え {toggles} 回")

        # ⚠⚠ **走行の頭で持ち越していないか**（ユーザー指定「周回リセット時に次に持ち込まない」）。
        #    前の体験者の選択が残ると、何も押していない人が陽気な曲でホラーを見る。
        head = _relief(relief_rows[0])
        if head[0] != "0" or head[1] not in ("0", "-"):
            verdict("FAIL", f"走行の頭で既に軽減モードだった（relief={'/'.join(head)}）— "
                            "前の体験者から持ち越している"
                            "（TitleScreen.BeginTitle の HorrorRelief.Reset が効いていない）")
        # 平時に既存の音を触っていないか（＝ 倍率が 1.00 のまま）。
        bad_off = [e for e in off_rows
                   if _f(_relief(e)[2]) is not None and abs(_f(_relief(e)[2]) - 1.0) > 0.01]
        if bad_off:
            got = _relief(bad_off[0])[2]
            verdict("FAIL", f"軽減モードでないのに既存の音の倍率が {got}（本来 1.00）— "
                            "AudioListener.volume を誰かが書いている")
        if not on_rows:
            miss = [e for e in relief_rows if _relief(e)[2] == "-"]
            if len(miss) == len(relief_rows):
                verdict("FAIL", "軽減モードの音（HorrorReliefAudio）がシーンに居ない — "
                                "長押ししても音は 1 ビットも変わらない"
                                "（`.\\tools\\unity.ps1 menu scene` で焼き直す）")
            else:
                verdict("OK", "軽減モードには誰も入っていない（自動走行では普通）")
        else:
            # ① 既存の音が半分になったか（engine から読み直した値）。
            bad_gain = [e for e in on_rows
                        if _f(_relief(e)[2]) is None or abs(_f(_relief(e)[2]) - 0.5) > 0.01]
            if bad_gain:
                got = _relief(bad_gain[0])[2]
                verdict("FAIL", f"軽減モードなのに既存の音の倍率が {got}（本来 0.50）— "
                                "AudioListener に書けていない")
            else:
                verdict("OK", f"軽減モードのあいだ既存の音は 0.50 倍だった（{len(on_rows)} 標本）")
            # ② 陽気な曲が**本当に鳴った**か。音量ではなく**再生位置が進んだか**で見る
            #    （音量だけなら、クリップを掴めていなくても 1.00 と出る）。
            times = [_f(_relief(e)[4]) for e in on_rows]
            times = [t for t in times if t is not None]
            if not times or max(times) <= 0.0:
                verdict("FAIL", "軽減モードなのに陽気な曲が鳴っていない"
                                "（再生位置が 0 のまま）— bed_relief の焼き忘れか掴み損ね"
                                "（`py -3.11 tools/ingest-sounds.py --only bed_relief`）")
            elif max(times) < 1.0:
                verdict("WARN", f"陽気な曲の再生位置が {max(times):.1f}s しか進んでいない — "
                                "入った直後に走行が終わったのでなければ止まっている")
            else:
                verdict("OK", f"陽気な曲は {max(times):.1f}s まで進んだ（本当に鳴っている）")
        w()

    # ---------------- タブレットの口（2026-09-11・canon/LEDGER.md 0185 / 0187）----------------
    # 観測の出どころは C# の `ShowTelemetryHost` の `visitor`（5 つ組）。**片方だけ直すと沈黙して食い違う。**
    #   `<口が開いているか>/<受けた累計>/<枠の受理番号>/<書いた受理番号>/<書いた回数>`
    # ⚠ タブレットは卓を経由せず Quest 自身の口（:8090）へ繋ぐ。だから「開いているか」がここにしか無い。
    # ⚠ 値そのもの（どの言語・軽減か）は `lang` / `relief` が持つ。ここは経路の証拠。
    vis_rows = [e for e in events if e.get("ev") == "sum" and "visitor" in e]
    if vis_rows:
        w("## タブレットの口（体験前に選んだ言語・軽減がこの機へ届いたか）")

        def _vis(e):
            f = str(e.get("visitor", "")).split("/")
            f += ["-"] * (5 - len(f))
            return f

        listening = any(_vis(e)[0] == "1" for e in vis_rows)
        recv = max((int(_vis(e)[1]) for e in vis_rows if _vis(e)[1].isdigit()), default=0)
        pend = max((int(_vis(e)[2]) for e in vis_rows if _vis(e)[2].isdigit()), default=0)
        appl = max((int(_vis(e)[3]) for e in vis_rows if _vis(e)[3].isdigit()), default=0)
        n_ap = max((int(_vis(e)[4]) for e in vis_rows if _vis(e)[4].isdigit()), default=0)
        w(f"  口 {'開' if listening else '閉'} / 受けた {recv} / 枠の受理番号 {pend} / 書いた受理番号 {appl} / 書いた回数 {n_ap}")
        if not listening:
            verdict("FAIL", "タブレットの口（:8090）を開けていない — タブレットは繋げない"
                            "（VisitorPortal の bind 失敗。logcat の `[VisitorPortal]` を見る）")
        elif recv == 0:
            verdict("OK", "口は開いているが、タブレットからはまだ何も届いていない（自動走行では普通）")
        else:
            # 注意書きが出ている段（`ev=title stage=Wait` の区間）で、枠の受理番号が書いた受理番号より
            # 大きいまま続いていたら、TitleScreen の書き込みが効いていない。
            # ⚠ 段は `ev=sum` には無い。`ev=title` の縁から各標本の時刻の段を引く。
            tstages = sorted((fnum(e, "t", 0), str(e.get("stage"))) for e in events if e.get("ev") == "title")

            def _stage_at(t):
                cur = "Off"
                for tt, st in tstages:
                    if tt <= t:
                        cur = st
                    else:
                        break
                return cur

            stuck = [e for e in vis_rows
                     if _stage_at(fnum(e, "t", 0)) == "Wait" and _vis(e)[2].isdigit() and _vis(e)[3].isdigit()
                     and int(_vis(e)[2]) > int(_vis(e)[3])]
            if len(stuck) >= 3:
                verdict("FAIL", f"注意書きの段で枠（受理 {pend}）が書かれないまま（{len(stuck)} 標本）— "
                                "TitleScreen.Update の VisitorPrefs.ApplyPending が効いていない")
            elif n_ap == 0:
                verdict("WARN", f"タブレットから {recv} 件受けたが 1 度も書いていない"
                                "（注意書きの段が無い走行なら普通）")
            else:
                verdict("OK", f"タブレットの設定をこの機へ {n_ap} 回書いた（受けた {recv}・受理 {appl}）")
        w()

    # ---------------- 音（鳴ったか）----------------
    # ⚠⚠ **音は録画に映らない。** 画は `quest-record.py` が撮って人が開けば分かるが、
    #    音は実機で被って聴く以外に確かめる手段が無い（しかもこの作業をしているシュビーは
    #    耳が聞こえない）。だから**ログが唯一の証拠**で、ここが緑でなければ音は無い。
    #    観測の出どころは C# の `ShowSoundDirector` / `ShowTelemetryHost`。
    #    **片方だけ直すと沈黙して食い違う**ので、キーを足すときは対で直すこと。
    w("## 音（鳴ったか）")
    sfx_events = [e for e in events if e.get("ev") == "sfx"]
    built = effect_samples(events, "sndBuilt")
    aud = effect_samples(events, "sndAud")
    lpf = effect_samples(events, "sndLpf")
    sw_n = effect_samples(events, "swN")
    sw_alert = effect_samples(events, "swAlert")
    sw_var = effect_samples(events, "swVar")

    if not built and not sfx_events:
        w("  音の観測キーが 1 つも無い（この計装より前のビルドのログ）")
        verdict("WARN", "音を観測していないビルドのログ — 鳴っていたかどうかが分からない。"
                        "`tools/unity.ps1 menu scene` で [Sound] を焼き直したか確認する")
    else:
        # -- 音源を掴めたか（掴めていなければ以降は全部無意味）
        miss = 0
        for b in built:
            try:
                miss = max(miss, int(str(b).split("/", 1)[1]))
            except (IndexError, ValueError):
                pass
        if built:
            w(f"  音源: {built[-1]}（掴めた/掴めなかった）")
            if miss > 0:
                verdict("FAIL", f"音源を {miss} 本掴めていない — 該当の節目は完全に無音。"
                                "`py -3.11 tools/make-sounds.py` を走らせ "
                                "`tools/unity.ps1 menu sound-import` で取り込み直す")
            else:
                verdict("OK", "音源はすべて掴めている")

        # -- 音が定位しているか（`canon/LEDGER.md` 0130）
        #    ⚠⚠ **定位していないことは、画にも録画にも一撃のログにも出ない。**
        #       spatializer（Meta XR Audio）はステレオのクリップを処理しないので、
        #       `spatialBlend=1` でも頭の中で鳴るだけ ＝ **音は鳴っている**。
        #       snd3d=<名簿で掴めた本数>/<ステレオのまま>/<いま 3D で鳴っている敷く音>。
        d3 = effect_samples(events, "snd3d")
        if d3:
            last = str(d3[-1])
            parts = last.split("/")
            if len(parts) == 3:
                try:
                    have, stereo, live = (int(x) for x in parts)
                except ValueError:
                    have = stereo = live = -1
                if have >= 0:
                    w(f"  定位: 名簿 {have} 本 / ステレオのまま {stereo} 本 / "
                      f"いま 3D で鳴っている敷く音 {live} 本")
                    if stereo > 0:
                        verdict("FAIL", f"3D で鳴らす音が {stereo} 本ステレオのまま — "
                                        "音は鳴るが**定位しない**（頭の中で鳴る）。"
                                        "`py -3.11 tools/ingest-sounds.py` / `make-sounds.py` で"
                                        "焼き直して `tools/unity.ps1 menu sound-import`")
                    elif have == 0:
                        verdict("FAIL", "3D の名簿を 1 本も掴めていない — "
                                        "`Resources/Sound/` が空か、名簿と焼いた名前が食い違っている")
                    else:
                        verdict("OK", f"3D で鳴らす音は {have} 本ともモノ（定位する）")
                    # ⚠ 敷く音が 1 本も 3D になっていない ＝ 頭かスクリーンの Transform を
                    #    掴めていない（掴めないときは 2D へ落とす設計なので**無音にはならない**）。
                    peak_live = max((int(str(x).split("/")[2]) for x in d3
                                     if len(str(x).split("/")) == 3
                                     and str(x).split("/")[2].lstrip("-").isdigit()),
                                    default=0)
                    if peak_live == 0:
                        verdict("WARN", "3D で鳴っていた敷く音が 1 度も無い — 装置の唸りも"
                                        "人形の笑いも鳴らない走行なら正常。鳴っていたのに 0 なら"
                                        "頭（CenterEyeAnchor）かスクリーンを掴めていない")
                    else:
                        verdict("OK", f"敷く音が最大 {peak_live} 本 3D で鳴っていた")
        else:
            verdict("WARN", "定位の観測が無い（snd3d が 1 度も出ていない）— "
                            "この計装より前のビルドのログ")

        # -- 人形の笑いの方角（`canon/LEDGER.md` 0130「周囲のランダムな位置」）
        #    ⚠ **どの 2 つも近づきすぎていないこと**を見る。90° の区画へ 1 つずつ入れて
        #      ±35° 振るので、いちばん近い 2 つでも 20° は空く。空いていなければ引き直しが壊れている。
        az = effect_samples(events, "sndAz")
        if az:
            last = str(az[-1])
            try:
                degs = [float(x) for x in last.split("/")]
            except ValueError:
                degs = []
            if len(degs) == 4:
                gaps = []
                for i in range(4):
                    for j in range(i + 1, 4):
                        d_ = abs(degs[i] - degs[j]) % 360.0
                        gaps.append(min(d_, 360.0 - d_))
                w(f"  人形の笑いの方角: {'° / '.join(f'{g:.0f}' for g in degs)}°"
                  f"（いちばん近い 2 つで {min(gaps):.0f}°）")
                if min(gaps) < 20.0:
                    verdict("FAIL", f"笑いの方角が重なっている（最短 {min(gaps):.0f}°）— "
                                    "`SpatialAudio.PickLaughBearings` の区画割りが壊れている")
                else:
                    verdict("OK", f"笑いが 4 方向に散っている（最短 {min(gaps):.0f}°）")

        # -- 劇伴を置き換える 3 つ（`canon/LEDGER.md` 0131）
        #    ⚠⚠ **どれも画に 1 ビットも出ない。** 異世界の演出は画としては出るが、
        #       そのとき音が入れ替わったかは録画からは分からない。
        wind = timed_samples(events, "sndWind")
        curse = timed_samples(events, "sndCurse")
        white = timed_samples(events, "sndWhite")
        score_s = timed_samples(events, "sndScore")

        def _peak(rows):
            vals = [v for _t, v in rows if v is not None]
            return max(vals) if vals else None

        def _both_up(rows_a, rows_b, thr=0.30):
            """両方が `thr` を超えている標本のうち、**次の標本でも超えていたもの**の時刻。

            ⚠ 1 標本だけでは落とさない（2026-09-04）。入れ替えは等パワーの対なので、
            クロスフェードの中央では両方が 0.71 まで立つ。それは「同時に鳴っている」ではなく
            入れ替えの途中。標本は 2 秒おきで入れ替えは最長 2 秒なので、**2 標本続けて**
            両方が立っていれば入れ替えではない。
            """
            b_at = {round(t, 1): (v or 0.0) for t, v in rows_b}
            ts = sorted((t, (v or 0.0)) for t, v in rows_a)
            hits = []
            for i, (t, v) in enumerate(ts):
                if v <= thr or b_at.get(round(t, 1), 0.0) <= thr:
                    continue
                if i + 1 < len(ts):
                    t2, v2 = ts[i + 1]
                    if v2 > thr and b_at.get(round(t2, 1), 0.0) > thr:
                        hits.append(t)
            return hits

        # 異世界（バックルームズ）が映った回数は演出のカットから数える。
        otherworld_steps = [e for e in events if e.get("ev") == "step"
                            and str(e.get("src", "")) == "plate"
                            and fnum(e, "played", 0.0) == 1.0]
        if wind:
            pk = _peak(wind)
            w(f"  別の場所の風: 最大 {pk:.2f}")
            if pk is not None and pk > 0.01:
                verdict("OK", f"異世界が映っているあいだ風が鳴った（最大 {pk:.2f}）")
                # ⚠ 「その時流れてる環境音は切る」— 風が立っているあいだ劇伴が 0 であること。
                bad = _both_up(wind, score_s)
                if bad:
                    verdict("FAIL", f"風と劇伴が同時に鳴っている（{len(bad)} 標本）— "
                                    "0131 は「その時流れてる環境音は切る」")
                else:
                    verdict("OK", "風が鳴っているあいだ劇伴は切れていた")
            elif otherworld_steps:
                verdict("FAIL", "別の場所のカットが画に出たのに風が 1 度も鳴っていない — "
                                "`ShowSoundDirector.OtherworldCuePrefix`（backrooms）と "
                                "show.json の cue id が食い違っていないか見る")
            else:
                verdict("WARN", "別の場所の演出に到達していない走行（風は鳴らなくて正常）")
        else:
            verdict("WARN", "風の観測が無い（sndWind）— この計装より前のビルドのログ")

        if curse:
            pk = _peak(curse)
            w(f"  呪いの 2 本: 最大 {pk:.2f}")
            reached = any(fnum(e, "lap", 0.0) is not None
                          and (fnum(e, "lap", 0.0) or 0) >= 3 for e in events if e.get("ev") == "seg")
            if pk is not None and pk > 0.01:
                verdict("OK", f"3 周目 B から呪いの 2 本が鳴った（最大 {pk:.2f}）")
                bad = _both_up(curse, score_s)
                if bad:
                    verdict("FAIL", f"呪いの 2 本と劇伴が同時に鳴っている（{len(bad)} 標本）— "
                                    "0131 は「クロスフェードで入れ替える」")
            elif reached:
                verdict("FAIL", "3 周目まで進んだのに呪いの 2 本が 1 度も鳴っていない — "
                                "`bed_beat` / `bed_horror2` を掴めているか（sndBuilt）を見る")
            else:
                verdict("WARN", "3 周目 B に到達していない走行（呪いの 2 本は鳴らなくて正常）")

        if white:
            pk = _peak(white)
            w(f"  ホワイトノイズ: 最大 {pk:.2f}")
            released = any("coarseRel" in e for e in events)
            if pk is not None and pk > 0.01:
                verdict("OK", f"呪いが排除された後にホワイトノイズが鳴った（最大 {pk:.2f}）")
            elif released:
                verdict("FAIL", "呪いが排除されたのにホワイトノイズが鳴っていない — "
                                "`bed_white` を掴めているか（sndBuilt）を見る")

        # -- 心音（`canon/LEDGER.md` 0175）
        #    ⚠⚠ **画にも録画にも一撃のログにも 1 ビットも出ない。** しかも素材は正体が 150Hz より
        #       下にあるので（内蔵SP -21.1dB）、実機で耳を当てても確かめられない。ここが唯一の証拠。
        #    ⚠ **劇伴の入れ替えの仲間ではない**（足す音）。だから下の `bg_power`（背景の総量が
        #       1 から動かないか）には**入れない** — 入れると正しい走行が毎回 1 を超える。
        heart = timed_samples(events, "sndHeart")
        if heart:
            pk = _peak(heart)
            on = [t for t, v in heart if (v or 0.0) > 0.01]
            reached3 = any((fnum(e, "lap", 0.0) or 0) >= 3
                           for e in events if e.get("ev") == "seg")
            span = f"（{on[0]:.1f}s 〜 {on[-1]:.1f}s ＝ {on[-1] - on[0]:.0f} 秒）" if on else ""
            w(f"  心音: 最大 {pk:.2f}{span}")
            if pk is not None and pk > 0.01:
                verdict("OK", f"心音が鳴った（最大 {pk:.2f}）{span}")
                # ⚠ 終わりの縁だけ見る。**呪いとの重なりは落とさない** — 心音が退く縁と
                #   呪いが立つ縁は同じ所（3 周目 B）なので、入れ替わりの途中は必ず重なる。
                #   落とすべきは「最後まで鳴りっぱなし」の方。
                tail = [v for _t, v in heart[-2:]]
                if tail and min(tail) > 0.30:
                    verdict("FAIL", f"心音が走行の最後まで鳴っている（最後の標本 {tail[-1]:.2f}）— "
                                    "終わりの縁（3 周目 A の録画カットの終わり / 3 周目 B）が "
                                    "1 度も来ていない")
            elif reached3:
                verdict("FAIL", "3 周目まで進んだのに心音が 1 度も鳴っていない — "
                                "`bed_heart` を掴めているか（sndBuilt）と、"
                                "接近の演出が走ったか（ev=take L2C2#1）を見る")
            else:
                verdict("WARN", "3 周目 A に到達していない走行（心音は鳴らなくて正常）")
        else:
            verdict("WARN", "心音の観測が無い（sndHeart）— この計装より前のビルドのログ")

        # -- 目が開く音（0131）
        #    ⚠ **目が開いた数と対で見る。** 開いているのに 0 なら鳴っていない。
        eye_sfx = effect_samples(events, "sndEye")
        if eye_sfx:
            last = str(eye_sfx[-1])
            parts = last.split("/")
            if len(parts) == 2 and parts[0].isdigit():
                hits, big = int(parts[0]), parts[1]
                eyes_open = max((int(str(v).split("/")[1]) for v in effect_samples(events, "eyes")
                                 if len(str(v).split("/")) >= 2
                                 and str(v).split("/")[1].isdigit()), default=0)
                w(f"  目が開く音: {hits} 発（開いた目は最大 {eyes_open} 個 / 大きい目 {big}）")
                if eyes_open <= 1:
                    verdict("WARN", "目が 1 つも開いていない走行（音も鳴らなくて正常）")
                elif hits == 0:
                    verdict("FAIL", f"目が {eyes_open} 個開いたのに一撃が 1 発も鳴っていない — "
                                    "`sfx_eye_1..6` を掴めているか（sndBuilt）を見る")
                elif big == "0":
                    verdict("FAIL", "大きい目の音が鳴っていない（sfx_eye_big）— "
                                    "見開く瞬間に山が来る仕掛けが効いていない")
                else:
                    verdict("OK", f"目の一撃が {hits} 発・大きい目の音も鳴った")

        # -- **実際に音量を書いたか。** 指示がいくら正しくてもここが 0 なら無音
        if aud:
            vals = [fstr(a) for a in aud]
            vals = [v for v in vals if v is not None]
            peak = max(vals) if vals else 0.0
            w(f"  鳴っていた音量の最大: {peak:.2f}（敷く音の合計）")
            if peak < 0.01:
                verdict("FAIL", "走行中ずっと音量が 0 だった — 音は 1 度も出ていない。"
                                "[Sound] がシーンに焼かれているか見る "
                                "(grep で m_Name: '[Sound]' を Assets/Scenes/Main.unity から探す)")
            else:
                verdict("OK", f"敷く音が鳴っていた（最大 {peak:.2f}）")

        # -- 隔離が**音にも**出たか（部屋の帯域が閉じたか）
        if lpf:
            vals = [fstr(x) for x in lpf]
            vals = [v for v in vals if v is not None]
            if vals:
                w(f"  部屋の帯域: {min(vals):.1f}kHz 〜 {max(vals):.1f}kHz")
                if max(vals) - min(vals) < 1.0:
                    verdict("WARN", "部屋の帯域が動いていない — 隔離が閉じても音が変わっていない"
                                    "（導入まで走らなかった走行なら正常）")
                else:
                    verdict("OK", "隔離が閉じたときに部屋の帯域が狭まっている")

        # -- 劇伴が本編で鳴り続けているか（`canon/LEDGER.md` 0115）
        #    ⚠⚠ 2026-08-23 に**逆になった**判定。旧版（0088）は「本編で鳴っていたら FAIL」で、
        #    いまは**本編で鳴っていなかったら FAIL**。周ごとの環境音（旧 `sndAmb`）は退役し、
        #    1〜3 周目の背景はこれ 1 本しか無いので、ここが 0 なら**背景が丸ごと無音**。
        #    ⚠ 鳴らしているのは BgmDirector なので `sndAud`（敷く音の合計）には 1 ビットも出ない。
        score = timed_samples(events, "sndScore")
        if score:
            peak = max(v for _t, v in score)
            w(f"  劇伴: 最大 {peak:.2f}（黒から 3 周目の終わりまで鳴り続ける）")
            if peak <= 0.01:
                verdict("FAIL", "劇伴が 1 度も鳴っていない（sndScore が 0 のまま）— "
                                "**本編の背景が無音**。BgmDirector が居ないか "
                                "show.json の bgm 指定が無い（`canon/LEDGER.md` 0115）")
            else:
                # 本編（`phase=Run` かつ終幕より前）の最小値を見る。1 度でも落ちていたら、
                # そこが「区切り」として聞こえる ＝ ユーザーが言った「流れ続ける」に反する。
                #
                # ⚠⚠ **終幕と `Finished` を外す。** どちらも劇伴が 0 へ落ちるのが正なので、
                #    入れると**正しい走行が毎回 FAIL する**（実測の走行は 71 標本のうち
                #    15 が `Finished` だった）。終幕の縁は `ev=outro` の最初の時刻で切る。
                # ⚠⚠ **終幕の縁は `stage=Off` を除いて取る**（2026-09-04）。起動直後に
                #    `ev=outro stage=Off` が 1 本出るので、それを含めて min を取ると t≈5 秒が
                #    「終幕の頭」になり、**本編の標本が 1 つも残らず、この判定は黙って飛んでいた**
                #    （走行 20260903_210843: 劇伴が 0 まで落ちた標本が 4 つあるのに何も言わなかった）。
                t_outro = min([fnum(e, "t", 0.0) for e in outro
                               if str(e.get("stage", "")) not in ("", "Off")], default=None)

                def in_run(e):
                    if e.get("phase") != "Run":
                        return False
                    return t_outro is None or fnum(e, "t", 0.0) < t_outro

                # ⚠⚠ **見るのは劇伴 1 本ではなく「背景の総量」**（2026-09-04）。0131 から劇伴は
                #    風・呪いの 2 本・ホワイトノイズに**入れ替わる**ので、劇伴だけ見ると正しい走行が
                #    毎回 FAIL する。入れ替えは等パワーの対（`SoundBedLogic.Tick`）なので、
                #    4 つの**パワーの和**は途中でも 1 から動かない。ここが 0.5 を割ったら谷。
                #    ⚠ 初版（Perceptual の対）は中央で 0.20 まで凹んでいた — その走行はここで落ちる。
                def bg_power(e):
                    p = 0.0
                    for k in ("sndScore", "sndWind", "sndCurse", "sndWhite"):
                        v = fnum(e, k)
                        if v is not None:
                            p += v * v
                    return p

                run = [(fnum(e, "t", 0.0), bg_power(e)) for e in events
                       if e.get("ev") == "sum" and "sndScore" in e and in_run(e)]
                holes = [t for t, p in run if p < 0.5]
                if run and holes:
                    verdict("FAIL", f"本編で背景が凹んでいる（{len(holes)} 標本・最初は t={holes[0]:.1f}s）— "
                                    "劇伴と置き換える 3 つ（風・呪い・ホワイトノイズ）の合成パワーが "
                                    "0.5 を割った。等パワーの入れ替えなら和は 1 から動かない")
                elif run:
                    verdict("OK", f"本編で背景が途切れていない"
                                  f"（劇伴＋置き換えの合成パワー 最小 {min(p for _t, p in run):.2f}）")
                # ⚠⚠ **取り分が 1 でも、音源が止まっていれば無音。** `sndScore` は
                #    `ShowSoundDirector` が書いた倍率でしかなく、鳴らしているのは BgmDirector。
                #    0115 より前は本編で劇伴が黙っている前提だったので、レーンが止まっていても
                #    誰も困らなかった。**いまはこれが背景そのもの**なので、両方見る。
                lane = [str(e.get("bgm", "")) for e in events
                        if e.get("ev") == "sum" and "bgm" in e and in_run(e)]
                if lane and "0" in lane:
                    verdict("FAIL", f"本編で BGM のレーンが止まっている"
                                    f"（{lane.count('0')}/{len(lane)} 標本で bgm=0）— "
                                    "取り分（sndScore）が 1 でも音源が止まっていれば無音")
                elif lane:
                    verdict("OK", f"本編で BGM のレーンが生きていた（{len(lane)} 標本すべて bgm=1）")
                else:
                    verdict("OK", f"劇伴が鳴っている（最大 {peak:.2f}・本編まで走っていない走行）")

        # -- 節目の一撃
        by_id = {}
        for e in sfx_events:
            by_id[str(e.get("id", "?"))] = by_id.get(str(e.get("id", "?")), 0) + 1
        if by_id:
            w("  節目の音: " + " / ".join(f"{k}×{v}" for k, v in sorted(by_id.items())))
        if "MISSING" in by_id:
            verdict("FAIL", "音源が見つからない節目がある（ev=sfx id=MISSING）")

        # 導入まで走ったなら、3 つの節目は必ず鳴っているはず
        # ⚠ 「隔離が閉じる」（SealClose）と「装置が点く」（Swap）は 2026-08-15 に判定から外した。
        #    段が旧構成へ戻って隔離が閉じる段が無くなり、スクリーンが出る瞬間は
        #    ユーザー指定の音源を持つ ScreenOn が取る（canon/LEDGER.md 0044）。
        #    ここに残すと「出るはずのものが出ない」と毎回誤検出する。
        intro_ran = any(e.get("stage") == "Swap" for e in intro)
        if intro_ran:
            # ⚠⚠ 2026-08-16 に 2 つ動いた（`canon/LEDGER.md` 0057）。
            #    - 鈴（Bell）は**段 5 のクロスフェードが終わった所**（＝完全にスクリーンに
            #      なったとき）へ。段 5 は 4.5 秒あるので、待ちで取り逃す心配は無い
            #    - ノイズ（ScreenNoise）は**鳴らさない**。ここに残すと毎回誤検出する
            want_ids = ("Bell", "Shatter", "ScreenOn")
            for want, label in (("Bell", "完全にスクリーンになった所の鈴"),
                                ("Shatter", "現実が割れる"),
                                ("ScreenOn", "スクリーンが出る")):
                if by_id.get(want, 0) == 0:
                    verdict("FAIL", f"導入は段 Swap まで進んだのに「{label}」の音が鳴っていない"
                                    f"（ev=sfx id={want} が 0 本）")
            if all(by_id.get(k, 0) > 0 for k in want_ids):
                verdict("OK", "導入の 3 つの節目が全部鳴った")

        # -- 終幕の電源断（`canon/LEDGER.md` 0125）--------------------------------
        #    ⚠⚠ **音は録画に映らない**ので、`ev=sfx id=PowerOff` が唯一の証拠。
        #       画（clMax）が正しく潰れていても、音だけ黙って落ちていることがある
        #       （音源が焼かれていない / Resources に入っていない / 発声器が起きていない）。
        #    ⚠ **終幕まで走った走行だけ見る**（途中で切れた走行で毎回 FAIL を出さない）。
        outro_ran = any(e.get("stage") in ("Collapse", "Dark", "Report", "Done") for e in outro)
        if outro_ran:
            n_off = by_id.get("PowerOff", 0)
            if n_off == 0:
                verdict("FAIL", "終幕まで進んだのに電源が落ちる音が鳴っていない"
                                "（ev=sfx id=PowerOff が 0 本）— 画だけ潰れて無音で終わっている")
            elif n_off > 1:
                verdict("FAIL", f"電源が落ちる音が {n_off} 回鳴っている（1 回のはず）— "
                                "終幕を抜ける前に再武装している")
            else:
                verdict("OK", "終幕の電源が落ちる音が 1 度だけ鳴った")
        # -- 人形がたくさん出てくる所の笑い（`canon/LEDGER.md` 0062）
        #    鳴る縁は「締めのカットが報告を待ち始めたこと」なので、**その場面まで走ったときだけ**
        #    見る（走り切らなかった走行で毎回 FAIL を出さない）。待ったことの証拠は
        #    `ev=comms` の `wait=1`（③の連絡と同じ signal を音も読んでいる）。
        #    ⚠⚠ **一撃ではなくループ**（2026-08-16・0066 で作り替えた）ので、`ev=sfx` には出ない。
        #       見るのは `ev=sum` の `sndDolls`（いま書いている音量）。
        closing_ran = any(str(e.get("wait")) == "1" for e in comms)
        dolls = [float(v) for v in effect_samples(events, "sndDolls")
                 if str(v) not in ("-", "nc")]
        if closing_ran or (dolls and max(dolls) > 0.01):
            if not dolls:
                verdict("WARN", "人形の笑いの観測が無い（sndDolls が 1 度も出ていない）— "
                                "古い APK か、ShowSoundDirector が居ない")
            elif max(dolls) <= 0.01:
                verdict("FAIL", "締めのカットが報告を待ったのに人形が笑っていない"
                                "（sndDolls が 0 のまま — bed_dolls_laugh を掴めているか、"
                                "ShowSoundDirector が TimelineDirector を掴めているか）")
            else:
                verdict("OK", f"人形がたくさん出てくる所で笑った（sndDolls 最大 {max(dolls):.2f}）")
            # 押したあとも鳴っていたら、止める経路が壊れている（次の体験者へ持ち越す）。
            if dolls and dolls[-1] > 0.01 and any(e.get("id") in ("MarkLogged", "MarkNothing")
                                                  for e in comms):
                verdict("WARN", "報告のあとも人形が笑ったまま走行が終わっている"
                                f"（最後の sndDolls={dolls[-1]:.2f}）")

        # -- **周囲に大勢いるか**（`canon/LEDGER.md` 0139）
        #    ⚠⚠ **画にも録画にも 1 ビットも出ない。** 笑いは体ごとに 1 声で焼いてあり、
        #       体ごとに別の方角へ置く。1 点から鳴っていても音は鳴るので、ここだけが証拠。
        #       満点は 4 周目 A の群れで 8・3 周目 C で 7（一人 1 ＋ 2 体 ＋ 4 体）。
        laugh = [int(v) for v in effect_samples(events, "sndLaugh")
                 if str(v) not in ("-", "nc") and str(v).lstrip("-").isdigit()]
        if not laugh:
            verdict("WARN", "笑いの点の観測が無い（sndLaugh）— この計装より前のビルドのログ")
        else:
            peak = max(laugh)
            w(f"  笑いが鳴っていた場所の数: 最大 {peak}")
            if peak == 0:
                verdict("WARN", "笑いが 1 度も鳴っていない走行（3 周目まで走っていなければ正常）")
            elif peak == 1:
                verdict("FAIL", "笑いが 1 か所からしか鳴っていない — 体ごとに焼いた 15 本を"
                                "掴めていない（`py -3.11 tools/ingest-sounds.py --only "
                                "bed_dolls_laugh bed_doll_one bed_dolls_grow_a bed_dolls_grow_b` "
                                "の後に `tools/unity.ps1 menu sound-import`）")
            elif peak < 7:
                verdict("WARN", f"笑いの点が最大 {peak} か所 — 群れ（8）にも 3 周目 C（7）にも"
                                "届いていない。そこまで走っていない走行なら正常")
            elif peak > 15:
                # 体は 15 しか居ない（群れ 8 ＋ 3 周目の 1+2+4）。超えたら数え方が壊れている。
                verdict("FAIL", f"笑いの点が最大 {peak} か所 — 焼いてある体は 15 しか無い。"
                                "数え方が壊れている（ShowSoundDirector.PlaceLaughLayer）")
            elif peak > 8:
                # ⭐ **これは正常。** 3 周目 C の 7 体が退きながら 4 周目 A の群れ 8 体が立つ
                #    2 秒ほど、**両方が実際に鳴っている**（実測 t=121.4 で 群れ 0.11 / 入替 2.26）。
                verdict("OK", f"笑いが最大 {peak} か所から鳴った"
                              "（3 周目の 7 体と群れの 8 体が入れ替わる 2 秒は重なる）")
            else:
                verdict("OK", f"笑いが最大 {peak} か所から鳴った（周囲に大勢いる）")
            # ⭐ **増え方がそのまま出る。** 3 周目は 1 → 3 → 7、4 周目 A で 8。
            #    ⚠ 数が増えずに音量だけ上がっているなら、体ごとに分けた意味が消えている。
            steps = sorted({v for v in laugh if v > 0})
            if len(steps) >= 2:
                w(f"  笑いの増え方: {' → '.join(str(v) for v in steps)} か所")

        # -- 入れ替わった人形の笑い（`canon/LEDGER.md` 0086）
        #    ⚠ これもループなので `ev=sfx` には出ない。見るのは `ev=sum` の
        #      `sndSwap`（3 枚の合計の音量）と `sndSwell`（増え具合 0..1）。
        #    ⚠ **2 つを対で見る。** 合計だけでは「増えた」のか「大きくなった」のか分けられない。
        swap = timed_samples(events, "sndSwap")
        swell = timed_samples(events, "sndSwell")
        # 区間の窓（`ev=zone` の次の区間まで）。増えてよいのは 3 周目 C だけ。
        zones = [(fnum(e, "t", 0.0), int(fnum(e, "cam", -1)), int(fnum(e, "lap", -1)))
                 for e in events if e.get("ev") == "zone"]
        zones.sort()
        windows = [(t, zones[i + 1][0] if i + 1 < len(zones) else 1e9, cam, lap)
                   for i, (t, cam, lap) in enumerate(zones)]
        lap3 = [wd for wd in windows if wd[3] == 3]
        if lap3 or swap:
            if not swap:
                verdict("WARN", "入れ替わった人形の笑いの観測が無い（sndSwap が 1 度も出ていない）"
                                "— 古い APK か、ShowSoundDirector が居ない")
            else:
                peak = max(v for _t, v in swap)
                # ⚠⚠ **画と突き合わせる。** 映像の中に人形が立っている ＝ 笑うはずの縁。
                #    周の番号ではなく**効果どうし**を比べるので、台本が変わっても効く。
                # ⚠⚠ **`cg=` の 2 つ目を読む**（2026-08-22）。1 つ目は「CG の層が描かれているか」で、
                #    覆いの形を出す**人の代役**でも 1 になる。1 つ目で判定していた頃は、
                #    2 周目 C の保持（代役）を「人形が立っている」と読んで
                #    「立っているのに笑っていない」と誤って FAIL を出していた。
                #    旧ログ（1 桁の `cg=1`）は人形かを区別できないので**判定から外す**。
                mute = [e for e in events
                        if e.get("ev") == "sum" and str(e.get("cg", "")).endswith("/1")
                        and (fnum(e, "sndDolls", 0.0) or 0.0) <= 0.05
                        and "sndSwap" in e and (fnum(e, "sndSwap", 0.0) or 0.0) <= 0.01]
                if len(mute) >= 2:
                    verdict("FAIL", f"映像の中に人形が立っているのに笑っていない（cg=…/1 で sndSwap=0 が "
                                    f"{len(mute)} 回）— bed_doll_one を掴めているか、"
                                    "ShowSoundDirector が ShowCgLayer を掴めているか")
                if lap3 and peak <= 0.01:
                    verdict("FAIL", "3 周目に入ったのに入れ替わった人形が笑っていない"
                                    "（sndSwap が 0 のまま — bed_doll_one を掴めているか、"
                                    "ShowSoundDirector が ShowCgLayer を掴めているか）")
                elif peak > 0.01:
                    verdict("OK", f"入れ替わった人形が笑った（sndSwap 最大 {peak:.2f}）")
                # 増えるのは C だけ。A・B で増えていたら区間の判定が壊れている。
                for t0, t1, cam, _lap in lap3:
                    grew = [v for t, v in swell if t0 <= t < t1]
                    if not grew:
                        continue
                    if cam == 2:
                        w(f"  3 周目 C（{t1 - t0:.0f} 秒）で増え具合 {max(grew):.2f} まで")
                        if t1 - t0 >= 8.0 and max(grew) < 0.3:
                            verdict("FAIL", f"3 周目 C に {t1 - t0:.0f} 秒居たのに人形が増えていない"
                                            f"（sndSwell 最大 {max(grew):.2f}）")
                    elif max(grew) > 0.05:
                        verdict("FAIL", f"3 周目 カメラ {cam} で人形が増えている"
                                        f"（sndSwell 最大 {max(grew):.2f} — 増えるのは C だけ）")
                # 締めの群れと重ならないこと（重なると「たくさん出てくる」が濁る）。
                # ⚠ 同じ `ev=sum` の行に両方載るので、行ごとに突き合わせる。
                # ⚠ 1 行だけなら**渡している最中**（群れが 0.25 秒で立ち、一人ぶんが 0.2 秒で引く）。
                #    2 行以上 ＝ 2 秒以上重なっていたら、渡していないで並んで鳴っている。
                both = [e for e in events
                        if e.get("ev") == "sum"
                        and (fnum(e, "sndSwap", 0.0) or 0.0) > 0.05
                        and (fnum(e, "sndDolls", 0.0) or 0.0) > 0.5]
                if len(both) >= 2:
                    verdict("FAIL", f"締めの群れと入れ替わりの笑いが同時に鳴っている"
                                    f"（{len(both)} 回・4 周目 A では群れだけが鳴る）")

        if intro_ran:
            # ⚠ 鳴らさなくなったものが鳴っていたら**戻ってしまっている**（0057）。
            if by_id.get("ScreenNoise", 0) > 0:
                verdict("FAIL", f"ノイズが {by_id['ScreenNoise']} 回鳴っている — "
                                "2026-08-16 に鳴らさないと決めたもの（SoundCueLogic を見る）")
        # ⚠ 家鳴り（Creak）は 2026-08-15 に全廃した（`canon/LEDGER.md` 0049）。
        #    鳴っていたら**戻ってしまっている**ので落とす。
        if by_id.get("Creak", 0) > 0:
            verdict("FAIL", f"家鳴りが {by_id['Creak']} 回鳴っている — 2026-08-15 に全廃したもの"
                            "（SoundCueLogic の Creak の経路が復活していないか見る）")

        # -- カメラ切替の音（1 回の体験でいちばん多く鳴る音）
        if sw_n:
            last = str(sw_n[-1])
            if last == "nc":
                verdict("FAIL", "切替音の音源が無い — カメラ切替がすべて無音。"
                                "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                "`tools/unity.ps1 menu sound-import`")
            elif last != "-":
                zone_switches = len([e for e in events if e.get("ev") == "screen"])
                w(f"  カメラ切替の音: {last} 回（画面の切替 {zone_switches} 回）")
                try:
                    if int(last) == 0 and zone_switches > 0:
                        verdict("FAIL", f"画面は {zone_switches} 回切り替わったのに切替音が 0 回")
                    elif int(last) > 0:
                        verdict("OK", f"切替音が {last} 回鳴った")
                except ValueError:
                    pass
        # -- 警告つきの切替音（人形視点が差し込まれるカット・canon/LEDGER.md 0106）
        #    ⚠ 画にも動画にも違いが出ないので、ここが唯一の証拠。
        if sw_alert:
            last = str(sw_alert[-1])
            want = exp.get("switchSfxSteps", 0)
            if last == "nc":
                verdict("FAIL", "警告つきの切替音の音源が無い — 人形視点の差し込みも"
                                "素の切替音で鳴っている。"
                                "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                "`tools/unity.ps1 menu sound-import` を走らせる")
            elif last != "-":
                # ⚠⚠ 2026-09-04 から 3 つ組（0145）: <回数>/<直前の倍率>/<警告だけを掴めたか>。
                #    古いログは回数だけなので、分けて読む。
                parts = last.split("/")
                w(f"  警告つきの切替音: {parts[0]} 回（著作は {want} カット）")
                try:
                    n = int(parts[0])
                    if n == 0 and want > 0:
                        verdict("WARN", f"警告つきの切替音が 1 度も鳴っていない"
                                        f"（著作は {want} カット）— その差し込みに到達して"
                                        "いないか、配線が切れている")
                    elif n > 0:
                        verdict("OK", f"警告つきの切替音が {n} 回鳴った")
                except ValueError:
                    pass

                if len(parts) >= 3:
                    # ⚠⚠ **「回を重ねて大きくなったか」はここでしか分からない**（0145）。
                    #    画にも録画にも出ないし、回数が正しくても大きさは一定でありうる
                    #    （0134 で踏んだ形と同じ）。
                    if parts[2] != "1":
                        verdict("FAIL", "警告だけの音源を掴めていない（sfx_switch_warn）— "
                                        "混ぜた 1 本で鳴っているので、警告は回を重ねても"
                                        "大きくならない。"
                                        "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                        "`tools/unity.ps1 menu sound-import`")
                    else:
                        gains = []
                        for v in sw_alert:
                            q = str(v).split("/")
                            if len(q) < 2:
                                continue
                            try:
                                g = float(q[1])
                            except ValueError:
                                continue
                            if g > 0 and (not gains or abs(g - gains[-1]) > 1e-4):
                                gains.append(g)
                        if gains:
                            w("  警告の育ち: " + " → ".join(f"{g:.2f}" for g in gains))
                        if len(gains) >= 2 and gains[-1] > gains[0] + 1e-3:
                            verdict("OK", f"警告が回を重ねて大きくなった"
                                          f"（{gains[0]:.2f} → {gains[-1]:.2f}）")
                        elif len(gains) >= 2:
                            verdict("FAIL", f"警告が {len(gains)} 回鳴ったのに大きくなっていない"
                                            f"（{gains[0]:.2f} → {gains[-1]:.2f}）— "
                                            "ラン開始で育ちが落ちていないか、"
                                            "AlertEscalationLogic が効いていない")

        # -- 切替音の変種（`canon/LEDGER.md` 0112）
        #    ⚠⚠ **画にも動画にも違いが出ない。** 「毎回同じではなく」が効いた証拠はここだけ。
        #       swVar=<焼けた本数>/<この走行で鳴った異なり数>。
        if sw_var:
            last = str(sw_var[-1])
            if last != "-" and "/" in last:
                try:
                    built, used = (int(x) for x in last.split("/", 1))
                except ValueError:
                    built = used = -1
                if built >= 0:
                    w(f"  切替音の変種: {built} 本を掴み、{used} 種が鳴った")
                    if built < SWITCH_VARIANTS_WANT:
                        verdict("FAIL",
                                f"切替音の変種が {built}/{SWITCH_VARIANTS_WANT} しか無い — "
                                "焼き忘れか `menu sound-import` 忘れ。そのぶん同じ波形が並ぶ。"
                                "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                "`tools/unity.ps1 menu sound-import`")
                    elif used <= 1 and _sw_played(sw_n) >= 2:
                        verdict("FAIL",
                                f"{built} 本あるのに {used} 種しか鳴っていない — "
                                "変種を選ぶ経路（`SwitchAudioCue.Pick`）が効いていない")
                    elif used >= 2:
                        verdict("OK", f"切替音が {used} 種で鳴った（{built} 本中）")

        # -- 人形の呼びかけ（2 周目 C の追いつき・canon/LEDGER.md 0109）
        #    ⚠ 1 回の体験で 1 度しか鳴らない。`ev=sfx id=DollCall` が唯一の証拠で、
        #      画にも動画にも差は出ない（声はカットより 0.2 秒長いだけで画を止めない）。
        want_call = exp.get("dollCallSteps", 0)
        calls = [e for e in sfx_events if str(e.get("id")) == "DollCall"]
        if want_call:
            w(f"  人形の呼びかけ: {len(calls)} 回（著作は {want_call} カット）")
            if not calls:
                verdict("WARN", f"人形の呼びかけが 1 度も鳴っていない（著作は {want_call} カット）— "
                                "①そのカットの素材（当日撮る人形視点）が無くて**カットごと飛んだ** "
                                "②その差し込みに到達していない ③音源を掴めていない、の順に疑う。"
                                "①は下の「演出のカット」節の『素材（clip / still）が解決できない』に出る "
                                "＝ 当日まではこれが正常な 0 で、実装の証拠にはならない。"
                                "③なら `py -3.11 tools/ingest-sounds.py --only sfx_doll_call` の後に "
                                "`tools/unity.ps1 menu sound-import`")
            else:
                verdict("OK", f"人形の呼びかけが {len(calls)} 回鳴った")
                # ⚠⚠ **「後ろから」が成立したかは sndCall にしか出ない**
                #    （`canon/LEDGER.md` 0130）。0 が正面 / 180 が真後ろ。
                #    -1 は頭（CenterEyeAnchor）を掴めずに 2D で鳴らしたということ ＝
                #    **音は鳴っているので画にもログの回数にも出ない**。
                call_az = [str(v) for v in effect_samples(events, "sndCall") if str(v) != "-"]
                seen_az = [int(v.split("/", 1)[1]) for v in call_az
                           if "/" in v and v.split("/", 1)[1].lstrip("-").isdigit()
                           and int(v.split("/", 1)[0]) > 0]
                if not seen_az:
                    verdict("WARN", "呼びかけの方角の観測が無い（sndCall）— "
                                    "この計装より前のビルドのログ")
                elif seen_az[-1] < 0:
                    verdict("FAIL", "呼びかけが 2D で鳴った（頭の Transform を掴めていない）— "
                                    "「後ろから」が成立していない。CenterEyeAnchor を探す経路"
                                    "（`ShowSoundDirector.Resolve`）を見る")
                elif not (145 <= seen_az[-1] <= 215):
                    verdict("FAIL", f"呼びかけが後ろから鳴っていない（方角 {seen_az[-1]}°・"
                                    "0 が正面 / 180 が真後ろ）")
                else:
                    verdict("OK", f"呼びかけが後ろから鳴った（方角 {seen_az[-1]}°）")
        elif calls:
            verdict("FAIL", f"著作に無い人形の呼びかけが {len(calls)} 回鳴った — "
                            "show.json の dollCall と実機が食い違っている")

        # -- カットが差し替える劇伴（呼びかけの次のカット・canon/LEDGER.md 0119）
        #    ⚠ **これも画に出ない。** 追いついた文脈を運ぶのは黒い覆いではなく曲になったので、
        #      ここが鳴っていなければ **2 周目 C の追いつきが 3 周目 A へ何も渡していない**。
        want_tracks = exp.get("stepBgmTracks") or []
        if want_tracks:
            lane_ev = [e for e in events if e.get("ev") == "bgm"]
            played = [str(e.get("trk", "")) for e in lane_ev]
            w(f"  カットが替える劇伴: 著作 {'/'.join(want_tracks)} / "
              f"実機のレーン {' → '.join(played) if played else '（1 度も変わっていない）'}")
            missing = [t for t in want_tracks if t not in played]
            if missing:
                verdict("WARN", f"カットが指した劇伴が鳴っていない（{'/'.join(missing)}）— "
                                "①そのカットの素材が無くて**カットごと飛んだ** "
                                "②その差し込みに到達していない "
                                "③`bgmTracks` にその id が無い ④卓が音源を配れていない、の順に疑う。"
                                "①なら下の「演出のカット」節に理由が出る")
            else:
                verdict("OK", f"カットが劇伴を差し替えた（{'/'.join(want_tracks)}）")
                # ⚠ 「鳴った」だけでは足りない。**呼びかけの後**でなければ演出として成立しない。
                if calls:
                    t_call = min(fnum(e, "t", 0.0) for e in calls)
                    t_swap = min([fnum(e, "t", 0.0) for e in lane_ev
                                  if str(e.get("trk", "")) in want_tracks], default=None)
                    if t_swap is not None and t_swap < t_call:
                        verdict("FAIL", f"劇伴が呼びかけより先に替わっている"
                                        f"（劇伴 {t_swap:.1f}s < 呼びかけ {t_call:.1f}s）— "
                                        "カットの並びが著作と食い違っている")

    # ---------------- 効果の実在 ----------------
    # 「段が進んだ」「演出が走った」は、画・音に何かが出たことを意味しない。
    # 2026-07-31 に FAIL ゼロ・演出 7 本 OK と判定した走行の画を録ったら、導入演出が 1 段も
    # 出ていなかった（パススルー未初期化 / シェーダのビルド剥がれ / カメラ背景が不透明）。
    # ここは「その結果、画・音に何かが出たか」だけを見る節。
    # ---------------- 乱れ（回数で大きくなったか）----------------
    # `canon/LEDGER.md` 0055。起きるたびに強さ・尺・音が少しずつ上がり、終盤で頭打ちになる。
    # ⚠ level（glitch=）だけ見ても**台本が強いのか回数で育ったのか区別できない**ので、
    #   回数（glN）と進み（glE）を対で見る。観測の出どころは C# の `ShowTelemetryHost`。
    #   **片方だけ直すと沈黙して食い違う。**
    gl_n = [int(v) for v in effect_samples(events, "glN") if str(v).lstrip("-").isdigit()]
    gl_e = [float(v) for v in effect_samples(events, "glE")
            if str(v).replace(".", "", 1).lstrip("-").isdigit()]
    if gl_n:
        w("## 乱れ（回数で大きくなったか）")
        w(f"  起きた回数 {max(gl_n)} 回 / 大きくなり具合 最大 {max(gl_e) if gl_e else 0:.2f}")
        levels = [float(v) for v in effect_samples(events, "glitch")
                  if str(v).replace(".", "", 1).lstrip("-").isdigit()]
        if levels:
            w(f"  画に出た強さ 最大 {max(levels):.2f}")

        # 回数は単調に増える（減ったらランを跨いだか、数え直している）。
        if any(b < a for a, b in zip(gl_n, gl_n[1:])):
            verdict("WARN", "乱れの回数が途中で減っている — ラン開始を跨いだ走行か、"
                            "GlitchFx.ResetAll が本編中に呼ばれている")
        if max(gl_n) <= 1:
            verdict("WARN", f"乱れが {max(gl_n)} 回しか起きていない — 育ち方は判定できない")
        elif not gl_e or max(gl_e) <= 0.001:
            verdict("FAIL", "乱れは複数回起きたのに大きくなっていない（glE=0）— "
                            "GlitchFx が GlitchEscalationLogic を通していない疑い")
        elif max(gl_e) < 0.5:
            verdict("WARN", f"終盤でも大きくなり具合が {max(gl_e):.2f} 止まり — "
                            "乱れの回数が想定より少ない（体験が短いか、著作が減った）")
        else:
            verdict("OK", f"乱れが {max(gl_n)} 回起きて、{max(gl_e):.2f} まで育った")
        w()

    # ------------------------------------------------------------------
    # 報告で異常が消えたか（canon/LEDGER.md 0050「報告したらそれらが消え」）
    #
    # ⚠⚠ 押した回数（marks）と消えた回数（disN）は**別物**。消せる演出の方が少ないので、
    #    marks だけ見ても機構が効いたかは 1 ビットも分からない。必ず対で読む。
    # ⚠ 「消えすぎ」も見る — 3 周目の録画（作品の核）に旗が立つと、押しボタン 1 つで飛ぶ。
    # ------------------------------------------------------------------
    dismissible = [t for t in exp.get("takes", []) if t.get("dismissible")]
    dis_n = [int(v) for v in effect_samples(events, "disN") if str(v).lstrip("-").isdigit()]
    dismissed = [e for e in events
                 if e.get("ev") == "take" and e.get("st") == "end" and e.get("why") == "mark"]
    marks = [e for e in events if e.get("ev") == "mark"]
    anchor = exp.get("outroAnchorId", "")

    if dismissible or dismissed:
        w("## 報告で異常が消えたか")
        w(f"  報告 {len(marks)} 回 / 消えた {max(dis_n) if dis_n else 0} 回"
          f" / 「報告で消える」と著作した演出 {len(dismissible)} 本")
        for t in dismissible:
            w(f"    著作 L{t['lap']}C{t['camera']} {t['id']}")
        for e in dismissed:
            w(f"    消えた t={fnum(e, 't', 0):.1f} {e.get('id')}")

        # 著作と実機が食い違っていないか。旗が立っていない演出が消えたら、機構がどこかで漏れている。
        armed_ids = {t["id"] for t in dismissible}
        stray = [e.get("id") for e in dismissed if e.get("id") not in armed_ids]
        if stray:
            verdict("FAIL", f"「報告で消える」と著作していない演出が消えた: {', '.join(map(str, stray))}"
                            " — 旗の解決（TakeRunner.SetTakes）か構造ガードが漏れている")

        # 終幕の合図が指す演出は畳めない（畳むと残りのカットを飛ばして終幕が早撃ちされる）。
        if anchor and any(t["id"] == anchor for t in dismissible):
            verdict("FAIL", f"終幕の合図（run.outro.afterTakeId={anchor}）が指す演出に"
                            "「報告で消える」が立っている — 実機は無効にするが、著作を直すこと")
        if anchor and any(e.get("id") == anchor for e in dismissed):
            verdict("FAIL", f"終幕の合図の演出 {anchor} が報告で畳まれた — 終幕が早撃ちされている")

        # 数え方の食い違い（ev=take の理由と ev=sum の累計は同じ 1 つの出来事を数えている）。
        if dis_n and max(dis_n) != len(dismissed):
            verdict("WARN", f"消えた回数が食い違う（disN={max(dis_n)} / ev=take why=mark={len(dismissed)}）"
                            " — どちらかの観測が取りこぼしている")

        if not dismissible:
            verdict("WARN", "「報告で消える」演出が 1 本も著作されていないのに消えている")
        elif not marks:
            verdict("WARN", "体験者が 1 度も報告していないので、消える機構は検証できていない")
        elif not dismissed:
            verdict("WARN", f"報告 {len(marks)} 回に対して 1 本も消えていない — "
                            "消せる演出が走っていない区間で押したか、旗が実機へ届いていない"
                            "（`ev=take st=end why=` が全部 done なら後者）")
        else:
            verdict("OK", f"報告 {len(marks)} 回で演出が {len(dismissed)} 本消えた")
        w()

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

    # -- 導入演出（覆い・パススルー・隔離・封印の箱・管の点灯）
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

        # -- 隔離殻（会場を黒で落とし、実物の壁と足元の床だけを残す面）
        # 出ない経路が 3 つあり、どれもログ以外に気づく手段が無い:
        #   ①シェーダがビルドから剥がれた（shellBuilt=0）
        #   ②layout.room / floor が未著作（shellBox=0）
        #   ③HMD 位置合わせが未完了（shell=0 のまま／殻は捏造しないので出さない側へ倒す）
        shell_built = effect_samples(events, "shellBuilt")
        if shell_built:
            any_effect_key = True
            if "0" in shell_built:
                verdict("FAIL", "隔離殻を組めていない（ContainmentShell の Shader.Find が null）— "
                                "シェーダがビルドから剥がれた疑い。GraphicsSettings の "
                                "m_AlwaysIncludedShaders に FixedCamVr/ContainmentShell を入れる")
            elif "1" in shell_built:
                w("  隔離殻: 実体を組めている")

        # ⚠ **導入は体験エリアの外で流れる**（canon/LEDGER.md 0005）。外に居るあいだ中を隠すのは
        # 封印の箱の仕事なので、**隔離殻が 0 なのが正常**。殻が出るのは「中に入ってしまった」時だけ。
        shell = effect_samples(events, "shell", t_from=real_t, t_to=run_t)
        if shell:
            any_effect_key = True
            w(f"  隔離（真っ黒）が出た標本: {shell.count('1')}/{len(shell)}"
              "（外に居れば 0 が正常・中に入ると 1）")

        # **これが今いちばん大事な 1 行。** 導入のあいだに実物の壁と床を見せたら約束違反。
        rev = effect_samples(events, "shellRev", t_from=real_t, t_to=run_t)
        if rev:
            any_effect_key = True
            if "1" in rev:
                verdict("FAIL", "導入のあいだに中の様子（実物の壁と床）が見えている — "
                                "固定視点になるまで見せない約束（canon/LEDGER.md 0005）が破れている")
            else:
                verdict("OK", "導入のあいだ中の様子は 1 度も出ていない")

        boxes = []
        for v in effect_samples(events, "shellBox"):
            try:
                boxes.append(int(v))
            except ValueError:
                pass
        # 箱の数は「見せる」設定のときだけ意味がある（終幕）。導入では組まないので 0 が正常。
        if boxes and rev and "1" in rev:
            any_effect_key = True
            w(f"  隔離が許した箱: 最大 {max(boxes)} 個（床 1 + 部屋の壁・箱）")
            if max(boxes) == 0:
                verdict("WARN", "隔離の幾何が 1 つも無い（layout.room も layout.floor も未著作）— "
                                "卓の 🧱 部屋で実物の壁を引き、較正パネルで床の実寸を入れる")
            elif max(boxes) < 2:
                verdict("WARN", "隔離が床しか許していない（layout.room に壁が無い）— "
                                "実物の壁まで黒く消える。卓の 🧱 部屋で壁を引く")

        # ⚠ 封印の箱（box / boxBuilt）は 2026-08-15 に観測ごと外した。箱を退避して
        #   重み sealBox を全段 0 にしたので、常に 0 が並ぶだけになり誤検出の材料にしかならない。

    # -- 段 4 の連続開口（全視野から本編スクリーンへ閉じる）
    # `aper` は IntroVeil が単一 quad へ実際に配った閉じ量の最大値。
    # 基底面を検査する。0208 の破片は別メッシュなので下で独立に検査する。
    aper_all = [v for v in effect_samples(events, "aper") if v not in ("", "-")]
    if aper_all:
        any_effect_key = True
        anums = []
        for v in aper_all:
            try:
                anums.append(float(v))
            except ValueError:
                pass
        if anums:
            peak = max(anums)
            w(f"  連続開口の到達: 最大 {peak:.2f}")
            # Frame だけの短い録画は途中で終わりうる。Swap まで観測できた走行だけ終端を要求する。
            if "Swap" in stages or "Done" in stages:
                if peak <= 0.01:
                    verdict("FAIL", "段 Frame に達したのに開口が動いていない（aper が 0 のまま）— "
                                    "IntroVeil.Apply と menu intro の絵で確かめる")
                elif peak < 0.99:
                    verdict("FAIL", f"連続開口が閉じ切っていない（aper={peak:.2f}）— "
                                    "Frame → Swap の遷移で 1.00 に到達する必要がある")
                else:
                    verdict("OK", f"連続開口がスクリーンまで閉じた（到達 {peak:.2f}）")

        quads = []
        for v in effect_samples(events, "aperQ"):
            try:
                quads.append(int(v))
            except ValueError:
                pass
        if quads:
            if any(n != 1 for n in quads):
                verdict("FAIL", f"導入の覆いが単一 quad ではない（aperQ={sorted(set(quads))}）— "
                                "基底面と破片メッシュの個数を分けて観測する")
            else:
                w("  導入の覆い: 単一 quad を維持")

        rects = [v for v in effect_samples(events, "aperRect") if v not in ("", "-")]
        if rects:
            valid_rect = False
            for value in rects:
                try:
                    parts = [float(x) for x in str(value).split(",")]
                    valid_rect = valid_rect or (len(parts) == 3 and all(x > 0 for x in parts))
                except ValueError:
                    pass
            if not valid_rect:
                verdict("FAIL", "連続開口のスクリーン矩形が解けていない（aperRect が不正）")

    else:
        # 2026-09-13 より前の走行だけを読む互換経路。新ログで shatC=0 を要求しない。
        shat_all = [v for v in effect_samples(events, "shat") if v not in ("", "-")]
        if shat_all:
            any_effect_key = True
            snums = []
            for v in shat_all:
                try:
                    snums.append(float(v))
                except ValueError:
                    pass
            if snums:
                w(f"  旧破砕ログの到達: 最大 {max(snums):.2f}")
                if "Frame" in stages:
                    if max(snums) <= 0.01:
                        verdict("FAIL", "段 Frame に達したのに現実が 1 画素も割れていない（shat が 0 のまま）— "
                                        "セル格子を組めていない疑い。menu intro の絵で確かめる")
                    else:
                        verdict("OK", f"現実が割れた（到達 {max(snums):.2f}）")
        cells = [v for v in effect_samples(events, "shatC") if v not in ("", "-")]
        if cells and all(v == "0" for v in cells):
            any_effect_key = True
            verdict("FAIL", "旧破砕ログのセル格子が 0 枚（IntroVeilShatterMesh を組めていない）")

    # 0208 の不揃いな破片。旧ログにはこの印が無いため適用しない。
    if "art" in effect_samples(events, "shatStyle"):
        any_effect_key = True
        def fracture_numbers(key):
            values = []
            for value in effect_samples(events, key):
                try:
                    values.append(float(value))
                except (ValueError, TypeError):
                    pass
            return values

        peaks = fracture_numbers("shat")
        pieces = fracture_numbers("shatC")
        complete_fracture = "Swap" in stages or "Done" in stages
        if "Frame" in stages or complete_fracture:
            if not pieces or max(pieces) <= 0:
                verdict("FAIL", "破片メッシュを組めていない（shatC が欠損または 0）")
            else:
                w(f"  不揃いな破片: {int(max(pieces))} 枚")
            if complete_fracture:
                if not peaks or max(peaks) < 0.70:
                    verdict("FAIL", "破片を収束まで描画できていない（shat が欠損または到達不足）")
                elif pieces and max(pieces) > 0:
                    verdict("OK", f"破片を収束まで描画した（到達 {max(peaks):.2f}）")

    # -- スクリーンの管の点灯
    # ⚠ 2026-08-15 から導入の全段で 1（点いていて、まだ何も映していない）。
    #    0 が出たら画がまるごと消えている。
    ig_all = [v for v in effect_samples(events, "ignite") if v not in ("", "-")]
    if ig_all:
        any_effect_key = True
        if "nc" in ig_all:
            verdict("FAIL", "スクリーンの管の点灯を書く先を掴めていない（ignite=nc）— "
                            "映像の出方（_IntroLive）も書けないので段 5 が画に出ない。"
                            "IntroDirector の screen 参照（MjpegScreen の Renderer）を見る")
        else:
            nums = []
            for v in ig_all:
                try:
                    nums.append(float(v))
                except ValueError:
                    pass
            if nums:
                w(f"  管の点灯: 最大 {max(nums):.2f} / 最後 {nums[-1]:.2f}")
                # ⚠⚠ **既定は 1（点いている）。** 演出の外で 0 のままだと画がまるごと消える。
                if nums[-1] < 0.999:
                    verdict("FAIL", f"走行の終わりで管が消えたまま（ignite={nums[-1]:.2f}）— "
                                    "画がまるごと消えている。IntroDirector が畳む経路のどこかで "
                                    "1 を書き戻していない")
                if min(nums) < 0.999:
                    verdict("FAIL", f"導入の途中で管が消えている（最小 {min(nums):.2f}）— "
                                    "2026-08-15 以降、管は全期間 1 が正しい")
                else:
                    verdict("OK", "管は全期間 1（点いたまま、映すものだけが変わる）")

    # -- 周回で進む解像度の劣化（装置が痩せていく・canon/LEDGER.md 0012）
    # ⚠ 「進みの数値が動いた」は画に出たことを意味しない。**書く先を掴めたか**（coarseMat）と
    #    **実際に書いたブロック数**（cbx）を対で見る。掴めていなければ 1 画素も変わらない。
    def _decay_nums(key, t_from=None, t_to=None):
        out = []
        for v in effect_samples(events, key, t_from=t_from, t_to=t_to):
            if v in ("", "-"):
                continue
            try:
                out.append(float(v))
            except ValueError:
                pass
        return out

    decay_mat = effect_samples(events, "coarseMat")
    if decay_mat:
        any_effect_key = True
        if "0" in decay_mat:
            verdict("FAIL", "解像度の劣化を書く先が無い（CameraFeelFx がスクリーンの Renderer を"
                            "掴めていない）— 進みが動いても画は 1 画素も変わらない")

    decay_run = _decay_nums("coarse", t_from=run_t)
    if decay_run:
        any_effect_key = True
        blocks = [b for b in _decay_nums("cbx", t_from=run_t) if b > 0]
        line = f"  解像度の劣化: 進み 最大 {max(decay_run):.2f}"
        if blocks:
            line += f" / 枠を横切るブロック 最小 {min(blocks):.0f}"
        w(line)

        # 本編に入ってから十分な時間が経ったのに進んでいないなら、ラン相の判定か配線が死んでいる。
        run_span = (max(fnum(e, "t", 0.0) for e in events) - run_t) if run_t is not None else 0.0
        if max(decay_run) <= 0.001 and run_span > 10.0:
            verdict("FAIL", f"本編に {run_span:.0f} 秒居るのに解像度の劣化が 1 度も進んでいない — "
                            "ShowRunDirector が CameraFeelFx を掴めていない疑い")
        elif max(decay_run) > 0.001:
            verdict("OK", f"解像度の劣化が進んだ（最大 {max(decay_run):.2f}）")

        # **段差を作らないことが仕様**（LEDGER 0012「急に落ちるではなくばれないように」）。
        # サマリは 2 秒ごとなので、頭打ち 0.035/秒 なら 1 標本あたり 0.07 が上限。
        jumps = [b - a for a, b in zip(decay_run, decay_run[1:]) if b > a]
        if jumps and max(jumps) > 0.09:
            verdict("WARN", f"進みが 1 標本（約 2 秒）で {max(jumps):.2f} 上がった — 段差に見える疑い。"
                            "ScreenDecayLogic.MaxRisePerSec を下げる")

        # 呪いが解けたら視界が戻る（canon/LEDGER.md 0083）。
        # ⚠ ラッチ（coarseRel）と**実際に下がったか**を対で見る。ラッチだけ見ると、
        #   Tick が止まって戻り切らなかった場合に「戻った」と誤読する。
        # ⚠ 見るのは **coarseShown**（画に出た側）。生の coarse は単調のままが正しい —
        #   音（装置の声の痩せ）と AI の侵食が読んでいるので、生が下がったらそちらが壊れている。
        released = [str(v) for v in effect_samples(events, "coarseRel")]
        if "1" in released:
            first = next(i for i, e in enumerate(events)
                         if e.get("ev") == "sum" and str(e.get("coarseRel")) == "1")
            after = [float(e["coarseShown"]) for e in events[first:]
                     if e.get("ev") == "sum" and e.get("coarseShown") is not None]
            raw_after = [float(e["coarse"]) for e in events[first:]
                         if e.get("ev") == "sum" and e.get("coarse") is not None]
            w(f"  呪いの解除: 視界を戻し始めた（画に出た進み 最小 {min(after):.2f}）"
              if after else "  呪いの解除: 視界を戻し始めた")
            if after and min(after) > 0.05:
                verdict("FAIL", f"呪いが解けたのに視界が戻り切っていない（画の進み {min(after):.2f} 止まり）— "
                                "ScreenDecayLogic の戻しが途中で止まっている疑い"
                                "（相が Run を出た所で凍っていないか）")
            elif after:
                verdict("OK", "報告で呪いが解け、視界が元へ戻った")
            # 生まで下がっていたら、音の痩せが一緒に戻っている（＝「直った」を音で宣言している）。
            if raw_after and min(raw_after) < max(raw_after) - 0.05:
                verdict("FAIL", "呪いの解除で**生の劣化まで**下がっている — "
                                "装置の声（bed_device の痩せ）が新品へ戻ってしまう。"
                                "画へ書くのは Shown、音へ渡すのは Progress")

            # ⚠⚠ **AI の侵食も一緒に消える**（`canon/LEDGER.md` 0129 —
            #    「呪いを消したら普通のエージェントに戻るようにしてほしい」）。
            #    画（coarseShown）と**別々に壊れる**ので畳まない — 画が戻っても侵食が残れば、
            #    体験者から見えるのは「呪いを消したのにエージェントがバグったまま」。
            gl_after = []
            for e in events[first:]:
                if e.get("ev") != "sum":
                    continue
                try:
                    gl_after.append(float(e["commsGl"]))
                except (KeyError, TypeError, ValueError):
                    pass
            if gl_after and min(gl_after) > 0.01:
                verdict("FAIL", f"呪いが解けたのに AI の侵食が残っている"
                                f"（commsGl {min(gl_after):.2f} 止まり）— "
                                "報告した後も文字が化け、顔が市松人形のまま。"
                                "CommsPanel.ResolveCorruption が ScreenDecayReleaseK を"
                                "渡せていない疑い")
            elif gl_after:
                verdict("OK", "呪いが解け、AIエージェントが普通に戻った（侵食が 0 まで落ちた）")
        elif max(decay_run) > 0.5:
            verdict("WARN", "視界が劣化したまま解除されずに終わった — "
                            "締めのカット（untilMark）へ報告が届いていないか、押されなかった")

    # 導入のあいだは 0 でなければならない（「1 周目の最初は今くらいの解像度」）。
    decay_intro = _decay_nums("coarse", t_to=run_t)
    if decay_intro and max(decay_intro) > 0.001:
        any_effect_key = True
        verdict("FAIL", f"導入の時点で解像度が既に落ちている（進み {max(decay_intro):.2f}）— "
                        "劣化は本編（Run 相）でしか進めない約束が破れている")

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

    # -- 左右分割と第 2 の差し替え層（canon/LEDGER.md 0050）
    # ⚠ 「カットが指した」は画に出たことを意味しない。分割は書く先（CameraFeelFx の material）を
    #    掴めていなければ 1 画素も割れず、第 2 層は素材を非同期で読むので、マスクの読込に失敗すると
    #    黙って 1 層目のままになる。どちらもログ以外に確かめる手段が無い。
    # ⚠ もう 1 つ見るのは**固着**。分割はカットの中でしか書かれないので、演出が終わった後に
    #    残っていると画が割れたまま次の体験者へ持ち越される（この codebase が 4 回踏んだ型）。
    want_split = [f"{t['id']}#{i}" for t in exp["takes"]
                  for i, s in enumerate(t["steps"]) if (fstr(s.get("splitX")) or 0.0) > 0.0]
    want_ovl2 = [f"{t['id']}#{i}" for t in exp["takes"]
                 for i, s in enumerate(t["steps"]) if s.get("overlay2CueId")]
    spl_vals = _decay_nums("spl")
    ovl2_vals = _decay_nums("ovl2")
    if want_split or want_ovl2 or spl_vals or ovl2_vals:
        any_effect_key = True
        w()
        w("### 左右分割と第 2 の差し替え層")
        w(f"  著作: 分割 {len(want_split)} カット / 第 2 層 {len(want_ovl2)} カット")
        if spl_vals:
            w(f"  画に出た分割の最大 {max(spl_vals):.2f}（標本 {len(spl_vals)}）")
        if ovl2_vals:
            w(f"  画に出た第 2 層の最大 {max(ovl2_vals):.2f}（標本 {len(ovl2_vals)}）")

        if want_split and not spl_vals:
            verdict("WARN", "分割を観測していないビルドのログ（spl キーが無い）")
        elif want_split and max(spl_vals) <= 0.0:
            verdict("FAIL", f"分割を指すカットが {len(want_split)} 本あるのに画は一度も割れていない"
                            f"（{', '.join(want_split)}）— CameraFeelFx が material を掴めているか"
                            "（coarseMat）と、そのカットが飛ばされていないかを見る")
        elif want_split:
            verdict("OK", f"画が割れた（最大 {max(spl_vals):.2f}）")

        if want_ovl2 and not ovl2_vals:
            verdict("WARN", "第 2 層を観測していないビルドのログ（ovl2 キーが無い）")
        elif want_ovl2 and max(ovl2_vals) <= 0.0:
            verdict("FAIL", f"第 2 層を指すカットが {len(want_ovl2)} 本あるのに素材が一度も載っていない"
                            f"（{', '.join(want_ovl2)}）— マスクか素材の読込に失敗している。"
                            "実機ログの [ScreenOverlay] を見る")
        elif want_ovl2:
            verdict("OK", f"第 2 層が載った（最大 {max(ovl2_vals):.2f}）")

        # 固着は「演出が全部終わった後」の標本だけで判定する。走行の途中でログが切れた場合に、
        # 割れている最中を固着と読まないため（最後の演出イベントが end なら誰も画面を持っていない）。
        take_evs = [e for e in events if e.get("ev") == "take"]
        if take_evs and take_evs[-1].get("st") == "end":
            t_end = fnum(take_evs[-1], "t", 0.0)
            after_spl = _decay_nums("spl", t_from=t_end)
            after_ovl2 = _decay_nums("ovl2", t_from=t_end)
            if after_spl and max(after_spl) > 0.0:
                verdict("FAIL", f"演出が終わった後も画が割れたまま（spl={max(after_spl):.2f}）— "
                                "次の体験者へ持ち越される。畳む経路のどこかで 0 を書き戻していない")
            if after_ovl2 and max(after_ovl2) > 0.0:
                verdict("FAIL", f"演出が終わった後も第 2 層が残っている（ovl2={max(after_ovl2):.2f}）")

    # -- 歩行誘導（canon/LEDGER.md 0079）
    # ⚠ ここが見るのは 3 つ。
    #   ①実体を組めたか（シェーダが剥がれると 1 画素も出ないのに段は進む）
    #   ②**円に着いてから導入が始まったか**（着く前に始まったら、装置が出した指示が嘘になる）
    #   ③段 0 を抜けた後に残っていないか（床の光が残ると「まだ歩け」に見える）
    guide_evs = [e for e in events if e.get("ev") == "guide"]
    guide_raw = [str(v) for v in effect_samples(events, "guide") if str(v) not in ("", "-")]
    if guide_evs or guide_raw:
        any_effect_key = True
        w()
        w("### 歩行誘導（矢印と円）")
        stages = [e.get("st", "?") for e in guide_evs]
        w(f"  段: {' → '.join(stages) if stages else '（1 度も出ていない）'}")
        gparts = [v.split("/") for v in guide_raw if v.count("/") == 3]
        gbuilt = [p[0] for p in gparts]
        chev = []
        for p in gparts:
            try:
                chev.append(int(p[1]))
            except ValueError:
                pass
        if chev:
            w(f"  山形 {max(chev)} 個（標本 {len(gparts)}）")
        spot_ev = next((e for e in guide_evs if "spot" in e), None)
        if spot_ev is not None:
            w(f"  円: ({spot_ev.get('spot','?')}) 半径 {spot_ev.get('r','?')}m"
              f" / {'卓で著作' if spot_ev.get('auth') == '1' else '壁の角から導出'}")

        if gbuilt and all(v == "0" for v in gbuilt):
            verdict("FAIL", "歩行誘導の実体を組めていない（guide の 1 つ目が 0）— "
                            "FixedCamVr/WalkGuideArrow / WalkGuideRing がビルドから剥がれている疑い"
                            "（ProjectSettings の Always Included を見る）")
        elif not guide_evs and not gparts:
            verdict("WARN", "歩行誘導を観測していないビルドのログ（guide キーが無い）")
        elif not guide_evs:
            verdict("WARN", "歩行誘導が 1 度も出ていない — 床も壁の角も開始位置も未著作の疑い"
                            "（卓の 🧱 部屋 / 🎬 開始位置）。導入は従来どおり接近で始まる")
        else:
            timed_out = any(e.get("to") == "1" for e in guide_evs)
            arrived = any(e.get("st") == "Arrive" for e in guide_evs)
            # 導入が段 0 を抜けた時刻（ev=intro stage=Real の最初）。
            t_real = next((fnum(e, "t", 0.0) for e in events
                           if e.get("ev") == "intro" and e.get("stage") == "Real"), None)
            t_arrive = next((fnum(e, "t", 0.0) for e in guide_evs if e.get("st") == "Arrive"), None)
            if chev and max(chev) == 0:
                verdict("WARN", "円だけが出ていて矢印が 1 つも無い（山形 0）— "
                                "道筋が短すぎる（壁の腕が床の縁に近い）")

            # -- 説明と対で出たか / 矢印 → 円 の順（canon/LEDGER.md 0079 の赤入れ 4）
            #    ⚠ どちらも**画からは区別できない**（保険で出た矢印も、順序が逆の円も、
            #      1 枚の絵では同じに見える）。ここが唯一の手掛かり。
            trail_ev = next((e for e in guide_evs if e.get("st") == "Trail"), None)
            spot_ev2 = next((e for e in guide_evs if e.get("st") == "SpotIn"), None)
            if trail_ev is not None and "told" in trail_ev:
                if trail_ev.get("told") == "1":
                    verdict("OK", "エージェントが説明を始めた縁で矢印が出た")
                else:
                    verdict("WARN", "説明が来ないまま保険（WalkGuideLogic.TellTimeoutSec）で矢印が出た — "
                                    "⓪b「矢印の方向から…」が届いていない"
                                    "（連絡の面を組めていない / CommsPanel の walkGuide 配線を見る）")
            if trail_ev is not None:
                r0 = fstr(trail_ev.get("ring", "0"))
                if r0 is not None and r0 > 0.01:
                    verdict("FAIL", f"矢印が出始めた時点で円が出ている（ring {r0:.2f}）— "
                                    "矢印が全部出てから円、の順が壊れている")
            if spot_ev2 is not None:
                a0 = fstr(spot_ev2.get("arrow", "0"))
                if a0 is not None and a0 < 0.99:
                    verdict("FAIL", f"円が開き始めた時点で矢印が出切っていない（arrow {a0:.2f}）")
            if arrived and t_real is not None and t_arrive is not None:
                if t_arrive <= t_real + 0.5:
                    verdict("OK", f"円へ着いてから導入が始まった（着 {t_arrive:.1f}s → 段 1 {t_real:.1f}s）")
                else:
                    verdict("FAIL", f"円へ着く前に導入が始まった（段 1 {t_real:.1f}s → 着 {t_arrive:.1f}s）— "
                                    "IntroInput.guidingToSpot が渡っていない疑い。指示が嘘になる")
            elif timed_out:
                verdict("WARN", "円へ着かないまま上限（WalkGuideLogic.HoldMaxSec）を超えて"
                                "従来の開始判定へ戻した — 自動走行では正常。実機で出たら円の場所を疑う")
            elif t_real is not None:
                verdict("FAIL", "誘導が出ているのに、着きも諦めもしないまま導入が始まった — "
                                "開始の経路が誘導を通っていない")

            # 固着。段 0 を抜けた後に床の光が残っていないか。
            if t_real is not None:
                after_g = [str(v) for v in effect_samples(events, "guide", t_from=t_real + 3.0)
                           if str(v).count("/") == 3]
                left = []
                for v in after_g:
                    p = v.split("/")
                    for k in (2, 3):
                        f = fstr(p[k])
                        if f is not None:
                            left.append(f)
                if left and max(left) > 0.01:
                    verdict("FAIL", f"導入が始まった後も誘導が残っている（guide {max(left):.2f}）— "
                                    "床に矢印が残ると「まだ歩け」に見える。畳む経路を見る")

    # -- 闇に開く目（canon/LEDGER.md 0075）
    # ⚠ 「カットが指した」は画に出たことを意味しない。シェーダが実行時 Shader.Find なので
    #    ビルドから剥がれると 1 画素も出ないまま演出だけ正常に走る（2026-07-31 に覆いで踏んだ型）。
    #    しかも**闇に出る演出なので、録画では暗くて確かめにくい**。手掛かりはこのキーだけ。
    # --- 装置が打っている時計（canon/LEDGER.md 0108）---
    #
    # ⚠ 著作に紐づかない（カットが指すものではなく**常に出ている**）ので、
    #   「出るはずだったか」を show.json から導けない。だから走行そのものを見る。
    # ⚠ 画には「時計が無い」としか出ず、しかも左上の小さな字なので録画では気づきにくい。
    osd_all = [str(v) for v in effect_samples(events, "osd")]
    if osd_all:
        any_effect_key = True
        w()
        w("### 装置の時計（左上の日付と時刻）")
        parts = [v.split("/") for v in osd_all if v.count("/") == 2]
        if not parts:
            # 値が全部 `-` ＝ シーンに ScreenOsd が居ない。**`menu scene` の焼き直し忘れ**。
            verdict("FAIL", "時計の実体がシーンに居ない（osd が常に -）— "
                            ".\\tools\\unity.ps1 menu scene でシーンを焼き直す")
        else:
            built = max(int(p[0]) for p in parts if p[0].isdigit())
            ticks = [int(p[1]) for p in parts if p[1].isdigit()]
            fades = []
            for p in parts:
                try:
                    fades.append(float(p[2]))
                except ValueError:
                    pass
            # ⚠⚠ **`timed_samples` は使えない。** あれは `fnum` で数にならない値を落とすので、
            #   `osd=1/47/1.00` のような複合値は**全部落ちて空になる**（2026-08-23 に踏んだ —
            #   `max()` が空列で落ち、**走行のレポートが 1 つも出なくなっていた**）。
            #   複合値のキーは時刻を自分で拾う。
            osd_t = [fnum(e, "t", 0.0) for e in events
                     if e.get("ev") in ("intro", "sum") and "osd" in e]
            span = max(osd_t) if osd_t else 0.0
            grew = (max(ticks) - min(ticks)) if ticks else 0
            w(f"  組めた: {'はい' if built else 'いいえ'}"
              f" / 刻んだ回数: {max(ticks) if ticks else 0}"
              f" / 濃さ: {max(fades) if fades else 0:.2f}")
            if not built:
                verdict("FAIL", "時計の版か材質を掴めていない（osd の 1 つ目が 0）— "
                                "py -3.11 tools/make-osd-font.py で版を焼き、"
                                "Read/Write Enabled が立っているか見る")
            elif ticks and max(ticks) == 0:
                verdict("FAIL", "時計を 1 度も敷いていない（osd の 2 つ目が 0）")
            elif grew == 0 and span > 10.0:
                # ⚠ **秒は必ず変わる**ので、増えていないなら敷き直しが止まっている
                #   （＝画に出ている時刻がその時点で凍っている ＝ 録画に見える）。
                verdict("FAIL", f"時計が止まっている（{span:.0f} 秒のあいだ刻んだ回数が "
                                f"{max(ticks)} のまま）")
            elif fades and max(fades) <= 0.001:
                verdict("FAIL", "時計を敷いているのに画へ書いていない（osd の 3 つ目が 0.00）")
            elif span > 30.0 and grew < span * 0.5:
                verdict("WARN", f"時計の刻みが走行の長さに追いついていない"
                                f"（{span:.0f} 秒で {grew} 回）")

    # --- 時計の右の周回（canon/LEDGER.md 0167）---
    #
    # ⚠ ここも著作に紐づかない（常に出ている）ので、走行そのものを見る。
    # ⚠⚠ **異世界のあいだは時刻ごと `?` になる**（osdLap=mask）。時計と音（風）は
    #   同じ 1 本（`TakeRunner.OtherworldActive`）を読んでいるので、**片方だけ出ていたら
    #   配線が切れている**。画には「時計が読める」としか出ないので気づけない。
    lap_all = [str(v) for v in effect_samples(events, "osdLap")]
    if lap_all:
        any_effect_key = True
        w()
        w("### 時計の右の周回（0167）")
        w(f"  出た値: {' '.join(sorted(set(lap_all)))}")
        shown = [v for v in lap_all if v not in ("", "-")]
        segs = [e for e in events if e.get("ev") == "seg"]
        if not shown:
            if segs:
                verdict("FAIL", f"区間へ {len(segs)} 回入ったのに周回が 1 度も出ていない"
                                "（osdLap が常に -）— 時計が区間の周を掴めていない")
            else:
                verdict("WARN", "区間へ 1 度も入っていない走行（周回は出なくて正常）")
        else:
            other_cues = [e for e in events
                          if e.get("ev") == "cue" and str(e.get("st")) == "on"
                          and str(e.get("id", "")).startswith("backrooms")]
            if "mask" in lap_all:
                verdict("OK", "別の場所が映っているあいだ、時刻と周回が ? になった")
            elif other_cues:
                verdict("FAIL", f"別の場所のカット（{other_cues[0].get('id')}）が画に出たのに"
                                "時計が ? にならなかった — 音の風と同じ 1 本"
                                "（TakeRunner.OtherworldActive）を読めているか見る")
            else:
                verdict("WARN", "別の場所の演出に到達していない走行（? は出なくて正常）")
            if "last" not in lap_all and any(fnum(e, "lap", 0.0) >= 4.0 for e in segs):
                verdict("FAIL", "帰りの区間へ入ったのに「最後」が出ていない"
                                "（osdLap に last が無い）")

    want_eyes = [f"{t['id']}#{i}" for t in exp["takes"]
                 for i, s in enumerate(t["steps"]) if (fstr(s.get("eyes")) or 0.0) > 0.0]
    eyes_raw = [str(v) for v in effect_samples(events, "eyes") if str(v) not in ("", "-")]
    if want_eyes or eyes_raw:
        any_effect_key = True
        w()
        w("### 闇に開く目")
        w(f"  著作: {len(want_eyes)} カット")
        # 3 つ組は 0093（2026-08-19）より前のビルド。5 つ組は 進み と 速さ が付く。
        # 7 つ組（2026-08-23）は **カットの指示** と **流しきり中か** が付く —
        # 「著作が言っていないのに目が開く」を外から切り分けるために足した。
        parts = [v.split("/") for v in eyes_raw if v.count("/") in (2, 4, 6)]
        built = [p[0] for p in parts]
        opened = []
        fades = []
        for p in parts:
            try:
                opened.append(int(p[1]))
            except ValueError:
                pass
            try:
                fades.append(float(p[2]))
            except ValueError:
                pass
        if opened:
            w(f"  同時に開いた目の最大 {max(opened)} 個（標本 {len(parts)}）")

        # 位置で開閉しているか（canon/LEDGER.md 0093）。進み -1 = 測れていない。
        spans, rates = [], []
        for p in parts:
            if len(p) < 5:
                continue
            try:
                spans.append(float(p[3]))
            except ValueError:
                pass
            try:
                rates.append(float(p[4]))
            except ValueError:
                pass
        if spans:
            measured = [v for v in spans if v >= 0.0]
            if not measured:
                verdict("WARN", "区間の進みを 1 度も測れていない（eyes の 4 つ目が常に -1）— "
                                "位置合わせが済んでいないか layout が届いていない。"
                                "目の開閉はカットの尺で起きている（0093 より前の挙動）")
            else:
                w(f"  区間の進み 最大 {max(measured):.2f}（測れた標本 {len(measured)}/{len(spans)}）")
                if rates and max(rates) > 1.01:
                    w(f"  追い上げが効いた（速さ 最大 {max(rates):.2f}）")
                elif rates:
                    w("  追い上げは 1 度も要らなかった（区間の半ばまでに開き切っている）")

        if want_eyes and not parts:
            verdict("WARN", "目を観測していないビルドのログ（eyes キーが無い）")
        elif built and all(v == "0" for v in built):
            verdict("FAIL", "目の実体を組めていない（eyes の 1 つ目が 0）— "
                            "FixedCamVr/AnomalyEyes がビルドから剥がれている疑い"
                            "（ProjectSettings の Always Included を見る）")
        elif want_eyes and opened and max(opened) == 0:
            verdict("FAIL", f"目を指すカットが {len(want_eyes)} 本あるのに 1 つも開いていない"
                            f"（{', '.join(want_eyes)}）— そのカットが飛ばされていないか"
                            "（ev=step）と、区間の進み（eyes の 4 つ目）が動いているかを見る")
        elif want_eyes and opened and max(opened) < 2:
            verdict("WARN", f"大きい目しか開いていない（最大 {max(opened)} 個）— "
                            "0093 の追い上げ（区間の半ばで倍速）が効いていれば開眼まで必ず届く。"
                            "速さ（eyes の 5 つ目）が 1.00 のままなら、終了要求が届いていない")
        elif want_eyes and opened:
            verdict("OK", f"目が開いた（同時に最大 {max(opened)} 個）")

        # 固着。目はカットの中でしか指されないので、演出が終わった後に残っていたら
        # 次の体験者の視界に最初から目が居ることになる。
        take_evs2 = [e for e in events if e.get("ev") == "take"]
        if take_evs2 and take_evs2[-1].get("st") == "end":
            t_end2 = fnum(take_evs2[-1], "t", 0.0)
            # ⚠ 猶予は 2.0 → 7.0 秒（0093）。カットが終わっても目は切らず流しきるので、
            #   最悪ケース（兆しの途中で区間が変わる）で 4.92/2 の追い上げ ＋ 閉じ 1.6 ＝ 4.1 秒残る
            #   （0094 で開くのが 8.4 → 4.92 秒になった。猶予は据え置き）。
            after = [str(v) for v in effect_samples(events, "eyes", t_from=t_end2 + 7.0)
                     if str(v).count("/") in (2, 4)]
            stuck = [p.split("/")[2] for p in after]
            vals = []
            for v in stuck:
                try:
                    vals.append(float(v))
                except ValueError:
                    pass
            if vals and max(vals) > 0.0:
                verdict("FAIL", f"演出が終わった後も目が残っている（eyes の 3 つ目 {max(vals):.2f}）— "
                                "次の体験者の視界に最初から目が居る。畳む経路を見る")

        # 誰も言っていないのに目が開き直した（2026-08-23 の実害・4 周目 A で目が 1 つ）。
        # ⚠⚠ **「流しきり」と混ざらない指標を選ぶ。** カットが終わっても目は数秒残るのが正しい（0093）ので、
        #    「カットの指示が 0 なのに開いている」を見ると毎回引っかかる。見るのは
        #    **一度閉じ切った所から、指示が 0 のまま開き直した**という縁だけ。
        #    実測の形: …/0.00/…/1.00/0（3 周目 C・閉じ切った）→ …/1.00/…/0.00/1（4 周目 A・1 つ開いた）。
        seq = [(fnum(e, "t", 0.0), str(e["eyes"]).split("/")) for e in events
               if e.get("ev") in ("intro", "sum") and str(e.get("eyes", "")).count("/") == 6]
        rogue, prev = [], None
        for t, p in seq:
            try:
                fade, want = float(p[2]), float(p[5])
            except ValueError:
                continue
            # ⚠ 直前の**指示**は見ない。実測では直前の標本がまだ 3 周目 C の `1.00` を出していて
            #   （カット切替は滞在 0.5 秒待ち）、そこを条件に入れると**この事故自身が素通りする**。
            #   見るのは「閉じ切っていた（prev の不透明度 0）」と「いま開いていて指示が 0」の 2 つだけ。
            if prev is not None and prev <= 0.001 and fade > 0.001 and want <= 0.001:
                rogue.append(t)
            prev = fade
        if rogue:
            verdict("FAIL",
                    f"カットが何も言っていないのに目が開き直した（t={rogue[0]:.1f}"
                    + (f" ほか {len(rogue) - 1} 回" if len(rogue) > 1 else "") + "）— "
                    "宣言していない区間で出番が始まっている。区間の判定は 2 つの速さで進む"
                    "（居場所は線を跨いだ瞬間・カット切替は滞在 0.5 秒後）ので、"
                    "その隙に前の区間の言い残しで始まっていないかを見る（EyesCueLogic.Declare）")

    # -- 目の視界ジャック（canon/LEDGER.md 0099）
    # ⚠ 「写真が届いた」と「画に出た」と「どう終わったか」は別。当日フォルダが空のまま走ると
    #    ジャックは黙って出ない（体験は壊れないので、ここでしか気づけない）。
    want_jack = [f"{t['id']}#{i}" for t in exp["takes"]
                 for i, s in enumerate(t["steps"])
                 if s.get("eyeJack") and (fstr(s.get("eyes")) or 0.0) > 0.0]
    jack_raw = [str(v) for v in effect_samples(events, "jack") if str(v) not in ("", "-")]
    jack_ev = [e for e in events if e.get("ev") == "jack"]
    if want_jack or jack_raw or jack_ev:
        any_effect_key = True
        w()
        w("### 目の視界ジャック（当日写真）")
        w(f"  著作: {len(want_jack)} カット")
        jparts = [v.split("/") for v in jack_raw if v.count("/") == 3]
        jbuilt = [p[0] for p in jparts]
        photos, shown = [], []
        for p in jparts:
            try:
                photos.append(int(p[1]))
            except ValueError:
                pass
            try:
                shown.append(int(p[2]))
            except ValueError:
                pass
        if photos:
            w(f"  端末に用意できた写真 最大 {max(photos)} 枚")
        begins = [e for e in jack_ev if e.get("st") == "begin"]
        ends = [e for e in jack_ev if e.get("st") == "end"]
        for e in begins:
            w(f"  t={fnum(e,'t',0):7.1f}  乗っ取り開始（{e.get('n','?')} 枚 / 1 枚 {e.get('per','?')}s）")
        for e in ends:
            w(f"  t={fnum(e,'t',0):7.1f}  返した（{JACK_END_WHY.get(e.get('why',''), e.get('why','?'))}"
              f" / 出した {e.get('shown','?')} 枚）")

        if want_jack and not jparts:
            verdict("WARN", "視界ジャックを観測していないビルドのログ（jack キーが無い）")
        elif jbuilt and all(v == "0" for v in jbuilt):
            verdict("FAIL", "視界ジャックの面を組めていない（jack の 1 つ目が 0）— "
                            "FixedCamVr/EyeJack がビルドから剥がれている疑い"
                            "（ProjectSettings の Always Included を見る）")
        elif want_jack and photos and max(photos) == 0:
            verdict("WARN", "当日写真が 1 枚も端末に用意できていない（jack の 2 つ目が 0）— "
                            "卓の eyejack フォルダが空か、配れていない。"
                            "体験は壊れないが、ジャックは出ない（目だけになる）")
        elif want_jack and not begins:
            verdict("FAIL", f"視界ジャックを指すカットが {len(want_jack)} 本あるのに 1 度も出ていない"
                            f"（{', '.join(want_jack)}）— そのカットが飛ばされていないか（ev=step）と、"
                            "目が開いたか（eyes）を見る")
        elif begins and shown and max(shown) == 0:
            verdict("FAIL", "乗っ取ったのに写真を 1 枚も画に出していない（jack の 3 つ目が 0）")
        elif begins:
            verdict("OK", f"視界ジャックが出た（{len(begins)} 回 / 出した写真 最大 {max(shown) if shown else 0} 枚）")

        # 終わり方の分岐（0099）。done = 写真が尽きた（止まっている人）/ cut = 区間の畳み（歩く人）。
        # ⚠ wd（安全網）が出たら尺の計算が壊れている。
        for e in ends:
            if e.get("why") == "wd":
                verdict("FAIL", "視界ジャックが安全網で打ち切られた（why=wd）— "
                                "写真の尺の計算が壊れている（EyeJackLogic を見る）")
        # 乗っ取ったまま終わっていないか（凍結の型）。
        if len(begins) > len(ends):
            verdict("FAIL", "視界ジャックが乗っ取ったまま返っていない — 視界が写真で塞がったまま終わる")
        # 固着（演出が終わった後も乗っ取っている）。
        take_evs3 = [e for e in events if e.get("ev") == "take"]
        if take_evs3 and take_evs3[-1].get("st") == "end":
            t_end3 = fnum(take_evs3[-1], "t", 0.0)
            after_j = [str(v) for v in effect_samples(events, "jack", t_from=t_end3 + 2.0)
                       if str(v).count("/") == 3]
            if any(p.split("/")[3] == "1" for p in after_j):
                verdict("FAIL", "演出が終わった後も視界ジャックが出ている（jack の 4 つ目が 1）— "
                                "次の体験者の視界が写真で塞がる。畳む経路を見る")

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
