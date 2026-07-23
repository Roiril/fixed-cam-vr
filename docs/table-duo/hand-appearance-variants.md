# 手だけアバターの見た目バリアント

作成: 2026-06-29 / **実装: 2026-07-01（3 バリアント切替 実装済み・実機の指の見え方は要確認）/ 2026-07-23 FullBody（フル Remy 化）追加**
関連: [study-design.md](study-design.md) / [.claude/plans/2026-06-11_table-duo_study.md](../../.claude/plans/2026-06-11_table-duo_study.md)

手役の見た目を 4 種類（Default=Meta 白手 / Realistic=人間の手 / Robot=機械の手 / **FullBody=人役と同じフル Remy**）に切り替えられるようにする。
下半分（§0〜§6.5）は実装前のリサーチ／設計メモ。**実装の実体は次の「実装済みサマリ」を正とする**。

---

## FullBody（手役のフル Remy 化・2026-07-23）

ホスト卓から手役を**人役とまったく同じ提示仕様のフル Remy アバター**へ切り替える第 4 バリアント。実装は
「人役の既存経路の再利用」であり、Remy 専用の新描画コードは無い:

- **同期**: 既存チェーンをそのまま使う。`HandVariant.FullBody = 3`（`_studyFlags` bit2-3 の 2bit にちょうど収まる）。
  `FacilitatorPanel`（白手/リアル/ロボ/Remy の 4 ボタン化）→ `TableDuoPlayer.ServerForceHandVariant` →
  `_forcedVariant`（server write）→ 手役 owner `StudyConfig.ApplyForcedVariant` → 申告値書き直し → 全端末追従。
- **提示状態の判定は純ロジック [`HandPresentation`](../../Assets/TableDuo/Scripts/Hands/HandVariant.cs) に集約**
  （EditMode テスト `HandPresentationTests` が真理値表を固定）:
  - `RemoteHandsOnly`: 手役でも FullBody 申告中は false → リモート view を `RemoteAvatarView.Create(handsOnly:false)`
    ＝人役と同一の Remy IK 経路で**再構築**（`TableDuoPlayer.BuildRemoteView`。人役・ホスト観戦から顔つきの Remy が見える。
    観戦一人称の頭潰し状態は再構築をまたいで引き継ぐ）
  - `SelfBodyActive`: 手役 FullBody 中は人役と同じ `LocalSelfBody`（頭ボーン scale 0.01 潰し＝自分の顔が視界と干渉しない）
    をローカル pose で駆動。`ShowSelfBody`（tdv_selfbody・既定 on）の規則も人役と共通
  - `SuppressLeftHand`: FullBody 中は片手モード（OneHandMode）でも左手抑制を**解除**＝人役と同じ両手トラッキング。
    戻すと再抑制
  - `WhiteHandVisible`: FullBody 中は白手メッシュ非表示（Remy 手が代替。selfBody off 時のみ白手を残す＝人役と同じ）
- **戻しの完全復元**: owner 側は `TableDuoPlayer.ApplyOwnerPresentation`（冪等リコンサイル）が切替のたびに
  自己ボディ生成/破棄・左手抑制・白手可視を上記述語で整合させる。`LocalSelfBody.OnDestroy` が席下の Remy 実体を明示破棄。
  白手/リアル/ロボへ戻すと従来の手だけアバター挙動（片手・パック手リターゲット）に一致する（`HandPresentationTests.ReturningFromFullBody_RestoresLegacyHandPresentation`）
- **⚠ `HandVariantTable.IsExternalRig` は `Realistic || Robot` 明示**（旧 `!= Default`）。FullBody を external 扱いすると
  パック手構築（prefab 不在）へ流れて失敗するため。ここが唯一の既存挙動に触る変更点で、3 バリアントの挙動は不変
- 起動フラグ: `tdv_hand=remy`（別名 full / fullbody）で初期条件から FullBody にできる
- ピンチ掴み（`PinchGrabInteractor`）は描画非依存なので FullBody でも無変化。左手が有効化されるぶん人役同様に左手掴みも効く
- **⚠ 実機未検証（2026-07-23 実装。EditMode 375/375・コンパイル OK）**。実機確認点: 切替時の見た目遷移・
  手役自身の視界（頭潰し・Remy 手の位置で掴み狙いがズレないか）・戻した後の手だけ挙動の退行有無

---

## 実装済みサマリ（2026-07-01）

**採用モデル**: ユーザーが購入した **VR Hands Starter Pack (Left & Right HANDS)**（Unity Asset）。
`Assets/TableDuo/ThirdParty/VRHandsStarterPack/` に **Robot Hand / Male Hand のみ**を GUID 保持で抽出済み
（パック全 9 種のうち 2 種。他は未インポート）。
- **Realistic = Male Hand**（Low Poly 使用）／ **Robot = Robot Hand**（Black 使用）／ **Default = 従来の Meta 白手**。
- §6.5 で本命候補にしていた Marcin 無料モデル / Handy Hands は**不採用**（購入品で確定）。

**切り替え方法**:
- 起動フラグ `tdv_hand`（intent extras / コマンドライン）: `default` / `realistic`（=male/human/skin）/ `robot`。調査は 1 セッション 1 種の初期条件に使う。
- **セッション中の切替 = ホスト卓 [`FacilitatorPanel`](../../Assets/TableDuo/Scripts/Net/FacilitatorPanel.cs) の巡回ボタン 1 個**（2026-07-21〜）。対象は **手役（Role=Hand）の手だけ**。押すと Default→リアル→ロボ→… を巡回し、`TableDuoPlayer.ServerForceHandVariant` → `_forcedVariant`（server write）→ 手役 owner が `StudyConfig.ApplyForcedVariant`。旧: 左コントローラ Y 巡回（`HandVariantWatcher`）・クライアント別の白手/リアル/ロボ列は**撤去**（コントローラ=視点リセット専用 / UI は手役 1 ボタンのみ）。
- Editor 既定は `ConnectionManager.studyHandVariant`（フラグがあればフラグ優先。優先順位＝ホスト強制 > 起動フラグ）。
- **リモート描画は申告値で同期（2026-07-10〜）**: 自分の手＝ローカル選択のまま。相手（手役）の手は
  「手役端末が `_studyFlags`（bit2-3）で申告したバリアント」で描く（`TableDuoPlayer` が切替時に
  申告値を書き直し → 受信側 `RemoteAvatarView.SetHandVariant` が再構築）。**ホスト卓で手役の手を変えると
  手役本人・人役の視界・ホスト観戦の全端末が自動で同じ見た目になる**（late join も `_studyFlags` 初期同期で一致）。
  人役自身の手は Remy IK 手でバリアント経路を通らず不変。

**適用範囲**: 自分の手（[`LocalVariantHand`](../../Assets/TableDuo/Scripts/Net/LocalVariantHand.cs)）＋相手の手（[`RemoteAvatarView`](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs)）の両方。

**接続時の出現（2026-07-01）**: 手役アバターは**接続した時点で右手を卓上の休めポーズで出現**し、トラッキングが来たら実手位置へスナップして追従する（[`RemoteAvatarView.HandView.ShowAtRest`](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs)）。従来は「一度トラッキングされるまで非表示」で、手が視界外だと手役が丸ごと消えて見えた。左手は従来どおり「使われるまで非表示」（片手モードで左手が出ない）。出現時の手モデルは選択中の変種。

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

**実機バグ4件の根治（2026-07-10）**: Quest 実機で Y 切替時に ①指の曲げ軸異常（Realistic 反り/Robot 横曲がり）
②白手が二重表示 ③手首で白手と直交（指先が甲方向）④ホストに切替が映らない、が発覚。原因と修正:
- **①③＝同一原因**: `LocalVariantHand` がメッシュを**ライブ手首 bone に identity で吊るし**、手首 bone(i=0) を
  リターゲットしていなかった（アンカー回転×bone0.localRotation が階層で二重に乗り、パックリグの手首 bind 差が
  未補正）。→ 検証済み経路（RemoteHandView / Preview）と同じ「**手アンカー（skeleton.transform）に吊るし
  i=0 から全 bone を Solve**」に修正。
- **②**: Meta SDK の `OVRMeshRenderer.Update()`（ConfidenceBehavior.ToggleRenderer）が毎フレーム白手 SMR の
  enabled を復活させる。SMR だけ切っても無効。→ 上流の **OVRMeshRenderer ごと enabled=false**（LocalSelfBody と同手法）。
- **④**: バリアントが「ローカル表示選択」設計で申告値（_studyFlags bit2-3）はスポーン時 1 回書き・不一致警告
  専用だった。→ 切替時に申告値を書き直し、受信側は申告値で描画（上記「リモート描画は申告値で同期」）。
  あわせて外部リグの Default 降格判定を「受信側 Captured」→「送信元リレー layout」に統一
  （手キャプチャの無い PC 観戦ホストで恒久降格＋毎フレーム再構築空回りしていた）。

**実機（Quest）でさらに確認する点**: 修正 4 件の実機再確認・手の大きさ（`RefHandLenMeters`）。

**参照コピー式への全面刷新（2026-07-11〜12・上記「式 A で成立」を更新）**: 実機で「手首から後ろ向きに生える＋
Robot/Realistic で曲げ軸が逆」が発覚し、数値実測で式 A の前提崩壊を確定（layout の bind は live の中立でない:
pinky0 で 173° 乖離／layout FK とメッシュ実階層が 100° 乖離）。→ **隠し Meta 白手を同コンテナに生成して live で
駆動し、実ワールド回転 × 定数オフセット `C_i` をパック bone にコピーする参照コピー式**（`HandRetarget.ApplyFromReference`）
へ変更。式 A（`Solve`）は RemyAvatarRig の指専用に残存。あわせて 2026-07-12 実機指摘 2 件:
- **サイズ**: 固定 `RefHandLenMeters` → **白手リファレンスの実測長**（手首→中指遠位）に合わせる方式へ（Δ0.0cm）。
- **Realistic の親指**: 3 節リグの末節に thumb2 を当てていたのを **thumb3（先端）** に変更（`HandVariantTable`）。
  先端の曲げが白手/Robot と揃う。
検証は `Preview Hand Variants (screenshot)` → `Temp/HandVariantPreview/directions.txt`（指方向/甲法線/親指方向/
実測長の Default との差分）。

**指先の曲がり残留の根治（2026-07-16・指先 tip bone を aim チェーンに追加）**: 実機で
①Realistic（Male）＝指を伸ばしても指先が曲がって見える ②Robot＝つまみで親指が曲がりすぎる、が発覚。
- **原因**: `HandRetarget.FingerChains` が各指 {mcp, mid, distal} で、aim ループが `k < Length-1` のため
  **末節 bone（thumb=5/index=8/…）が aim されず**、親 2 段の aim 回転を無補正でドラッグ継承していた。
  見た目の指先を決める tip（末節の子）が BoneId 0–18 のマップ外で、末節の向きを白手に合わせる手段が無かった。
  加えて Male は authored rest に白手の約 2 倍の指カール（累積 23–27°）が焼き込まれており、末節無補正だと残る。
- **修正**: OVR の指先マーカー **BoneId 19–23（ThumbTip..PinkyTip）をマッピングに追加**し、各指チェーンを
  1 節延長（例 thumb `{3,4,5,19}`）。aim ループの最終段が末節 bone を白手の末節方向へ向ける。
  **tip 自体は回転駆動しない**（aim の方向参照点のみ・`HandBoneTable.IsFingerTip` で参照コピー step から除外）。
  - tip の bone 名: Default=`<side><finger>_finger_tip_marker`（既存 `HandBoneTable`）/
    Male=`Upward<Finger>.R_end`・`.L_end` / Robot=`Bone_<Finger>Upper_end`（左右同名）。いずれも末節 bone の子 leaf。
  - `AvatarPose.BonesPerHand=24` で bone 配列は元々 19–23 を収容済み（`RemoteHandMeshProvider` の配列拡張は不要）。
    live の OVR 回転が 19–23 に来なくても tip は bind のまま追従＝ world 位置は正しい。
- **診断強化**: `directions.txt` に **tipDir（pack vs white の末節→tip 方向角）** を rest/fist 両ポーズで追加
  （従来の mcp→末節 指標では末節 1 節の向きズレを拾えず本症状を見逃した穴を塞ぐ）。
  ポーズは rest / fist / **pinch** の 3 種（症状②がつまみ動作だったため）。
  - pinch 選択は**末節 bone のローカル FK + tip bind オフセット延長**で親指先-人差し指先の最接近フレームを
    選ぶ（録画 tdv_handrec_real_20260610.bin は **tip bone(19-23) の ParentIndex が非解剖学的**に記録されて
    おり、HandLandmarks の FK だと指先間距離が全フレーム定数 2.44cm → 最初の開き手フレームが選ばれる罠）。
  - 撮影向きは**正準化**（手の幾何フレームを「指=上・手のひら=正面カメラ」へ回す）。録画の手首向きに
    依存せず 3 バリアントを同条件比較できる。アングルは front/threequarter/top/back/bottom + yaw±60 の 7 種
    （手のひら正面ビューは屈曲指がカメラ方向へ倒れ遠近短縮で開き手に見える——曲げの比較は top / yaw を見る）。
- **追修正（同日・Male 親指の第一関節折れの分配）**: tip aim 導入後、Realistic だけ rest で親指が
  第一関節（IP）から折れて見えた。原因＝白手の rest 親指は計 19° のカーブを thumb2/thumb3 の 2 関節に
  13°+11° と分散して曲げるが、**Male は 3 節（thumb2 欠け）なので弦 aim の帰結として弦 vs 末節方向の差
  14° が唯一の IP 関節に集中**（しかも Male 末節はスパンの 43% と長く目立つ。白手 27%）。Robot は 4 節で
  分散するため無症状。→ `HandRetarget` の aim で**中間 bone 欠けを跨いだ弦 aim のとき、弦方向と「子の先の
  白手方向」の中間（Slerp 0.5）へ向けて折れ角を親側と半分ずつ分配**。tipDir は全指 0° 維持・Male 親指の
  弦偏差 4°（意図した分配ぶん）・rest 上面で折れ解消を確認。

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

## 4. 切り替え UI / 起動フラグ案（当初案・実装で更新済み）

> ⚠ この節は 2026-06-29 の当初案。実装は上の「切り替え方法」が正。現行は enum `HandVariant { Default, Realistic, Robot }`、
> セッション中切替はホスト卓 FacilitatorPanel の巡回ボタン（手役のみ）で **NGO 同期あり**（当初案の「ローカルで完結」は誤り。
> 手役の見た目は人役・ホスト観戦の全端末へ申告値同期で反映される）。

- `StudyConfig` に `static HandVariant SelectedHandVariant` を追加（当初案どおり実装）。
- 起動時指定: `tdv_role` と同じ adb extras 方式（例 `-e tdv_hand robot`）。1 セッション 1 種で固定＝調査運用の初期条件。
- 実機での即時切替はオペレータ（ホスト卓）操作へ集約（参加者コントローラからは切替不可＝視点リセット専用）。

## 5. 未解決の人間判断ポイント

1. ~~**3 種を「調査条件」として使うか / 単なる演出オプションか**~~ → **決定: 調査条件として採用（2026-07-02・ユーザー確定）**。within-pair 3 ブロック（各ブロック = 1 バリアント固定・`tdv_hand` で再起動切替）、順序はペア間カウンターバランス。ブロック設計・除外基準 → [study-design.md](study-design.md) §2「手の見た目バリアント」/ 運用 → [study-protocol.md](study-protocol.md) §4。
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
