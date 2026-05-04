# Changelog

このプロジェクトの変更履歴。フォーマットは [Keep a Changelog 1.1.0](https://keepachangelog.com/ja/1.1.0/) に準拠し、バージョニングは Phase 単位で扱う。

## [Unreleased]

### Added
- GitHub Actions による Unity EditMode テストの自動実行ワークフロー（`.github/workflows/unity-test.yml`）
- ワークフロー運用手順とシークレット取得ガイド（`.github/workflows/README.md`）
- 本 CHANGELOG

## [Phase 3] - 2026-05-03

映像加工 / CG 合成 4 系統のプロトタイプを比較検証する段階。

### Added
- 映像加工 / CG 合成プロトタイプ 4 系統と比較レポート（CRT 風 / フィルム粒子 / 薄い埃 / Sobel エッジ）
- `Assets/Art/Shaders/Fx/` 配下のシェーダ群と `FxSandbox.unity`
- `Tools > FixedCamVr > Setup > Create CRT Material` などの Editor メニュー
- README に Phase 2.7 / Phase 3 の成果を反映

## [Phase 2.7] - 2026-05-03

プレイヤー位置連動でカメラを切り替える仕組みを追加。

### Added
- `PlayerZone` / `PlayerZoneTracker` によるプレイヤー位置連動カメラ切替
- 検証用 `ZoneSandbox.unity` シーン

## [Phase 2.5] - 2026-05-03

Debug シーンを Main と機能統一し、UX を底上げ。

### Added
- 推奨ワークスペース準備メニューと作業フローのドキュメント
- Debug シーンに Hierarchy グループと SourceLabel を追加し Main と統一
- Hierarchy セパレータの着色とレイアウト保存メニュー
- Unity Editor 開発体験改善（Tools メニュー / Custom Inspector / Hierarchy 整理）

### Changed
- DroidCam 実 IP の反映と権限 allow リストの整理
- エディタレイアウト保存と MCP 接続再発メモを反映

## [Phase 2] - 2026-05-03

複数 DroidCam の切替表示と HMD なし検証フローを整備。

### Added
- 複数 DroidCam の切替表示と UX 改善
- EditMode テスト（WrapIndex / BuildUrl 系）と Debug シーンによる HMD なし検証フロー
- TextMesh Pro / `OVROverlayCanvasSettings` の導入
- Claude ハーネスに Unity MCP 診断コマンドと接続手順メモ

## [Phase 1] - 2026-05-03

DroidCam MJPEG をスクリーンに表示する PoC。

### Added
- DroidCam MJPEG をスクリーンに表示する PoC
- Meta XR All-in-One SDK 201.0.0 の導入

## [Phase 0] - 2026-05-03

プロジェクト初期化とハーネス整備。

### Added
- fixed-cam-vr プロジェクト初期化と Claude/agent ハーネス配置
- プロジェクト用ハーネスの作り込み（`.claude/` / `.agent/`）
- `.editorconfig`
