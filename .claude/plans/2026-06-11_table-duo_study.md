# 実行計画: TableDuo 手アバター調査機能（シュビー用）

設計 → [docs/table-duo/study-design.md](../../docs/table-duo/study-design.md) / プロトコル → [study-protocol.md](../../docs/table-duo/study-protocol.md)
前提コード: Phase 0–4 実装済み（`16a588c` 時点）。掴み・pose 同期・録画・Kenney 見た目は動作済み。

## T1: 役割・条件フラグ

- `ConnectionManager.ResolveAutoMode` を拡張: `tdv_role`（full|hand）/ `tdv_marker`（on|off, 既定 off）/ `tdv_hands`（one|two, 既定 one）を intent extras / コマンドライン から取得し `StudyConfig`（static）へ
- `TableDuoPlayer`: 席と avatar 種別を **role から決定**（従来の「host=full」を置換。host でも hand 役可）。同 role 衝突時は警告ログ
- 片手モード: 手役のローカル描画は右手のみ（左 OVRHand GO を無効化）+ 送信 pose の TrackedL=false 固定
- マーカー: `RemoteAvatarView.Create` に StudyConfig.marker を渡す
- **ロスト時フリーズ**: HandView のフェードを「最終姿勢で静止」に変更（調査既定）。ロスト/復帰を SessionLogger にイベント送出

## T2: 絵カードデッキ

- **片面絵柄＋共通裏面**（両面同柄は照準シグナルが消える — 監査指摘）
- 薄い箱（10×14×0.5cm 目安）×12枚。表: アイコンテクスチャ（Kenney Game Icons CC0 等から食べ物・動物・行動アイコン）、裏: 共通柄
- Grabbable + NetworkObject + NetworkTransform（小物と同じ構成）
- テーブル端にカード置き場（2列）。Setup メニューで生成
- `CardProp` コンポーネント: 保持中フラグと「表面法線」を SessionLogger へ提供

## T3: 手役専用 目標配置パネル

- ローカルのみ表示（ネットワーク非同期）。手役デバイスでのみ instantiate
- **配置位置: テーブル作業空間の外**（手役の席の斜め上方）— 参照時の頭・手の向きが相手に読める位置を避ける（監査指摘）
- 内容: 3小物の目標位置を上から見た簡略図（テクスチャ数枚をローテーション。セッション開始時にランダム選択、ログに記録）

## T4: SessionLogger（ホスト）

- 30Hz CSV: `epoch_ms, clientId, head(pos,rot), wristR(pos,rot), landmarks×7(手首,掌中心,5指先), pinchR, trackedR, [wristL... two-hand時]`
  - ランドマークは同期済み bone 回転 + HandSkeletonLayout から算出（受信側で再構成済みの Transform を読むのが簡単）
- イベント行: `grab/release(objectId), trackingLost/Regained(clientId), mark(label), cardHeld(cardId, normal→各ランドマーク角度 or 生pose)`
- ファイル名: `tdv_session_<yyyyMMdd_HHmmss>.csv`（**起動ごとに新規** — swap 再起動で上書きしない）
- カード保持中はカード pose も毎行記録（法線分析はオフライン）
- 書き込みは StreamWriter + 文字列ビルダ再利用（GC 配慮。ただし調査ビルドは 72Hz 維持できれば十分、過剰最適化しない）
- pull 補助: `tools/pull-tdv-logs.ps1`（adb pull → ./logs/<date_pair>/）

## T5: FacilitatorMarkServer

- ホストで HttpListener（port 7780）: `GET /mark?label=xxx` → SessionLogger にイベント行 + 200 応答
- LAN 内のみ・調査ビルドのみ有効（StudyConfig）

## T6: 紙モノ・図

- 手の図（指差し用）: 手の輪郭線画 SVG → 印刷可能 PNG を docs/table-duo/assets/ に
- 役割カード・観察シートは protocol 内の雛形を印刷

## T7: 調査ビルド + パイロット

- ConnectionManager.showGui を調査ビルドで off（StudyConfig or ビルド時フラグ）
- パイロット 1 ペア（**所有者が手役を一度経験**）→ protocol 改訂 → 凍結

## 実装順序

T1 → T4 → T2 → T3 → T5 → T6 → T7（L0 で T1/T4 を検証 → L1 で カード/パネル → パイロットは L2）

## 検証

- L0: FakeHandDriver + 2プロセスで role 指定・CSV 出力・mark エンドポイント
- L1: カード照準ログの実データ妥当性（自分で被って1枚見せて CSV の角度を目視確認）
- パイロット: プロトコル通し → 改訂

## 未決（ユーザー）

公表予定（倫理手続き）/ 参加者の知己関係 / 部屋レイアウト — design §8 参照

---

## 実装＆実機検証状況（2026-06-15 シュビー記録）

**L2（実機2台）で接続〜役割〜目線〜手メッシュまで動作確認済み。** 調査機能 T1–T6・リプレイ T8–T10 実装済み。

### 動いていること（実機2台で確認）
- 単体 Quest ×2 を LAN 直結（host/client）。`tdv_role full|hand` で役割割当 → 席/アバター種別/片手モードが正しく適用
- VR 描画・目線高（EyeLevel + 席=目線アンカー y=1.15）・相手アバター表示
- 絵カード/食べ物小物の配置、手役のみの目標配置パネル、SessionLogger/ReplayRecorder/MarkServer（host）
- リモートの手は**関節を骨（カプセル）で繋いだ手の形**で描画（旧: バラバラの点）

### 実機運用 runbook（このビルド＝BuildVariants 標準）
- パッケージ: **`com.roiril.tableduo`**（旧 manage_build 版の `com.UnityTechnologies...urpblank` ではない）
- ビルド: Unity メニュー `Tools/FixedCamVr/Build TableDuo APK` → `Builds/tableduo.apk`（quest-build スキル参照）
- インストール: `adb -s <serial> install -r --no-streaming Builds\tableduo.apk`
- 起動（host=full / client=hand）:
  ```
  adb -s <hostSerial>   shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode host   -e tdv_role full
  adb -s <clientSerial> shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode client -e tdv_ip <hostIP> -e tdv_role hand
  ```
- 実機 IP: `adb -s <serial> shell ip route | grep wlan0`。検証時の例 host=192.168.11.10 / client=192.168.11.11
- スクショ: `adb -s <serial> shell screencap -p /sdcard/x.png && adb pull ...`（PowerShell の `>` はバイナリ破損するので device 保存→pull）

### ⚠ 罠（再発防止・他シュビー向け）
1. **ビルド前にシーンをクリーン状態にすること**（2 回踏んだ）。L0/リプレイ検証で OVRCameraRig 無効・DebugCamera 有効・ReplayViewer 有効・FakeHandDriver 有効にしたまま保存→ビルドすると、実機で VR にならず平面表示になる。**ビルド直前に `Setup TableDuo Scene` を再実行**すれば確実にクリーン（OVRCameraRig 有効 / DebugCamera・ReplayViewer 無効 / FakeHandDriver 無効）
2. **目線高は EyeLevel + 席 y=1.15**。FloorLevel だと実身長で目線が変わり個人差が出る。席（[TableDuo]/Seats/Seat0,1）が目線アンカー＝位置が目の位置・forward が視線。`Tools/FixedCamVr/Diagnostics/Preview Eye - Seat0/1` で確認、ギズモ（SeatEyeGizmo）で調整
3. **ハンドトラッキングは「自分の手を見てる」時だけ高精度**。手をカメラ視界（FOV）に入れないと conf=Low/tracked=False。`HandPoseSampler.logDiagnostics=true` で `[TDV-DIAG]`（OVRHand の tracked/valid/conf, skeleton init/bones, 計算後 trackedR）を 1Hz 出力 → 切り分け。既定オフ
4. **片手モードは右手を残す**（左を抑制）。左手しか動かさないと相手に映らない。左右選択/両手は未実装（要望次第）
5. **人（フル）側は自分の体を一人称描画してない**（自分の手のみ）。自分の胴体表示は未実装（要望次第）
6. APK サイズが 62MB→88MB に増えたのは再 Setput でのアセット再生成由来とみられる。動作影響なし

### 未対応（要望待ち）
- 片手モードの左右選択 / 両手対応
- 人側の自分の胴体（一人称）表示
- リプレイの音声同期実運用（クラップ mark で校正）
- パイロット本番（所有者が手役を一度経験 → プロトコル凍結）

### 2026-06-15 追記2: 見た目修正（テーブル配置・白い手メッシュ・診断ログ）
- **テーブル配置**: Setup が `WorldBounds(table)` で天板の実高(topY)・XZ 範囲を測り、小物/カード/ランプをその上に接地・範囲内へ。maxWidth で天板が縮む（0.7→約0.5m）ため固定 Y のハードコードは禁止。数値検証済み（apple/card 底面=天板）
- **リモートの手をメッシュ化**: `RemoteHandMeshProvider`（Systems）が Meta の `OVRCustomHandPrefab_L/R` とローカル手と同じ白マテリアルを供給。`RemoteAvatarView.HandView.TryBuildMeshHand()` が OVRHand/OVRCustomSkeleton/Animator を剥がし SkinnedMeshRenderer + CustomBones だけ残して同期 bone(localRotation, BoneId 順)で駆動。供給不在時はカプセル手にフォールバック。Net asmdef に Oculus.VR 参照追加。**実機装着での見た目は未検証**（bone 向き/手首オフセットの補正が要る可能性）
- **診断ログ**: `HandPoseSampler.logDiagnostics`（既定 false）で `[TDV-DIAG]`（OVRHand tracked/valid/conf 等）1Hz 出力。手トラッキング不調の切り分け用
