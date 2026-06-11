---
name: camera-fleet
description: 配信スマホ 3 台の実機構成（機種・アプリ・IP・スロット割り当て）と検証状況
metadata: 
  node_type: memory
  type: project
  originSessionId: 6d7a1a02-c926-4301-b3e5-40f96aeedf21
---

# 配信カメラ実機フリート（2026-06-11 確定）

| スロット | 端末 | アプリ | IP（DHCP・揮発） | 検証 |
|---|---|---|---|---|
| Phone01 | iPhone 13 Pro | IP Camera Lite（:8081/video、Basic admin/admin） | 192.168.11.22 | Editor 受信・描画 OK |
| Phone02 | Pixel 7a | fixed-cam-streamer（:8080） | 192.168.11.9 | Editor 受信 OK |
| Phone03 | Pixel 7 Pro | fixed-cam-streamer（:8080） | 192.168.11.27 | Editor 受信 OK |

**Why:** 機種混在（iPhone ×1〜2 + Pixel ×2）が実運用構成。IP は現場の Wi-Fi ルータ次第で変わるので、host 値は参考値（毎現場で `/info` or curl で再確認）。

**How to apply:**
- host 変更は `Assets/Settings/Cameras/Phone0X.asset` を MCP `manage_scriptable_object` で書き換え（ローカル値・コミット禁止 — [[git-workflow]] のユーザー所有ファイル規約）
- 全端末超広角可: Pixel は streamer のレンズ選択（実測 FOV ≈128°）、iPhone は IP Camera Lite の「Back Ultra Wide Camera」選択（13 Pro 実機確認済み）
- iPhone の配信が重い（≈30Mbps）/ ウォーターマーク等の運用注意は [.claude/rules/streaming.md](../rules/streaming.md) の「iPhone（iOS）ソース」参照
- 3 セッション前の旧 IP（Pixel=192.168.11.8 / 11.12 等）がログや asset に残っていても気にしない（DHCP 変動）
