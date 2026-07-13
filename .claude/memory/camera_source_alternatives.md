---
name: camera_source_alternatives
description: 配信カメラをスマホ以外に替える検討の全体像（判定基準=MJPEG直pull／RTSPはQuestネイティブ受信可／技適／JP入手機種）
metadata:
  node_type: memory
  type: project
  originSessionId: 09dc8cde-cc1e-4b2b-82d3-eccb5e1a5d43
---

廻リ視の配信元をスマホ（IP Camera Lite / fixed-cam-streamer）から専用カメラ等に替える検討の集約（2026-07-13）。関連: [[camera_c120_go2rtc_plan]]（go2rtc中継の技術検証）/ [[iphone_camera_streamer_plan]] / [[camera_fleet]]。

## 判定の唯一基準
受信側 [MjpegStreamReceiver](../../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) は「HTTP GET で multipart/x-mixed-replace の MJPEG を返すサーバ」なら機種問わず映せる。**IP Camera Lite が良かった理由=iPhone上にHTTPサーバが立ちMJPEGを直吐き→Quest直pull・PC不要**。それだけ。「WiFiカメラ＝PC必須」ではなく「MJPEGを吐かない（RTSPのみの）カメラだから変換PCが要る」だけ。

## 2つの成立ルート（どちらも PC 中継なし）
1. **MJPEG直吐きカメラ → 現行コードのまま・Unity改修ゼロ・遅延100–200ms（現状並）**。該当機種: Amcrest ProHD系(`/cgi-bin/mjpg/video.cgi?channel=0&subtype=1`・ただし新FWはDigest認証/640x480 substream限定・JP入手難)、ESP32-CAM（`/stream`がネイティブMJPEG）。Tapo/Reolink/SwitchBot等の量販機はRTSPのみで不可。
2. **RTSPカメラ → Questネイティブ受信**（2026-07-13 Opusサブエージェント2本で確定）。**MJPEG以外もPC中継なしで成立する**:
   - Unity VideoPlayer は rtsp/HLS 非対応（http progressive のみ）＝標準機能では不可
   - **Vizario H264RTSP for Unity**（有料アセット）＝Quest2/3でVulkan/GLES3両対応と販売元明言・唯一のQuest実績明言源。720p30実働報告。トライアル320x240で遅延実測可 → 最小工数
   - **vlc-unity**（videolan公式・LGPL・ARM64/Vulkan明記）＝自前ビルドで無料。予算ゼロ縛りの本命
   - **ExoPlayer(Media3) を AAR自作**＝Android公式がRTSP(H.264/Basic/Digest認証)サポート。SurfaceTexture→テクスチャ橋渡しを自作、Vulkanが難所
   - **横断リスク**: QuestのVulkan×映像テクスチャが地雷（WebRTCは「Vulkanで黒画面/GLES3なら動く」報告多数）。GLES3強制はProjectSettings共有ゆえTableDuo同居アプリに波及。②③は Vulkan 対応明言で有利
   - WebRTC(com.unity.webrtc)は「カメラ側がWebRTCを喋る」前提＝素のRTSPカメラは変換ゲートウェイ必須で「中継なし」を満たさず、本要件では非推奨
   - go2rtc中継(RTSP→MJPEG)も選択肢だがPC常時稼働＋再エンコード遅延（[[camera_c120_go2rtc_plan]]）

## ⚠ 技適（公開展示=IVRCで効く・重要）
- Amazon.co.jpの汎用ESP32-CAMは**技適未取得が大半→公開展示で電波発射は電波法グレー〜アウト**。技適付きWT-ESP32-CAMは主にAliExpress＋付属アンテナが技適対象外。**「外部アンテナ版ESP32-CAMをJPで技適クリアして即使う」は現実的に不成立**。ESP32-CAMは自宅の近距離検証専用と割り切る。
- **ATOM Cam 2 は日本の消費者製品＝技適取得済み**。公開展示で安心。RTSP公式対応済み（昔は非公式ツール必須→改善）。ただしハード製造は中国系・RTSPはH.264なので上記②のQuestネイティブ受信 or go2rtc が要る。「中華ハードNG」なら落ちる（「中華クラウドNG」だけなら国内クラウド運用で通せる）。

## Tapo/ATOM Cam 2 のクラウド切断・実用性（2026-07-13 Opus調査2本）
- **Tapo（有線給電機 C110/C120等）はWAN遮断で「映像を一切外に出さないRTSP運用」が可能**。ただし初回だけネット＋Tapoアカウント＋アプリ必須（完全オフライン初期化は不可・Web UI無し）。セットアップでRTSP用カメラアカウント作成＋Tapo Care切りSD録画→以後VLAN/ルータでWAN向けだけ遮断（LAN内許可）→AWSへのphone-home停止・RTSPはLAN内継続（C110/C225実測）。副作用=時刻同期壊れる（映像無害）。送信先は中国直でなくAWS（keep-alive・中身は第三者未検証）。**残るのはベンダーリスク**（TP-Link製自体・米規制逆風）＝ポリシー判断。「中華クラウドNGだがWAN遮断可」なら C120 復活候補。
- **ATOM Cam 2 は用途に不適**（ハードは理想=1080p/120°対角(水平102°)/IP67/USB常時給電/¥2,980/技適ほぼ確実、だがソフト運用が×）: RTSP公式対応(FW4.58.0.91〜・URL`rtsp://user:pass@ip:554/live`)だが **①「15分〜6時間でフリーズ」報告多数（数時間無人展示と衝突）②遅延HEVC2〜3秒＝現行スマホ100-200msの1桁悪化③2.4GHzのみ＝3台+Quest集約で干渉崩壊・実質全台有線化前提④RTSP認証が4桁数字で再起動ごとに変わる（固定は非公式atomcam_tools頼み）**。ハードは中国系OEM(Hualai/Wyze系)、クラウドは日本AWS・WAN遮断で封じ込め可。**据置・防水1台運用以外は非推奨**。

## 除外・却下
- **Tapo C120**: 中華クラウドNGで却下（[[camera_c120_go2rtc_plan]]）
- **GoPro**: RTMP push/UDP/HLSのみでMJPEG-HTTP無し→不適
- **Amcrest**: 技術的には最適(ProHD系MJPEG直・クラウド任意)だがJP入手難・国内代理店は予算オーバー・ハードはDahua OEM(中華ハードNGなら落ちる)
- **自作PWA(iPhone)**: getUserMediaで内蔵超広角も使えるが、iOSはHTTPサーバを立てられずpush型→**PC中継必須**。遅延150–300ms(canvas→JPEGが重い)。Quest無改造で済むのが利点

## JP入手できる現実的候補（Tapo価格帯・~120°）
- **ATOM Cam 2**（¥7,567・[B094YDG7ZV](https://www.amazon.co.jp/dp/B094YDG7ZV)・120°・技適○・RTSP公式）
- **ESP32-CAM-MB**（[B09L6LF7BB](https://www.amazon.co.jp/dp/B09L6LF7BB)・書込用USB付）+ 120°OV2640レンズ（[B0DK1ZY79K](https://www.amazon.co.jp/dp/B0DK1ZY79K)）。MJPEG直・改修ゼロ・遅延現状並だが技適で自宅検証用

## 推奨検証順（未確定・要ユーザー実機）
遅延が全ての分岐点。①Vizarioトライアルで手持ちRTSP源→Quest実機遅延を数時間で判定 ②並行でESP32-CAM(¥3,000弱)でMJPEG直の遅延・画質を確認。許容できる方に倒す。廻リ視は自分の動きが映像に映る遅延が知覚直結なので購入前に実測必須。
