---
name: project_overview
description: fixed-cam-vr プロジェクトのスタック・目的・主要構成
type: project
---

# fixed-cam-vr プロジェクト概要

## 目的

Meta Quest 3 を装着した人の視界に、バイオハザード風の固定視点カメラ映像を表示する VR 体験。実世界はパススルー OFF（暗転）または部分的に表示。スマホ（DroidCam / IP Webcam）を複数台使った無線カメラからの MJPEG 映像を VR 内のスクリーンに描画する。

**Why**: ホラーゲーム的な閉塞感と固定視点演出を、現実空間に重ねた擬似 LARP 体験として実装する実験。

**How to apply**: 機能追加判断時、「映像表示 → 加工 → CG 合成 → スクリーン外演出」の段階的拡張を意識。最初から作り込まない。

## スタック

- Unity 2022.3.62f2 LTS / URP
- ターゲット: Meta Quest 3（Android / IL2CPP / ARM64）
- XR SDK: Meta XR All-in-One SDK + XR Interaction Toolkit + OpenXR Plugin
- 映像: MJPEG over Wi-Fi（DroidCam / IP Webcam）→ Unity 内 Texture2D 更新

## 段階的マイルストーン

1. PoC: Quad に DroidCam 1 台の MJPEG を表示（Quest 3 ネイティブ）
2. 複数台 + カメラ切替
3. パススルー暗転 + CCTV 風シェーダ（走査線・ノイズ）
4. CG 合成・3D 演出（スクリーン外オブジェクト・空間オーディオ）

## 重要な制約

- **書き込みレス**: ビルド時間が長いため、コンパイル検証は最小化
- **実機検証はユーザー依存**: Quest 3 デプロイ後の動作はシュビーから確認できない
- **90Hz 維持**: GC アロケーションをフレーム内で発生させない（特に MJPEG デコード）
