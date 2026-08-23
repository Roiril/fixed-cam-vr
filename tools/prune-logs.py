"""検証出力を刈り込む。既定は dry-run。

    py -3.11 tools/prune-logs.py              # 何が消えるか見るだけ
    py -3.11 tools/prune-logs.py --apply      # 実削除（ごみ箱を経由しない）
    py -3.11 tools/prune-logs.py --assets --apply   # Assets/Screenshots も含める

`Logs/` と `Assets/Screenshots/` は .gitignore 済みなので、消したら戻らない。
だから「消してよいもの」ではなく「残すもの」を先に決めてある。

残すもの:

- **走行の数値証拠は日付に関わらず全部**（`*_xp.log` / `*_logcat.log` / `*_meta.json` /
  `*-report.md`）。1 走行あたり数百 KB しかないのに、古い走行と数値で比べる需要はこれで足りる。
  容量を食っているのは同じ走行の mp4（1 走行 300〜450MB）の方
- **直近 --keep-days 日ぶん**（既定 2 日）。並行セッションが同じ working copy で動いているので、
  今日と昨日は丸ごと避ける
- **`Logs/gen-plate/`**。`selftest.py` が README で古い走行名を名指しし、`yield.py` は手元の
  全走行を母数に合格率を出す。0.26GB しかないので刈っても得が無い
- **`Logs/sound/ingest/`**。焼き直せない元音源

⚠ `Assets/` 配下は Unity がアセットDB を握っているので、ビルドや Editor が動いている間に消すと
再インポートが走って進行中の作業を壊す。`--assets` は unity.exe が居ないときだけ通る。
"""

import argparse
import datetime as dt
import os
import re
import shutil
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGS = os.path.join(PROJ, "Logs")
SHOTS = os.path.join(PROJ, "Assets", "Screenshots")

DATE_IN_NAME = re.compile(r"(20\d{6})")

# 走行の数値証拠。日付に関わらず残す
EVIDENCE_SUFFIXES = ("_xp.log", "_logcat.log", "_meta.json", "-report.md", "_xp-report.md")

# 丸ごと触らない（理由は module docstring）
PROTECTED = ("gen-plate", os.path.join("sound", "ingest"))

# 古いものを刈ってよい Logs のサブディレクトリ
PRUNABLE_SUBDIRS = (
    "preview",
    "title",
    "swap-look",
    "swap-motion",
    "swap-ref",
    "sealedbox",
    "shatter",
    "osd",
    "comms-face",
    "walk-guide",
)


def is_protected(path):
    rel = os.path.relpath(path, LOGS)
    return any(rel == p or rel.startswith(p + os.sep) for p in PROTECTED)


def dir_size(path):
    total = 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total


def date_of(path):
    """ファイル名に埋まった日付を優先し、無ければ mtime を使う。"""
    m = DATE_IN_NAME.search(os.path.basename(path))
    if m:
        try:
            return dt.datetime.strptime(m.group(1), "%Y%m%d").date()
        except ValueError:
            pass
    return dt.date.fromtimestamp(os.path.getmtime(path))


def unity_is_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq Unity.exe", "/NH"],
            capture_output=True,
            text=True,
            timeout=30,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return True  # 確かめられないなら動いている扱いにする（安全側）
    return "Unity.exe" in out


def collect(keep_from, mp4_keep_from, include_assets):
    """(bytes, path, isdir, reason) の一覧を返す。"""
    plan = []

    def add_file(path, cutoff, reason):
        if os.path.isfile(path) and date_of(path) < cutoff:
            plan.append((os.path.getsize(path), path, False, reason))

    # 1. Logs/capture は mp4 だけ刈る
    cap = os.path.join(LOGS, "capture")
    if os.path.isdir(cap):
        for name in os.listdir(cap):
            if name.lower().endswith(".mp4"):
                add_file(os.path.join(cap, name), mp4_keep_from, "capture/mp4")

    # 2. Logs/evidence は日時ディレクトリ丸ごと
    ev = os.path.join(LOGS, "evidence")
    if os.path.isdir(ev):
        for name in sorted(os.listdir(ev)):
            path = os.path.join(ev, name)
            if not os.path.isdir(path):
                continue
            m = DATE_IN_NAME.match(name)
            if m:
                day = dt.datetime.strptime(m.group(1), "%Y%m%d").date()
            else:
                day = dt.date.fromtimestamp(os.path.getmtime(path))
            if day < keep_from:
                plan.append((dir_size(path), path, True, "evidence/古い走行"))

    # 3. その他の Logs サブディレクトリは古いファイルだけ
    for sub in PRUNABLE_SUBDIRS:
        base = os.path.join(LOGS, sub)
        if not os.path.isdir(base) or is_protected(base):
            continue
        for root, _dirs, files in os.walk(base):
            for name in files:
                add_file(os.path.join(root, name), keep_from, f"Logs/{sub}")

    # 4. Logs 直下の古いビルドログ
    if os.path.isdir(LOGS):
        for name in os.listdir(LOGS):
            path = os.path.join(LOGS, name)
            if os.path.isfile(path) and name.endswith(".log"):
                add_file(path, keep_from, "古いビルドログ")

    # 5. Editor プレビューの出力（.meta と対で消す）
    if include_assets and os.path.isdir(SHOTS):
        for root, _dirs, files in os.walk(SHOTS):
            for name in files:
                if name.endswith(".meta"):
                    continue  # 本体を消すときに一緒に消す
                path = os.path.join(root, name)
                if date_of(path) >= keep_from:
                    continue
                size = os.path.getsize(path)
                meta = path + ".meta"
                if os.path.isfile(meta):
                    size += os.path.getsize(meta)
                plan.append((size, path, False, "Assets/Screenshots"))

    return plan


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--apply", action="store_true", help="実際に削除する（既定は dry-run）")
    ap.add_argument("--keep-days", type=int, default=2, help="直近これだけの日数は残す（既定 2）")
    ap.add_argument(
        "--mp4-keep-days",
        type=int,
        default=1,
        help="走行録画 mp4 だけは短く残す（既定 1 ＝今日ぶんのみ）",
    )
    ap.add_argument(
        "--assets",
        action="store_true",
        help="Assets/Screenshots も刈る（Unity が動いていないときだけ）",
    )
    args = ap.parse_args()

    if args.assets and unity_is_running():
        print("中止: Unity が動いている。Assets/ を消すと再インポートが走って進行中の作業を壊す。")
        print("      Logs だけなら --assets 無しで通る。")
        return 1

    today = dt.date.today()
    keep_from = today - dt.timedelta(days=args.keep_days - 1)
    mp4_keep_from = today - dt.timedelta(days=args.mp4_keep_days - 1)

    plan = collect(keep_from, mp4_keep_from, args.assets)

    print("APPLY（実削除）" if args.apply else "DRY-RUN（--apply で実削除）")
    print(f"残す境界: {keep_from} 以降（mp4 は {mp4_keep_from} 以降）")
    print()

    by_reason = {}
    for size, _path, _isdir, reason in plan:
        acc, count = by_reason.get(reason, (0, 0))
        by_reason[reason] = (acc + size, count + 1)

    for reason, (size, count) in sorted(by_reason.items(), key=lambda kv: -kv[1][0]):
        print(f"{size/1048576:9.1f} MB  {count:5d} 件  {reason}")
    total = sum(s for s, _p, _d, _r in plan)
    print("-" * 44)
    print(f"{total/1048576:9.1f} MB  {len(plan):5d} 件  合計")

    if not args.apply:
        if plan:
            print()
            print("--- 消える最大 10 件 ---")
            for size, path, _isdir, _reason in sorted(plan, reverse=True)[:10]:
                print(f"{size/1048576:9.1f} MB  {os.path.relpath(path, PROJ)}")
        return 0

    freed = 0
    failed = []
    for size, path, isdir, _reason in plan:
        try:
            if isdir:
                shutil.rmtree(path)
            else:
                os.remove(path)
                meta = path + ".meta"
                if os.path.isfile(meta):
                    os.remove(meta)
            freed += size
        except OSError as exc:
            failed.append((path, str(exc)))

    print()
    print(f"削除完了: {freed/1048576:.1f} MB")
    if failed:
        print(f"失敗 {len(failed)} 件:")
        for path, err in failed[:10]:
            print(f"  {os.path.relpath(path, PROJ)}: {err}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
