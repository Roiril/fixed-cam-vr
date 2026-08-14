#!/usr/bin/env python3
"""[XP] ログと録画を突き合わせて、「その瞬間に画に何が出ていたか」を切り出す。

    py -3.11 tools/xp-evidence.py <logfile> <eye.mp4> [--out logs/evidence/<日時>/] [--offset <秒>]

出るもの:
  - 導入の各段・演出の始まりと終わり・相の変化・砂嵐の始まりの **フル解像度 PNG**
  - 一覧用のコンタクトシート（縮小・タイルに条件を焼く）
  - `index.md`（時刻対応の根拠と、切り出した全カットの表）

## 縮小しない（重要）

一覧のためにグリッドへ縮めると **1cm の線が消える**。2026-07-31 に構造の線が「0 本」に見えて
誤診しかけた。判定に使うのは必ずフル解像度の個別 PNG で、コンタクトシートは索引にすぎない。

## 時刻の対応

logcat の行頭は壁時計 (`07-31 16:02:19.506`)。`[XP] t=4.74` と組にすると **`t=0` の壁時計**が出る。
録画開始の壁時計と引き算すれば対応が付く:

    録画秒 = XP秒 + (t=0 の壁時計 - 録画開始の壁時計)

録画開始の壁時計をどこから取るかで確からしさが変わる。上から順に使う:

  1. `--offset`（人が実測した値。`録画秒 = XP秒 + offset` なので、録画が XP より先に始まって
     いれば負の値になる）
  2. サイドカー `<日時>_meta.json` の `record_started_iso`（quest-record.py が書く。**これが正**）
  3. mp4 のファイル名の日時 + `RECORD_START_DELAY_SEC`（**推定**。index.md にそう明記する）

3 は quest-record.py が「アプリを起動 → VR モードを待つ → 録り始める」順で動くことに由来する
概算で、数秒ずれる。ずれたまま黙って対応表を出さないこと。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timedelta

import cv2
import numpy as np

XP = re.compile(r"\[XP\]\s+(.*)$")
# logcat -v time の行頭。`07-31 16:49:58.057 I/Unity   (10831): ...`
LOGCAT_TIME = re.compile(r"^(\d{2})-(\d{2})\s+(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s")
PID = re.compile(r"/\w+\s*\(\s*(\d+)\s*\)")
STAMP = re.compile(r"(\d{8})_(\d{6})")

# quest-record.py が「日時スタンプを打ってから実際に録り始める」までの概算秒。
# 内訳: wake / force-stop / rm / am start の adb 往復 ≈ 1.5s + warmup 5.0s + screenrecord の起動 ≈ 0.5s。
# ⚠ `--serial` を渡さないと quest-fleet.py pick の探索（数秒）がこの前に挟まるので、推定はその分だけ
#    遅れる（＝実際の対応はもっと負に寄る）。サイドカーがあるならそちらが常に正しい。
RECORD_START_DELAY_SEC = 7.0

# フル解像度で切り出す PNG のほかに作る索引。1 枚あたりのタイル数と幅。
SHEET_COLS = 4
SHEET_ROWS = 6
TILE_W = 420

# H.264 は黒を 0 では符号化しない（16 前後）。「黒い」「黒くない」の判定はこの幅を見込む。
BLACK_MEAN = 12.0
NOT_BLACK_MEAN = 20.0

# 短い段はこの秒数を刻んで全部撮る（中央 1 枚では核心を外す）。
SHORT_STAGE_SEC = 2.0
SHORT_STAGE_STEP = 0.2
# 段の頭は遷移そのものが写るので、少しだけ後ろへずらす。
STAGE_LEAD_SEC = 0.15


# ---------------------------------------------------------------- ログ

def parse_kv(body: str) -> dict:
    out = {}
    for tok in body.split():
        if "=" in tok:
            k, _, v = tok.partition("=")
            out[k] = v
    return out


def wall_clock(line: str, ref: datetime):
    """logcat の行頭を datetime にする。年が無いので参照時刻の年を当てる。"""
    m = LOGCAT_TIME.match(line)
    if not m:
        return None
    mon, day, hh, mm, ss, ms = (int(x) for x in m.groups())
    for year in (ref.year, ref.year - 1, ref.year + 1):
        try:
            d = datetime(year, mon, day, hh, mm, ss, ms * 1000)
        except ValueError:
            continue
        if abs((d - ref).total_seconds()) < 86400 * 180:
            return d
    return None


def load_rows(path: str, ref: datetime):
    """[XP] 行を (壁時計, t, キー辞書, pid) で返す。生の行も全部返す（プロセス切り出し用）。"""
    rows, raw = [], []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            raw.append(line)
            m = XP.search(line)
            if not m:
                continue
            body = m.group(1).split("|")[0]
            ev = parse_kv(body)
            try:
                t = float(ev.get("t", ""))
            except ValueError:
                continue
            pm = PID.search(line)
            rows.append({
                "wall": wall_clock(line, ref),
                "t": t,
                "ev": ev,
                "pid": pm.group(1) if pm else None,
                "line_no": len(raw) - 1,
            })
    return rows, raw


def slice_last_run(rows):
    """最後の `ev=boot` と同じプロセスの行だけにする。

    ログに前回走行が混ざると、演出も導入も 2 回ぶん出て判定が二重になる。
    ⚠ boot 行そのものから切ってはいけない。起動直後の警告（シェーダが見つからない 等）は
      boot より前に出る。切るなら **プロセス（pid）の始まり**から。
    """
    boots = [r for r in rows if r["ev"].get("ev") == "boot"]
    if len(boots) < 2:
        return rows, len(boots)
    pid = boots[-1]["pid"]
    if not pid:
        return [r for r in rows if r["t"] >= 0 and r["line_no"] >= boots[-1]["line_no"]], len(boots)
    return [r for r in rows if r["pid"] == pid], len(boots)


def zero_wall(rows):
    """`t=0` の壁時計。全行の (壁時計 - t) の中央値を採る（1 行だけだと外れ値に弱い）。"""
    cands = [r["wall"] - timedelta(seconds=r["t"]) for r in rows if r["wall"]]
    if not cands:
        return None
    cands.sort()
    return cands[len(cands) // 2]


# ---------------------------------------------------------------- 録画開始時刻

def sidecar_path(video: str) -> str:
    base = os.path.basename(video)
    for suffix in ("_eye.mp4", "_raw.mp4", ".mp4"):
        if base.endswith(suffix):
            base = base[: -len(suffix)]
            break
    return os.path.join(os.path.dirname(video), base + "_meta.json")


def stamp_of(video: str):
    m = STAMP.search(os.path.basename(video))
    if not m:
        return None
    try:
        return datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S")
    except ValueError:
        return None


def resolve_offset(video: str, rows, offset_arg):
    """(offset秒, 推定か, 根拠の文) を返す。offset は `録画秒 = XP秒 + offset`。"""
    t0 = zero_wall(rows)
    t0_txt = t0.strftime("%H:%M:%S.%f")[:-3] if t0 else "不明"

    if offset_arg is not None:
        return offset_arg, False, (
            f"`--offset {offset_arg:+.2f}` を人が指定した（t=0 の壁時計は {t0_txt}）")

    side = sidecar_path(video)
    if os.path.exists(side):
        try:
            with open(side, "r", encoding="utf-8") as fh:
                meta = json.load(fh)
            started = meta.get("record_started_iso")
            if started and t0:
                rec0 = datetime.fromisoformat(started)
                off = (t0 - rec0).total_seconds()
                return off, False, (
                    f"サイドカー {os.path.basename(side)} の record_started_iso="
                    f"{rec0.strftime('%H:%M:%S.%f')[:-3]} と、t=0 の壁時計 {t0_txt} の差")
        except (ValueError, OSError, json.JSONDecodeError) as e:
            print("sidecar unusable: %s" % e, file=sys.stderr)

    st = stamp_of(video)
    if st and t0:
        rec0 = st + timedelta(seconds=RECORD_START_DELAY_SEC)
        off = (t0 - rec0).total_seconds()
        return off, True, (
            f"⚠ **推定**。mp4 のファイル名の日時 {st.strftime('%H:%M:%S')} に、quest-record.py が"
            f"録り始めるまでの概算 {RECORD_START_DELAY_SEC:.1f} 秒を足して録画開始とみなし、"
            f"t=0 の壁時計 {t0_txt} との差を取った。数秒ずれる。"
            f"正確に出すには `--offset` を渡すか、次の走行から出るサイドカーを使う")
    return None, True, "録画開始の壁時計が分からない（サイドカーもファイル名の日時も無い）"


# ---------------------------------------------------------------- 切り出す瞬間

class Moment:
    def __init__(self, group, kind, name, xp_t, desc):
        self.group = group      # index.md での並び順（0=導入 1=演出 2=相 3=砂嵐）
        self.kind = kind        # ファイル名の頭
        self.name = name
        self.xp_t = xp_t
        self.desc = desc        # 何が起きているはずか（日本語）
        self.rec_t = None
        self.path = None
        self.actual_rec = None
        self.mean = None
        self.note = ""

    def label(self) -> str:
        """コンタクトシートに焼く ASCII の見出し（cv2 は日本語を描けない）。"""
        return f"{self.kind} {self.name}"


def collect_moments(rows):
    ms = []

    # -- 導入の段
    intro = [r for r in rows if r["ev"].get("ev") == "intro"]
    last_t = rows[-1]["t"] if rows else 0.0
    for i, r in enumerate(intro):
        stage = r["ev"].get("stage", "?")
        start = r["t"]
        end = intro[i + 1]["t"] if i + 1 < len(intro) else min(start + SHORT_STAGE_SEC, last_t)
        dur = max(end - start, 0.0)
        if dur < SHORT_STAGE_SEC:
            # 短い段は中央 1 枚では核心を外す（実測で Structure は 1.1 秒しか無かった）。
            t = start + 0.05
            while t < end:
                ms.append(Moment(0, "intro", stage, t,
                                 f"導入の段 {stage}（{dur:.1f} 秒の段を {SHORT_STAGE_STEP} 秒刻みで）"))
                t += SHORT_STAGE_STEP
        else:
            ms.append(Moment(0, "intro", stage, start + STAGE_LEAD_SEC,
                             f"導入の段 {stage} の頭"))
            ms.append(Moment(0, "intro", stage, start + dur / 2.0,
                             f"導入の段 {stage} の中央"))

    # -- タイトル画面（2026-08-14 に追加）
    # ⚠ **体験の入口なのに切り出していなかった。** 題字も、その前に出る注意書き（安全の掲示）も、
    #    走行の画から探す手段が無く、時刻を手で計算して切り出していた。
    #    Wait は「A を待っている真っ暗」なので、頭ではなく**終わりぎわ**を撮る
    #    （頭は位置合わせから抜けた直後で、まだ黒が立っていないことがある）。
    title = [r for r in rows if r["ev"].get("ev") == "title"]
    for i, r in enumerate(title):
        stage = r["ev"].get("stage", "?")
        if stage in ("Off", "Done"):
            continue
        start = r["t"]
        end = title[i + 1]["t"] if i + 1 < len(title) else min(start + SHORT_STAGE_SEC, last_t)
        dur = max(end - start, 0.0)
        if stage == "Wait":
            # 注意書きが読める大きさかを見る 1 枚。A の直前（＝ 出切っている所）を撮る。
            ms.append(Moment(0, "title", stage, max(start + 0.5, end - 0.5),
                             "タイトルの待ち（注意書きが出ているはず）"))
        elif dur < SHORT_STAGE_SEC:
            t = start + 0.05
            while t < end:
                ms.append(Moment(0, "title", stage, t,
                                 f"タイトルの段 {stage}（{dur:.1f} 秒を {SHORT_STAGE_STEP} 秒刻みで）"))
                t += SHORT_STAGE_STEP
        else:
            ms.append(Moment(0, "title", stage, start + STAGE_LEAD_SEC, f"タイトルの段 {stage} の頭"))
            ms.append(Moment(0, "title", stage, start + dur / 2.0, f"タイトルの段 {stage} の中央"))

    # -- 演出
    for r in rows:
        e = r["ev"]
        if e.get("ev") != "take":
            continue
        st = e.get("st", "?")
        ms.append(Moment(1, "take", f"{e.get('id','?')}_{st}", r["t"],
                         f"演出 {e.get('id','?')} が{'始まった' if st == 'begin' else '終わった'}"))

    # -- 相
    for r in rows:
        e = r["ev"]
        if e.get("ev") == "phase":
            ms.append(Moment(2, "phase", e.get("v", "?"), r["t"],
                             f"相が {e.get('v','?')} になった"))

    # -- 砂嵐の始まり
    for r in rows:
        e = r["ev"]
        if e.get("ev") == "storm" and e.get("v") == "on":
            ms.append(Moment(3, "storm", f"on_cam{e.get('cam','?')}", r["t"],
                             f"砂嵐が始まった（カメラ {e.get('cam','?')}）"))
    return ms


# ---------------------------------------------------------------- 切り出し

SAFE = re.compile(r'[<>:"/\\|?*\s]')


def out_name(m: Moment) -> str:
    name = SAFE.sub("-", m.name)
    return f"{m.kind}_{name}_xp{m.xp_t:.2f}_rec{m.rec_t:.2f}.png"


def extract(video: str, moments, outdir: str):
    """要求された瞬間をフル解像度・無加工で書き出す。範囲外は書かずに理由を残す。"""
    cap = cv2.VideoCapture(video)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 0
    dur = count / fps if fps else 0.0
    written = 0
    for m in sorted(moments, key=lambda x: x.rec_t):
        if m.rec_t < 0 or m.rec_t >= dur:
            m.note = f"録画の範囲外（録画は 0〜{dur:.1f} 秒）"
            continue
        idx = min(int(round(m.rec_t * fps)), max(count - 1, 0))
        cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
        pos_ms = cap.get(cv2.CAP_PROP_POS_MSEC)
        ok, frame = cap.read()
        if not ok or frame is None:
            m.note = "その位置のフレームを読めなかった"
            continue
        m.actual_rec = pos_ms / 1000.0 if pos_ms else m.rec_t
        m.mean = float(frame.mean())
        m.path = os.path.join(outdir, out_name(m))
        cv2.imwrite(m.path, frame)      # フル解像度・無加工（縮小すると細い線が消える）
        written += 1
    cap.release()
    return written, fps, dur


def contact_sheets(moments, outdir: str):
    """索引用のコンタクトシート。判定に使うのはフル解像度の個別 PNG の方。"""
    shots = [m for m in moments if m.path]
    if not shots:
        return []
    per = SHEET_COLS * SHEET_ROWS
    sheets = []
    for page in range((len(shots) + per - 1) // per):
        chunk = shots[page * per: (page + 1) * per]
        tiles = []
        for m in chunk:
            img = cv2.imread(m.path)
            if img is None:
                continue
            h = max(int(img.shape[0] * TILE_W / img.shape[1]), 1)
            small = cv2.resize(img, (TILE_W, h), interpolation=cv2.INTER_AREA)
            strip = np.zeros((44, TILE_W, 3), np.uint8)
            cv2.putText(strip, m.label()[:44], (6, 18),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 255), 1, cv2.LINE_AA)
            cv2.putText(strip, "rec %.2fs / xp %.2fs" % (m.actual_rec or m.rec_t, m.xp_t),
                        (6, 36), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (170, 220, 255), 1, cv2.LINE_AA)
            tiles.append(np.vstack([small, strip]))
        if not tiles:
            continue
        th = max(t.shape[0] for t in tiles)
        tiles = [np.vstack([t, np.zeros((th - t.shape[0], TILE_W, 3), np.uint8)])
                 if t.shape[0] < th else t for t in tiles]
        rows_img = []
        for i in range(0, len(tiles), SHEET_COLS):
            row = tiles[i: i + SHEET_COLS]
            while len(row) < SHEET_COLS:
                row.append(np.zeros((th, TILE_W, 3), np.uint8))
            rows_img.append(np.hstack(row))
        path = os.path.join(outdir, "_contact_%d.png" % (page + 1))
        cv2.imwrite(path, np.vstack(rows_img))
        sheets.append(path)
    return sheets


# ---------------------------------------------------------------- 索引

GROUP_TITLE = ["導入の段", "演出", "相", "砂嵐"]


def sanity_notes(moments, estimated: bool):
    """「黙って嘘の対応を出さない」ための見張り。日本語の警告文を返す。"""
    out = []
    outside = [m for m in moments if m.note.startswith("録画の範囲外")]
    if outside:
        out.append(f"切り出せなかったカットが {len(outside)} 件ある（録画の範囲外）。"
                   "録画の秒数が足りないか、時刻の対応がずれている")
    hint = "まず時刻の対応を疑う（推定で出している）" if estimated else "画に出ていない方を疑う"
    # ⚠ **段 Black が黒くないのは正常なので見張らない**（2026-07-31 に確定）。
    #    段の名前に反して、ここは開始の合図（通過ラインを横切る）を待つ区間で、
    #    実測 17 秒ある。真っ黒にすると体験者は何も見えないまま歩いて線を越えることになり
    #    運用が成立しないので、意図的に現実を見せている（`IntroLogic.cs` の Black 段のコメント）。
    #    ここで警告を出すと、次に見る人が「時刻の対応がずれている」と誤読する。
    for m in moments:
        if m.mean is None or m.kind != "intro":
            continue
        if m.name == "Real" and m.mean < BLACK_MEAN:
            out.append(f"段 Real の画が真っ黒（録画 {m.actual_rec or m.rec_t:.2f} 秒 / 平均輝度 "
                       f"{m.mean:.1f} / `{os.path.basename(m.path or '')}`）。"
                       f"導入の主役は現実の映像なので、パススルーが出ていない疑い — {hint}")
            break
    return out


def write_index(path, log, video, moments, offset, estimated, basis, fps, dur, sheets, notes):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# 画の証拠 — %s\n\n" % os.path.basename(video))
        fh.write("- ログ: `%s`\n" % log)
        fh.write("- 録画: `%s`（%.1f 秒 / %.1f fps）\n" % (video, dur, fps))
        fh.write("- 時刻の対応: **録画秒 = XP秒 %+.2f**\n" % offset)
        fh.write("- 根拠: %s\n" % basis)
        if estimated:
            fh.write("- **この対応は推定なので、数秒ずれている前提で見ること。**\n")
        fh.write("\n")
        if notes:
            fh.write("## 気をつけること\n\n")
            for n in notes:
                fh.write("- ⚠ %s\n" % n)
            fh.write("\n")
        if sheets:
            fh.write("## 索引（コンタクトシート）\n\n")
            for s in sheets:
                fh.write("- `%s`\n" % os.path.basename(s))
            fh.write("\n**判定に使うのは下の個別 PNG。** シートは縮小してあるので細い線が消える。\n\n")

        for g, title in enumerate(GROUP_TITLE):
            group = [m for m in moments if m.group == g]
            if not group:
                continue
            fh.write("## %s\n\n" % title)
            fh.write("| 録画秒 | XP秒 | 何の瞬間か | 平均輝度 | ファイル名 |\n")
            fh.write("|---:|---:|---|---:|---|\n")
            for m in sorted(group, key=lambda x: x.xp_t):
                if m.path:
                    fh.write("| %.2f | %.2f | %s | %.1f | `%s` |\n" % (
                        m.actual_rec or m.rec_t, m.xp_t, m.desc, m.mean or 0.0,
                        os.path.basename(m.path)))
                else:
                    fh.write("| - | %.2f | %s | - | %s |\n" % (m.xp_t, m.desc, m.note or "切り出せなかった"))
            fh.write("\n")


# ---------------------------------------------------------------- main

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log")
    ap.add_argument("video")
    ap.add_argument("--out", default=None, help="出力先（既定 logs/evidence/<録画の日時>/）")
    ap.add_argument("--offset", type=float, default=None,
                    help="録画秒 = XP秒 + この値。録画が先に始まっていれば負")
    ap.add_argument("--all-runs", action="store_true",
                    help="ログを最後の走行だけに絞らない")
    args = ap.parse_args(argv)

    if not os.path.exists(args.log):
        print("no such log: %s" % args.log, file=sys.stderr)
        return 1
    if not os.path.exists(args.video):
        print("no such video: %s" % args.video, file=sys.stderr)
        return 1

    ref = stamp_of(args.video) or datetime.fromtimestamp(os.path.getmtime(args.video))
    rows, _raw = load_rows(args.log, ref)
    if not rows:
        print("no [XP] lines in the log", file=sys.stderr)
        return 1
    n_boot = 0
    if not args.all_runs:
        rows, n_boot = slice_last_run(rows)

    offset, estimated, basis = resolve_offset(args.video, rows, args.offset)
    if offset is None:
        print("cannot line up the log with the video: %s" % basis, file=sys.stderr)
        print("  pass --offset <sec> (rec = xp + offset)", file=sys.stderr)
        return 1

    moments = collect_moments(rows)
    if not moments:
        print("nothing to cut out (no intro / take / phase / storm events)", file=sys.stderr)
        return 1
    for m in moments:
        m.rec_t = m.xp_t + offset

    stamp = stamp_of(args.video)
    outdir = args.out or os.path.join("logs", "evidence",
                                      stamp.strftime("%Y%m%d_%H%M%S") if stamp else "latest")
    os.makedirs(outdir, exist_ok=True)

    written, fps, dur = extract(args.video, moments, outdir)
    sheets = contact_sheets(moments, outdir)
    notes = sanity_notes(moments, estimated)
    index = os.path.join(outdir, "index.md")
    write_index(index, args.log, args.video, moments, offset, estimated, basis,
                fps, dur, sheets, notes)

    print("moments=%d written=%d skipped=%d" % (len(moments), written, len(moments) - written))
    print("offset=%+.2fs (%s)" % (offset, "estimated" if estimated else "measured"))
    if n_boot > 1:
        print("log had %d runs; used the last one" % n_boot)
    if notes:
        print("warnings=%d (read index.md)" % len(notes))
    print("index -> %s" % index)
    return 0


if __name__ == "__main__":
    sys.exit(main())
