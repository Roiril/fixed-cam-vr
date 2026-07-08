---
name: table_duo_wrist_anchor_basis
description: Remy 駆動の全解（手首W=live FK・指=FK位置Aim・骨盤ヒンジ・鎖骨アシスト・手首ツイスト分散）と、その真因調査で確定した OVR 手アンカーの実基準
metadata: 
  node_type: memory
  type: project
  originSessionId: 1da31df7-e8bb-4c10-a796-241b736694dd
---

# Remy（人役フルアバター）駆動の完成形（2026-07-08）

[RemyAvatarRig](../../Assets/TableDuo/Scripts/Net/RemyAvatarRig.cs) は手首・指・リーチ・座位リーンを次の実データ駆動で解く。
いずれも「定数ハードコード / prefab authored 姿勢 / スクショ目視」を基準にせず、**実録画・layout の FK か Mixamo 実アニメ解析**を一次証拠にしたのが要点。

| 課題 | 解 | 一次証拠 |
|---|---|---|
| 手首の向き | `hand.rotation = seat*wristRot*W`。W は **layout骨長×live bone回転の FK** でアンカー基底を毎フレ実測して導出（bind FK 不可＝live bone0 に Y180 定回転あり） | 実録画 FK |
| 手首の候補生成 | 校正定数 ±X/+Y は誤り。既定値は実録画実測（右手 指≈(-0.27,-0.57,0.78)） | 同上 |
| 指の曲がり | **FK 位置ベース Aim**（各 Mixamo 指を FK した OVR 子関節方向へ AimBone）。回転移植（HandRetarget）は**曲げ軸が素手=local X・コントローラ駆動=local Z で 90°違い**破綻する。FK 位置は物理的で規約非依存 | 素手録画 vs 当日replay の曲げ軸比較（X vs Z）を Python 検算 |
| リーチ不足 | 鎖骨アシスト（腕長 95% 超で肩を目標へ最大25°差し出す）＋ 骨盤ヒンジで肩ごと前進 | — |
| 座位リーン | **骨盤ヒンジ**（Hips 70%前傾＋UpLeg 逆回転で足接地維持＋Spine 25%/胸椎 5%）。背骨曲げ方式は猫背に見えた | Mixamo `Sitting Laughing.fbx` バイナリ解析（Hips 44°+前後17.6cm・UpLeg 43°で相殺・胸椎ほぼ0°） |
| 手首ねじれ細り | swing-twist 分解でツイスト 50% を前腕へ分散（手のワールド向きは不変） | — |

計画: [.claude/plans/2026-07-08_remy-hip-hinge-lean.md](../plans/2026-07-08_remy-hip-hinge-lean.md)。
汎用手法「バイナリ資産を自前パースして一次証拠にする」はグローバル `~/.claude/rules/work-style.md` #2 へ昇格済み
（リターゲット位置ベース化はドメイン特化のため本 memory に留置）。

---

# OVR 手アンカー基準の実測（2026-07-07・実機録画から確定）

`TestData/tdv_handrec_real_20260610.bin`（実機録画・layout 同梱）を直接デコードして FK した結果:

- **右手アンカー空間での実ジオメトリ（全フレーム一定）**:
  - 手首→中指付け根の指方向 ≈ **(-0.27, -0.57, 0.78)**（+X から **約104°**）
  - 手の甲法線 ≈ (-0.14, 0.81, 0.57)（+Y から **約36°**）
  - つまりアンカーは掌に整列しておらず**グリップポーズ様に傾いた軸**。「指=±X・甲=+Y」のような綺麗な軸には乗っていない
- **bone0（wrist）の localRotation はランタイムで恒等かつ不変**（アンカーが手に剛体追従するため）
- 左手はこの録画では未トラッキングで基準未実測

## なぜ 2 回の「校正」が両方外れたか
1. 旧 memory「右手 identity=指-X・掌上」→ 誤り
2. b687b81 の白手ゴースト校正「指+X・甲+Y」→ **prefab の authored ボーン姿勢を基準にした**のが誤り。
   ランタイムは同期 bone が authored localRotation を上書きする（bone0 live=identity ≠ authored）ので、
   prefab を素置きした姿は「アンカーから見たランタイムの手」ではない。
   RemoteHandView（白手）が正しく見えるのは root=wristRot に **live bone チェーン全体**を載せるから。

## 教訓
- **アンカー基準の定数をハードコードしない**。W は受信/キャプチャ済み HandSkeletonLayout
  （BindLocalPos だけで knuckle 方向が出る: f0=normalize(pos_middle1)、side=pos_index1-pos_pinky1）
  から実データ FK で毎セッション導出する。左右も自動で正しくなる
- 校正の正基準は「prefab の見た目」ではなく**実録画データの FK**（Python で直接デコード可能、
  フォーマット= PoseRecordingFile.cs）
- 予測: [TDV-REMY] 診断（WireTap 中 1Hz）の fAngle は現実装で **~104° 一定**になるはず（検証可能）
- 指リターゲット HandRetarget.Solve（live*inv(ovrBind)*varBind）は OVR/Mixamo の親相対軸が
  揃っている前提だが、この基準ズレと同程度に揃っていない疑い＝指の曲がり軸ズレの候補

関連: [[table_duo_pc_host_and_wiretap]]（3大バグ根治記録）
