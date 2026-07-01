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

**状態（2026-07-01 PC 検証済み）**: `Tools/FixedCamVr/Diagnostics/Preview Hand Variants`（実録画 tdv_handrec_real を3種に当てスクショ・Play不要）で確認。
**Default=正常 / Realistic=正常**（指ポーズ自然・肌材質OK・スケール適正）。**Robot=指を動かすと崩れる**（静止bindは正常）。
**ワールド空間FKリターゲットを試したが撤去**（Robot の爆発は減るが working だった Realistic を爪状に退行させ、
手首 bone が約90°別向きに焼かれてる問題も残った。手首整列を足すと Realistic をさらに破壊）→ **式A(`HandRetarget.Solve`)に確定**
（Realistic/Default は式Aが最良、世界FKは naive 再挑戦しない）。Robot 根因＝機械リグ（各指が独立剛体・軸バラバラ＋手首別向き）で
クォータニオン転写と本質的に不適合。現実解＝**Blenderで Robot リグ再調整**（指軸+手首を OVR/Male へ）or Robot専用1DOF屈曲駆動 or 静止指妥協。
現状 Robot は selectable だがスクランブル（既知の未完）。`Preview Hand Variants` メニューで実データ検証可。詳細
[docs/table-duo/hand-appearance-variants.md](../../docs/table-duo/hand-appearance-variants.md)「実装済みサマリ」。

関連: [[table_duo_study_status]] / [[parallel_projects_isolation]]（本作業は TableDuo 単独・fixedcam 非干渉）。
Setup 編集後は `Tools/FixedCamVr/Setup/Setup TableDuo Scene` 再実行で再配線。
