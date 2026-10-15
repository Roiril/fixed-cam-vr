#!/bin/bash
# Redmi Pad SE へ APK を USB で入れる。
#   bash tablet/install-confirm.sh Builds/doctor-tablet-test.apk [serial]
#
# Redmi は「USB経由でインストール」の確認ダイアログを数秒で自動拒否する
# （INSTALL_FAILED_USER_RESTRICTED。設定がオフという意味ではない。memory/redmi_tablet_install.md）。
# adb install を裏で走らせ、画面の階層を読んで、見出しに USB とアプリ名（廻リ視）が
# 出ていることを照合してから「インストール」を押す。「この選択を記憶」は触らない。
set -u
APK="$1"
SERIAL="${2:-122d7871}"
TMP="$(mktemp -d)"
adb -s "$SERIAL" install -r --no-incremental "$APK" > "$TMP/install.log" 2>&1 &
PID=$!
for _ in $(seq 1 20); do
  sleep 1
  kill -0 "$PID" 2>/dev/null || break
  # ⚠ Git Bash は /sdcard/... を Windows のパスへ書き換えるので変換を止める
  MSYS_NO_PATHCONV=1 adb -s "$SERIAL" shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1
  MSYS_NO_PATHCONV=1 adb -s "$SERIAL" exec-out cat /sdcard/ui.xml > "$TMP/ui.xml" 2>/dev/null
  if grep -q "USB" "$TMP/ui.xml" && grep -q "廻リ視" "$TMP/ui.xml"; then
    XY=$(py -3.11 - "$TMP/ui.xml" <<'PY'
import re, sys
x = open(sys.argv[1], encoding="utf-8").read()
for m in re.finditer(r"<node [^>]*>", x):
    n = m.group(0)
    t = re.search(r'text="([^"]*)"', n)
    if t and t.group(1) in ("インストール", "Install") and 'enabled="true"' in n:
        a, b, c, d = map(int, re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', n).groups())
        print((a + c) // 2, (b + d) // 2)
        break
PY
)
    if [ -n "$XY" ]; then
      echo "確認ダイアログの「インストール」を押す: $XY"
      adb -s "$SERIAL" shell input tap $XY
      break
    fi
  fi
done
wait "$PID"; RC=$?
tail -2 "$TMP/install.log"
rm -rf "$TMP"
exit $RC
