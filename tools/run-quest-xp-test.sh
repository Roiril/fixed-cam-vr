#!/usr/bin/env bash
# Quest 実機で体験を走らせ、[XP] ログを取って解析にかける。
#
#   bash tools/run-quest-xp-test.sh idle 90        # 静置観測（配信・砂嵐・遅延・表示 fps）
#   bash tools/run-quest-xp-test.sh walk 300       # 自動走行（導入 → 3 周 → 終了）
#   SERIAL=2G0YC1ZF7S06BW bash tools/run-quest-xp-test.sh walk 300   # 機を指名する
#
# 機を指名しなければ tools/quest-fleet.py が **いちばん冷えている機**を選ぶ。走った機は温まるので、
# 続けて回すと自然にもう 1 台へ移る（＝交互運用が待ち時間ゼロで成立する）。走行中は使わない機を
# 寝かせる（NOSLEEP=1 で無効化）。
#
# ■ ログ収集について（3 回落として分かったこと）
#
# 落ちた原因は「Unity のログ量」ではなかった。実測した 2.18MB のうち Unity 由来は 24% で、
# 残りは XrCameraHal / PasspointManager / libcamerahal ── **他プロセスがバッファを押し流していた**。
# しかも Debug.Log に付くスタックトレースだけで 3,941 行 179KB あった。
# だから対策は 3 つで 1 組:
#
#   1. タグを絞る            … ここ（Unity:V DEBUG:V *:E）で他プロセスを落とす
#   2. スタックを出さない    … ShowTelemetryHost.Bootstrap の SetStackTraceLogType（Development のみ）
#   3. バッファ拡張を実測確認… `-G` は端末に拒否されても黙って成功を返す。必ず `-g` で読んで出す
#
# 収集はストリームとダンプの**両方**を取る。ストリームは走行中に別の adb を打つと daemon 再起動で
# 黙って切れる（実害あり）。ダンプはバッファに残っている分しか取れない。片方が壊れてももう片方が
# 残るので、行数の多い方を採用する。
set -u

MODE="${1:-idle}"
SECS="${2:-90}"
PKG=com.roiril.mawarimi
ACT="$PKG/com.unity3d.player.UnityPlayerActivity"
OUTDIR="${OUTDIR:-logs}"
STAMP=$(date +%Y%m%d_%H%M%S)

mkdir -p "$OUTDIR"

# ---- 走らせる機を決める --------------------------------------------------
if [ -z "${SERIAL:-}" ]; then
  SERIAL=$(python tools/quest-fleet.py pick -v 2>/dev/null | head -1)
  if [ -z "$SERIAL" ]; then
    echo "使える Quest が見つからない。adb devices と quest-fleet.py list を見て。" >&2
    exit 1
  fi
  echo "機の選択: $SERIAL（いちばん冷えている機）"
else
  echo "機の指定: $SERIAL"
fi

LOG="$OUTDIR/xp_${MODE}_${STAMP}.log"
DUMP="$OUTDIR/xp_${MODE}_${STAMP}.dump.log"
ENV="$OUTDIR/xp_${MODE}_${STAMP}.env.txt"

echo "== $MODE / ${SECS}s / $SERIAL =="
# 走行時の機の状態を残す。後からレポートを見返したとき「どの機で・どの帯域で走ったか」が
# 分からないと、受信 fps や演出の欠落を読み違える。
python tools/quest-fleet.py list 2>/dev/null | tee "$ENV" | sed -n "1,4p"

# ⚠ 2.4GHz だと MJPEG の受信が配信の 2/3 まで落ちる。導入演出の最後の段（Swap）は
# 「映像が届いていること」(liveFresh) を要求し、3 秒待って駄目なら **段を飛ばして終わる**。
# つまり帯域が細いだけで演出が出ず、原因をコードだと取り違える。走る前に言う。
BAND=$(python tools/quest-fleet.py list 2>/dev/null | grep "$SERIAL" | grep -o "2\.4G" | head -1)
if [ -n "$BAND" ]; then
  echo "  ⚠ この機は 2.4GHz に繋がっている。受信 fps が落ち、導入の Swap が出ないことがある"
  echo "    （原因をコードと取り違えないこと。5GHz へ移すのは HMD の Wi-Fi 設定から）"
fi

# ---- 走行の準備 ----------------------------------------------------------
# スリープ中は am start が黙って失敗する（quest-build スキルの罠）。
adb -s "$SERIAL" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1
adb -s "$SERIAL" shell am force-stop "$PKG" >/dev/null 2>&1

# 使わない機は寝かせる（熱と電池を使わせない）。走行中に adb を触らないよう、ここで済ませる。
if [ "${NOSLEEP:-0}" != "1" ]; then
  python tools/quest-fleet.py sleep --others "$SERIAL" 2>/dev/null | sed 's/^/  sleep: /'
fi

adb -s "$SERIAL" logcat -G 16M >/dev/null 2>&1
BUF=$(adb -s "$SERIAL" logcat -g 2>/dev/null | head -1 | tr -d '\r')
echo "  logcat バッファ: $BUF"
case "$BUF" in
  *16Mb*|*16.0Mb*|*"16 MiB"*) : ;;
  *) echo "  ⚠ 16M への拡張が通っていない。タグ絞りで足りるはずだが、取りこぼしたらここを疑う" ;;
esac
adb -s "$SERIAL" logcat -c >/dev/null 2>&1

# ---- 走行 ----------------------------------------------------------------
# Unity 本体 + native crash(DEBUG) + 他プロセスの Error だけ。これで実測 2.18MB → 0.6MB。
adb -s "$SERIAL" logcat -v time Unity:V DEBUG:V "*:E" > "$LOG" 2>&1 &
LOGPID=$!
sleep 2   # ストリームが始まってからアプリを起動する（起動直後の行を落とさない）

if [ "$MODE" = "walk" ]; then
  adb -s "$SERIAL" shell am start -e xpwalk 1 -n "$ACT" >/dev/null 2>&1
else
  adb -s "$SERIAL" shell am start -n "$ACT" >/dev/null 2>&1
fi
echo "起動した。${SECS} 秒待つ…（走行中は adb を触らない）"

# ⚠ ここで adb を打つと daemon が再起動してストリームだけが黙って死ぬ。黙って待つ。
sleep "$SECS"

sleep 2
kill "$LOGPID" 2>/dev/null
wait "$LOGPID" 2>/dev/null

# ---- 回収 ----------------------------------------------------------------
# ストリームが途中で切れていてもバッファには残っている。両方取って多い方を使う。
adb -s "$SERIAL" logcat -d -v time Unity:V DEBUG:V "*:E" > "$DUMP" 2>&1

n_stream=$(grep -ac '\[XP\]' "$LOG" 2>/dev/null || echo 0)
n_dump=$(grep -ac '\[XP\]' "$DUMP" 2>/dev/null || echo 0)
echo "  [XP] 行数: ストリーム $n_stream / ダンプ $n_dump"

if [ "$n_dump" -gt "$n_stream" ]; then
  echo "  ダンプの方が多い → そちらを採用（ストリームが途中で切れた）"
  BEST="$DUMP"
else
  BEST="$LOG"
  rm -f "$DUMP"
fi
echo "ログ: $BEST ($(wc -l < "$BEST") 行 / [XP] $(grep -ac '\[XP\]' "$BEST") 行)"

# 走行の記録（次の pick が「前回使っていない方」を選べるように）。
python tools/quest-fleet.py mark "$SERIAL" --sec "$SECS" --mode "$MODE" >/dev/null 2>&1

# 3 分の走行なら [XP] は 200 行前後。極端に少ないなら収集が壊れている。
if [ "$(grep -ac '\[XP\]' "$BEST")" -lt 20 ]; then
  echo "⚠ [XP] が少なすぎる。Development ビルドか、テレメトリの起動を確認する" >&2
fi

python tools/analyze-xp-log.py "$BEST" --show tools/web-compositor/show.json
