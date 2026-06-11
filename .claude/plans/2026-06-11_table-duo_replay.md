# 実行計画: TableDuo セッションリプレイ（stimulated recall 用）T8–T10

目的: セッション全体（両者の全身振り＋小物＋イベント）を記録し、**直後に PC 画面で自由視点リプレイ**しながら両操作者と見返して質的分析（意図↔解釈の突き合わせ）を行う。
背景 → [docs/table-duo/study-design.md](../../docs/table-duo/study-design.md) §4 / 手法上の位置づけ: stimulated recall。

前提: 調査機能 T1–T6 実装済み（`bfe0fd0`）。SessionLogger CSV は分析用で**リプレイには不十分**（手首+7点のみ・全boneなし）。

## 設計判断（確定）

| 論点 | 決定 | 理由 |
|---|---|---|
| 記録場所 | **ホスト1箇所**（SessionReplayRecorder） | ホストは両者の AvatarPose 全量を既に受信している |
| 記録内容 | 両者の全 AvatarPose（全bone）30Hz + 全 prop/card transform（変化時のみ ≤10Hz）+ イベント + 役割 | リプレイ完全性。CSV は分析用として併存 |
| フォーマット | 新バイナリ `TDVR1`（PoseRecordingFile の pose 序列化を流用） | 既存コーデック再利用 |
| 音声 | **アプリ内録音はしない**。室内レコーダー音声をビューアで並走再生 | Quest マイク権限・実装コストに見合わない |
| 音声同期 | **クラップと同時に `curl /mark?label=clap`** → replay 内の clap epoch と音声内クラップ時刻の差をビューアのオフセット欄に入力 | 既存 MarkServer だけで成立 |
| ビューア | **Editor Play Mode 上のフラット画面**（ビルド不要）。NGO は起動しない | 3人でモニタを囲んで議論する運用。自由カメラ＋「手の視点」プリセットが研究上の武器 |
| 手スケルトン構造 | ホスト側キャプチャの layout をファイルに同梱（両者とも近似） | クライアントの layout は送信していない（ライブ描画と同じ近似で十分） |

## T8: SessionReplayRecorder（ホスト・調査ビルドに同梱）

`Assets/TableDuo/Scripts/Net/SessionReplayRecorder.cs`（Systems に追加）

- ファイル: `persistentDataPath/tdv_replay_<yyyyMMdd_HHmmss>.bin`（起動ごと新規・FileShare.Read）
- ヘッダ: magic `TDVR1` / 開始 epochMs / StudyConfig / HandSkeletonLayout L,R（無ければフラグ）
- レコード（type byte + epochMs int64）:
  - `0x01` pose: clientId(byte枠), AvatarPose 全量（PoseRecordingFile.WritePose 流用）
  - `0x02` prop: propId(byte), pos3f, rot4f — **サーバ権威 transform を変化時のみ**（位置1mm/回転1°閾値、最大10Hz/個）
  - `0x03` event: label, detail（SessionLogger.LogEvent にフックして二重書き）
  - `0x04` role: clientId, role, seatIndex（プレイヤー spawn 時）
  - `0x05` propRegistry: propId → 名前（初出時）
- サンプリング: Update 30Hz、ConnectionManager.TryGetPose で全接続クライアント分
- サイズ見積: 2人×30Hz×~800B ≈ 48KB/s ≈ **40分で ~115MB**（Quest ストレージ・adb pull とも許容）
- 回収: 既存 `tools/pull-tdv-logs.ps1` がそのまま拾う（files ディレクトリごと pull）

### 検証（L0）

Editor ホスト + FakeHandDriver(File) で数十秒記録 → ファイル生成・サイズ・レコード数を確認するエディタメニュー `Tools/FixedCamVr/Diagnostics/Dump TableDuo Replay`（ヘッダとレコード集計を Console に出すだけの小物）

## T9: ReplayViewer（Editor Play Mode）

`Assets/TableDuo/Scripts/Net/ReplayViewer.cs` + Setup に無効状態で配置（`[TableDuo]/ReplayViewer`）

- 起動: Inspector でファイルパス指定 → enable → Play（NGO は開始しない。ConnectionManager.autoMode=None のままにする運用）
- ロード: 全レコードをメモリへ（PC 前提・~115MB可）。clientId ごとの pose 列 / prop 列 / イベント列に索引
- 描画: 記録された役割で RemoteAvatarView を席下に生成（既存品）。prop はシーン内同名 GameObject を直接動かす（NetworkObject だが未スポーンなのでただの GO）
- 再生制御（IMGUI パネル）:
  - 再生/停止・速度 0.25/0.5/1/2x・タイムラインスライダー（シーク=各列で時刻≤tの直近を適用）
  - **イベント一覧クリックでジャンプ**（mark/grab/trackingLost が議論の頭出しポイント）
  - 現在 epoch とセッション内経過の表示
- カメラ: FlyCamera（WASD+マウス）+ プリセット3つ — 席0頭 / **席1の手（手の視点）** / 俯瞰。「手から見たあなた」を見せるのが stimulated recall の強い揺さぶりになる
- 音声並走: wav を `UnityWebRequestMultimedia` でロード、AudioSource 再生。オフセット欄（clap mark epoch − 音声内クラップ秒）を手入力。シーク時は音声も追従
  - 室内レコーダーが m4a の場合に備え `tools/convert-audio.ps1`（ffmpeg → wav）を用意（ffmpeg 無ければ手動変換を案内）

### 検証

- L0: T8 で作った記録を再生 → アバター2体・小物・イベントジャンプ・シーク動作
- L1: 実機1台で短いセッションを記録 → 実手データのリプレイ確認

## T10: ドキュメント改訂（見返しセッションの組込み）

- consent-template: 「記録の再生を本人と視聴し議論すること・その議論も録音すること」を追記
- study-protocol: **Phase 8 見返し（30分・同日中）** を追加
  - 進め方: イベントジャンプで頭出し → 手役「何を伝えようとした？」→ 人役「何だと受け取った？」→ ズレを記録
  - 視点切替（手の視点）を見せた時の反応も記録（RQ3 プローブ）
  - 産物: **意図↔解釈 対応表**（観察シートに様式追加）
  - 全体所要を 40分 → 70–80分/ペアに改訂
- study-design §4: リプレイ記録の行を追加・所要時間更新

## 実装順序と検証ゲート

1. T8 → L0 で記録ファイル確認（Dump メニュー）
2. T9 → L0 リプレイ成立 → L1 実手データで確認
3. T10 ドキュメント改訂
4. ビルド＆実機インストール（T8 はホスト実機で動く必要がある）
5. パイロット（既存 T7）に見返しセッションを含めて通し → プロトコル凍結

## リスク

| リスク | 対処 |
|---|---|
| 記録ファイルが大きい（~115MB/40分） | pull 時間は USB で数十秒。ストレージは Quest 128GB に対し誤差 |
| 音声同期がずれる | clap mark と音声クラップの突合を Phase 0 の手順に明記。±100ms 精度で議論用途には十分 |
| Editor Play でシーン内 NetworkObject の警告 | NGO 未起動なら実害なし。出たら ReplayViewer 有効時に NetworkManager GO を無効化 |
| prop の変化時記録の取りこぼし | 閾値を緩めに（1mm/1°）。最悪 grab イベント＋pose から補える |
