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
（`HandVariantWatcher`）。Editor 既定は `ConnectionManager.studyHandVariant`。ネット非同期＝ローカル表示選択
（2台調査は両端末を同フラグ起動）。適用は自分の手（`LocalVariantHand`）＋相手の手（`RemoteAvatarView`）両方。

**駆動のキモ**: パック手は Meta の `b_*` と別命名・別バインドなので、同期 OVR 24bone を直当てすると指が壊れる。
- BoneId→bone名 対応表 = `HandVariantTable`（Unity で実リグ実測して作成。Male=`.R/.L`・Pre/Lower/Medium/Upward、
  Robot=`Bone_*` 側なし・Pre/Lower/Middle/Upper）。
- バインド差分リターゲット = `HandRetarget.Solve(live, ovrBind, varBind)`。ovrBind は手 bind（layout/skeleton.BindPoses）、
  varBind は生成時のメッシュ bind。Default は bind 一致なので従来通り直接代入（回帰なし）。
- 配置/スケール/材質 = `RemoteHandMeshProvider.BuildExternalHand`（手首を親原点整列、手首→中指遠位を
  `RefHandLenMeters=0.15m` に自動スケール、URP/Lit 肌/金属材質で全 Renderer 上書き＝パック Standard 材質のマゼンタ回避）。

**状態**: コンパイル OK（run_tests 6/6）・Setup 焼込済み・**実機未検証**。実機で見る点＝①指の曲がり軸②手首の向き
③手の大きさ。ずれたら `RefHandLenMeters`/`HandVariantTable`/変種別軸補正で調整。詳細
[docs/table-duo/hand-appearance-variants.md](../../docs/table-duo/hand-appearance-variants.md)「実装済みサマリ」。

関連: [[table_duo_study_status]] / [[parallel_projects_isolation]]（本作業は TableDuo 単独・fixedcam 非干渉）。
Setup 編集後は `Tools/FixedCamVr/Setup/Setup TableDuo Scene` 再実行で再配線。
