# Remy 上体リーンを「骨盤ヒンジ方式」へ作り直す計画

**status: done**（2026-07-08 実装・実機確認済み。手首/指/リーチ/リーン一式と同コミット）
関連: [table_duo_wrist_anchor_basis](../memory/table_duo_wrist_anchor_basis.md) / RemyAvatarRig.cs（未コミットの現行実装）

## 背景 / 現状

- HMD 頭位置追従（上体リーン）を [RemyAvatarRig.ApplySpineLean](../../Assets/TableDuo/Scripts/Net/RemyAvatarRig.cs) で実装済み（未コミット）。
  背骨3節（Spine/Spine1/Spine2）を回して頭を HMD へ追わせる。配分は均等→「下寄せ 60/25/15」に変更済み
- **実機評価: 背骨を曲げる方式は猫背（姿勢が悪い人）に見える**（2026-07-08 ユーザー指摘）
- Mixamo 公式の着座モーション `Sitting Laughing.fbx` をバイナリ解析した結果（一次証拠）:

| ボーン | 回転量 | 並進 | 役割 |
|---|---|---|---|
| Hips（骨盤） | 44° | 前後 17.6cm・上下 9cm | **主役**: 骨盤ごと前傾＋スライド |
| Left/RightUpLeg | 43°/40° | — | 骨盤の逆回転で足を接地維持 |
| Spine | 18° | — | 下段が中程度 |
| Spine1 / Spine2 | 4.7°/4.6° | — | **ほぼ固定＝背中は真っ直ぐ** |

→ 自然な着座リーンは**背骨曲げではなく股関節ヒンジ**（骨盤前傾＋太もも逆回転）。

## 設計（採用方式）

「頭目標への回転」を次の配分で解く。毎フレ・ベース姿勢から解き直し（既存の累積防止パターン踏襲）:

1. **Hips を回す（主動・総回転の ~70%）**
   - pivot=Hips 原点。頭→目標の FromToRotation を Hips に前乗せ
   - ⚠ Hips は全ボーンの root。回すと脚も回る → 次で補正
2. **UpLeg 左右を逆回転（接地補正）**
   - Hips に掛けた回転の逆 `Quaternion.Inverse(hipStep)` を LeftUpLeg/RightUpLeg に前乗せ
   - これで太もも以下は世界空間で不動＝足・膝が浮かない/めり込まない
   - Mixamo 実データと同じ構造（Hips 44° vs UpLeg 43° がほぼ相殺）
3. **Spine に残り ~25%、Spine1/2 に ~5%**（背中は真っ直ぐのまま僅かに追従）
4. **Hips 並進はやらない**（席固定の設計。Mixamo の 17.6cm スライドは椅子上の尻ずれ表現で、
   調査アプリでは頭位置合わせは回転ヒンジで十分。必要になったら第2段として検討）

既存の他要素は不変:
- 鎖骨アシスト（届かない時だけ肩を差し出す）は維持
- 手首ツイスト分散（前腕 50%）は維持
- 上限角 MaxLeanDeg=45° は総回転に対して維持
- firstPerson の -4cm 目標補正（_leanTargetOffset）は維持

## 実装手順

1. RemyAvatarRig に `_hips`（`mixamorig:Hips`）と `_lUpLeg/_rUpLeg`（`mixamorig:LeftUpLeg/RightUpLeg`）の
   Transform + ベース localRotation を追加（ApplySeatedPose **後**に capture — 座位ポーズが脚を回すため）
2. `ApplySpineLean` を改名 `ApplyHipHingeLean` に書き換え:
   - リストア: hips/upLegs/spine0/1/2 をベースへ
   - pivot=Hips 位置で頭→目標角を測り clamp
   - hipShare=0.70 / spineShare=0.25 / chestShare=0.05 で分配、
     hipStep を UpLeg 2 本へ逆適用
3. 順序は現行どおり Drive 冒頭（腕 IK・頭回転より前）
4. EditMode 37 テスト pass 確認 → APK/desktop ビルド → 2 Quest install → ホスト再起動 → 実機で
   「前に乗り出したとき背中が真っ直ぐか・足が接地したままか・相手から自然か」を確認
5. OK なら手首/指/リーン一式をまとめてコミット（doc-sync: table_duo memory 更新）

## リスク / 注意

- **UpLeg 補正の回転順序**: hipStep はワールド前乗せなので、UpLeg への逆適用もワールド前乗せ
  `upLeg.rotation = Quaternion.Inverse(hipStep) * upLeg.rotation` … だが UpLeg の**位置**は
  Hips 回転で動く（骨盤に付いているので当然）。膝から下の世界位置を完全固定したいわけではなく
  「太ももの向きを保つ」だけで見た目は足りる想定。膝位置の僅かな移動は許容
- Hips が動くと **root 位置基準（頭=席原点合わせ）**は構築時計算のままで OK（bind 時に決めた定数）
- 一人称（selfbody）では骨盤回転で腿が視界に入る量が変わる → 実機で違和感確認
- 45° clamp を超える大きな乗り出しでは追従しきれない（仕様）

## 検証チェックリスト（実機）

- [ ] 前乗り出しで背中が真っ直ぐ（猫背でない）
- [ ] 足が床から浮かない・めり込まない
- [ ] 後ろ反り（背もたれ方向）も不自然でない
- [ ] 手のリーチが前乗り出しで伸びる（肩が前に出る）
- [ ] selfbody（一人称）で腿・胴の見え方に違和感がない
- [ ] 相手プレイヤー視点で人らしい着座姿勢
