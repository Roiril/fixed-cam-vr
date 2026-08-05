---
name: camera-fleet
description: 配信スマホ 3 台の実機構成（機種・アプリ・IP・スロット割り当て）と検証状況
metadata: 
  node_type: memory
  type: project
  originSessionId: 6d7a1a02-c926-4301-b3e5-40f96aeedf21
  modified: 2026-08-05T02:15:41.648Z
---

# 配信カメラ実機フリート（2026-08-05 更新）

| スロット | 端末 | アプリ | IP（DHCP・揮発） | 検証 |
|---|---|---|---|---|
| Phone01 / cam A | Pixel 7a（3C251JEHN03582） | fixed-cam-streamer **実機 v0.7.0**（:8080・cameraId 刻印済み） | 192.168.11.17（8/5） | `/info` 応答・配信中 |
| Phone02 / cam B | Pixel 7a（37081JEHN03028） | fixed-cam-streamer **実機 v0.7.0**（:8080・cameraId 刻印済み） | 192.168.11.20（8/5） | `/info` 応答・配信中 |
| Phone03 / cam C | Pixel 7a（37201JEHN14152） | fixed-cam-streamer **実機 v0.7.0**（:8080・cameraId 刻印済み） | 192.168.11.23（8/5） | `/info` 応答・配信中 |

**⚠ v0.9.0（端末の傾きを配る）はビルド済みだが 3 台とも未インストール**（2026-08-05）。
実機は v0.7.0 なので `/info` に `tiltState` が無く、卓の 📱 端末の傾きパネルは
「この端末は傾きを送っていません」と出る。**入れるには USB 接続が要る**
（無線 adb は 3 台とも 5555 が閉じていて `adb connect` は拒否される。実測）。
入れ方は skill `streamer-android-build`（`./gradlew installDebug`）。
入れた後は**端末を水平に置いて上下が 0° 付近になるか**を必ず見る
（センサの符号と `targetRotation` の対応は実機でしか確かめられない唯一の箇所）。

**2026-07-17 から現行フリートは Pixel 7a ×3 に統一**（全台 streamer v0.2.0・認証なし・:8080）。iPhone 13 Pro + IP Camera Lite（:8081・Basic admin/admin）は予備構成へ降格 — 使う時は該当カメラの auth を戻す。

**v0.5.0（2026-07-18）**: 決定性キャプチャプロファイル — EIS/OIS/AF を明示 OFF（EIS は端末既定 ON で超広角を不可視クロップしていた・dumpsys 実測）、フリッカー 50Hz・露出補正 0 固定、AE/AWB は AUTO 維持（🔒は AE/AWB のみ）。実効値は `/health` の eisMode/cropRatio/iso 等で観測可。lensFovDeg は物理レンズ intrinsic 由来に是正（超広角 =104.3°、旧 129° は誤算出）。

**v0.4.0（2026-07-18）**: 配信中核を Service へ移管。HOME/画面 OFF/別アプリでも 30fps 維持（旧: 2.5fps に激減）。

**v0.3.0（2026-07-18）**: cameraId（A/B/C）を端末に刻印（画面巨大表示・再起動永続）+ fixedcam-discovery/1（UDP :8830、probe→unicast announce）。IP が変わっても Web 卓が自動追従・Quest が ID で自動張り替え。ID は .26=A / .12=B / .27=C（show.json と一致）。

**v0.2.0（2026-07-17）**: カメラエラー自動復旧 / WifiLock + WakeLock 定期更新 / 録画容量ガード / レンズ・ロック永続化 / `/info` appVersion・`/health` clientCount 追加。版確認は `curl http://<ip>:8080/info` の appVersion が最速。

**⚠ 起動の罠（2026-07-17 実害）**: 画面が Doze/スリープ中に `am start` でアプリを起動すると **Activity が resume されずカメラが bind 直後に閉じ、フレーム 0 のまま**になる（HTTP サーバだけ生きる）。→ 起動手順は `input keyevent KEYCODE_WAKEUP` + `wm dismiss-keyguard` → `am force-stop` → `am start`。起動後は KEEP_SCREEN_ON で維持される。

**Why:** 機種混在（iPhone ×1〜2 + Pixel ×2）が実運用構成。IP は現場の Wi-Fi ルータ次第で変わるので、host 値は参考値（毎現場で `/info` or curl で再確認）。

**How to apply:**
- host 変更は `Assets/Settings/Cameras/Phone0X.asset` を MCP `manage_scriptable_object` で書き換え（ローカル値・コミット禁止 — [[git-workflow]] のユーザー所有ファイル規約）
- 全端末超広角可: Pixel は streamer のレンズ選択（実測 FOV ≈128°）、iPhone は IP Camera Lite の「Back Ultra Wide Camera」選択（13 Pro 実機確認済み）
- iPhone の配信が重い（≈30Mbps）/ ウォーターマーク等の運用注意は [.claude/rules/streaming.md](../rules/streaming.md) の「iPhone（iOS）ソース」参照
- 3 セッション前の旧 IP（Pixel=192.168.11.8 / 11.12 等）がログや asset に残っていても気にしない（DHCP 変動）
