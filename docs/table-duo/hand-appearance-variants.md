# 手だけアバターの見た目バリアント

作成: 2026-06-29 / **実装: 2026-07-01（3 バリアント切替 実装済み・実機の指の見え方は要確認）**
関連: [study-design.md](study-design.md) / [.claude/plans/2026-06-11_table-duo_study.md](../../.claude/plans/2026-06-11_table-duo_study.md)

手の見た目を 3 種類（Default=Meta 白手 / Realistic=人間の手 / Robot=機械の手）に切り替えられるようにする。
下半分（§0〜§6.5）は実装前のリサーチ／設計メモ。**実装の実体は次の「実装済みサマリ」を正とする**。

---

## 実装済みサマリ（2026-07-01）

**採用モデル**: ユーザーが購入した **VR Hands Starter Pack (Left & Right HANDS)**（Unity Asset）。
`Assets/TableDuo/ThirdParty/VRHandsStarterPack/` に **Robot Hand / Male Hand のみ**を GUID 保持で抽出済み
（パック全 9 種のうち 2 種。他は未インポート）。
- **Realistic = Male Hand**（Low Poly 使用）／ **Robot = Robot Hand**（Black 使用）／ **Default = 従来の Meta 白手**。
- §6.5 で本命候補にしていた Marcin 無料モデル / Handy Hands は**不採用**（購入品で確定）。

**切り替え方法（両対応）**:
- 起動フラグ `tdv_hand`（intent extras / コマンドライン）: `default` / `realistic`（=male/human/skin）/ `robot`。調査は 1 セッション 1 種固定に使う。
- 実機トグル: **左コントローラ Y（Button.Two / LTouch）**で Default→Realistic→Robot 巡回（[`HandVariantWatcher`](../../Assets/TableDuo/Scripts/Net/HandVariantWatcher.cs)）。ハンドトラッキング中は発火しない（設営・お試し用）。
- Editor 既定は `ConnectionManager.studyHandVariant`（フラグがあればフラグ優先）。
- ネット非同期＝各クライアントのローカル表示選択。2 台調査では両端末を同じフラグで起動する。

**適用範囲**: 自分の手（[`LocalVariantHand`](../../Assets/TableDuo/Scripts/Net/LocalVariantHand.cs)）＋相手の手（[`RemoteAvatarView`](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs)）の両方。

**駆動方式（別リグ命名への対応）**: パックの手は Meta の `b_*` とは別命名・別バインドなので、同期される
OVR 24bone をそのまま当てると指が壊れる。対策 2 つ:
1. **BoneId→bone 名対応表**（[`HandVariantTable`](../../Assets/TableDuo/Scripts/Hands/HandVariantTable.cs)、実リグを Unity で実測して作成）。
   - Male: 側サフィックス `.R`/`.L`。各指 `Pre`(中手骨)→`Lower`(基節)→`Medium`(中節)→`Upward`(末節)。親指 3 本・手首=`Root`。
   - Robot: 側サフィックス無し（`Bone_*`）。各指 `Pre`→`Lower`→`Middle`→`Upper`（親指 4 本）・手首=`Bone_Hand`。
2. **バインド差分リターゲット**（[`HandRetarget`](../../Assets/TableDuo/Scripts/Hands/HandRetarget.cs)）:
   `target = liveLocal * inv(ovrBind) * varBind`。ovrBind は受信側/ローカルの手 bind、varBind は生成時に控えたメッシュ側 bind。

**配置・スケール・材質**（[`RemoteHandMeshProvider.BuildExternalHand`](../../Assets/TableDuo/Scripts/Net/RemoteHandMeshProvider.cs)）:
パック手は原点からオフセット・巨大スケールなので、手首 bone を親原点へ整列し、手首→中指遠位が
`RefHandLenMeters=0.15m` になるよう自動スケール。パック同梱の Standard 材質は URP でマゼンタ化するため、
**URP/Lit の肌/金属材質を生成して全 Renderer を上書き**（材質変換不要）。Setup が
`TableDuoRealisticHand.mat` / `TableDuoRobotHand.mat` を生成し provider に配線する。

**PC 検証結果（2026-07-01・`Tools/FixedCamVr/Diagnostics/Preview Hand Variants` で実録画データを 3 種に適用しスクショ）— 3 種とも実用的に動作**:
- **Default（Meta 白手）: 正常**。指の曲がり・スケール・配置 OK。
- **Realistic（Male Hand）: 正常**。実データの指ポーズを自然に再現、肌 URP 材質でマゼンタ無し、スケール適正。
- **Robot: 正常（式 A で動く）**。**⚠ 初見で「崩れてる」と誤判定したが、実際は視点の問題だった**
  — Robot は多数の機械リンク（アクチュエータ様の平板）を持つ嵩張るモデルで、斜め/正面からはパーツが重なって
  散らばって見える。**上面（`robot_top` / `03_top`）から見ると掌プレート＋4 指＋親指がポーズどおり並ぶ普通のロボットハンド**。
  指は実ポーズに追従し、Default の指配置とも整合する。多角度・Robot 単体の寄り確認は
  `Preview Robot Only` メニュー（背景の手を排して周回撮影）。

**リターゲットの結論**: `HandRetarget.Solve`（式 A = `live·inv(ovrBind)·varBind`）で 3 種とも成立。
- **世界空間 FK リターゲットは試したが撤去**（Robot の見え方改善を狙ったが working だった Realistic を退行させた。
  そもそも Robot は式 A で問題なかったので不要だった）。naive な世界 FK は再挑戦しない。
- Robot の指トラッキング精度はフレーム毎に厳密検証はしていない（機械モデルで判別しづらい）。気になれば
  握り拳など明確なジェスチャーのフレームで `Preview Robot Only` を撮って確認する。実機での最終確認は別途。

**実機（Quest）でさらに確認する点**: ローカル手の手首の向き（親追従のバインド軸ずれ）・手の大きさ（`RefHandLenMeters`）。

**設定変更時**: `TableDuoSceneSetup` を編集したら `Tools/FixedCamVr/Setup/Setup TableDuo Scene` を再実行して
シーンに焼き直す（provider の変種参照・`LocalVariantHand`・`HandVariantWatcher` を再配線）。

---

## 0. 一番重要な前提：見た目は「調査変数」

手の見た目は美的選択ではなく **対人知覚に直接効く実験操作**になる。先行研究：

- **リアルな人間の手 = 不気味の谷が最も強い**。「生物的な見た目」×「ハンドトラッキングの非生物的ジッター」のミスマッチが谷の主因。
  義手のようなリアル系は、解剖学的・ロボット的な手より同ポーズでも eerie と評価される（Tinwell ほか / Saygin ほかの kinematics ミスマッチ説）。
- **Meta 公式ガイドも outline / cartoon hands を標準**とし、passthrough でのアバターハンドは谷リスクで非推奨。
- **非人間（ロボット）アバターは低 experience / 低 agency を帰属されやすい**（mind perception 研究）。
  → これは RQ3「手のどこに目があると思うか」「手にどれだけ意図を読むか」と直結する **意図的に操作したい変数**になりうる。
- 指の本数を減らすとリアル手は presence が落ちるが、抽象手は落ちない（多少のデフォルメに強い）。

→ **結論**: 見た目は between-condition の操作として扱い、1 セッション 1 種に固定する。RQ3/RQ4 の解釈時に「どの見た目だったか」を必ず記録する。

## 1. 3 バリアントの位置づけ

| バリアント | 知覚上の狙い | 不気味の谷 | 実装コスト | 調査での使いどころ |
|---|---|---|---|---|
| **Simple（現状）** | 匿名・無機質・最小手掛かり | 低（抽象） | ゼロ | ベースライン条件。視線/目の位置が最も曖昧 |
| **Robot** | 非人間・機械・低 agency | 低 | **低** | agency 帰属の対比条件。谷を踏まず安全 |
| **Human** | 高 agency・親近感 | **高** | 高 | 拡張条件。やるなら stylized 肌止まり |

推奨優先度: **Robot を最初に作る**（コスト最小・知見的に美味しい）→ Human は最後（交絡＋高コスト＋谷リスク）。

## 2. アーキテクチャ前提（コード調査で確定済み・2026-06-29）

- **ポーズ駆動と見た目は完全分離**。同じボーン回転配列（24 bone）がどの形状の手も動かす。
  見た目を差し替えても `HandPoseSampler` / `PoseCodec` / `RemoteAvatarView.HandView.Tick` は**無改修**。
- **見た目タイプはネット同期されない＝ローカル表示の選択**。各クライアントが独立に選べる。
  調査では「人側クライアントが見る相手の手」を起動フラグで固定するのが自然。
- 差し替えフックは 3 箇所だけ（いずれも `Assets/TableDuo/`、`FixedCamVr.*` 非依存）：

| # | フック | ファイル | 変更内容 |
|---|---|---|---|
| ① | リモート手 prefab/material 供給 | `Scripts/Net/RemoteHandMeshProvider.cs` | `GetPrefab(isRight)` / material を variant 別に拡張 |
| ② | リモート手メッシュ構築 | `Scripts/Net/RemoteAvatarView.cs`（`TryBuildMeshHand`） | variant を受けて prefab 選択 |
| ③ | ローカル手 material 割り当て | `Scripts/Editor/TableDuoSceneSetup.cs`（`AddHand` 行 570 付近） | ハードコード mat を variant 選択へ |
| ④ | カプセルフォールバック見た目 | `Scripts/Net/RemoteAvatarView.cs`（プリミティブ生成） | **Robot の本体に転用可**（後述） |

## 3. 各バリアントの実装方針

### Simple（現状維持）
- Meta `OVRCustomHandPrefab_L/R` のスキンドメッシュ + `TableDuoLocalHand.mat`（肌色）。
- 変更なし。ベースライン。必要なら半透明ゴースト material 化も低コスト。

### Robot（推奨・低コスト）
- **既存のカプセルフォールバック経路を主役に昇格させる**のが最安。
  `RemoteAvatarView` は bone 階層にプリミティブ（球/カプセル）を生成する経路を既に持つ。
  → セグメント形状（箱/円柱）＋金属マテリアル＋関節にエミッシブのアクセントを当てるだけ。**メッシュのリグ不要**。
- material: URP/Lit、金属灰（metallic 高 / smoothness 中）＋ 関節 emissive（シアン等）。
- 利点: ボーン名マッピング問題を回避（プリミティブは階層から直接組む）。谷を踏まない。

### Human（最後・要注意）
- OVR ボーン名規則（`b_<L|R>_wrist` … `HandBoneTable` 参照）にリグされた肌メッシュが必須。
  外部素材（TurboSquid / CGTrader 等）は **ボーン名が合わず再リグ前提**。
- リアル過ぎると谷＋調査交絡 → **stylized 寄りの肌**（軽い SSS、控えめなディテール）に留める。
- 優先度最下位。Robot/Simple が固まってから着手。

## 4. 切り替え UI / 起動フラグ案

- `StudyConfig` に `enum HandVariant { Simple, Robot, Human }` と `static HandVariant SelectedHandVariant` を追加。
- 起動時指定: `tdv_role` と同じ adb extras 方式（例 `-e tdv_hand robot`）。1 セッション 1 種で固定＝調査運用に最適。
- 実機での即時切り替えが欲しければ、後付けでオペレータ操作（左コントローラ等）から `SelectedHandVariant` を変える経路も足せる（同期不要なのでローカルで完結）。

## 5. 未解決の人間判断ポイント

1. **3 種を「調査条件」として使うか / 単なる演出オプションか**。前者なら N とカウンターバランス設計に影響（study-design 改訂が要る）。
2. **Robot/Human を「相手にだけ」見せるか、自分のローカル手にも反映するか**。身体所有感（self-hand と co-embodied hand の一致が ownership を上げる）に効く。
3. Human を作る場合のメッシュ調達（自作リグ / Blender で OVR ボーン名にリネーム / 既存無料素材の再リグ）。
4. 左手非表示・頭マーカー無しの既定条件と見た目バリアントの組み合わせ爆発をどう絞るか。

## 6.5 モデル調達の調査結果（2026-06-29）

「ネット上のモデルをそのまま使いたい」を受けた調査。判定基準は **Oculus Hand スケルトン（`b_*` ボーン名）にリグ済みか** と **FBX 等エンジン非依存のソースが含まれるか**。

| モデル | 案 | 価格/ライセンス | Unity 可否 | 判定 |
|---|---|---|---|---|
| Oculus Quest Hand Tracking Realistic Texture（Marcin / Sketchfab） | 人間的 | 無料 Free Standard | **可**（unitypackage + 左右 FBX + .blend 同梱・4,600 tris） | **本命**。ボーン名が `b_*` か取り込み時に要確認（Quest 対応謳いなので可能性大） |
| Hand Tracking Asset Bundle（Fab）| 3案 | Personal ¥3,234 / Pro ¥19,414 | **不可（UE 専用）** | **買わない**。配布が `.uasset`/`.uproject` のみ＝FBX 無し。Unity 化は UE 経由 FBX 書き出し＋再リグ＋再マテリアルでROI最悪。Fab メタの assetFormats に unreal-engine しか無いことを確認 |
| **VR Hand Models Mega Pack "Handy Hands"（Unity AS / 200607）** | **3案** | 約 $15 | **可（URP/HDRP/Built-in 全対応）** | **Asset Store の本命**。8種×左右（Robot/Stylized/Male/各種グローブ）＝**1個で3案カバー**。最小 4,324 tris・2048²。弱点＝汎用 VR リグで "NO CONTROLLER SYSTEM"＝**Oculus 手スケルトンへ再リグ要の公算大**（import 後にボーン名 `b_*` を確認） |
| Animated Hands with Gloves + HDRP（Unity AS / 48520）| 人間/グローブ | $13.50 | **不可** | **HDRP 専用**（URP でシェーダ全ピンク）＋ **"Not VR-Ready"・ハンドトラッキング非リグ**（焼き込みアニメ）。再リグ＋URP移植で Marcin 無料より高コスト |
| VR Hands and FP Arms Pack（Unity AS / 77815）| 人間/グローブ | 有料 | △ | **非推奨**。FP（一人称）アーム＋コントローラ用 Mecanim。前腕付き・ハンドトラッキング非リグ |
| TurboSquid / CGTrader robot hand | ロボット | 無料〜有料 | 要再リグ | Oculus 命名でない＝Blender で再リグ前提 |

**購入判断の3条件（全部満たすこと）**: ① URP 対応（or マテリアル自作前提）② Oculus/Meta Hand スケルトンにリグ済み（"hand tracking ready"。単なる "VR"/"rigged"/"animated" は不可。FP アーム≠ハンドトラッキング手）③ FBX ソース付き（`.uasset` 専用・HDRP 専用は地雷）。

**結論**: 人間=Marcin 無料モデルで確定候補 / ロボット=セグメント自作が最善（モデル調達不要）/ シンプル=現状維持。
有料品は上記3条件で機械的に弾く。

## 参考（リサーチ）

- Meta Horizon OS — Hand representation（outline/cartoon 推奨、passthrough で realistic は谷リスク）
- 不気味の谷 × アバター手の embodiment（NCBI PMC6911036: sight/touch の virtualization 不一致で revulsion）
- 義手の eeriness（lifelike prosthetic > anatomical/robotic）
- 社会的 VR のアバター appearance と presence / agency 帰属（robot vs human-like avatar の trust game 研究 等）
</content>
</invoke>
