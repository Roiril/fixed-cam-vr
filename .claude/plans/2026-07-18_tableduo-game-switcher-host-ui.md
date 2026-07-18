---
status: in-progress
created: 2026-07-18
updated: 2026-07-18
slug: tableduo-game-switcher-host-ui
---

# TableDuo: ボドゲのランタイム切替 + 操作系の再設計（Quest=体験 / PC ホスト=運用）

## 設計思想（根本方針）

**「Quest 側は体験、PC ホスト側は運用」**。

- Quest コントローラ = **視点リセットだけ**（非常口）。体験中の操作はすべてハンドトラッキング（ピンチ掴み）
- ファシリテーション操作（ボドゲ切替・手の見た目・盤面リセット・マーク）は**サーバ権威の PC ホスト UI（IMGUI）**に集約
- 研究アプリとして「条件の操作は実験者だけができる」形に統一（従来の左 Y トグルは参加者が触れてしまう）

現状調査で確定した前提（2026-07-18）:
- 視点リセットは実装済み・完成形: `ControllerRecenterWatcher`（右 A 単押し / 両グリップ 3s → `RigRecenter.HeadToSeat`）→ **不変**
- 撤去対象は 2 つ: 左 Y = `HandVariantWatcher`（バリアント巡回）、右 B = `WireTapRecorder` の OVRInput バインド（F9 + PC GUI が残る）
- **掴み判定はコライダー非依存**: `PinchGrabInteractor.FindNearestFree` は `FindObjectsOfType<Grabbable>()` + pivot 距離のみ。→ stow は Renderer/Collider off だけでは不十分、**Grabbable 側の明示フラグでゲート**する
- 手バリアントは Owner-write の `_studyFlags` による分散申告モデル。ホストから変える経路は現状ゼロ
- 卓上プロップは全部 in-scene NetworkObject（プレハブ登録なし）。プレイヤーだけ runtime spawn

## ゲーム切替 = stow/show 方式（spawn/despawn しない・SetActive もしない）

NGO 1.x の in-scene despawn/respawn・spawned NetworkObject への SetActive はどちらも地雷原なので、
**全ゲームを常時 spawn したまま「気配だけ消す」**:

- Setup が全ゲームを `[TableDuo]/Props/Game_<id>` 別ルートにベイク（DSA は既存 `PlaceDeepSeaAdventure` を `Game_dsa` 配下へ、algo は新 `PlaceAlgo` を `Game_algo` 配下へ）
- **`GameSwitcher`**（新規 NetworkBehaviour・`[TableDuo]/GameSwitcher` in-scene NetworkObject）が `NetworkVariable<byte> activeGame`（server write）で全 peer に同期
- **ApplyLocal（全 peer）**: 非アクティブゲームの Renderer.enabled=false / Collider.enabled=false / `Grabbable.SetStowed(true)`
- **サーバ追加処理（切替時）**: 旧・新両セットとも「保持中は ServerForceRelease → 初期姿勢へ復元 → stow 側 rb.isKinematic=true + 速度ゼロ / show 側 isKinematic=false」＝**切替は両ゲームの盤面リセットを兼ねる**
- 遅参加: NetworkVariable 初期同期 + OnNetworkSpawn で ApplyLocal（spawn 順対策に 1 フレーム遅延適用）
- ベイク時も非デフォルトゲームは stow 状態（Renderer/Collider off + kinematic）で保存 → Editor シーン/Preview が散らからない + 起動直後からランタイム既定と一致
- `BoardReset` は全 Grabbable 走査のまま（stow 中も初期姿勢へ戻すだけで無害）
- `PinchGrabInteractor.FindNearestFree` に `IsStowed` スキップを追加 + `Grabbable.RequestGrabServerRpc` にもサーバ側ガード（二重防御）

採用理由: 掴み判定がコライダー非依存と判明したため「フラグ + Renderer/Collider off」で完結でき、
NGO のアクティブ状態セマンティクスに一切触らない。BoardReset・NetworkTransform・遅参加の既存挙動が全部そのまま乗る。
（scale 懸念: ゲーム数 × 30 プロップ程度の常時 spawn は NGO 的に問題ない。数百級になったら runtime spawn 方式を再検討）

## algo（アルゴ）プロップ

- 資産: `Assets/TableDuo/ThirdParty/Algo/glb/`（white_0..11 / black_0..11 の 24 枚。model-lab `models/algo` 産、
  42×66mm・厚 2mm・実寸・表面のみテクスチャ＝**裏は無地で face-down にすると値が隠れる**）
- `PlaceAlgo`: **実プレイ開始形**（2026-07-18 ユーザー指定・参考画像準拠）= 中央やや -X に裏向き山札 16 枚
  （積み上げ・yaw90）+ 人役/手役の手前に裏向き手札 4 枚ずつ。ベイクは固定シード（20260718）擬似シャッフル＝
  冪等のまま見た目もランダム。scale 1.3、physics:true（CCD 不要）、`SetSurfaceClamp` 必須
- **完全ランダム配り直し = `AlgoDealer`**（Systems・algoRoot 配線）: スロット（=ベイク姿勢 24 個）を
  BoardReset 流儀で採取し、カード→スロットを Fisher-Yates で permute（白黒込み）。ホスト UI
  「アルゴ配り直し」ボタン + `mark?label=algo_deal`。⚠ GameSwitcher 切替はベイク配置へ戻すため、
  アルゴ開始時に配り直しを押す運用
- ルール裁定はコード化しない方針を維持（従来どおり）

## 手バリアントのホスト制御

- `TableDuoPlayer` に `NetworkVariable<byte> _forcedVariant`（**server write**・既定 255=未強制）を追加
- サーバ API `ServerForceHandVariant(HandVariant?)` — ホスト UI が対象プレイヤーに設定
- **Owner が forced 変更を観測 → `StudyConfig.ApplyForcedVariant(v)`（`HandVariantLockedByFlag` を貫通して SelectedHandVariant 更新 + イベント発火）→ 既存の `_onOwnerVariantChanged` → `WriteStudyFlags` → `_studyFlags` 更新 → 全 peer 再構築**。
  = 既存パイプラインを丸ごと再利用し、forced は「サーバ→owner の指示チャネル」に徹する
- `WarnIfHandVariantMismatch` の LogError は Log に降格（ホスト強制でバリアントが端末間で意図的に変わり得るため）

## PC ホスト UI（`FacilitatorPanel`・新規 MonoBehaviour on Systems）

IMGUI・ガード = `Application.platform != Android && NetworkManager.IsServer && IsListening`（調査本番の tdv フラグ起動でも表示 — 実験者用のため）。画面右側に配置（左上の Spectator/WireTap GUI と非干渉）。

- **ボドゲ切替**: GameSwitcher の表示名ボタン列（アクティブ強調）
- **手の見た目**: 接続クライアント別の行（clientId + 役割）× [白手][リアル][ロボ][解除] → `ServerForceHandVariant`
- **盤面リセット**: `BoardReset.ResetBoard()`
- **マーク**: テキスト + 送信（`SessionLogger.LogEvent("mark", ...)`＝MarkServer と同じ）
- `FacilitatorMarkServer` に `game_<id>` ラベル追加（`curl /mark?label=game_algo` で切替＝reset_board と同じ遠隔導線）

## コントローラ簡素化

| バインド | 処置 |
|---|---|
| 右 A 単押し / 両グリップ 3s（視点リセット） | **維持**（変更なし） |
| 左 Y（`HandVariantWatcher`） | **削除**（ファイルごと。Setup の AddComponent も除去。`StudyConfig.CycleHandVariant` が他から未参照なら同時削除） |
| 右 B（`WireTapRecorder` の OVRInput） | **バインドのみ削除**（F9 + PC GUI は残す） |
| キーボード 1/2/3（Spectator）/ F9 / ReplayViewer | 維持（PC 側ファシリテーション） |

## 実装順序

1. algo GLB 取込（済）+ Unity refresh で glTFast import
2. ランタイム: GameSwitcher / Grabbable.SetStowed / PinchGrabInteractor フィルタ / TableDuoPlayer forced / StudyConfig.ApplyForcedVariant / FacilitatorPanel / MarkServer game_ ラベル / WireTapRecorder B 除去 / HandVariantWatcher 削除
3. Editor: TableDuoSceneSetup — Props をゲーム別ルート化・PlaceAlgo・GameSwitcher 配線（SerializedObject）・ベイク時 stow・HandVariantWatcher 除去・FacilitatorPanel 追加
4. 検証: refresh→DLL mtime→エラー 0→EditMode tests→Setup 再生成→TablePreview→（可能なら L0 スモーク）
5. doc-sync: README・memory（study_status / tabletop_prop_authoring）・本 plan

## 検証状態

- [x] コンパイル（エラー 0・全 TableDuo DLL 再ビルド mtime 確認）/ EditMode **93/93 pass**（2026-07-18）
- [x] Setup 再生成（ALGO 24・DSA 58・GameSwitcher・Game_dsa/Game_algo をシーン YAML で確認）+ TablePreview（俯瞰・斜め: DSA 全表示・algo 不可視＝stow ベイク成功・破綻なし）
- [ ] L0 or 実機: 切替の全 peer 同期（遅参加含む）・stow 中の掴み排他・手バリアント強制の全視点反映・アルゴ薄板物理
- **実機未検証**（→ docs/table-duo/remaining-tasks.md §C に追記済み）
