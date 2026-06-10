# 実行計画: TableDuo Phase 0–3（シュビー用）

対象要件: [docs/table-duo/requirements.md](../../docs/table-duo/requirements.md)（2026-06-10 改訂版・fixed-cam-vr 同居）

**進捗（2026-06-10）**: Phase 0–2 のコード一式 + シーン生成メニュー実装済み（本ファイル末尾「実装状況」参照）。

## 着手前チェックリスト（毎セッション）

1. `unity-status` スキルで MCP 接続・コンパイル状態確認
2. C# 変更後は毎回 `refresh_unity` → `read_console` でエラー 0 を確認してから次へ

## ディレクトリ / asmdef 構成（実装済みの実構成）

```
Assets/TableDuo/
├── Scenes/TableDuoMain.unity        ← Tools/FixedCamVr/Setup/Setup TableDuo Scene で生成
├── Prefabs/TableDuoPlayer.prefab    ← 同上メニューで生成
└── Scripts/
    ├── Hands/      TableDuoVr.Hands.asmdef   (Oculus.VR 参照)
    │   ├── AvatarPose.cs / IHandPoseSource.cs / HandPoseSampler.cs
    │   └── Playback/ PoseRecordingFile.cs / HandPoseRecorder.cs / FakeHandDriver.cs
    ├── Net/        TableDuoVr.Net.asmdef     (Hands + NGO 参照)
    │   ├── PoseCodec.cs / ConnectionManager.cs
    │   └── TableDuoPlayer.cs / RemoteAvatarView.cs
    └── Editor/     TableDuoVr.Editor.asmdef
        └── TableDuoSceneSetup.cs
Assets/Tests/TableDuo/  TableDuoVr.Tests.asmdef (PoseCodecTests)
```

namespace は `TableDuoVr.<Feature>`。`[SerializeField] private` / `#nullable enable` / Update 内 new 禁止（CLAUDE.md 準拠）。**FixedCamVr.\* への参照禁止**。

---

## Phase 0: 疎通 + ハンドトラッキング単体 + 検証基盤

ゴール: Editor(Link) と実機で自分の手が動く。手データの記録/再生ができる。

### タスク

1. **シーン骨格** (`Main.unity`): OVRCameraRig（ハンドトラッキング有効・`OVRHand`/`OVRSkeleton` 左右）、テーブル仮ジオメトリ（1.2×0.8m、高さ 0.72m）、席アンカー 2 つ（向かい合わせ、テーブル端から 0.5m）
2. **`Hands/HandPoseSampler.cs`**: OVRSkeleton から毎フレーム wrist pose + 24 bone localRotation を読み、`AvatarPose` struct（プレーン C#、まだ NGO 非依存）に詰める。アロケーション 0（配列再利用）
3. **`Diagnostics/HandPoseRecorder.cs`**: `AvatarPose` 列をリングバッファに記録 → JSON or バイナリで `Application.persistentDataPath` に保存。Editor メニュー `Tools/TableDuoVr/Diagnostics/Save Hand Recording`
4. **`Diagnostics/FakeHandDriver.cs`**: 保存した記録をループ再生して `AvatarPose` を吐く。`HandPoseSampler` と同一インターフェース（`IHandPoseSource`）を実装 — 以降の全レイヤはこのインターフェースだけを見る
5. **Editor メニュー**は `Tools/TableDuoVr/` 配下に統一（fixed-cam-vr の階層規約踏襲）

### 検証

- L1: Quest Link で Editor Play → 自分の手が見える（Link のハンドトラッキングは Oculus PC アプリの Beta 設定が必要 — ダメなら実機で記録だけ取る）
- 実機ビルド 1 回: 手が動く + 記録保存 → 記録ファイルを PC に `adb pull` → L0 で FakeHandDriver 再生確認
- コンパイル OK / 実機検証はユーザー依頼を明示

---

## Phase 1: NGO LAN 直結 + 頭 pose 同期

ゴール: L0 で 2 プロセス同期 → L1 で Editor↔実機。相手の頭が見える。

### タスク

1. **`Net/ConnectionManager.cs`**: NetworkManager + UnityTransport 設定。Host 開始 / IP 指定で Client 接続。ポート固定（7777）。`INTERNET` パーミッション確認
2. **`Net/PlayerNetObject.cs`** (NetworkBehaviour): 接続順で席 0/1 に割当（ホスト=席0）。スポーン時に自分のリグを席アンカーへアライン
3. **頭 pose 同期**: まず NetworkVariable<Vector3+Quaternion>（30Hz tick）で頭だけ。受信側は単純 lerp/slerp 補間。リモート頭は仮 Cube 表示
4. **接続デバッグ UI**: ワールド固定の簡易パネル（Host ボタン / IP 入力 / Connect / 状態表示）。実機ではハンドレイ or 視線で操作できる最低限
5. **L0 検証パス**: PC スタンドアロンビルド（Windows, XR 無効で起動するフラグ）or ParrelSync 導入を検討 → **まず PC ビルドの `-batchmode` ホスト + Editor クライアントで十分**。FakeHandDriver で頭 pose をダミー駆動

### 検証

- L0: PC ホスト + Editor クライアントで相手 Cube が動く
- L1: Editor(Link) ホスト + 実機クライアント
- PlayMode テスト: 席割当ロジック（接続順 → 席 index）

---

## Phase 2: AvatarState 同期 + リモート手描画

ゴール: 相手の指が動いて見える。

### タスク

1. **`Net/AvatarState.cs`**: `INetworkSerializable` 実装。head + wrist L/R + boneRot L/R (half×24×2) + handTracked L/R。`NetworkSerialize` で half 変換しつつ書き込み。**per-bone NetworkTransform 禁止を厳守**
2. **送信**: owner が 30Hz tick で NetworkVariable<AvatarState> 更新（または unreliable RPC — まず NetworkVariable で実装し、ジッタが気になったら切替）
3. **`Avatar/RemoteHandRenderer.cs`**: 受信 AvatarState から手メッシュの bone を駆動。OVRSkeleton の bone 構成と同じリグの手メッシュ（Meta XR サンプルの手メッシュ流用可）。補間: pose は lerp、bone は slerp（タイトループ・アロケーション 0）
4. **トラッキングロスト**: handTracked=false で 0.3s フェードアウト（点滅禁止）
5. **自分の手はローカル描画のまま**（ネット経由にしない — 要件 §4）

### 検証

- L0: FakeHandDriver の記録データを 2 プロセス間で流して指が再現されるか
- L1: 実手で確認
- EditMode テスト: AvatarState のシリアライズ往復（write→read で値一致、half 精度誤差以内）
- Profiler: 送受信パスのアロケーション 0 確認

---

## Phase 3: アバター v1 + テーブル + 掴み

ゴール: 2人で物を渡せる。役割が見た目で分かる。

### タスク

1. **アバター v1**: A=頭メッシュ+両手 / B=両手+頭マーカー（小さな発光リング）。matcap 系シェーダ 1 枚（Shader Graph）
2. **`Interaction/Grabbable.cs`** (NetworkBehaviour) + **`Interaction/GrabManager.cs`**:
   - ピンチ検出（`OVRHand.GetFingerIsPinching`）+ 近接判定で grab 候補
   - `GrabRequestServerRpc(objectId, hand)` → ホスト裁定（未保持なら付与・先着勝ち）→ ownership 移譲 → owner がキネマティック追従
   - Release も ServerRpc。敗者はすり抜け（無反応でよい）
3. **テーブル + 小物**: 掴める Cube/球 数個をテーブルに配置（NetworkObject）
4. **席アライン仕上げ**: スポーン時 recenter（席正面=テーブル中心）

### 検証

- L0: FakeHandDriver にピンチ記録を含め、掴み裁定の同時要求テスト（PlayMode テストで GrabManager の裁定ロジックを直接叩く）
- L2: **ここで初の実機 2 台検証**（物の受け渡し・レイテンシ実感）→ ユーザー依頼
- PlayMode テスト: 同時 GrabRequest → 先着のみ ownership

---

## Phase 4–5（概要のみ・着手時に詳細化）

- 4: A に胴体（席固定アンカー+頭追従）、`RecenteredPose` 購読で席再アライン、腕 IK は見栄え評価後に判断
- 5: UDP discovery、perf パス（必要時のみ量子化）、**ゲームルール実装（ユーザー決定待ち — Phase 3 完了までに決める）**

## リスクと対処

| リスク | 対処 |
|---|---|
| Link でハンドトラッキングが動かない環境 | Phase 0 で最初に確認。ダメなら記録/再生（L0）中心に切替 |
| NGO 1.x と Meta XR の干渉（両方 Update でリグを触る） | リグ操作はローカルのみ、ネットはデータ層だけ触る分離を asmdef で強制 |
| ホスト Quest のスリープでセッション死 | 開発中は adb で近接センサ無効化。v1 仕様として「死んだら再起動」 |
| 手メッシュのリグが OVRSkeleton と合わない | Meta XR サンプルの手メッシュ/マテリアルを流用して回避 |

## コミット規約

`<type>：<日本語要約>`（グローバル規約）。Phase 完了ごとに master へコミット。`ProjectSettings/` 差分は XR 設定追加など理由を明示できるもののみ含める。

---

## 実装状況（2026-06-10 シュビー記録）

実装済み（コンパイル検証は Unity 起動時に要確認）:

- **Phase 0 相当**: AvatarPose / IHandPoseSource + Registry / HandPoseSampler（layout キャプチャ込み）/ PoseRecordingFile（layout 同梱バイナリ）/ HandPoseRecorder / FakeHandDriver（Synthetic + File 再生）
- **Phase 1 相当**: ConnectionManager（手動IP + intent/コマンドライン引数で自動接続、named message 配送 + server リレー）/ TableDuoPlayer（席割当・リグアライン・30Hz送信）
- **Phase 2 相当**: PoseCodec（頭・手首 f32 / bone half、~460B/pose）/ RemoteAvatarView（フル=頭箱+手、手だけ=頭マーカー+手、bone 階層遅延構築）
- シーン生成: TableDuoSceneSetup（テーブル/席/OVRCameraRig+OVRHand/NetworkManager/Systems/DebugCamera、冪等）
- テスト: PoseCodecTests（round-trip 精度 + MaxBytes 内）

### 未実装（次セッション以降）

1. **Unity 起動 → コンパイル確認 → Setup TableDuo Scene 実行 → L0 検証**（FakeHandDriver Synthetic で 2 プロセス同期）
2. Phase 3: 掴み（GrabRequest ServerRpc + ホスト裁定）+ テーブル小物
3. 受信 pose の補間（現状は 30Hz 生適用。カクつくなら lerp バッファ追加）
4. トラッキングロストのフェード（現状 activeSelf 即時切替）
5. recenter（RecenteredPose 購読）= Phase 4

### 自動接続の使い方（実機）

```
adb shell am start -n <pkg>/com.unity3d.player.UnityPlayerActivity -e tdv_mode host
adb shell am start -n <pkg>/com.unity3d.player.UnityPlayerActivity -e tdv_mode client -e tdv_ip 192.168.x.x
```

PC ビルド / Editor: `-tdvMode host|client -tdvIp <ip>`
