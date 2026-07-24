# TableDuo「あと6画のくま」ペングリップ再設計（ToolGripDriver）

2026-07-24。旧 MarkerHoldTilt を ToolGripDriver へ全面置換。ペン保持・描画を VR の一般解へ寄せた。

## 診断（置換前の何が悪かったか）

旧 [MarkerHoldTilt](../../Assets/TableDuo/Scripts/Net/MarkerHoldTilt.cs)（削除済）:
- ペンは「掴んだ瞬間の手首→ペン相対姿勢を凍結して手首に剛体追従」+ LateUpdate(order120) が回転を毎フレ全上書き
  （yaw=手首 forward 水平射影・pitch=手の高さの関数 `min(45°, asin((手Y−面Y)/0.0923))`・ロール破棄）
- 描けるのは手が面上 0〜65mm の帯にある間だけ。手の傾き・ひねりがペンに反映されない → **持ちにくい・書きにくい**
- 接触判定はサーバ専用（PadPaintCanvas.ServerTick）で保持者本人の線もサーバ往復後にしか出ない → **二重遅延**
- 接触帯は y∈[面−4mm, 面+2mm] の単一閾値・平滑なし → ストローク端の欠け・チカチカ切れ

## 仕様 → 実装 対応表

| 仕様 | 実装 |
|---|---|
| A. ペン保持を「手首→ピンチ点線」基準に。位置と回転を両方確定。俯角 +15°・下向き 80° クランプ・面クランプ | [ToolGripDriver](../../Assets/TableDuo/Scripts/Net/ToolGripDriver.cs)（Pen モード）+ 純ロジック [PenGripLogic](../../Assets/TableDuo/Scripts/Net/PenGripLogic.cs) |
| ピンチ点 OneEuro 平滑 + dir 指数 slerp（rate20）・掴みエッジでリセット | [OneEuroFilter](../../Assets/TableDuo/Scripts/Net/OneEuroFilter.cs)（float/Vector3）+ ToolGripDriver.SmoothDir |
| 消しゴム Flat モード（yaw のみ・底面を面近くに置く・6mm 帯の手動合わせ廃止） | ToolGripDriver（Flat モード）+ PenGripLogic.ComposeFlatPose |
| フォールバック（layout/bones 不在）: 既存の手首相対追従へ | ToolGripDriver は上書きせず return → Grabbable/PinchGrabInteractor の追従が生きる |
| B. 接触ヒステリシス（端の欠け・チカチカ解消） | PadPaintLogic.ContactGate（DownTol 2mm / UpTol 6mm）+ TryGetUv。旧 TryGetContactUv 削除 |
| C. 保持者ローカル即時インク（二重遅延解消）+ エコー抑止 | PadPaintCanvas.ClientPredictTick + PadPaintLogic.ShouldSuppressEcho（0.75s 窓） |
| D. シーン焼き込み | TableDuoSceneSetup.AddToolGripDriver（ペン=Pen / 消しゴム=Flat・mode/tipDistance/tableTopY/padTransform を焼く） |

### サーバ側の手 pose 取得経路

サーバは保持者（リモート含む）のピンチ点を **本人の手寸法で FK** する:
`Grabbable.HolderSeat`（掴み時解決済）+ `ConnectionManager.TryGetPose(clientId)`（pose+bones）+
`ConnectionManager.GetHandLayout(clientId, right)`（本人 layout・未受信は host layout フォールバック）→
`HandLandmarks.Compute` → (thumbTip+indexTip)/2 = 席ローカルピンチ点 → seat.TransformPoint で world 化。
`HandSkeletonLayout.CapturedL/R` はローカル手専用なのでサーバのリモート FK には使わない。
保持者ローカル（非サーバ本人）は最新 `HandPoseSourceRegistry.Best` + `PinchGrabInteractor.LocalSeat` + Captured layout。

## 主なコード変更

- 新規: ToolGripDriver.cs / PenGripLogic.cs / OneEuroFilter.cs（+ テスト PenGripLogicTests / OneEuroFilterTests）
- 削除: MarkerHoldTilt.cs / MarkerTiltLogic.cs / MarkerTiltLogicTests.cs
- Grabbable.cs: `HolderHand` / `HolderSeat` アクセサ追加（最小限・掴み挙動は不変）
- PinchGrabInteractor.cs: `static LocalSeat`（Initialize でセット・domain reload リセット）
- PadPaintLogic.cs: TryGetUv / ContactGate / ShouldSuppressEcho 追加・TryGetContactUv 削除
- PadPaintCanvas.cs: ServerTick を ContactGate 化 + 非サーバ ClientPredictTick（即時インク）+ RPC エコー抑止
- TableDuoSceneSetup.cs: AddMarkerTilt → AddToolGripDriver（消しゴムにも Flat で新規焼き込み）

## 調整定数（すべて const・実機調整前提）

GripBack=0.025m（ピンチ点の 2.5cm 先がペン先）/ ExtraPitchDeg=15 / MaxDownDeg=80 / HoldDropM=0.02（消しゴム）/
DirSlerpRate=20 / OneEuro minCutoff=1.5・beta=0.05・dCutoff=1 / ContactGate DownTol=2mm・UpTol=6mm / EchoSuppress=0.75s。
SerializeField にしない（既存シーン YAML 焼き付きで 0 に読まれる罠回避＝LongPressSec と同流儀）。

## 実機確認チェックリスト（⚠ Quest 実機未検証・EditMode + コンパイルのみ）

- [ ] ペンが手の傾き・ひねりに自然応答し「持ちやすい・書きやすい」か（指先近くを軸に回るか）
- [ ] ペン先が紙に接地して線が引けるか（面クランプでめり込まない・手を持ち上げても線が続くか）
- [ ] 保持者本人・相手・ホスト観戦で同じ見え方か（本人ローカル楽観・リモート NT 補間 1 フレーム遅れ）
- [ ] リリースで寝かせ接地（空中浮き無し）か
- [ ] 消しゴム（Flat）が yaw のみ追従・底面が面に着いてペン線だけ消せるか
- [ ] 保持者ローカル即時インク: 自分の線が遅延なく出るか / エコーで二重に描かれないか
- [ ] ヒステリシスでストローク端が欠けない・チカチカ切れないか
- [ ] **フィルタ係数の現場調整**: OneEuro（minCutoff/beta）・dir slerp rate・俯角・GripBack を体感で詰める
- [ ] TableDuoSceneSetup 再実行（`Tools/FixedCamVr/Setup/Setup TableDuo Scene`）でペン 2 本＋消しゴムに ToolGripDriver が焼かれるか
- [ ] fallback 経路（layout 未受信の遅参加直後）で少なくとも旧来の掴み追従に落ちて破綻しないか
