---
name: table_duo_wrist_anchor_basis
description: OVR 手アンカーの実ランタイム基準（指≠±X・グリップ様に傾いた軸）— Remy 手首ズレの真因と、prefab authored ポーズを校正基準にしてはいけない理由
metadata: 
  node_type: memory
  type: project
  originSessionId: 1da31df7-e8bb-4c10-a796-241b736694dd
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
