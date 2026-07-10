---
status: in-progress
created: 2026-07-10
updated: 2026-07-10
slug: tableduo-auto-connect
---

# TableDuo ホスト自動発見・自動接続（クライアントはアプリを開くだけで繋がる）

## 概要

現状、host/client の起動は adb intent extras（`tdv_mode` / `tdv_ip` / `tdv_role`）が必須で、
シュビー（PC + adb）を介さないとセッションを組めない。これを
**「PC でホストを立てる → Quest はランチャーからアプリを開くだけ → 自動接続」** にする。
研究の現場運用（実験者が PC 1 台で回す・被験者はヘッドセットを被るだけ）に直結する。

## 現状の起動フロー（把握済み）

- [ConnectionManager.Start()](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs) が
  `ResolveAutoMode()` で `tdv_mode`（host|client）と `tdv_ip` を読み、無ければ
  serialize 済み `autoMode`（既定 None）→ OnGUI の Host/Join ボタン待ち（VR 内では実質押せない）
- 役割は `tdv_role`（full|hand|spectator）→ `StudyConfig.ForcedRole`。**無指定の client は Hand に固定フォールバック**
  （TableDuoPlayer.OnNetworkSpawn: `ForcedRole ?? (owner==server ? Full : Hand)`）
  → PC ホスト構成では 2 台とも Hand になってしまうので、自動接続には役割の自動割当が必須
- client 切断後の自動再接続は実装済み（指数バックオフ・`_lastClientAddress` 再利用）。
  「接続前のホスト探索」だけが欠けている

## 設計

### 1. LAN ホスト発見 = UDP ブロードキャストビーコン（新規 2 部品）

```
PC host                             Quest client（フラグ無し起動）
─────────                           ─────────────────────────
HostBeacon                          HostDiscovery
  StartHost 成功後に開始               起動直後から UDP 7778 を listen
  1Hz で UDP 7778 へ broadcast   →    beacon 受信 → 送信元 IP へ StartClient
  payload:                            接続確立で listen 停止
   magic "TDVB1"                      切断されたら listen 再開（再発見）
   NGO port (7777)
   buildId（ビルド時刻）
   sceneHash（後述の照合用）
```

- **HostBeacon**（`TableDuoVr.Net.HostBeacon`）: `UdpClient` で `255.255.255.255:7778` +
  サブネットブロードキャスト両方へ 1Hz 送信。ホスト終了で停止。L0 デスクトップでも動く（OVR 非依存）
- **HostDiscovery**（`TableDuoVr.Net.HostDiscovery`）: 受信スレッド（or 非同期）で beacon を待つ。
  **タイムアウトで諦めない**（ホストが後から立っても、被ったまま待てば繋がる）。
  複数 host が見えたら最初の 1 つ（研究運用では同一 LAN に 1 host 前提。混線対策は magic + port で最低限）
- ポート 7778 は MarkServer(7780) / NGO(7777) と衝突しない新規。PC firewall 許可が初回必要（README に記載）

### 2. 起動モードの既定を「client + 自動発見」へ

`ResolveAutoMode` の優先順位を変更:

| 起動条件 | 挙動 |
|---|---|
| `tdv_mode=host` | 従来どおり即 host（PC スクリプト用・変更なし） |
| `tdv_mode=client` + `tdv_ip` | 従来どおり即 client（明示指定は常に勝つ） |
| `tdv_mode=client`（ip 無し） | **HostDiscovery で発見 → 接続** |
| **フラグ一切無し（= ランチャー起動）** | **HostDiscovery で発見 → 接続**（これが新既定） |

- Editor / Inspector の `autoMode`=Host はシーン焼き込みで従来動作を保持（L0 テスト互換）
- OnGUI の手動 Host/Join ボタンは温存（フォールバック・デバッグ用）
- 探索中は VR 内に控えめな状態表示（「ホストを探しています…」+ 発見時に消える。
  既存 `_status` GUI とは別に、HMD 内で見える head-locked の小さな 3D テキスト）

### 3. 役割の自動割当（サーバ裁定・接続順）

`tdv_role` 無しの client は「auto」を申告し、**host が席の空き順に割当てる**:

- 席0（Full=人役）が空いていれば Full、埋まっていれば席1（Hand=手役）
- 割当は接続 approval 後に host → client へ通知（既存の `_role` NetworkVariable を
  server-write に寄せるか、割当専用 RPC。TableDuoPlayer の役割決定タイミングを
  「owner が自己申告」から「server 割当を owner が受領」へ変更する）
- `tdv_role` 明示があれば従来どおりそれが勝つ（調査ブロックの条件固定はこちら）
- 途中離脱 → 再接続で同じ席に戻れるよう、**deviceId（SystemInfo.deviceUniqueIdentifier）→ 席**を
  host がセッション中記憶する（被り直しただけで役割が入れ替わる事故を防ぐ）

### 4. ビルド不一致の見える化（今日の事故の恒久対策）

beacon の `buildId`（ビルド時刻を焼き込み）と `sceneHash` を client が照合し、
**不一致なら接続は許すが HMD/observer 双方に警告を常時表示**
（「ホストとビルドが違います — 掴み等が壊れます」）。
2026-07-10 の「駒が掴めない（host が古くて RPC 破棄）」を silent failure にしない。
buildId は `BuildVariants` がビルド時に `Resources/TableDuoBuildInfo.asset`（ScriptableObject）へ刻む。

### 5. 運用フローの変化

| 手順 | 現在 | 導入後 |
|---|---|---|
| 1 | シュビーに依頼 or ps1 実行 | PC で host 起動（ps1 or デスクトップショートカット。ダブルクリック可） |
| 2 | adb で 2 台に intent 起動 | **Quest でアプリを普通に開く（×2 台）** |
| 3 | — | 自動発見 → 自動接続 → 役割自動割当（先着=人役・後着=手役） |

- `tableduo-pc-host.ps1` は温存（ログ収集・鮮度チェック・役割明示のフル管理経路）。
  被験者運用では「host だけ立てて Quest は手で開く」が最短経路になる
- 役割を入れ替えたい時: 両方いったんアプリ終了 → 人役にしたい方を先に開く
  （将来: VR 内で役割スワップ操作を足す余地あり。まず接続順で運用）

## 実装フェーズ

### Phase 1: ホスト発見・自動接続（コア）— 2026-07-10 実装・実機 E2E 確認済み

- [x] `HostBeacon.cs` 新規（host 側 1Hz ブロードキャスト・StartHost 成功時に自動開始）
- [x] `HostDiscovery.cs` 新規（client 側 listen・発見で `StartClient(ip)`・切断で再開）
      ⚠ 受信スレッドで `Resources.Load`（TableDuoBuildInfo.SceneHash）を呼ぶと即死 → Begin() で先読み（実害から修正）
- [x] `ConnectionManager.ResolveAutoMode` 拡張（フラグ無し → Discovery 既定）
- [ ] 探索中の VR 内状態表示の**目視確認**（実装済み・HMD 未確認）
- [x] 検証: 実機 E2E（extras 無し am start ×2 → 発見→接続→割当まで全自動成立）。L0 loopback 検証は実機成立でスキップ

### Phase 2: 役割の自動割当 — 2026-07-10 実装・実機確認済み

- [x] client の「auto」申告 → host が席空き順に Full/Hand 割当（`役割自動割当: client1 → Full` ログ確認）
- [x] deviceId → 席の再接続スティッキネス（実装済み・ログに device= 表示）
- [x] `tdv_role` 明示との優先順位整理（明示 > 記憶 > 接続順）
- [ ] 被り直し（アプリ再起動）で同じ席に戻る実機確認

### Phase 3: ビルド不一致の見える化 — 2026-07-10 実装

- [x] `BuildVariants` が sceneHash を `Assets/TableDuo/Resources/TableDuoBuildInfo.txt` へ焼き込み（gitignore・BOM 無し UTF-8）
- [x] beacon に sceneHash を載せ、client 照合（不一致警告の**表示は HMD 未確認**）
- [x] memory 更新（table_duo_pc_host_and_wiretap に運用・罠を追記）

## リスク・既知の罠

- **UDP broadcast が届かない AP 分離 Wi-Fi**（ゲスト SSID 等の client isolation）: その環境では
  従来の `tdv_ip` 明示にフォールバック（探索表示に「見つからない時は…」を出す）
- **Quest のバックグラウンド制限**: listen スレッドは OnApplicationPause で止めて復帰時に再開
- **NGO の approval タイミング**: 役割割当を spawn 前に済ませたい場合は ConnectionApproval
  コールバックの利用を検討（maxClients 制限の enforcement も接続後切断 → approval 拒否に寄せられる）
- **役割自動割当と調査条件の交絡**: 調査ブロックでは必ず `tdv_role` 明示（ps1 経由）で固定し、
  自動割当はデモ・開発・パイロットの利便機能と位置づける（study-protocol に明記）

## 自律改善ログ

(作業中に気付いた改善点や学びをここに追記)
