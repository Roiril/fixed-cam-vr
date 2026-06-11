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
