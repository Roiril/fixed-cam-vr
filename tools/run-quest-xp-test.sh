#!/usr/bin/env bash
# Quest 実機で体験を走らせ、[XP] ログを丸ごと取って解析にかける。
#
#   tools/run-quest-xp-test.sh idle 90          # 静置観測（歩かない）
#   tools/run-quest-xp-test.sh walk 300         # 自動走行（導入 → 3 周 → 終了）
#
# logcat は **フィルタせずに全部取る**（-d ダンプ）。TakeRunner / SegmentRecorder / ScreenOverlay の
# 理由つき警告が体験の判定に要るため。溢れないようリングバッファを広げてから起動する。
set -u

MODE="${1:-idle}"
SECS="${2:-90}"
SERIAL="${SERIAL:-2G0YC1ZF7S06BW}"
PKG=com.roiril.mawarimi
ACT="$PKG/com.unity3d.player.UnityPlayerActivity"
OUTDIR="${OUTDIR:-logs}"
STAMP=$(date +%Y%m%d_%H%M%S)
LOG="$OUTDIR/xp_${MODE}_${STAMP}.log"

mkdir -p "$OUTDIR"

echo "== $MODE / ${SECS}s / $SERIAL =="

# スリープ中は am start が黙って失敗する（quest-build スキルの罠）。
adb -s "$SERIAL" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1
adb -s "$SERIAL" shell am force-stop "$PKG" >/dev/null 2>&1

# ログ収集は 3 通り試して、**ストリーム＋走行中は adb を触らない**に落ち着いた。
#   1. `-d` だけ（既定バッファ）      → 90 秒の観測で [XP] が 3 行しか残らない
#   2. ストリーム＋走行中に adb 併用  → daemon 再起動でストリームだけが黙って切れる
#   3. `-d` だけ（16M へ拡張）        → 300 秒は保たず、**前半（起動〜本編）が丸ごと落ちた**
# Unity が HudDump / HmdTrace をスタック付きで毎秒出すのでログ量が支配的。16M でも 5 分は保たない。
# **32M は端末に拒否されて既定のまま黙って据え置かれる**ので、広げたら必ず `-g` で確かめる。
adb -s "$SERIAL" logcat -G 16M >/dev/null 2>&1
echo "  logcat バッファ: $(adb -s "$SERIAL" logcat -g 2>/dev/null | head -1)"
adb -s "$SERIAL" logcat -c >/dev/null 2>&1

adb -s "$SERIAL" logcat > "$LOG" 2>&1 &
LOGPID=$!
sleep 2   # ストリームが始まってからアプリを起動する（起動直後の行を落とさない）

if [ "$MODE" = "walk" ]; then
  adb -s "$SERIAL" shell am start -e xpwalk 1 -n "$ACT" >/dev/null 2>&1
else
  adb -s "$SERIAL" shell am start -n "$ACT" >/dev/null 2>&1
fi
echo "起動した。${SECS} 秒待つ…"

# ⚠ 走行中は adb を一切触らない（daemon が再起動するとストリームだけが黙って切れる）。黙って待つ。
sleep "$SECS"

sleep 2
kill "$LOGPID" 2>/dev/null
wait "$LOGPID" 2>/dev/null
echo "ログ: $LOG ($(wc -l < "$LOG") 行 / [XP] $(grep -ac '\[XP\]' "$LOG") 行)"

python tools/analyze-xp-log.py "$LOG" --show tools/web-compositor/show.json
