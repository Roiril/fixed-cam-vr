---
name: sdk_decision
description: VR SDK 選定の経緯（Meta XR All-in-One + XRI 採用、他候補却下理由）
type: project
---

# SDK 選定の決定（2026-04-28）

採用：**Meta XR All-in-One SDK**（メイン）+ **XR Interaction Toolkit**（補助）+ **OpenXR Plugin**（基盤）

## 役割分担

| SDK | 用途 |
|---|---|
| Meta XR All-in-One SDK | Passthrough Projection、Composition Layer、Spatial Anchor、Hand Tracking、空間オーディオ |
| XR Interaction Toolkit | コントローラ入力抽象化、レイキャスト、UI イベント |
| OpenXR Plugin | ランタイム基盤（Meta XR が内部で利用） |

## 却下した候補

- **Oculus Integration（旧版）**: 非推奨化済。新規は Meta XR All-in-One 一択。
- **OpenXR 単体**: パススルーの細かい制御 API が無く、自前実装の工数過大。
- **XRI 単体**: パススルー Projection が無く、本プロジェクトの核要件（暗転・部分透過）を満たせない。
- **Unity PolySpatial**: Apple Vision Pro 用。Quest 3 ターゲット外。

**Why（採用理由の核）**: 本プロジェクトの最重要機能はパススルー制御（暗転 + 部分透過マスク）と Composition Layer による高品質スクリーン表示。これらは Meta XR 専用 API でしか実現できない。Quest 3 専用と割り切れる以上、マルチプラットフォーム対応の利点（XRI 単体）より機能網羅性を優先。

**How to apply**:
- パススルー / Spatial Anchor / Hand Tracking 関連は Meta XR 名前空間（`OVR*`）を使う
- コントローラ入力・UI レイキャストは XRI（`UnityEngine.XR.Interaction.Toolkit`）を使う
- 両者が衝突したら Meta XR 優先（Quest 3 ネイティブ機能を活かす）
