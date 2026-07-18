---
status: in-progress
created: 2026-07-08
updated: 2026-07-08
slug: tableduo-piece-physics
---

# TableDuo 卓上ピースの物理エンジン統合（投げる・ひっくり返す・転がす）

## 概要

現状の卓上ピースは物理ゼロ（Grabbable = サーバ駆動 kinematic 追従 + surfaceY/XZ 手動クランプ、
DiceRoller = リリース時に乱数確定表示）。ここに Rigidbody 物理を統合し、
サイコロを投げて転がす・ピースをひっくり返す・机の上で自然に滑らせる、を実現する。

## 設計方針（確定事項）

### 1. 物理はサーバ（PC ホスト）のみで回す

- 実機トポロジは PC=NGO host + Quest×2 client（table_duo_pc_host_and_wiretap 準拠）。
  **Rigidbody シミュレーションは host だけ**が実行し、クライアントへは既存どおり
  NetworkTransform（サーバ権威・補間）で降ろす
- クライアント側は `Rigidbody.isKinematic=true`（NGO の NetworkRigidbody を各ピースに追加すると
  非権威側を自動 kinematic 化してくれる）。クライアントに物理コストゼロ、決定性の問題も出ない
- 既存の Grabbable のサーバ裁定・NetworkVariable 構造はそのまま活かす

### 2. ピースの 2 状態モデル: Held（kinematic）⇄ Free（dynamic）

- **Held**: 現行どおり。`isKinematic=true` でサーバが手 pose 追従（`Grabbable.Update` の平滑追従・
  surfaceY クランプは保持中のみ残す — 手が卓を貫通しても駒は上に留まる現仕様は良い）
- **Free**: リリース時に `isKinematic=false` + gravity ON。以後は物理任せ。
  手動クランプは Free では使わない（テーブルの Collider が受け持つ）
- 遷移は Grabbable の Grab/Release/ForceRelease に集約（サーバのみが状態を触る）

### 3. リリース速度の推定（投げ・ひっくり返しの核）

- サーバは保持中の追従ターゲット（クランプ前の生 pos/rot）を**リングバッファ（直近 ~100ms）**に記録
- リリース時にバッファから線速度・角速度を最小二乗 or 端点差分で推定して
  `rigidbody.velocity / angularVelocity` に設定
- **クランプ必須**: 60Hz 受信 pose 由来でノイジー。線速度上限 ~3 m/s、角速度上限 ~4π rad/s。
  これが無いとダイスが部屋の外へ飛ぶ
- 平滑済み transform からではなく**追従ターゲット（受信 pose 由来）から**推定する
  （平滑 Lerp 後だと速度が減衰して「投げても落ちるだけ」になる）

### 4. コライダー構成（TableDuoSceneSetup が配線・冪等）

| 対象 | Collider | 備考 |
|---|---|---|
| 天板 | static BoxCollider（surfaceY に上面） | 現手動クランプの物理版 |
| 床（キルゾーン） | trigger、卓の下 -1m | 落下ピースを検知 → 初期位置へリスポーン（BoardReset の単品版） |
| ダイス | BoxCollider + **CCD (Continuous Dynamic)** | 高速投擲でのトンネリング防止 |
| トークン/駒 | Box or Capsule 1 個 | メッシュコライダー禁止（凸でも高コスト） |
| カード | **物理対象外**（Phase 2 で別扱い） | 薄板 dynamic はトンネリング・スタック暴れの温床 |
| 潜水艦ボード | **物理あり**（2026-07-18 統一。当初は kinematic 追従のみ＝薄板で除外していた） | ユーザー要望でチップと同じ AttachPiecePhysics に統一。掴み/投げ/転がし/卓外リスポーンが同挙動 |

> **2026-07-18 追記（ユーザー要望）**: (1) 潜水艦ボード（`DSA_Board`）を宝物チップと同じ物理（`AttachPiecePhysics` = Rigidbody mass0.1 + BoxCollider + NetworkRigidbody + TableProps）に統一。当初コメントの「薄板なので物理は付けない（kinematic 追従のみ）」を撤回。scale は実寸のまま（1.6x にするとチェーン配置と噛み合わない）。(2) 空気マーカー＝赤色マーカー（`DSA_air_marker`）を `chipScale`(1.6) → `chipScale*0.8`(1.28) に縮小。いずれも [TableDuoSceneSetup.PlaceDeepSeaAdventure](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs)。Setup 再生成で反映済み・**実機未検証**。

- 専用レイヤー `TableProps`: Table と自分同士のみ衝突。手・アバター・自己身体とは**衝突させない**
  （トラッキングの手にコライダーを付けると jitter で駒が爆ぜる。掴みは現行ピンチ方式のまま）
- PhysicMaterial: ダイス bounciness ~0.3 / friction ~0.6、天板 friction 高め（滑りすぎ防止）

### 5. サイコロ = 物理転がし + 静止面読み取り

- DiceRoller を「乱数確定」から「**静止時に上面を読む**」へ変更:
  リリース → 物理転がり → `rigidbody.IsSleeping()`（または velocity < ε が 0.5s 継続）で静止判定
  → ローカル 6 軸のうち world up と最も揃う軸から出目決定 → `_value` に反映
- 出目ラベル表示・DiceRolled イベント・CSV 連携は現行のまま（読み取り齟齬防止の設計意図を維持）
- 海底探検ダイス（1/2/3×2 面）は面→値マッピングテーブルで対応
- フォールバック: 5 秒静止しなければ強制スリープ + その時点の上面で確定（縁で立った等のスタック回避）

### 6. ひっくり返す

- 専用機構は作らない。**掴んで手首を返して離す**が回転オフセット保持 + 角速度引き継ぎ + 物理着地で
  自然に成立する（現行でも回転追従はある。足りないのは離した後の物理着地のみ）
- カードの表裏めくり（Phase 2）は物理でなく**アニメーション flip**（ピンチ短タップ → 180° 補間 +
  面状態 NetworkVariable）。薄物に物理は使わない

### 7. 並べ替え

- 現行のピンチ掴みで既に成立。物理化で改善されるのは「置いたときに駒同士が重ならない」
  （押し退け合い）と接地の自然さ。追加実装なし

## フェーズ

### Phase 1: 物理基盤（ダイス・トークン）— ✅ 2026-07-08 実装済み

- [x] TableDuoSceneSetup: 天板 BoxCollider（TableTopCollider）・`TableProps` レイヤー（TagManager slot 8）・
      衝突マトリクスは PiecePhysicsConfig（Systems 常駐）がランタイム設定（DynamicsManager は触らない）
- [x] ピース生成（PlaceModelRealScale physics:true）に Rigidbody + BoxCollider + NetworkRigidbody +
      PhysicMaterial（ダイスは CCD）。対象 = 海底探検ピースのみ（カードは非物理のまま = ユーザー指示）
- [x] Grabbable: Held⇄Free の kinematic 切替（全解放経路を ServerRelease に集約）
- [x] リリース速度推定（Grabbable 内リングバッファ 0.12s 窓 + 3.5m/s / 12rad/s クランプ、
      maxAngularVelocity=20 へ引き上げ）
- [x] 卓外落下 → spawn 位置リスポーン（キルゾーン trigger でなく y < surfaceY-0.8 判定に簡素化）。
      BoardReset に velocity リセット追加

### Phase 2: サイコロ出目の物理化 — ✅ 2026-07-08 実装済み

- [x] DiceRoller: 乱数確定を廃止（ユーザー指示）。静止検知（IsSleeping or 低速 0.4s 継続）+
      上面読み取り（faceValues[+X,-X,+Y,-Y,+Z,-Z]）+ 6s タイムアウト強制確定
- [x] DiceRolled イベント・ラベル表示・CSV 連携は現行インタフェースのまま維持
- [ ] ⚠ faceValues 既定 {1,2,3,1,2,3} は die.glb の実テクスチャ面と**未照合**。L0/実機で校正する

### Phase 3: 検証

- [ ] L0 デスクトップテスト（table_duo_l0_desktop_test）: FakeHandDriver で投擲 → 転がり → 静止 → 出目ログを自動確認
- [ ] EditMode テスト: 上面読み取り（回転 → 出目）・速度推定のクランプ
- [ ] L1 Editor+Link 実機 1 台: 投げ心地（速度スケール調整）・90Hz 維持・NetworkTransform 帯域
- [ ] ドキュメント同期: study-protocol / memory table_duo_study_status

### Phase 4（後回し・必要になったら）

- [ ] カード flip アニメーション + 面状態同期
- [ ] 手のひらで駒を押す（kinematic 手コライダー）— jitter 対策が重いので体験要求が出てから

## リスク・既知の罠

- **速度推定のノイズ**: 60Hz 受信 pose 起点。クランプと ~100ms 窓の平均化で抑える。それでも暴れるなら窓拡大
- **クライアント側の見え方**: 物理の細かいバウンドは NetworkTransform 補間で多少なまる。
  60Hz 送信 + 補間で実用上問題ない想定だが、L1 で要確認
- **保持中の他ピースとの衝突**: Held は kinematic なので Free ピースを一方的に押し退ける（正しい挙動）。
  ただし kinematic 同士（両手で 2 個保持）はすり抜け — 許容
- **Editor Play 検証**: OVR シーンの Link 無し Play はハング（unity_pitfalls）。検証は L0 ビルド経路を主力に

## 自律改善ログ

(作業中に気付いた改善点や学びをここに追記)
