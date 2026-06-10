---
name: fixed-cam-review-backlog
description: 2026-06-10 の fixed-cam 本体レビュー結果 — 修正済み項目と残バックログ
metadata: 
  node_type: memory
  type: project
  originSessionId: 93b1d0df-ed63-4c85-99db-74093d710291
---

# fixed-cam 本体レビュー（2026-06-10）

**Why:** TableDuo 着手で fixed-cam 側が古くなったためユーザー依頼で全面レビュー + 自己改善 1 回実施。

## 修正済み
- MjpegScreen.cs 単一モードの Texture2D 未初期化（Quest GPU 白ノイズ罠 [[unity_pitfalls]]）→ 黒初期化を追加

## 誤検知として棄却（再調査不要）
- MjpegStreamReceiver 受信ループの「無音死」→ 最外層 `catch (Exception)` で保護済み
- CameraStream.DroppedFrames のレース → Tick() はメインスレッド専用、レースなし
- Dispose 漏れ → MjpegStreamReceiver.Dispose() は Cancel + Wait(500ms) + Dispose で完備

## 残バックログ（優先度順）
1. OvrBridge / Fx 系 57 ファイルに `#nullable enable` 未付与（一括追加可、機能影響なし）
2. StreamMetadataFetcher: HttpClient.Timeout(5s) と CTS(3s) の二重タイムアウト → `Timeout.InfiniteTimeSpan` + CTS 一本化
3. StreamMetadata / StreamHealth の public フィールドは JsonUtility 用 DTO（規約例外）→ コメントで意図明示
4. DroidCam 互換コード（CameraSource 既定値・Ping DroidCams メニュー）は意図的保持。Phase 4 で削除判断
5. /info 定期 refresh は 1.5s 間隔 → スマホ回転の反映が最大 1.5s 遅れる（仕様として許容中）

**How to apply:** fixed-cam 側を再度触るセッションで 1–2 を先に消化する。

## 教訓（自己改善）
ルール/罠を rules・memory に追加した時、**既存コードのスイープをセットでやる**こと。streaming.md に「Texture2D 初期化必須」と書いた時点で MjpegScreen が未修正のまま残っていた。
