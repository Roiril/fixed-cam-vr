---
name: droidcam_endpoint
description: テスト用 DroidCam のエンドポイント情報（開発端末固有）
metadata: 
  node_type: memory
  type: reference
  originSessionId: 93b1d0df-ed63-4c85-99db-74093d710291
---

# DroidCam テスト端末【フォールバック専用】

**標準は fixed-cam-streamer に移行済み**（[[project_overview]]）。DroidCam は緊急時フォールバックのみ。以下はその時用の接続情報。

- **WiFi (LAN) IP**: `192.168.11.34`
- **MJPEG endpoint**: `http://192.168.11.34:4747/video`
- **HTTP UI**: `http://192.168.11.34:4747/`
- **ポート**: `4747`（DroidCam 標準）

LAN IP は Wi-Fi 環境（ルータの DHCP）に依存する。別環境で動かすときは DroidCam アプリ画面に表示される IP を確認する。

## 制約

- 同時 1 クライアントのみ受信可能（[unity_pitfalls.md](unity_pitfalls.md) 参照）
- スマホ側で DroidCam アプリがフォアグラウンドにある必要あり（バックグラウンドだと配信停止しがち）

## 切り替え

別カメラを足すときは `Assets/Settings/Cameras/` 配下に `CameraSource` ScriptableObject を作って `MjpegScreen.source` に差し込む。
