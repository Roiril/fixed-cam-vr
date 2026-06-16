# TableDuo 人側アバターを Remy（Mixamo フルボディ）に置換 — 設計

状態: **設計のみ（未実装）**。実装は本書を承認後に着手。
対象: TableDuo の **人側（Full / Seat0）アバター** だけ。手側（Hand）・廻リ視には触れない。

## 0. ゴールと制約

ユーザー要望:
- 人側を `Remy.fbx`（Mixamo 標準リグ）にする。**椅子に座らせる**。
- **胴体は固定**でよい（体幹トラッキングは無いので動かさない）。
- **手のポーズは厳密**に（指まで）。
- **手・頭・腕だけ IK** で確からしく。**肘の曲げは IK 推定で“そこそこ”**でよい。

利用可能な入力（既存 `AvatarPose`・受信側で参照）:
- 頭 pose（位置＋回転、席ローカル）
- 両手首 pose（位置＋回転、席ローカル）
- 両手の指 24 bone 回転（OVR Hand skeleton, `BonesL/R`）+ `TrackedL/R`（ロスト）
- **肩・肘・体幹・脚のトラッキングは無い** → これらは IK 推定 or 固定で作る

→ 結論: **座位の体幹・脚は固定ポーズ**、**頭=受信回転で駆動**、**腕=肩固定の2ボーン IK で手首ゴールへ**、**指=受信 bone を Remy 指へリターゲット**。

## 1. データフローと差し込み位置

人側アバターは **相手（手側）の HMD 上で `RemoteAvatarView` が描画**する。駆動データは受信した人側プレイヤーの `AvatarPose`（`_target`）。
→ Remy 化は **受信側 `RemoteAvatarView` の Full 経路の置換**。手側のローカル描画やネット仕様は不変（追加の同期は不要・既存 pose で足りる）。

実装方針:
- `RemoteAvatarView.Create(seat, handsOnly:false)` の Full 経路を、**Remy プレハブが供給されていれば humanoid 駆動**、無ければ**現行の procedural 簡易人型にフォールバック**（堅牢性・preview 継続）。
- humanoid 駆動は新クラス `RemyAvatarRig`（`TableDuoVr.Net`）に隔離。`RemoteAvatarView` は「Full の中身」をこれに委譲。
- プレハブ供給は `RemoteHandMeshProvider` と同型の `RemyAvatarProvider`（Systems 上・Setup が配線）、または `Resources.Load<GameObject>("RemyFullAvatar")`。**Resources 方式を採用**（preview/replay でも provider 不在で引けるため）。

## 2. アセット取り込み（Phase 0）

- `C:\Users\kouga\Downloads\Remy.fbx` → `Assets/ThirdParty/Mixamo/Remy.fbx`。
- Import: **Rig=Humanoid**（Avatar 自動生成）。bone GameObject 名は `mixamorig:*` のまま残るので**名前で参照可**。
- マテリアルを **URP/Lit へ変換**（Edit > Rendering > Materials > Convert、または手動）。実機マゼンタ回避（[[unity_pitfalls]] 既知）。テクスチャ展開。
- スケール検証: Mixamo は cm 基準で来ることが多い。Import の Scale Factor で実身長 ~1.6–1.7m になるよう調整（Hierarchy で Hips→Head の実寸を測る）。
- **プレハブ化**: `Assets/TableDuo/Resources/RemyFullAvatar.prefab`（SkinnedMeshRenderer + bone 階層）。
- **ライセンス**: Mixamo（Adobe）はプロダクト内利用はロイヤリティフリーだが**素体の単独再配布は不可**。`Assets/ThirdParty/Mixamo/License.txt` に出典明記。リポにコミットしてよいか実装前にユーザー確認（27MB のバイナリ追加のため）。

## 3. 配置（椅子に座らせる）

- `RemoteAvatarView`（Remy インスタンスの親）は **Seat0 アンカー（目線基準・y=1.15）** 配下。
- Remy をインスタンス化し、**Head bone のワールド位置 ≈ Seat0 原点**になるよう root(Hips) の localPosition を下げる（Hips ≈ 椅子座面高 ~0.45m を狙う。Chair0 は Setup が y=0 床に配置済み）。
- 体の正面（+Z）= 席 forward に合わせる（Seat0.rotation で親が向く）。
- 椅子へのめり込み/浮きは preview で詰める（座面高・尻位置の微調整）。

## 4. 座位の固定ポーズ（体幹＋脚）

トラッキングが無いので**起動時に1回だけ bone ローカル回転を座位へ設定して固定**（毎フレーム更新しない）。外部アニメ DL は不要（依存を増やさない方針）。近似初期値（preview で調整）:
- `UpLeg`（左右）: 股関節を前へ ~85°（太ももほぼ水平）
- `Leg`（膝）: ~ -85°〜-90°（すね垂直・足を床へ）
- `Foot`: 足裏を床と平行に ~ +5〜15°
- `Spine/Spine1/Spine2`: ほぼ直立、わずか前傾（合計 ~5〜10°）
- `Hips`: わずか後傾（着座の骨盤）
- 肩 `Shoulder`: ニュートラル（腕 IK の根本。固定）

→ これらは「胴体固定」要件そのもの。以後 IK は**腕・頭・指のみ**を上書きする。

## 5. 頭の駆動

- `Head` bone のワールド回転 = 受信 `HeadRot`（席ローカル→ワールド）。自然さのため **Neck に一部を分配**（例: Neck 35% / Head 65%）。
- **位置は固定**（胴体固定なので頭は回転のみ）。受信 `HeadPos` の並進は無視（要件どおり）。必要なら将来 Neck にわずかな lean を足す拡張余地。
- ロスト時（頭は常時 tracked 想定だが）受信が止まれば最後の回転で保持。

## 6. 腕の IK（肩固定の2ボーン解析 IK・“そこそこ”）

各腕、毎フレーム LateUpdate（座位固定ポーズの後）で解く。
- チェーン: `Arm`(上腕) → `ForeArm`(前腕) → `Hand`(手首)。**根本=Arm の位置は肩(Shoulder,固定)で決まり固定**。
- ゴール: 手首ワールド位置 = `Seat0.TransformPoint(WristPos)`、手首ワールド回転 = `Seat0.rotation * WristRot`。
- 解法（2ボーン解析・law of cosines）:
  1. `d = clamp(|goal-shoulder|, |l1-l2|+ε, l1+l2-ε)`（l1,l2=上腕/前腕長, bind から取得）
  2. 肘角を余弦定理で算出、上腕を goal 方向へ向け、**pole(肘ヒント)** 方向の平面で曲げる。
  3. `Hand` のワールド回転 = 受信 WristRot（前腕の後に上書き）。
- **pole(肘ヒント)**: 座位で手は前方机上 → 肘は**下・やや外側・後ろ**へ（右: down+(+x)+(-z) 寄り / 左: 鏡像）。固定ベクトルで“そこそこ”十分。
- 到達不能（手が体から離れすぎ）は d クランプで腕を伸ばし切る（破綻しない）。
- 肩(Shoulder)は基本固定。大きなリーチ時だけ微回転を足すのは**任意の後段拡張**（まずは無し）。
- FinalIK 等の有料/外部依存は使わない（自前解析 IK・~40行）。Mecanim 組み込み IK（`OnAnimatorIK`）は AnimatorController+ベースアニメが要るので**不採用**（自前の方が依存少・決定的）。

## 7. 指のリターゲット（厳密）

受信 OVR 24bone を Remy の指 bone へ。**OVR と Mixamo は bind 軸が違う**ため直接コピー不可 → **bind 相対リターゲット**。

マッピング（OVR BoneId → Mixamo, 片手分）:
| OVR | Mixamo |
|---|---|
| Thumb1/2/3 | Thumb1/2/3 |
| Index1/2/3 | Index1/2/3 |
| Middle1/2/3 | Middle1/2/3 |
| Ring1/2/3 | Ring1/2/3 |
| Pinky1/2/3 | Pinky1/2/3 |
| Thumb0(CMC)/Pinky0(中手) | 無し → Thumb1/Pinky1 に畳むか無視 |
| Wrist/ForearmStub/各 tip(19-23) | 指描画には使わない（手首は IK 側, tip は端点） |

各関節 j の式:
```
Δ_ovr  = inv(Bind_ovr_local[j]) * Cur_ovr_local[j]      // OVR 局所での bind からの差分
Δ_remy = B[j] * Δ_ovr * inv(B[j])                        // 基底変換（OVR局所→Remy局所）
R_remy_local[j] = Bind_remy_local[j] * Δ_remy            // Remy 局所回転を確定
```
- `B[j]`: OVR 局所軸→Remy 局所軸の固定回転。**両 skeleton の bind ワールド向きから起動時1回**算出（OVR=`HandSkeletonLayout.BindLocalRot`/親子構成, Remy=プレハブの bind）。
- これが「厳密な指」の本筋。**実装上いちばん難所**（handedness・軸規約でズレやすい）→ preview で1指ずつ検証して固める。

**フォールバック / 保険（厳密性を担保したい場合）**: Remy の手を隠し、**既存の Meta 白手メッシュ**（指が完全同期済み・[[ ]] `RemoteHandMeshProvider`）を手首に装着。指の厳密性は確実に出るが、白手が Remy の肌と不一致 → **肌トーンへマテリアル tint** で緩和可。
→ 推奨: **まずリターゲットを正攻**（視覚的に一体）。難航したら Meta 手装着へ即フォールバック（疎結合に作っておく）。

## 8. ロスト/凍結・ネット・性能

- ロスト時は現行同様 **最後の姿勢で凍結**（腕 IK も最後のゴールを保持／スナップしない）。区間は SessionLogger 記録済み。
- 追加のネット同期は**不要**（既存 pose で足りる）。受信は手側デバイスのみが Remy を描く（人側は自分の体を一人称では描かない＝現状踏襲）。
- 性能: Remy は SkinnedMeshRenderer 1体（Mixanim 標準 ~7–15k tris 程度）。最大2人・実際は手側が人側Remyを1体描く。IK は三角関数数十回/フレーム、指リターゲット 15関節×2。90Hz 余裕。`updateWhenOffscreen=true`。

## 9. プレビュー / テスト

- `TableDuoAvatarPreview`（既存スクショツール）を拡張: Remy プレハブを直接ロードして隔離 URP 描画。座位・腕 IK・頭向きを**正面/3-4/側面/ジェスチャー**で PNG 化 → わたしが Read で目視検証して詰める。
- 指リターゲットの検証は OVR bind が要る → preview では **代表的な手ポーズ（録画 or 合成 bind+既知curl）** を流すか、Meta 手装着版で見た目確認。
- 純計算（2ボーン IK・基底変換）は **EditMode テスト**化（既存 `RigRecenter` と同様、Transform 構築で到達点・角度を assert）。

## 10. わたしの決定（委任により確定。異論あれば差し替え）

1. **自前2ボーン解析 IK**（外部 IK 依存なし・AnimatorController 不要）
2. **座位＝起動時1回の bone 回転で固定**（外部アニメ DL 不要）
3. **指＝bind 相対リターゲットを正攻、Meta 手装着を疎結合フォールバック**
4. **Resources 経由でプレハブ供給**、Remy 不在時は **現行 procedural 人型へフォールバック**（preview も継続）
5. 体の上書きは **腕・頭・指のみ**、体幹・脚は固定（要件どおり）

## 11. 実装フェーズ（承認後）

- **P0 取り込み**: FBX import(Humanoid)/URP 変換/スケール/プレハブ化/ライセンス。リポ追加の可否確認。
- **P1 骨格**: `RemyAvatarRig` 生成・座位固定ポーズ・椅子フィット・頭駆動。preview で姿勢確認。
- **P2 腕 IK**: 2ボーン IK + pole。EditMode テスト + preview。
- **P3 指**: bind 相対リターゲット（or Meta 手装着）。1指ずつ preview 検証。
- **P4 仕上げ**: ロスト凍結・フォールバック・材質・性能・実機確認依頼。doc 同期（README/memory）。

## 12. リスク / 未決

- **指リターゲットの軸合わせ**が最大リスク（ズレやすい）。保険に Meta 手装着を常備。
- 椅子と尻/腿の貫通・浮き（座面高と Remy 比率の調整、preview 必須）。
- スケール（Mixamo cm）取り違え → 巨大/極小。import 時に必ず実寸確認。
- 肩固定での大リーチ時に肩が動かず不自然 → 必要なら肩アシスト回転を後段で。
- Mixamo ライセンス: 研究プロトタイプ内利用は可。**リポへの素体コミット可否**はユーザー判断（外部公開リポなら要注意）。
- 人側が**コントローラ保持中**（手動リセット時）は指データが来ない → その間は手が開く/最後の指で凍結。許容。

## 13. 関連
- 既存人側アバター（procedural・フォールバックとして残す）: [RemoteAvatarView.cs](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs)
- 手メッシュ供給（指フォールバック流用元）: [RemoteHandMeshProvider.cs](../../Assets/TableDuo/Scripts/Net/RemoteHandMeshProvider.cs) / 指 BoneId 表 [HandBoneTable.cs](../../Assets/TableDuo/Scripts/Hands/HandBoneTable.cs)
- スクショ検証: [TableDuoAvatarPreview.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoAvatarPreview.cs)
- 席/椅子/Systems 配線: [TableDuoSceneSetup.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs)
