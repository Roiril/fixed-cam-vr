---
name: table_duo_hand_variants
description: TableDuo 手の見た目3バリアント（Default/Realistic/Robot）切替の実装状態・駆動方式・実機要確認点
metadata: 
  node_type: memory
  type: project
  originSessionId: 6cc78a95-31c7-4311-a90d-908df6901065
---

TableDuo（ハンド）で手役の手メッシュを **Default=Meta白手 / Realistic=Male Hand / Robot=Robot Hand** の
3種に切り替える機能を実装（2026-07-01）。素材はユーザー購入の **VR Hands Starter Pack**。パックの
Robot/Male のみ `Assets/TableDuo/ThirdParty/VRHandsStarterPack/` に GUID 保持で抽出（他7種は未インポート）。

**切替**: 起動フラグ `tdv_hand=default|realistic|robot`（intent extras/CLI）＋ 実機は**左コントローラ Y** で巡回
（`HandVariantWatcher`）。Editor 既定は `ConnectionManager.studyHandVariant`。
**リモート描画は申告値同期（2026-07-10〜）**: 自分の手＝ローカル選択、相手の手＝相手端末の申告値
（`TableDuoPlayer._studyFlags` bit2-3。Y 切替で書き直し → `RemoteAvatarView.SetHandVariant` が再構築）＝
ホスト/観戦 PC にも切替が映る。調査は従来どおり両端末を同フラグ起動（不一致はエラーログ）。
適用は自分の手（`LocalVariantHand`）＋相手の手（`RemoteAvatarView`）両方。

**駆動のキモ（2026-07-11 参照コピー式へ全面刷新）**: パック手は Meta の `b_*` と別命名・別バインド・**別ローカル軸規約**。
- BoneId→bone名 対応表 = `HandVariantTable`（Unity で実リグ実測して作成。Male=`.R/.L`・Pre/Lower/Medium/Upward、
  Robot=`Bone_*` 側なし・Pre/Lower/Middle/Upper）。
- **参照コピー式リターゲット = `HandRetarget.ApplyFromReference`**: `BuildExternalHand` が**隠し Meta 白手**
  （authored bind・Renderer 無効・駆動系除去）を同コンテナに生成し、毎フレーム live ローカル回転をそこへ
  流し込み（＝Default 白手と同一の正解系）、実ワールド回転 × 定数オフセット `C_i=inv(metaWorld_i)*packWorld_i`
  をパック bone にコピー。**リグの軸規約・階層構造・bind 規約の仮定ゼロ**。生える向きは幾何フレーム
  （指方向×甲法線・`WristFrame`）で白手 authored に整列。BuiltHand.Instance は**コンテナ**（pack+MetaRefHidden・破棄1個）。
- **⚠ 旧式A（`HandRetarget.Solve`）はパック手に使ってはいけない**（2026-07-11 実機実害: 手首から後ろ向きに
  生える＋Robot 右曲がり/Realistic 逆曲がり）。数値実測の教訓: (a) layout.BindLocalRot は live の中立ではない
  （pinky0 で ~173° 乖離）、(b) layout.ParentIndex の FK とメッシュ実ワールド回転は中間ノードで ~100° 乖離。
  →「白手そのものを駆動して実測参照」が唯一堅牢。Solve は RemyAvatarRig の指（OVR→mixamo・実機検証済み）にだけ残存。
- 配置/スケール/材質 = `RemoteHandMeshProvider.BuildExternalHand`（手首を親原点整列、手首→中指遠位を
  `RefHandLenMeters=0.15m` に自動スケール、URP/Lit 肌/金属材質で全 Renderer 上書き＝パック Standard 材質のマゼンタ回避）。

**状態（2026-07-11 Editor 数値+目視検証済み・実機再確認待ち）**: `Preview Hand Variants (screenshot)` が
`Temp/HandVariantPreview/directions.txt` に指方向/甲法線の Default との角度差を出す（一次証拠）。
刷新後 Δfwd=0°・Δup≈10°・Δlat≈20°（式A 時代は 102°/74°）・多角度スクショで 3 種同ポーズ確認。
**⚠ Robot を一度「崩れてる」と誤判定したが視点の錯覚だった** — 機械リンクが嵩張り斜め/正面では重なって散って見えるが、
**上面(`robot_top`/`03_top`)では掌+4指+親指がポーズどおり並ぶ普通のロボットハンド**。多角度は `Preview Robot Only` メニュー（背景の手を排し周回）。
**ワールド空間FKリターゲットは試したが撤去**（Robotの見え方改善を狙ったが working だった Realistic を退行させた。そもそもRobotは式Aで問題なく不要だった）。naive世界FK再挑戦しない。
Robotの指トラッキング精度はフレーム毎の厳密検証は未（機械モデルで判別しづらい）。実機で最終確認。詳細
[docs/table-duo/hand-appearance-variants.md](../../docs/table-duo/hand-appearance-variants.md)「実装済みサマリ」。

**rest（接続直後 ShowAtRest）の向きバグ修正（2026-07-09）**: 外部リグ（Realistic/Robot）の右手が
**手のひら上向き**で出ていた。真因は `RemoteHandView.AlignRestForward` の甲法線 `cross(lateral, fwd)`
（lateral=pinky−index）が**左手でのみ甲側**になる符号だったこと（右手は index が pinky の左＝lateral≈+X で
手のひら側を向く）。`_isRight ? cross(fwd,lateral) : cross(lateral,fwd)` に修正。Default 白手はこの経路を
通らない（固定 euler）ので無症状だった＝「白手は正常・Robot だけ変」に見えるが実は Realistic も同罪、が切り分けの罠。
プレビューでのバリアント確認は `StudyConfig.SelectedHandVariant` を execute_code で一時変更 →
`Preview Hand Role Initial` / `Preview Table + Remy seated` → Default へ復元、で撮れる。

**実機バグ4件の根治（2026-07-10・実機再確認待ち）**: Y 切替で ①指の曲げ軸異常 ②白手二重表示 ③手首90°直交
④ホストに映らない。原因と修正（詳細 [docs/table-duo/hand-appearance-variants.md](../../docs/table-duo/hand-appearance-variants.md)）:
- **①③同一原因**: `LocalVariantHand` が**ライブ手首 bone に identity で吊るし bone0 を未リターゲット**
  → アンカー×bone0.local が二重に乗りパックリグの手首 bind 差が未補正。修正＝検証済み経路と同構造
  「**手アンカー（skeleton.transform）に吊るし i=0 から全 bone を HandRetarget.Solve**」。
  ⚠ 教訓: 手首 bone に吊るす発想は再発しがち — アンカーが正（[TDV-WRIST] で anchor=bone0 位置 delta=0）。
- **②**: `OVRMeshRenderer.Update()`（ConfidenceBehavior.ToggleRenderer）が**毎フレーム白手 SMR.enabled を復活**
  させる。SMR を切るだけでは無効 → **OVRMeshRenderer ごと切る**（LocalSelfBody→HandPoseSampler と同手法）。
- **④**: 申告値（_studyFlags bit2-3）がスポーン時1回書き・警告専用だった → 切替時書き直し＋受信側は申告値描画。
  外部リグ降格判定も「受信側 Captured」→「送信元リレー layout」（RemoteHandView.LayoutResolver）に統一
  （PC 観戦ホストで恒久 Default 降格＋毎フレ再構築空回りを修正）。

関連: [[table_duo_study_status]] / [[parallel_projects_isolation]]（本作業は TableDuo 単独・fixedcam 非干渉）。
Setup 編集後は `Tools/FixedCamVr/Setup/Setup TableDuo Scene` 再実行で再配線。
