#!/system/bin/sh
# Android 端末（Quest・スマホ）の Wi-Fi から台車へ /joy を送り、往復の時間を測る（2026-10-23）。
# 台車は x=0 y=0 なので動かない。PC の joy-jitter.py と違い、5GHz の Wi-Fi 越しの途切れが測れる。
#   adb -s <serial> push tools/romi/joy-from-device.sh /data/local/tmp/
#   adb -s <serial> shell sh /data/local/tmp/joy-from-device.sh 60 > out.txt
# 出力は 1 行 1 回「経過ms 往復ms 応答の先頭」。台車の /info の joyGapMaxMs / joyDeadman と突き合わせる。
SEC=${1:-60}
HOST=${2:-192.168.10.51}
END=$(( $(date +%s) + SEC ))
T0=$(date +%s%3N)
while [ "$(date +%s)" -lt "$END" ]; do
  A=$(date +%s%3N)
  R=$(printf 'GET /joy?x=0.000&y=0.000&cap=25&flip=0 HTTP/1.0\r\nHost: romi\r\n\r\n' | nc -w 1 "$HOST" 80 | head -c 12)
  B=$(date +%s%3N)
  echo "$((A - T0)) $((B - A)) $R"
  W=$((60 - (B - A)))
  [ "$W" -gt 0 ] && sleep "0.0$(printf '%02d' "$W")"
done
