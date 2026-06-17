---
name: iphone_camera_streamer_plan
description: iPhone配信カメラの方針 — デモはIP Cam Lite（ウォーターマーク許容）、自作するならPWA+WSリレー設計
metadata: 
  node_type: memory
  type: project
  originSessionId: 9412ef70-8490-4fdb-8914-a80ae10fd508
---

2026-06-17 決定。iPhone の配信カメラ方針:

- **デモ段階は IP Camera Lite を継続使用**。ストリーミング映像内にライセンス表示（ウォーターマーク）が入るのは許容する（自作のための Mac/証明書/設置運用コストを今はかけない判断）。
- **自作（ライセンス回避）は保留**。再検討時の本命設計は **PWA + WebSocket-JPEG リレー**:
  - ブラウザは HTTP サーバになれない → 今の「Quest が phone から MJPEG を pull」モデルは不可。代わりに **phone(PWA, getUserMedia)→ JPEG を WS push → PC リレー(capture-server.py 拡張で最新フレーム保持)→ 既存と同じ multipart MJPEG で `/video?cam=X` 再配信 → Quest は show.json の host を PC に向けるだけで無改造**。
  - 利点: Mac不要 / App Store不要 / Apple登録不要 / ライセンス不要 / iOS・Android 同一コード / **Quest 無改造** / PC インフラ（[[web_compositor]] の capture-server.py）流用。
  - 主な摩擦: ① getUserMedia は HTTPS 必須（LAN は自己署名/mkcert を各端末に1回入れる）② iOS Safari は画面ロック/バックグラウンドで配信停止（設置は autolock OFF・常時前面・給電）③ JS の JPEG エンコードは CPU/電池が重く fps はネイティブ未満。
  - **Unity の iOS ビルドは Mac+Xcode 必須**（Windows 単独で iPhone に入れられない）→ 却下。低遅延を突き詰めるなら将来 WebRTC（Quest 側に com.unity.webrtc 受信実装が要る大仕事）。
- **Android は自作 fixed-cam-streamer 据え置き**（TCP調整・AE fps固定・/info回転・/health の作り込みがある）。web 統一は PWA の品質を実測してから判断。

関連: [[camera_fleet]]（実機3台構成）。show.json の cameras[].host 差し替えで接続先を変えられる仕組みは既に実装済み（rules/streaming.md「show.json = 設定契約」）。
