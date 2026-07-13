---
name: camera_c120_go2rtc_plan
description: 配信元をスマホ→Tapo C120(RTSP)化する案の調査結論：go2rtc中継ならUnity改修ゼロ・遅延だけが唯一のリスク
metadata: 
  node_type: memory
  type: project
  originSessionId: 09dc8cde-cc1e-4b2b-82d3-eccb5e1a5d43
---

**⚠ 2026-07-13 C120 は却下**（セキュリティ＝中華クラウド接続 NG）。カメラ選定の全体像とその後の調査（RTSP をQuestネイティブでPC中継なし受信できる／技適／代替機種）は [[camera_source_alternatives]] に集約。以下は go2rtc 中継方式の技術検証として引き続き有効（Tapo に限らず RTSP カメラ全般に適用可）。

廻リ視の配信カメラをスマホ3台から **TP-Link Tapo C120 ×3** に置き換える案の調査結論（2026-07-11）。当初 C120 に決定→後日セキュリティ却下。

**なぜ C120**: 画角 120°対角/103°水平 ≒ iPhone Pro 超広角・C411 相当。**プラグイン給電（非バッテリー）機なので RTSP/ONVIF 対応**（Tapo はバッテリー機を RTSP 対象外にしている＝C411 等は不可）。Amazon.co.jp 限定モデルあり。同型3台で色味・画角を揃える。

**接続方式 = go2rtc 中継（RTSP→MJPEG 変換）**:
```
Tapo C120 ──RTSP(H.264)──▶ PC:go2rtc ──MJPEG/HTTP──▶ Quest(現行コードのまま)
```

**Unity 改修はゼロ**（[MjpegStreamReceiver](../../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) 実コード確認済み）:
- boundary は Content-Type から自動検出 → go2rtc の multipart をそのまま受ける
- `_uri.PathAndQuery` を送るのでクエリ付き videoPath (`/api/stream.mjpeg?src=cam1`) が通る
- chunked 自動デチャンク・Basic認証対応・`X-Capture-Ns/Seq` は任意（無くても映像正常）
- [CameraSource](../../Assets/Scripts/Streaming/CameraSource.cs) の host/port/videoPath/user/pass に go2rtc 出力先を入れるだけ

**変えるのはインフラ・設定のみ**:
- PC に go2rtc 新設（streams に3カメラの `rtsp://user:pass@ip:554/stream1`、出力 `http://<PC>:1984/api/stream.mjpeg?src=camN`）
- [Phone01-03.asset](../../Assets/Settings/Cameras/Phone01.asset) を go2rtc 向けに書換（host=PCのIP / port=1984 / videoPath=/api/stream.mjpeg?src=camN）
- カメラ3台は DHCP 予約で IP 固定、Tapo アプリでローカル account 作成、各カメラにコンセント

**唯一のリスク = 遅延増**: 現行スマホ直配信は 100–200ms。go2rtc 経路は H.264エンコード→デコード→MJPEG再エンコードで段が増え、体感が変わりうる。**1台で実測必須**。ダメなら (a)go2rtc調整 (b)AVProでネイティブRTSP(PC中継不要・有料) (c)スマホ構成に戻す。他の副作用: PC常時稼働必須（Quest単体運用の利点が消える）、MJPEG再エンコードのCPU負荷、Tapo substreamのfps上限、/info無しで自動回転メタ来ない（uvRotSteps手動補正）。

**影響しない**: ポストFX・オーバーレイcue・ゾーン連動・web-compositor 合成は全部テクスチャ処理なので配信元非依存でそのまま動く。設置は部屋の3角・高さ2.2–2.5m・俯角20–30°・中心狙い（監視カメラ風見下ろし）。C120 はマグネットベース（1/4ネジ無し）→自由配置は「C120用1/4変換アダプタ＋撮影用ライトスタンド」。
