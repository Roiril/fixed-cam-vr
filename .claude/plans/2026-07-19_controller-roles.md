---
status: done
created: 2026-07-19
updated: 2026-07-19
slug: controller-roles
---

# コントローラ操作の根本設計（Run/Staff モード分離）

> ⚠ **廃止（2026-07-20 supersede）**: Run/Staff/Registration の 3 モデルは
> [2026-07-20_staff-input-hud-redesign.md](2026-07-20_staff-input-hud-redesign.md) の **右手 4 入力・2 状態
> （Normal/Registration）モデル**に置き換えられた。両グリップ儀式・Staff 封印・左手/スティック・120s idle・
> cue 試射・StaffPanel チートシートは全廃。以下は経緯として残す（現行仕様ではない）。

## 概要

Web 卓 + 焼き込みスケジュール + ゾーン自動切替 + 自動追従スクリーンが揃った現在、
**体験者（ゲスト）がコントローラを使う正当な理由は存在しない**。現状は 8 系統の操作が常時むき出しで、
ゲストの誤爆が体験を破壊できる（A/B=カメラ切替・X=スクリーン凍結・Y=診断 HUD・両グリップ 3s=登録モード）。

**再定義: コントローラ = スタッフ専用の現場ツール**（唯一物理的にその場でしかできない位置合わせ + PC 不在時の診断）。
既定は全封印（Run モード）、明示的な儀式でのみ Staff モードへ。ユーザー委任（2026-07-19「全部自己判断で OK」）。

## 現状の操作（実コード裏取り済み・OvrControllerBridge.cs）

| 入力 | 現機能 | Run 時のリスク |
|---|---|---|
| 右 A / B | カメラ Next/Prev | 誤爆でゾーン自動と喧嘩（8s 抑止まで発動） |
| 左 X | head-lock トグル | スクリーンがワールド凍結＝体験崩壊 |
| 左 Y | HUD トグル | 診断テキストが視界に |
| 両グリップ 3s | 登録モード直行 | **ゾーン再生成・nudge まで届く＝最悪** |
| （登録中）A/B/スティック | マーク/確定/nudge | 同上 |
| 右グリップ単押し | cue トグル（server /command 経由） | 誤爆 + **PC 不在だと元々発火不能（既知の穴）** |

## 設計契約

### モードモデル

```
Run（既定・ゲスト安全）
  └ 両グリップ 3 秒長押し → Staff
Staff（スタッフ操作可能・チートシート表示）
  ├ 右スティック押し込み → Registration（既存の登録フロー）
  ├ 無操作 120 秒 or 両グリップ 3 秒 → Run へ戻る
  └ Registration 終了（確定/キャンセル）→ Staff へ戻る
```

- **Run モード: 全ボタン不活性**（両グリップ 3s の Staff 入口のみ）。ゲストが持っても何も起きない
- **Staff モード入場時**: HUD 自動 ON + head-locked チートシート（下表のバインディング一覧を表示。
  CourseRegGuidance の TextMesh パターン流用）。退場時 HUD を入場前の状態に復元
- モード状態は HUD STATE 行と Web 卓 heartbeat に出す（スタッフが遠隔でも把握できる）

### Staff モードのバインディング

| 入力 | 機能 |
|---|---|
| 右 A / B | カメラ Next / Prev（設営時の画確認。従来どおり Director 経由） |
| 左 X | head-lock トグル |
| 左 Y | HUD トグル |
| 右グリップ単押し | **cue 試射トグル**: server 接続中は従来の /command、**不在時はローカル発火へフォールバック**（ScreenOverlayController.PlayCueById / StopOverlay 直呼び・焼き込み cue で動く → 既知の穴を塞ぐ） |
| 右スティック押し込み | 登録モードへ（既存フロー: A=マーク・B=確定・スティック nudge） |
| 両グリップ 3s | Run へ戻る |

### 登録の精度・使いやすさ向上

1. **サンプル平均**: マーク時、A 押下の瞬間値でなく **A を押している間 0.5s のコントローラ位置を平均**して採用
   （手先ジッタの低減。押下→ホールド→離しで確定のリズム。ガイダンスに「押しながら 0.5 秒静止」と表示）
2. **確定前のライブ誤差表示**: ステップ 2 で 2 点間実測とベースライン既知値（1m）の誤差 % をガイダンスに常時表示
   （±15% の合否だけでなく「いま何 % ズレているか」を見ながら当てられる）
3. 既存の nudge・ワイヤーフレーム検証・±15% ゲート・registration.json 永続はそのまま

### 実装構成

- 純ロジック `ControllerModeLogic`（plain class）: Run/Staff/Registration の状態機械 + タイムアウト + 入場/退場イベント → EditMode テスト
- `OvrControllerBridge` はモードゲートを通してから各機能へ分配（既存の SerializeField ボタン割当は維持）
- チートシート表示は新規小型コンポーネント（StaffPanel）or CourseRegistrationController のガイダンス機構を汎用化
- cue 試射のローカルフォールバックは ShowControlClient.ToggleActiveCameraCue 内（server null/不通で分岐）
- キーボード操作（Editor 検証用 Tab/1-9/Space/H）は**ゲートしない**（Editor 専用・実機に影響なし）

## フェーズ

### Phase A: モード状態機械 + ゲート（opus 委譲）
- [x] ControllerModeLogic + テスト（Run 封印・儀式入場・タイムアウト・Registration 遷移・退場復元）
- [x] OvrControllerBridge のゲート組み込み（既存機能の移設・削除はしない）
- [x] StaffPanel チートシート + HUD 自動 ON/復元 + STATE/heartbeat への状態露出

### Phase B: 登録の精度向上（同一エージェント逐次）
- [x] マークのホールド平均（0.5s）+ ガイダンス文言更新
- [x] ステップ 2 のライブ誤差 % 表示

### Phase C: cue 試射のローカルフォールバック（同一エージェント逐次）
- [x] server 不在時に PlayCueById/StopOverlay 直呼び（焼き込み cue）

### Phase D: 検証（シュビー本体）
- [x] コンパイル + EditMode テスト + Setup 再実行 + Quest ビルド
- [x] ドキュメント同期（README 入力早見表・unity-vr.md 登録リチュアル表・onsite-checklist があれば）

## 境界・注意

- TableDuo・ProjectSettings・共有 .asset 不変。Web 卓（tools/）は触らない（heartbeat 露出は Unity 側送信のみ）
- 既存の登録フロー・Director・SignalLostFx の挙動契約は不変（入口のゲートだけ変わる）
- 実装報告は git 物証照合

## 自律改善ログ

- 2026-07-19 A/B/C 実装完了（opus 1 体逐次・git 物証照合）。テスト 131/131 pass（ControllerModeLogic 17 件追加）
- 逸脱採用: 純ロジックは新 asmdef `FixedCamVr.Input`（OvrBridge=Assembly-CSharp はテスト参照不可のため）。cue ローカル発火は既存 ResolveCue+PlayCue 経由。server 不通判定は 40s 無受信（long-poll 35s より長く）
- 新規 SerializeField の prefab 未記載 default(0) 対策として Start フォールバックガード（unity-prefab-fields の罠の恒久対処パターン）
- 実機試着未実施（Run 封印・Staff 動線・登録ホールドマークの操作感）
