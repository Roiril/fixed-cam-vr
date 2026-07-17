---
name: camera-fleet
description: 配信スマホ 3 台の実機構成（機種・アプリ・IP・スロット割り当て）と検証状況
metadata: 
  node_type: memory
  type: project
  originSessionId: 6d7a1a02-c926-4301-b3e5-40f96aeedf21
---

# 配信カメラ実機フリート（2026-07-17 更新）

| スロット | 端末 | アプリ | IP（DHCP・揮発） | 検証 |
|---|---|---|---|---|
| Phone01 / cam A | Pixel 7a（3C251JEHN03582） | fixed-cam-streamer **v0.2.0**（:8080） | 192.168.11.26（7/17） | Web 卓 /cam 200 OK・29.7fps |
| Phone02 / cam B | Pixel 7a（37081JEHN03028） | fixed-cam-streamer **v0.2.0**（:8080） | 192.168.11.12（7/17） | Web 卓 /cam 200 OK・29.7fps |
| Phone03 / cam C | Pixel 7a（37201JEHN14152） | fixed-cam-streamer **v0.2.0**（:8080） | 192.168.11.27（7/17） | Web 卓 /cam 200 OK・30fps |

**2026-07-17 から現行フリートは Pixel 7a ×3 に統一**（全台 streamer v0.2.0・認証なし・:8080）。iPhone 13 Pro + IP Camera Lite（:8081・Basic admin/admin）は予備構成へ降格 — 使う時は該当カメラの auth を戻す。

**v0.2.0（2026-07-17）**: カメラエラー自動復旧 / WifiLock + WakeLock 定期更新 / 録画容量ガード / レンズ・ロック永続化 / `/info` appVersion・`/health` clientCount 追加。版確認は `curl http://<ip>:8080/info` の appVersion が最速。

**⚠ 起動の罠（2026-07-17 実害）**: 画面が Doze/スリープ中に `am start` でアプリを起動すると **Activity が resume されずカメラが bind 直後に閉じ、フレーム 0 のまま**になる（HTTP サーバだけ生きる）。→ 起動手順は `input keyevent KEYCODE_WAKEUP` + `wm dismiss-keyguard` → `am force-stop` → `am start`。起動後は KEEP_SCREEN_ON で維持される。

**Why:** 機種混在（iPhone ×1〜2 + Pixel ×2）が実運用構成。IP は現場の Wi-Fi ルータ次第で変わるので、host 値は参考値（毎現場で `/info` or curl で再確認）。

**How to apply:**
- host 変更は `Assets/Settings/Cameras/Phone0X.asset` を MCP `manage_scriptable_object` で書き換え（ローカル値・コミット禁止 — [[git-workflow]] のユーザー所有ファイル規約）
- 全端末超広角可: Pixel は streamer のレンズ選択（実測 FOV ≈128°）、iPhone は IP Camera Lite の「Back Ultra Wide Camera」選択（13 Pro 実機確認済み）
- iPhone の配信が重い（≈30Mbps）/ ウォーターマーク等の運用注意は [.claude/rules/streaming.md](../rules/streaming.md) の「iPhone（iOS）ソース」参照
- 3 セッション前の旧 IP（Pixel=192.168.11.8 / 11.12 等）がログや asset に残っていても気にしない（DHCP 変動）
