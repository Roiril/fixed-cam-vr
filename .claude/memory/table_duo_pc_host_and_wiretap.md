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
- **🆕 自動接続（2026-07-10・E2E 実機確認済み）**: host さえ立てば **Quest はランチャーからアプリを開くだけ**で繋がる。host が UDP :7778 に 1Hz ビーコン → フラグ無し起動の client（HostDiscovery）が発見して自動接続 → **役割は接続順に host が自動割当（先着=Full 人役・次=Hand 手役、deviceId 記憶で被り直しても席維持）**。`tdv_role` 明示は常に自動割当より勝つ（調査ブロックはこちら）。ビーコンに sceneHash（ビルド時スタンプ、gitignore 対象 `TableDuoBuildInfo.txt`）を載せ、host/client ビルド不一致を検出。罠: HostDiscovery の受信スレッドで Resources.Load するとメインスレッド制約で即死（Begin() で先読み済み）。AP 分離 Wi-Fi では broadcast が届かないので従来 `tdv_ip` にフォールバック。計画 [2026-07-10_tableduo-auto-connect.md](../plans/2026-07-10_tableduo-auto-connect.md)。
- **前提**: PC は `Builds/tableduo-desktop/TableDuo.exe`（`Build TableDuo Desktop (L0 test)`）が最新であること。**PoseCodec だけでなくシーン/NetworkObject 構成（駒の追加・prefab 変更）でも desktop/Quest 両方焼き直す**。片方だけ更新すると NetworkObjectId が食い違い、RPC が `[Netcode] Deferred messages ... OnSpawn ... not received within 10 second(s)` で黙って捨てられる（2026-07-10 実害: 駒が掴めない＝クライアントの Grab RPC を古い host が解決できず破棄。クライアント側はピンチ検出も RPC 送信も正常に見えるので host ログの Deferred 警告が唯一の手がかり）。`tableduo-pc-host.ps1` が exe と apk の mtime を比較して古ければ警告する。PC の UDP 7777 受信を firewall 許可（初回）。

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

## 観戦カメラの視点切替（PC ホスト画面・2026-07-13）
PC ホスト（spectator）画面**左上の GUI ボタン／数字キー 1-3** で観戦カメラを3モード切替（[SpectatorController](../../Assets/TableDuo/Scripts/Net/SpectatorController.cs)）:
- **1=俯瞰**（既定・両者を等距離で俯瞰）/ **2=人役視点**（席0 の完全一人称）/ **3=手役視点**（席1 の完全一人称）
- 一人称は対象プレイヤーの頭 world pose にカメラを毎フレ追従し、当人の頭だけ潰す（胴・腕・手は残る＝体を見下ろせる）。純ローカル＝ネット非関与・Quest client 無変更。未接続なら「（対象 未接続）」表示でカメラ据え置き。WireTap の GUI（右上）とは別領域

## 操作ボタン（2026-07-06 現在）
- **A（右手）単押し**: 視点リセット（席へ再センタ）。両手グリップ3秒長押しも維持（[ControllerRecenterWatcher](../../Assets/TableDuo/Scripts/Hands/ControllerRecenterWatcher.cs)）
- **B（右手）単押し**: WireTap 記録トグル
- **Y（左手）**: 手バリアント巡回（調査フラグ起動中は無効）

## コントローラ把持で手が消えない（マルチモーダル・2026-07-06）
OVRManager を [TableDuoSceneSetup](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs) で設定（シーンに override 焼き込み）:
- `launchSimultaneousHandsControllersOnStartup=true`（手とコントローラ同時トラッキング）
- `controllerDrivenHandPosesType=Natural(2)`（握った手の骨格をコントローラ入力から自然な手形で駆動）
→ 素手ならハンドトラッキング、握れば手メッシュが出続ける（OVRHand.IsTracked 保持）。OVRProjectConfig.handTrackingSupport=1(ControllersAndHands) 前提（設定済み）。
- **⚠ 2026-07-11 実害①（真因は表示ゲート）: コントローラを握ると手が消える本当の原因は `OVRHand.m_showState` の既定値 `ControllerNotInHand`**。握ると OVRHand が IsDataValid=false を強制し OVRMeshRenderer が手メッシュを隠す（骨格データ自体は流れ続けており [TDV-WRIST] は握り中も出る＝データ正常・表示だけ死ぬ）。→ TableDuoSceneSetup の BuildHand で `m_showState=0(Always)` を焼き込み。multimodal 自体は動いていた（logcat で `detached_controller_meta` の interaction profile 遷移が出る・`checkMultimodalFeature` 拒否なし＝正常のサイン）。
- 同日の調査メモ: `SimultaneousHandsAndControllersEnabled`（ビルド時 capability）も true にしたが、**SDK v201 ではこのフラグを消費する build/runtime 経路が存在しない**（Editor UI と Body/Face 排他チェックのみ・マニフェスト生成にも bootconfig にも出ない）。無害なので設定は残すが、これ単体では何も変わらない。`TrackingFidelityService: not supported by app` ログはアプリ終了時などに出る紛らわしい残骸で、拒否の確定証拠には使えない。切り分けの正は「握った瞬間の interaction profile 遷移（hand→touch_controller_plus + detached 連動）」をライブ logcat で見ること。
- **⚠ 2026-07-11 実害②: Y ボタンの手バリアント切替がスクリプト起動で常時無効だった**。HandVariantWatcher が `LaunchedWithStudyFlags` で無効化していたが、このフラグは `tdv_role` 指定だけで立つ（＝tableduo-pc-host.ps1 経由は常に該当）。→ 新フラグ `StudyConfig.HandVariantLockedByFlag`（tdv_hand 指定時のみ true）で判定するよう変更。tdv_role 単独なら Y トグル有効。

## 運用フロー（PC ホスト + 2 Quest・確立版）
1. `tools/tableduo-pc-host.ps1` 一発（wake→Link ダイアログ潰し→起動→pid 確認まで内包）。手動なら:
   - PC: 旧 `TableDuo.exe` を全 kill → `TableDuo.exe -tdvMode host -tdvRole spectator -tdvL0 on -screen-fullscreen 0 -logFile <logs/host_*.log>` → ログに `Host 開始 port=7777` で listen 確認
   - Quest×2: `am force-stop` → **`input keyevent KEYCODE_WAKEUP`** → `am force-stop com.oculus.systemux` → `am start ... -e tdv_mode client -e tdv_role full|hand -e tdv_ip <PCのIP>` → `pidof` で起動確認 → ホストログ `リモート(clientN) 役割=...` で接続確認
2. 記録: PC 画面右上 GUI ボタン / F9（Quest は右 B）。CSV は PC の `%USERPROFILE%\AppData\LocalLow\DefaultCompany\TableDuo\tdv_wiretap_*.csv`

### 起動の罠（2026-07-06 実害・全部踏んだ）
- **Quest がスリープ（近接センサー OFF＝顔/マネキンから外れた）だと `am start` が黙って失敗**（エラーなし・pid 立たず・`dumpsys power | grep mWakefulness` が Asleep）→ `KEYCODE_WAKEUP` か HMD を被る/センサーを指で塞ぐ
- **USB 接続中の「Quest Link を開始しますか？」OS ダイアログがアプリ起動をブロック**（`Launch is blocked because: a Reprojected OS dialog is currently showing`）→ `am force-stop com.oculus.systemux`
- **PC ホストの旧プロセス残骸が MarkServer(7780) を掴む** → `MarkServer 起動失敗` は reset_board の curl だけ死ぬ（接続/記録/診断は無影響）。kill してもゴースト socket が残ることがあり、その時は PC 再起動まで放置で可
- adb install が「0 files pushed」で無言失敗することがある → もう一度 install（2回目で通る）

## 診断タグ一覧（手アバター不具合の切り分け）
| タグ | 出所 | 見るもの |
|---|---|---|
| `[TDV-SKEL]` | 送信側・スケルトン初期化時1回 | `type=HandLeft count=24` が正（`XRHandLeft count=26` なら handSkeletonVersion 再発） |
| `[TDV-WRIST]` | 送信側・1Hz | 手首アンカー vs 実 bone0 の差（delta=0 が正） |
| `[TDV-WIRE]` | 受信側・記録中1Hz | ワイヤ上の pose（wristEuler/boneMax/seq/age）＝データ側の健全性 |
| `[TDV-DRAW]` | 受信側・記録中1Hz | 描画適用（target vs applied/mode/varBind）＝描画側の健全性 |
切り分け手順: WIRE が荒れてる→送信/座標系、WIRE 正常で DRAW target≒applied なのに見た目が変→スケルトン体系/リターゲット、DRAW の applied が target とズレ→平滑/描画バグ。

## ⚠ 手アバター3大バグの根治記録（2026-07-06〜07）

### (2) 手/頭の系統的な位置ズレ（1bea205）
受信側（RemoteAvatarView/Grabbable/Remy）は pose を**席ローカル**として解釈するが、送信は
**trackingSpace ローカル**で採取していた。A ボタン（RigRecenter.HeadToSeat）は**リグごと平行移動**
して頭を席に合わせるため、以後 trackingSpace 原点≠席になり、リセット時の頭ドリフト分だけ
リモート手・頭が系統ズレ。→ **HandPoseSampler.ReferenceFrame に席を設定**（TableDuoPlayer.SetupOwner）
し「席から見た pose」を送る。座標系契約は「pose は常に席ローカル」で統一。
[TDV-WRIST] で delta=0 を確認済み＝アンカー採取説は棄却してこの結論に至った。

### (3) 人役 Remy の指が動かない（1bea205・P3 消化）
RemyAvatarRig は手首 IK のみで指を未駆動だった。OVR legacy BoneId（各指 1-3 節・15 ボーン）→
`mixamorig:<Side>Hand<Finger><1-3>` へ HandRetarget.Solve（バインド差分）で駆動。
ovrBind=受信側 Captured layout（観戦 PC 等 layout 無しは identity 近似＝多少オフセットするが動きは伝わる）。

### (1) 手崩れ・指非同期の根治（2026-07-06・b51a747）
リモート手が崩れ指が動かない真因＝ `Assets/Resources/OculusRuntimeSettings.asset` の
**`handSkeletonVersion: 1`(OpenXR 26 bone)**。SDK 201 導入時から入っており、OVRHand は
この**グローバル設定だけ**でスケルトン種別を決める（シーンの `_skeletonType` serialize 値は無関係）。
コード側は全経路 legacy 24 bone 前提（HandBoneTable / PoseCodec 24 枠 / OVRCustomHandPrefab）なので全ズレ。
**対処＝ 0 (legacy OVR) に戻す**。切り分けは HandPoseSampler の `[TDV-SKEL]` ダンプ
（`type=HandLeft count=24` なら正常 / `XRHandLeft count=26` なら再発）。
**再発経路: Meta Project Setup Tool の「Apply All」がこの値を OpenXR へ倒しがち**。手が崩れたらまずここを疑う。
WireTap の切り分け実績: ワイヤ側 [TDV-WIRE] は滑らか＝データ正常、[TDV-DRAW] target≒applied＝描画忠実、
なのに見た目が崩れる → スケルトン体系の不一致、という診断手順が有効だった。

## 人役の一人称自己アバター（tdv_selfbody・2026-07-07・ca8422b／既定 on 化 2026-07-10）
下を向くと自分の胴/腕/手が見える身体所有感。**既定 on**（2026-07-10 ユーザー採択・StudyConfig.ShowSelfBody=true）＝人役は自分の体が見えるのが標準。off にしたい時だけ `-e tdv_selfbody off`（旧: 既定 off で `on` 指定だった）。スクリプトの `-SelfBody` スイッチは既定 on 化で冗長（無害）。
- 仕組み: [LocalSelfBody](../../Assets/TableDuo/Scripts/Net/LocalSelfBody.cs) が相手に見えるのと同じ Remy を
  もう1体、**自分のローカル pose（HandPoseSourceRegistry.Best＝送信と同じ席フレーム値）**で駆動。
  [RemyAvatarRig](../../Assets/TableDuo/Scripts/Net/RemyAvatarRig.cs) の firstPerson モードで**頭ボーンを
  scale 0.01 で潰し**（視界を塞がない）body を席ローカル -Z へ 4cm（胸のニアクリップ貫通防止）。
- 白手は**レンダラーだけ非表示**（OVRHand/Skeleton は生きる＝トラッキング/ピンチ/送信/掴み判定は無変化）。
  手は Remy 手に一本化。掴み判定は描画非依存なので機能劣化なし（唯一の実機確認点＝Remy 手の IK 表示位置で
  掴み狙いがズレないか）。
- ローカル描画専用＝**相手に見える自分（ネット越し Remy）は不変**・ネット送信は無追加。
- 条件記録: `_studyFlags` bit4 同期・CSV `condition` 行に `selfBody=0/1`。既定 off は交絡回避
  （パイロットで on/off 比較して本番既定を決める）。
- Editor 検証: `Diagnostics/Preview Self Body (first-person)` → `Temp/AvatarPreview/07_selfbody_lookdown.png`
  ・`08_selfbody_straight.png`（頭が消えて体が下方に見えるのを確認済み）。

## 実機起動 早見表
| 役 | intent（PC host 構成） |
|---|---|
| 人 | `-e tdv_mode client -e tdv_role full  -e tdv_ip <PCのIP>` |
| 手 | `-e tdv_mode client -e tdv_role hand  -e tdv_ip <PCのIP>` |
| PC | `TableDuo.exe -tdvMode host -tdvRole spectator -tdvL0 on -logFile <path>` |

Quest 同士 host 構成（PC 不要の従来）は host を `-e tdv_mode host -e tdv_role full`、client の tdv_ip を host Quest の IP に。USB 接続中は host 側で Quest Link ダイアログが起動をブロックするので `adb -s <serial> shell am force-stop com.oculus.systemux` で潰してから起動（スクリプトは内包）。

## 明日（2026-07-07 以降）の実機確認チェックリスト
最新 APK（1bea205 ビルド）は両 Quest 導入済み・desktop も焼き直し済み。確認事項:
1. **位置ズレ解消**: 自分の HMD で見る自分の手と、ホスト/相手から見たリモート手が同じ場所か。**A ボタンで視点リセットした後も**ズレないか（これが今回の修正の核心）
2. **Remy（人役）の指**が動くか・曲がり方が不自然でないか（バインド差分リターゲットの軸ズレは実機でしか判定できない）
3. **Remy の手首位置**が自然か（席フレーム修正の効果）
4. 手役の指（前回 OK）が退行していないか
5. コントローラ把持で手が消えないか（マルチモーダル・素手テストでは未検証のまま）
