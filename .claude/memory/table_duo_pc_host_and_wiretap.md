---
name: table_duo_pc_host_and_wiretap
description: TableDuo の PC ホスト構成・WireTap 通信記録・マルチモーダル手/コントローラ・A/B ボタン運用
metadata: 
  node_type: memory
  type: project
  originSessionId: cdf1e893-5309-4360-bb69-b442c299a6ef
---

TableDuo（[[table_duo_study_status]]）の実機運用を楽にする 2026-07-06 追加分。コミット `feat：TableDuo ソロ実機検証機能＋操作性改善` 系。

## 推奨トポロジ: PC ホスト + Quest 2 台クライアント（2026-07-06 ユーザー採択）
```
PC (NGO host・spectator・L0 desktop build)  ← Quest A: client/full(人)  ← Quest B: client/hand(手)
```
- **利点**: SessionLogger CSV / WireTap が PC に直接落ちる／両者の pose が必ずワイヤを通ってから記録（計測対称性↑）／時計は PC 基準に両 client 整列（clockOffset 既存）／役割交代はセッション継続のまま Quest 再起動だけ／PC 安定で障害耐性↑。
- **唯一のデメリット**: 手→PC→人 の 2 ホップで named-message tick flush の +平均8ms/最悪16ms（TickRate=60）。まず WireTap+clockOffset で実測してから TickRate 上げを判断。
- **成立にコード変更ほぼ不要**: 既存の spectator ロール（[SpectatorController](../../Assets/TableDuo/Scripts/Net/SpectatorController.cs)）+ L0 デスクトップビルド（[[table_duo_l0_desktop_test]]）+ pose/layout リレー + 自動再接続がそのまま効く。maxClients=3 で host+2 がちょうど。
- **起動**: `tools/tableduo-pc-host.ps1`（PC の LAN IP 自動解決 → PC host を `-tdvMode host -tdvRole spectator -tdvL0 on -logFile` で起動 → Quest 2 台を client/full・client/hand で adb 起動。Link ダイアログ潰しも内包。`-NoHost` で Quest だけ繋ぎ直し、`-HandVariant`/`-DryRun` 可）。
- **前提**: PC は `Builds/tableduo-desktop/TableDuo.exe`（`Build TableDuo Desktop (L0 test)`）が最新であること。PoseCodec を変えたら desktop/Quest 両方焼き直す。PC の UDP 7777 受信を firewall 許可（初回）。

## ソロ実機検証（Quest 1 台被り + マネキン 1 台）: tdv_fake=on
片方の Quest をアプリ起動状態でマネキン頭に被せ host に、もう片方を自分が被って検証する用。
- host を `-e tdv_fake on` で起動すると [ConnectionManager.ConfigureFakeSenderIfRequested](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs) が **FakeHandDriver を ON**（Priority=10 で実トラッキングより優先）→ 固定の合成モーションを通常経路で送信し続ける。L0 と違い OVRCameraRig は生かすので HMD 描画は通常。
- 「相手が同じ動きをするズル」ではなく、受信側は無改変＝ネットワーク経路の実測になる。

## WireTap 通信記録（B ボタン / F9）
[WireTapRecorder](../../Assets/TableDuo/Scripts/Net/WireTapRecorder.cs)（Systems・Setup 配線）。**右コントローラ B（Quest）/ キーボード F9（PC host）**でトグル。開始〜停止の間、通信上のアバター動作を CSV 化:
- `dir=sent`: この端末が送出した pose（[ConnectionManager.LocalPoseSent](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs) ＝ワイヤ送出点タップ。**ローカル描画の手は経由しない**＝手側検証の主対象）
- `dir=recv`: 受信・Seq フィルタ通過後の相手 pose
- PC host（spectator）は送出しないので recv のみ（両 client 分＝origin 列で判別）＝両者のワイヤ全体像
- 出力 `persistentDataPath/tdv_wiretap_yyyyMMdd_HHmmss.csv`（列: tMs,dir,origin,seq,captureMs,tracked/pinch,head pos/euler,wrist L/R pos,indexBend L/R）。2s ごと flush。

## 操作ボタン（2026-07-06 現在）
- **A（右手）単押し**: 視点リセット（席へ再センタ）。両手グリップ3秒長押しも維持（[ControllerRecenterWatcher](../../Assets/TableDuo/Scripts/Hands/ControllerRecenterWatcher.cs)）
- **B（右手）単押し**: WireTap 記録トグル
- **Y（左手）**: 手バリアント巡回（調査フラグ起動中は無効）

## コントローラ把持で手が消えない（マルチモーダル・2026-07-06）
OVRManager を [TableDuoSceneSetup](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs) で設定（シーンに override 焼き込み）:
- `launchSimultaneousHandsControllersOnStartup=true`（手とコントローラ同時トラッキング）
- `controllerDrivenHandPosesType=Natural(2)`（握った手の骨格をコントローラ入力から自然な手形で駆動）
→ 素手ならハンドトラッキング、握れば手メッシュが出続ける（OVRHand.IsTracked 保持）。OVRProjectConfig.handTrackingSupport=1(ControllersAndHands) 前提（設定済み）。**実機で握り時の手の見た目・送信 pose を要確認**。

## 実機起動 早見表
| 役 | intent（PC host 構成） |
|---|---|
| 人 | `-e tdv_mode client -e tdv_role full  -e tdv_ip <PCのIP>` |
| 手 | `-e tdv_mode client -e tdv_role hand  -e tdv_ip <PCのIP>` |
| PC | `TableDuo.exe -tdvMode host -tdvRole spectator -tdvL0 on -logFile <path>` |

Quest 同士 host 構成（PC 不要の従来）は host を `-e tdv_mode host -e tdv_role full`、client の tdv_ip を host Quest の IP に。USB 接続中は host 側で Quest Link ダイアログが起動をブロックするので `adb -s <serial> shell am force-stop com.oculus.systemux` で潰してから起動（スクリプトは内包）。
