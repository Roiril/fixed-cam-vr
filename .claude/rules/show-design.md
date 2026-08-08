---
name: show-design
description: 廻リ視の体験そのものの仕様と経緯（ゾーン / 位置合わせ / 入力 / 導入・終幕 / 周回 / HMD 文言）。この領域の .cs とシーンを触る前に読む
paths:
  - "Assets/Scripts/Streaming/**"
  - "Assets/Scripts/Tracking/**"
  - "Assets/Scripts/Diagnostics/**"
  - "Assets/Scripts/Input/**"
  - "Assets/Scripts/OvrBridge/**"
  - "Assets/Scenes/**"
---

# 廻リ視の体験仕様

**書いてあるのは「いま何がどう決まっているか」と「なぜそうなったか」。**
Unity の書き方・ビルド・描画の罠は [unity-vr.md](unity-vr.md)、配信と show.json の契約は
[streaming.md](streaming.md) が正本。

## 座標駆動の体験設計（PlayerZone / カメラ自動切替）

体験設計で **HMD 座標は最重要の入力**。Quest 3 の標準ガーディアンを前提に以下を守る：

### 前提
- **実プレイレンジは ±1.3m 程度**（4 畳半 / 2.5–3m 四方の典型値）。これを超える前提でゾーンを置かない。
- HMD 高さは ≈1.6m。`PlayerZone.centerOffset` を 0 のまま運用するなら、ゾーン Transform.y を 1.0–1.2m に置いて `halfExtents.y=2` で全身を含める。
- **ゾーン形状は推測で決めず、実機で `[HudDump]` / `[HmdTrace]` を取って `pos` の min/max からプレイレンジを逆算してから決める**（[`HmdTrajectoryRecorder`](../../Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs) 参照）。

### Pick() の挙動を踏まえた設計
[`PlayerZoneTracker.Pick`](../../Assets/Scripts/Tracking/PlayerZoneTracker.cs) は次の順で評価する：
1. 直近ゾーンが `shrink` 後 AABB に含まれるなら維持（再評価スキップ）
2. それ以外は全ゾーンで full AABB 判定 → `priority` 大が勝ち、同値なら **配列先頭が勝つ**

これを踏まえると：

- **隣接ゾーンを 0.05–0.10m オーバーラップさせる** — `keepLastWhenOutside` に頼って間を埋めるとデッドゾーンで前ゾーン張り付きが起き、ユーザーが「切り替わらない」と感じる
- **Center を配列の先頭に置く** — オーバーラップ帯では Center が勝ち、中央寄りに自然に張り付く
- **`hysteresisShrink` は halfExtents の 20–30% を目安**（hx=0.5m なら 0.10–0.15m）。これより小さいと境界 jitter、大きいと「shrink から出ても full では現ゾーンが勝つ」状態が続いて切替が鈍くなる
- ヒステリシス幅 ≒ `hx_overlap` + `shrink` の効き方の組み合わせで決まる。**手で計算するか、Pick の事前条件をテストで固定する**（[`PlayerZoneSelectionTests`](../../Assets/Tests/Tracking/PlayerZoneSelectionTests.cs) を増やす）

### 既定ゾーン（廻リ視 周回経路・**推測配置**、`MainDemoSceneSetup` 自動配置）

L 字壁（西の腕 + 北の腕、中央）を時計回りに 南→東→北→西 と周回する想定（企画書 図4）。
カメラ C は AABB が矩形のみなので北 + 西の 2 ゾーンに分割（同 cameraIndex=2）。

```
A:South: (0,    1, -0.8)  hx=(1.4,  2, 0.55)  z ∈ [-1.35, -0.25]  cam 0
B:East:  (+0.8, 1, +0.2)  hx=(0.55, 2, 1.2)   x ∈ [+0.25, +1.35]  cam 1
C:North: (-0.2, 1, +0.8)  hx=(1.2,  2, 0.55)  z ∈ [+0.25, +1.35]  cam 2
C:West:  (-0.8, 1,  0)    hx=(0.55, 2, 1.0)   x ∈ [-1.35, -0.25]  cam 2
```

**⚠ 2026-06-11 時点で実測未校正**。パーテーションで L 字壁を組んだら `[HmdTrace]` を取り、
`MainDemoSceneSetup.cs` の値を上書きして `Setup Main Demo Scene` を再実行（冪等）。
再実行は [Tracker] を作り直すが、**`ShowControlClient.zoneTrackerToDisable` は Setup が新 Tracker へ自動で再配線する**
（`MainDemoSceneSetup` 2.7 節。手でアサインし直す必要はない。見つからなければ警告を出す）。

**⚠ 導入演出・終了の暗転を実機で試すなら Setup の再実行が必須**（2026-07-30 実害）。
`IntroDirector` / `IntroVeil` / `IntroStructureWire` / `ShowEndingFader` / `OVRPassthroughLayer` は
**シーンに焼かれた GameObject** なので、コードを実装しただけでは APK に入らない。
シーンの保存日がコードの実装日より古いときは、**ビルド前に必ず焼き直す**
（`grep "m_Name: Intro" Assets/Scenes/Main.unity` で 1 行も出なければ未配線）。

```powershell
.\tools\unity.ps1 menu scene
```

**⚠ ただし「コンポーネントが付いているか」は GameObject 名では確認できない**（2026-07-31 に誤診した）。
`IntroStructureWire` は `IntroDirector` と同じ GameObject に `AddComponent` されるので
`m_Name: IntroStructureWire` は**存在しないのが正常**。`OVRPassthroughLayer` も同様で、シーン YAML には
型名ではなく script guid で記録されるため `grep OVRPassthroughLayer` は 0 件になる。
これを「未配線」と読むと、実際には別にある原因（今回はパススルーの初期化失敗とシェーダの
ビルド剥がれ）を見落とす。**確実なのは実機ログを読むこと** — 今回の 3 件はすべてログに出ていた。

### ゾーン校正の再設計（2026-07-16〜・形状は PC / 位置合わせは HMD 2 点登録）

**旧 ZoneCalibrator（ゾーンを HMD 内でドラッグ・リサイズ・回転）は廃止**。校正を 2 つに分解した（設計 [.claude/plans/2026-07-16_zone-authoring-redesign.md](../plans/2026-07-16_zone-authoring-redesign.md)）：

1. **形状・カメラ割当 = 純データ**（物理レイアウトは固定）。PC の Web オペレータ卓が show.json `layout` を配り、[`ZoneLayoutSolver`](../../Assets/Scripts/Tracking/ZoneLayoutSolver.cs) が矩形 OBB へ決定的に展開 → [`ZoneLayoutApplier`](../../Assets/Scripts/Tracking/ZoneLayoutApplier.cs) が [GeneratedZones] へ生成し tracker.zones を差し替える。**HMD では形状を一切いじらない**。
   - **v2: タイルペイント（grid）モデル**（2026-07-16〜・既定の編集モデル）: フロアを正方タイル（`layout.grid`: tileM/cols/rows/cells）に分割し、各タイルをカメラ index で塗る。Solver は**カメラ毎のタイル集合を貪欲矩形分解**（行方向マージ→行間マージ・決定的）→ 各矩形を `overlapM/2` 全方向拡張して隣接カメラ境界で計 `overlapM` 重ねる。cells は rows 本の文字列（row0=北端 z=+d/2 / col0=西端 x=-w/2、`'0'..'8'`=カメラ / `'.'`=未割当）。不正 cells は警告 + 未割当扱いで例外にしない。
   - **v1: cuts（ループ切れ目）モデル**は後方互換で残す（`grid` があれば grid 優先。端末キャッシュに古い cuts しか無くても動く）。選択は `ZoneLayoutSolver.ChooseSource(hasGrid, hasCuts)`（grid 優先）。show.json layout present 判定は `ShowLayoutDef.HasData()`（grid か cuts）。
2. **位置合わせ = 剛体 3 DOF**（XZ 平行移動 + yaw）だけを [`CourseFrame`](../../Assets/Scripts/Tracking/CourseFrame.cs) が持ち、[`CourseRegistrationController`](../../Assets/Scripts/Tracking/CourseRegistrationController.cs) の **HMD 2 点登録**で解く（[Tracker] 上、`Setup Main Demo Scene` が自動配線）。

**フロアマップ（卓）の 7 モード**: 🖌 塗る（ゾーン）/ 📍 位置合わせ点（`layout.regPoints`）/ 🚶 歩かせる（シミュレータ）/
📐 カメラ姿勢（`cameras[].pose`・CG 人形の視点）/ **📏 通過ライン（`layout.lines` = 演出の発火点となる床の線分。
2026-07-27〜・契約は [streaming.md](streaming.md) の「開始規則『このラインを通過したら』」）** /
**🧱 部屋（`layout.room` = 壁・箱の 3D プロキシ。CG のオクルーダ／影の落ち先／較正参照を 1 幾何で兼ねる。
2026-07-27〜）** / **🎬 開始位置（`layout.startSpot` = 体験者がここに 0.5 秒留まると導入演出が始まる床の円。
1 点だけ。2026-07-30〜・契約は [streaming.md](streaming.md) の「導入演出」）**。
⚠ **導入の開始は「📏 通過ライン」を選ぶ方が既定**（`run.intro.startLineId`・⚙ 欄の「始まる合図」）。
線なら歩いて入ってくる動きのまま始まり、開始位置に立ち止まって待つ手順が要らない。
円は `startLineId` が空のときだけ使われる。どちらも**被っていること**が前提。
加えてモードに紐づかない **💡 CG 照明**パネル（`layout.room.light`）が常時出る。
ラインは**担当カメラの色**で描かれる（どの区間のものかが目で分かる）。
どのモードでもライン・カメラ印・開始位置は薄く描かれる（塗りながら位置関係が見える）。

⚠ **点を置く面が 4 つある**（位置合わせ点 / 較正の印 / 通過ライン / 開始位置）。**どれも別の集合**で、
コピーし合わない。兼用すると片方を動かしたときにもう片方が壊れる — 制約が違うため
（位置合わせ点は HMD で手が届く必要があり順序に意味がある / 較正の印は映像に写る必要がある /
通過ラインは事象 / 開始位置は立って待つ場所）。

#### 入力モデル（右コントローラ 4 入力のみ・2026-07-20〜）

体験者はコントローラを持たないため封印モード（旧 Run/Staff）を廃止。右手の A / B / グリップ / トリガー
だけで全操作を賄う（[`ControllerModeLogic`](../../Assets/Scripts/Input/ControllerModeLogic.cs) の 2 状態
Normal / Registration）。左手・スティック・cue 試射・操作チートシート（旧 StaffPanel）は撤去。

| 状態 | 入力 | 機能 |
|---|---|---|
| Normal | A（右）短押し | カメラ手動送り Next（設営・リハ確認用）。手動で覗いても**次のゾーン境界を跨げば即ゾーンのカメラへ戻る**（manualHold 撤廃・立ち止まってプレビュー）。インサート演出中は破棄＋赤メッセージ |
| Normal | B（右）短押し | ステータス表示トグル（StatusHud） |
| Normal | 右グリップ 2 秒長押し | ランリセット（周回リセット + cue 発火済みクリア・体験者交代） |
| Normal | 右トリガー 2 秒長押し | 位置合わせ（Registration）入場 |
| Registration | A（右） | 点サンプル（0.5s ホールド平均）/ Verify 中: やり直し |
| Registration | B（右） | Verify で確定・保存・退場 |
| Registration | 右トリガー 2 秒長押し | キャンセル退場（入場と対称） |

長押し閾値は 2 秒固定（const `LongPressSec`。SerializeField にすると既存シーン YAML で 0 に読まれる罠を避ける）。

#### HMD N 点登録リチュアル（約 10 秒）

| 操作 | 機能 |
|---|---|
| **右トリガー 2 秒長押し** | 登録モード開始（2026-07-20〜。Normal からの唯一の入場）。**有効な登録が既にあれば Review（確認）フェーズに着地**、未登録なら点 1 の Capture から始まる（2026-07-21〜） |
| Review: A（右） | 点 1 から再登録（Capture へ）。ワイヤーが実物に重ならない＝ズレている時に押す |
| Review: B（右） | 保存せず終了（既存登録は不変。`RegistrationConfirmed` は発火しない） |
| A（右）で点 1..N | **show.json `layout.regPoints` の点（2〜5・順序つき・Web 卓フロアマップの「📍 位置合わせ点」で配置）を順にタッチ**。床の×印テープの真上に先端をかざして A を 0.5 秒ホールド（位置サンプル平均・壁に触る必要なし）。**ホールド中は進捗バー `計測中 ▓▓▓░░ 0.3/0.5s` + 触覚 HoldTick ランプ**（2026-07-21〜）。0.5 秒未満で離すと不成立＝触覚 Error。regPoints 不在の旧 show.json は従来既定 2 点 (-0.5,0.5)/(0.5,0.5) にフォールバック |
| （ライブ表示） | 2 点目以降は「直前の点との実測距離 vs authored 距離の誤差 %」を表示しながら当てられる |
| （自動チェック） | N 点の 2D 剛体フィット（`RigidFit2D`・2 点時は従来解と同一）後、**max 残差 > 0.12m（maxResidualM）なら「どの点のタッチが悪いか」を表示してやり直し**（触覚 Error）。通過したら Verify へ（触覚 Fire） |
| Verify: （表示） | 1 行目に **`最大残差 0.05m（合格 ≤0.12m）`** を表示（2026-07-21〜） |
| Verify: B（右） | **確定**（`persistentDataPath/registration.json` へ保存 + モード終了）。残差・点数・保存日時も焼き込む |
| Verify: A（右） | 最初からやり直し（ステップ1へ戻る） |
| Verify: 右トリガー 2 秒長押し | キャンセル退場 |

- **スティックナッジ（平行移動・yaw 微調整）は廃止**（2026-07-20）。N 点剛体フィット + 残差ガード 0.12m が精度を担保し、やり直しが約 10 秒で安いため。
- **登録プレビューはトランザクション**（2026-07-23 監査修正・テスト `CourseFramePreviewSessionTests`/`CourseRegistrationControllerTests`）:
  `CourseFrame` に `BeginPreviewSession`/`CommitPreviewSession`/`RollbackPreviewSession` を実装。登録モード入場で Begin、
  B 確定（SaveRegistration）で Commit、**キャンセル退場（トリガー長押し）で Rollback** — 変換・要再登録フラグ・品質メタを
  コミット済み値へ復元し Changed を発火（ダーティ時のみ。Review-B の無変更退場では発火しない）。旧実装は残差ガード通過時の
  `SetRegistration(save:false)` プレビューがキャンセル後も残り、未保存の誤フィットにゾーンが整列したままだった。
  ホールド平均計時は純ロジック [`HoldAverageSampler`](../../Assets/Scripts/Tracking/HoldAverageSampler.cs)（dt 注入・`RegInput.deltaTime` を Bridge が供給）。
- **prefab の SerializeField は機械監査**（2026-07-23 監査修正・テスト `StreamingLogicPrefabFieldsTests`）: StreamingLogic.prefab の
  `OvrControllerBridge` ブロックに `statusButton` キーが無く Button.None（0）で読まれ、**実機で B ボタン全死**（StatusHud トグル +
  登録 Verify の B 確定不能）だった。prefab YAML を現行フィールドへ全書換（statusButton:2 追加・stale キー掃除）し、
  SerializedObject の実効値 + 生 YAML キー実在の二本立てテストで再発（missing/stale 双方）を機械検出する。
  MonoBehaviour に [SerializeField] を足したら prefab YAML への反映と本テストの更新を対で行うこと（unity-prefab-fields スキル参照）。
- **先端位置は RightHandAnchor の position をそのまま使う**（先端オフセット補正なし。誤差 2〜3cm は 1m ベースライン + 40cm 回廊 + 8cm オーバーラップに対して許容）。SerializeField `rightHandTransform`、null なら headTransform にフォールバック。
- **床の高さも同じタッチから測る**（2026-08-02〜）。それまで登録は 3 DOF（XZ + yaw）で **y を捨てていた**ため、
  壁や床のワイヤー・ゾーンのタイル・CG 人形が実際の床より下に出ていた。トラッキング原点は既に FloorLevel
  設定（`Main.unity` の `_trackingOriginType = 1`）なので**設定では直らない** — 実測して合わせる。
  - `floorY = median(タッチ位置の y) − layout.regTouchHeightM`（[`FloorHeightSolver`](../../Assets/Scripts/Tracking/FloorHeightSolver.cs)）。
    **中央値**を使うのは、1 点だけ床に着け損ねた登録で床が引っ張られないため
  - `layout.regTouchHeightM` は「コントローラを床から何 m の高さに構えるか」。**既定 0 = 床に着ける**。
    空中でホバーすると XZ が確実にぶれて残差ゲート 0.12m を圧迫するので、精度としては 0 が最善
  - y のばらつき（max−min）が 6cm を超えたら**警告を出す**（不合格にはしない）。「床に着けていない点がある」を
    現地で気づける、無料の品質ゲート
  - **⚠ `CourseFrame.CourseToWorld` の第 2 引数の意味が変わった**（旧: ワールド y の直指定 → 新: **床からの高さ**）。
    呼び出し側が渡していた定数（ワイヤー 0.03 / タイル 0.015 / ゾーン中心 1.0 / CG 人形の 0）はすべて
    「床からの高さ」のつもりの値なので、意味の変更で全部が正しく持ち上がる。**新しく呼ぶときは床基準で渡すこと**
  - `registration.json` に `originY` / `floorSpreadM` / `regSchema`(=2) を追加。旧ファイルは `regSchema=0` で
    読まれ、**originY は捨てて 0 にする**（測っていない 0 を「床が一致している」と読むと、ずれたまま
    「合っている」と表示することになる）。StatusHud と Review 画面が「床の高さは未測定」と名指しする
  - 較正（`cameras[].calib`）は不変。course 空間で解かれ、人形・影・部屋プロキシも同じ `CourseToWorld` を
    通るので、床ごと一様に平行移動するだけ
- **登録直後にワイヤーフレーム検証表示**：壁ポリライン（L の 2 辺・高さ既定 1m）+ フロア外周を LineRenderer でゴースト表示。show.json layout に wall/floor があればそれを、無ければ内蔵既定（フロア 1.8×1.8・regPoint から導出）を描く。ワイヤーは毎フレーム CourseFrame 変換に追従。
- **視界内ガイダンス**：自前 TextMesh は持たず、各ステップの指示を `GuidanceText` / `GuidanceColor` として公開し、単一サーフェス [`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs) が登録中に読み取って強制表示する（旧 [CourseRegGuidance] TextMesh は廃止・Tracking→Diagnostics の asmdef 依存を作らないプロバイダ方式）。save は B 確定でのみ registration.json を書く。文言のフォーマットは純関数 [`RegistrationGuidance`](../../Assets/Scripts/Tracking/RegistrationGuidance.cs)（進捗バー・残差行・Review ヘッダ）に切り出し EditMode テスト（`RegistrationGuidanceTests`）で固定。
- **確認（Review）フェーズ**（2026-07-21〜）：確定後でも位置ズレを見直せる道。登録開始時に `CourseFrame.HasRegistration`（json ロード済み or 今セッション確定済み）なら Capture でなく Review に着地し、既存登録のワイヤーフレーム + ゾーン床フットプリント + ヘッダ `登録済みの位置合わせを表示中（保存: <日時> / 残差 <X.XXm> / <N>点）` を出す。A=点 1 から再登録 / B=保存せず終了。recenter フラグが立っていれば橙で「⚠トラッキング原点が変わっています — 再登録を推奨」を追加。
- **登録品質・鮮度の永続化**（2026-07-21〜）：`registration.json` に `maxResidualM` / `pointCount` / `savedAtIso`（ローカル時刻 ISO）を追加（JsonUtility 欠損 default で後方互換・旧ファイルは「記録なし」表示）。`CourseFrame` が `HasRegistration` / `MaxResidualM` / `PointCount` / `SavedAtIso` を公開し、Review 表示と StatusHud のステータス行バッジ（`⚠未登録` / `登録済(残差0.05m)`・`⚠要再登録` 優先）に使う。
- **登録モード中はゾーン床フットプリント表示**（Capture/Verify/Review 全フェーズ）。show.json `layout.grid` があれば Web 卓フロアマップで塗った**生タイル**をカメラ別色で床に表示（[`ZoneGridFootprint`](../../Assets/Scripts/Tracking/ZoneGridFootprint.cs)＝単一メッシュ・頂点色・`CourseFrame.CourseToWorld` で焼き込み・Changed/LayoutChanged 駆動）。grid 不在（v1 cuts / 既定ゾーン）時は従来どおり現存 `PlayerZone` の OBB を Quad で床投影する。色は共有パレット [`ZonePalette`](../../Assets/Scripts/Tracking/ZonePalette.cs)。セル→course 座標は `ZoneLayoutSolver.CellRect`（SolveGrid とフットプリントが同一式を共有＝生タイルとゾーンが一致）。通常のボタン操作（カメラ切替・ステータス）は抑止される。**⚠ 実機未検証（2026-07-22）**。
- **OS recenter 検知**（Oculus ボタン長押し等でトラッキング原点が変わる）：OvrControllerBridge が `OVRManager.display.RecenteredPose` を購読 → `CourseFrame.MarkNeedsReRegistration()` で「要再登録」フラグ + 警告ログ + 視界警告を出す。**ゾーン動作は継続**（黙ってズレたまま動かさない、が目的）。再登録すればフラグは降りる。
- **PlayerZone は OBB（向き付きボックス）**: `Contains` はワールド差分をゾーンローカル軸（`transform.rotation`）へ射影して判定する。rotation が identity のときは従来 AABB と完全一致（既存テストもそのまま pass）。CourseFrame の yaw が各生成ゾーンの向きに乗る。
- 保存先は**端末ローカル**（Quest なら `/sdcard/Android/data/com.roiril.mawarimi/files/registration.json`）。1 変換（originXZ + yawDeg）のみを持つ。旧 `zone_calibration.json`（ゾーン個別の形状保存）は**廃止**。
- 入力は OvrBridge → `CourseRegistrationController.Feed()` 転送（Tracking asmdef は OVRInput 非依存のまま）。
- **⚠ Phase 3（HMD 登録）は実装済み・実機未検証**（2026-07-16、フィードバック改修 2026-07-21）。現地 L 壁で 2 点タッチ → ワイヤー重なり → 確定の一連、および再入場での Review 着地・サンプル進捗バー/触覚・残差表示を実機確認すること。show.json layout エディタ（Web 卓・Phase 2）は別作業。
- **登録ビュー（ZoneGridFootprint + ワイヤーフレーム）の検証ハーネス**（2026-07-22）: (1) `.	oolsNity.ps1 menu regviz`（Editor 多角度 PNG・Play 不要。show.json layout を注入し identity/登録後 × 真上/斜め/目線 を `Assets/Screenshots/regviz/` へ焼く。南北反転・変換ズレを机上で確認）、(2) `RegVizDebugDriver`（Development ビルド起動フック。`adb shell am start -e regviz 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity` で起動 5 秒後に登録モードへ自動入場 → `adb exec-out screencap` で見た目確認。`[RegVizDriver]` タグでログ）。どちらも registration.json / show.json を書かない（`SetRegistration(save:false)` + `SetLayoutForPreview` 注入）。**2026-07-22 実機 screencap で grid タイルの両眼描画・配色を確認済み**（Editor プレビューでは配色/南北/変換追従も確認済み。立体視のスケール感・ワイヤーの重なり精度のみ現地の人間確認が残り）。`exec-out screencap` が connection reset する時は `shell screencap -p /sdcard/x.png` → `pull` 方式で。
  - **HMD 内テキスト StatusHud の位置・サイズ感も検証対象に追加**（2026-07-22）: 見た目構築は `MainDemoSceneSetup.CreateStatusHudVisual`（本番 `CreateStatusHud` と共有・数値定義は 1 箇所）。(1) の Editor プレビューは eye アングルのみ HUD を写して `regviz_<identity/registered>_eye_hud.png` を +2 枚焼き（既存 6 枚は不変）、パネル/1 行の見かけ角を `[RegVizPreview] hud angular: panel=…°x…° line=…°` でログ。Edit Mode は Update/LateUpdate が走らないので `SendMessage("Update"/"LateUpdate")` で内容解決・配置を手動駆動する。(2) の実機フックは **フェーズ 2**（`[RegVizDriver] viz ready` の 12 秒後）で登録退場 → ステータス表示を ON にし `[RegVizDriver] phase2 status hud shown` をログ（親がこのマーカーで 2 枚目の screencap を撮る）。Tracking→Diagnostics の asmdef 参照禁止を守るため実機フックは `GameObject.Find("StatusHud").SendMessage("SetVisible", true)` で ON にする。
  - **日本語フォント（このハーネスが即日拾った実バグと恒久修正・2026-07-22）**: TMP 既定 LiberationSans SDF に CJK グリフが無く、**登録ガイダンス・ステータス行の日本語が実機で全て豆腐**だった（過去の「実機で文字が出てる風」スクショは ASCII 断片だけが見えていた）。修正 = `Assets/Art/Fonts/SourceHanSansJP-Normal.otf`（SIL OFL・同梱可）から `.	oolsNity.ps1 menu hud-font` で **使用文字を事前ベイクした Static アトラス** `Assets/Resources/Fonts/JapaneseHud SDF.asset` を生成し、`JapaneseHudFont.TryGet()`（Diagnostics）が Resources から供給、`StatusHud.Awake` が差し替える。**HUD の文言（RegistrationGuidance / StatusHud / CourseRegistrationController）に新しい漢字・記号を足したらメニュー再実行**（文字集合はソースから自動収集）。実機 screencap でガイダンス・ステータスとも日本語可読を確認済み。罠 3 つ: ①可変フォント（NotoSansJP-VF 等）は CreateFontAsset が通ってもグリフラスタライズ全滅 → **静的フォント必須** ②この Unity/TMP 世代はランタイム OS フォント→CreateFontAsset が全滅（58 候補実測）→ 同梱ベイクが唯一の確実経路 ③Editor プレビューの HUD ミラー（TMP 3D）は Edit Mode 制約で CJK が豆腐のまま = **配置・サイズ検証専用**。可読性の一次証拠は実機 screencap で取る。

### コントローラ触覚（振動）フィードバック（2026-07-21〜）

HMD を体験者が被っている間はスタッフに視覚（StatusHud）が見えない。押下の受理・進行・発火・失敗を
**右コントローラの振動**で伝える（純ロジック [`HapticSequenceLogic`](../../Assets/Scripts/Input/HapticSequenceLogic.cs)
＝「経過時間→振幅」・EditMode テスト [`HapticSequenceLogicTests`](../../Assets/Tests/Input/HapticSequenceLogicTests.cs)、
MonoBehaviour [`ControllerHaptics`](../../Assets/Scripts/OvrBridge/ControllerHaptics.cs) が毎フレーム
`OVRInput.SetControllerVibration` を RTouch へ適用）。両アプリ共通仕様（TableDuo 側も同じボキャブラリ）:

| パターン | 意味 | 波形（freq 0.5 固定） |
|---|---|---|
| Ack | 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る） | 40ms・amp 0.25 |
| Action | 短押しアクション実行（カメラ Next / ステータストグル / 点サンプル確定） | 80ms・amp 0.5 |
| HoldTick | 長押しカウント進行 | 連続・amp 0.10→0.30 の progress 比例ランプ |
| Fire | 長押し発火・モード遷移・確定保存 | 80ms×2（間 80ms）・amp 0.8 |
| Error | 失敗・拒否（登録の残差 NG やり直し） | 50ms×3（間 60ms）・amp 0.6 |

- **重畳優先度（固定仕様）**: 単発パターンは「ピーク振幅が厳密に大きい後着だけ差し替え、同ピーク・低ピークは再生中なら無視」
  （Ack→Action は昇格 / Fire 中の Ack は無視 / 確定保存の二重 Fire = RegistrationConfirmed と ModeChanged が 1 回に畳まれる）。
  HoldTick は連続の床として単発と **max** 合成。数値は SerializeField ではなく **const**（旧シーン YAML で 0 に読まれる罠回避）
- 配線: [`OvrControllerBridge`](../../Assets/Scripts/OvrBridge/OvrControllerBridge.cs) が down エッジ→Ack /
  Normal アクション→Action / 長押し進捗→SetHoldProgress / ModeChanged・RunReset→Fire。登録フローの節目は
  `CourseRegistrationController` の `PointCaptured`/`FitRejected`/`RegistrationConfirmed` イベントを Bridge が購読
  （Tracking asmdef は OVRInput 非依存を維持）。`haptics` 参照は null 許容（未配線でも全機能が従来通り動く）
- **接続表示**: StatusHud の接続行は**廃止**（2026-07-23）。コントローラ未接続の切り分けは ControllerGuidePanel が
  自動で消えることで行う（Bridge が `OVRInput.IsControllerConnected(RTouch)` を **ControllerGuidePanel へのみ** push。
  StatusHud への push は撤去）。**⚠ 2026-08-07〜はガイドパネル自体が「スタッフが見ているとき」しか出ない**ので、
  「押しても振動しない」の切り分けは**まず右 B を押す**（B で StatusHud とガイドパネルが同時に出る）。
  StatusHud 本文は 2026-07-23 に○×表記・cm 化へ改稿（カメラ健全性は `カメラ1○ カメラ2×`＝○届いてる/×届いてない・
  半角スペース 2 個区切り、位置残差は `位置合わせOK（ずれ 5cm）` の整数 cm。`[NORMAL]` 行頭・`信号 ●●○`・丸数字・接続行は廃止）。
  スタッフ向け操作早見表は右コントローラ追従の [`ControllerGuidePanel`](../../Assets/Scripts/Diagnostics/ControllerGuidePanel.cs)
  （Bridge が `PushModeLabel` で NORMAL/REG 本文を切替。位置合わせ中は門が自動で開くので REG 本文は従来どおり読める）。
  **インサート演出中に A を押すと手動切替は破棄**され、
  ガイドパネル上部に赤 1 行 `演出中は切り替えできません` を数秒出す（[`ControllerGuidePanel.ShowTransient`] + 失敗振動）。
- **⚠ 実機未検証**（2026-07-21）。振幅・波形の体感、コントローラ未接続時のガイドパネル消灯は現場調整前提

### 体験の骨格（導入 → 3 周 → 終了）— 2026-07-29〜

企画書（学会論文版）の「3 区間を 3 周・導入を含め 3 分以内」を状態機械にしたもの。契約は
[streaming.md](streaming.md) の「体験の骨格」節が正本。Unity 側で押さえるべき点だけ:

- 相は `Intro` / `Run` / `Finished`。実行体 [`ShowRunDirector`](../../Assets/Scripts/Streaming/ShowRunDirector.cs) は
  `Setup Main Demo Scene` が [Tracker] に載せる（未配置なら ShowControlClient が実行時に自動生成する）。
- 導入・終了の間は **`CueScheduler.SetShowGate(false)`** で区間進行を止める。これ 1 点で演出・端末内録画・
  区間 post / BGM・実測滞在が全部止まる。**画面のカメラ切替は止めない**（導入では映像を出したいから）。
- 終了の黒は [`ShowEndingFader`](../../Assets/Scripts/Diagnostics/ShowEndingFader.cs)（CenterEyeAnchor 直下）。
  `StartupFader` は解除後に自分を Destroy するので再利用できない。
- StatusHud の 1 行目が相と経過を出す（`導入中 0:12` / `2周目/全3周 ・ 経過 1:05` / `体験おわり…`）。
  **HMD 内の文言を足したら `.\tools\unity.ps1 menu hud-font` を再実行**（忘れると実機で豆腐）。

#### 導入の中止は本当に中止になる（2026-08-06 修正）

トラッキング原点が変わると `IntroDirector.AbortIntro` が演出を畳むが、**それだけでは中止が中止に
ならなかった**。中止は `IntroLogic.Disable()` を通るので `Active`（`_stage != Off && != Done`）が
false へ落ち、`ShowRunDirector` が渡す `introPlaying` も false になる。`introMinSec` は起動から
数えていて設営でとうに過ぎており、体験者はスタート区間に居るので、**中止したその瞬間に
「時間経過 ＋ スタート区間に居る」が揃って本編へ飛んでいた**（ずれた座標で 3 周が始まり、
体験者は壁の位置が違う世界を手でたどる）。しかも警告は相で門を閉じていたので同時に消えていた。

- [`IntroDirector.Aborted`](../../Assets/Scripts/Streaming/IntroDirector.cs) を公開し、
  `ShowRunLogic.Tick(…, introAborted)` の `auto` 条件へ入れる。**明示操作（⏭）は通す**
  — 「ずれていても進めたい」は運営の判断で、コードが止める話ではない
- 視界は [`ShowRunDirector.ShouldBlackout`](../../Assets/Scripts/Streaming/ShowRunDirector.cs) →
  `ShowEndingFader` の黒が閉じる（終了 1.5s / 中止 0.4s）。新しい凍結ラッチは足していない
  （唯一のラッチは `_aborted` で、落ちるのは `BeginIntro` だけ）
- **復帰は 1 段**。`IntroDirector.TryRecoverFromAbort` が「位置合わせが確定し直された」を
  `CourseRegistrationStampProvider`（= `CourseFrame.SavedAtIso`）の変化で検出して導入をやり直す。
  ⚠ 要再登録フラグの false 化では代用できない（プレビュー `SetRegistration(save:false)` でも降りるので、
  B 確定の前に再開して登録ビューと演出が混ざる）

#### 終幕（2D スクリーン → パススルー）— 2026-08-07 に実行体を入れた

導入の逆を辿って現実へ戻して終わる。判断は [`OutroLogic`](../../Assets/Scripts/Streaming/OutroLogic.cs)、
配線は [`OutroDirector`](../../Assets/Scripts/Streaming/OutroDirector.cs)（`IntroDirector` と同じ
GameObject に載る）。段は **Warm**（裏でパススルーを点火して待つ・画は本編のまま）→ **Unswap**（枠の中身が
映像から現実へ）→ **Open**（枠が開く）→ **Restore**（色と質感が戻る）→ **Hold**（素のパススルー）。
既定の尺は 1.5 / 2.5 / 2.0 / 1.5 = **7.5 秒**（`run.outro` で調整）。

- **覆いは導入と同じ [`IntroVeil`](../../Assets/Scripts/Streaming/IntroVeil.cs)**。開口の式を共有しないと
  「閉じた形」と「開く形」が食い違う
- **パススルーの見え方は `PassthroughStyler` が `Weights` を毎フレーム読む**（導入と同じ経路。
  重みの語彙 `IntroWeights` を共有しているのでそのまま繋がる）。走っている方を読み、両方走ったら終幕優先
- ⚠ **終幕が有効なら終了で黒を出さない**（`ShowRunDirector.ShouldBlackout` が `OutroDef.enabled` を見る）。
  黒で閉じてから現実へ戻すと継ぎ目が 2 回になり、「終わった」と思わせた後に画が戻るので締まらない
- ⚠ **`PassthroughStyler.disableWhenDone` は終幕の後は効かせない**（`Stage == Done` を見る）。
  切ると最後に真っ黒になって「現実へ戻った」が台無しになる
- ⚠ **点火待ちは `PassthroughReadyProvider`（OvrBridge から注入）**。Streaming asmdef は OVR を
  参照しない規約なので、判定は向こうから差し込む。null なら true ＝待たずに進む
  （`OutroLogic.WarmMaxSec` = 1.5s の上限もあるので「本編のまま固まる」ことはない）
- `run.outro` のキーが無い show.json では JsonUtility が `enabled=false` に化けるので、
  `ShowOutroDef.LooksUnset()` で検出して既定へ落とす（導入と同じ罠・同じ手当て）
- **`run.outro.lineId` は未実装**（スキーマだけ）。いまは `run.endGraceSec` の経過で始まる

#### 本編へ入る判定は「演出が終わったか」（2026-08-06 置き換え・慣らし歩行を外した）

きょうの体験で**慣らし歩行は要らない**と判断し、`run.introMinSec` を 20 → **0** にした
（導入は導入演出だけ。あとは 1 周目のカメラ A で少し動く。A–B の境界は結構進まないと切り替わらないので
想定外の切替は起きない、というユーザーの実測）。**ただし値を 0 にするだけでは演出が 1 度も出なくなる。**

旧判定は `introPlaying`（＝演出が進行中か）の否定で、**段 0（開始待ち）はそこに含まれない**ため
「進行中でない」が成立していた。慣らしの 20 秒が「演出が始まるまでの猶予」を兼ねていたので露見して
いなかったが、0 にすると**体験者がスタート区間に立った時点で本編へ飛ぶ**。

→ [`IntroDirector.Completed`](../../Assets/Scripts/Streaming/IntroDirector.cs)（`BeginIntro` で落ち
`FinishIntro` で立つ。演出無効の設定では最初から true）を新設し、`ShowRunLogic.Tick` の引数を
`introPlaying` → **`introCompleted`（既定 true ＝ 演出を知らない呼び出し側では止めない）** へ置き換えた。
2026-07-30 の実害（`Holding` を使うと段 3・段 4 の条件待ちで false へ落ち、枠が出た直後に本編へ飛んで
Swap が一度も出なかった）も、「終わった」判定なら構造的に起きない。

⚠ **慣らしが 0 になったので、演出が終わった瞬間から 1 周目の録画が始まる**（`CueScheduler` のゲートが
開く）。3 周目の背景はその録画なので、体験者が固定視点に慣れる前の歩き方がそのまま素材になる。

#### HMD に出す文言の規約（2026-08-06 制定 / 2026-08-07 改訂）

**⚠ 体験者の視界には文字を 1 つも出さない**（2026-08-07・ユーザー指摘「体験者が被っているときに表示する
文字、世界観を壊すので消してください。スタッフの時は表示していい」）。読み手を面で分ける旧規約は
「体験者向けの面」を許していたが、機器の言葉がホラー体験の中に混ざる時点で世界が壊れる。

**門は 1 つ**：[`StatusHud.StaffViewing`](../../Assets/Scripts/Diagnostics/StatusHud.cs)
＝ 右 B のステータス表示 **or** 位置合わせ作業中。どちらも**コントローラを持っている人にしか
起こせない**ので、体験者が被っている間は構造的に立たない（体験者はコントローラを持たない運用）。
HMD 内の文字面はすべてここを見て出入りする。**解決できないときは出さない側へ倒す**
（判定できない環境で文字が復活する方の失敗を既定にしない）。

| 面 | いつ出るか | 出すもの |
|---|---|---|
| `IntroPrompt`（視線前方 1.5m） | スタッフが見ているときだけ | 導入の合図（`右手をあげてください` / `歩いてください` / `そのまま前へ進んでください`）。**体験者へは口頭で伝える** |
| `ShowEndingFader` の黒（0.3m） | 黒は誰にでも / 文字はスタッフだけ | 中止時の `少しお待ちください`。⚠ 黒が 1.5m より手前なので、**この 1 行は黒を持つ側が描く**（`IntroPrompt` に置くと隠れる） |
| `StatusHud`（視線前方） | **右 B で開いたときだけ**（自動で開く経路は持たない） | 相・周回・場所・異常 1 件 ＋ 直し方 |
| `ControllerGuidePanel`（手元） | スタッフが見ているときだけ | ボタンの早見表だけ。⚠ 2 秒で消える `ShowTransient` と手を下げると視界外なので、**復帰手順の置き場にしない** |

⚠ **`StatusHud` の自動表示（旧 `recenterAutoShowSec` = 要再登録で 5 秒だけ開く）は廃止した。**
体験者の視界へ業務連絡が湧く唯一の経路だった。異常はスタッフが右 B で開けば最優先の 1 件として
必ず出るし、卓の heartbeat にも出ている。**本編中に位置がずれても自動では知らせない**ので、
現場では卓を見るか、気になったら B で開く。

⚠ **段 5 の「右手をあげてください」が体験者に出なくなった。** これは 3 周目の反転の伏線
（画面の中の自分は上げるが、3 周目の背景は 1 周目の録画なので上がらない）で、体験の核心に効く
唯一の指示だった。**HMD を被せる前にスタッフが口頭で伝える**運用にする。伝え忘れると
3 周目の反転が成立しないので、[onsite-checklist](../../docs/onsite-checklist.md) の声掛けに入れてある。

⚠ **「押しても振動しないときはガイドパネルが出ているか見る」は、先に右 B を押してからになった**
（B を押せばパネルもステータスも出る）。

- **異常は「何が起きたか」＋「何をすれば直るか」の 2 行で、最優先の 1 件だけ**を本文と差し替えで出す。
  文言は [`RecoveryGuidance`](../../Assets/Scripts/Diagnostics/RecoveryGuidance.cs) が 1 箇所で持ち、
  `RecoveryGuidanceTests` が**全異常に手順があること**を機械で固定する（異常を足すには手順を書く以外の
  道が無い）。旧実装は 8 種のうち復帰動作を書いているものが **0** で、読んだスタッフは現場で黙って立った
- **数値は「その数字で判断が変わるか」で残す。** 残す = ずれ cm / カメラ番号 / 経過 / 周回。
  消した = 遅れの常時表示（100ms 超のみ）/ 揺らぎの内訳 / cue の id / `カメラ切替中…`（0.17 秒で読めない）
- **語は 3 語に固定**（「位置合わせ」「×印」「点」）。廃語 = 登録 / 再登録 / 基準点 / マーク / 残差 /
  誤差 / 周回リセット / 砂嵐 / course。テストが混入を落とす
- **体験者に取れる手が無いことは体験者に出さない**（旧 `位置合わせがまだです（スタッフが始めます）`）。
  減るのは不安ではなく没入で、機器の言葉がホラー体験の入口に出た瞬間に世界が壊れる

### 周回カウントと cue 自動発火（2026-07-17〜）

「何周目のどのゾーンで cue を出すか」の事前オーサリング（詳細は [streaming.md](streaming.md) の該当節と
[.claude/plans/2026-07-17_pre-authored-cue-schedule.md](../plans/2026-07-17_pre-authored-cue-schedule.md)）:

- [`LapCounter`](../../Assets/Scripts/Tracking/LapCounter.cs)（[Tracker] に `Setup Main Demo Scene` が自動配置）が
  **`CameraSwitchDirector.ZoneCommitted`（ショーの時計）**を購読し、show.json `layout.course.order` の**順方向一致でのみ**進行ポインタを進める。
  `order[0]` 復帰で lap++（1 始まり）。逆走・行き来・スキップは前進しない
  （2026-07-25 段 B 以前は `CameraStreamRegistry.ActiveChanged` / `SwitchCommitted` ＝**画面**を購読していた。
   director 未割当時のフォールバックとしてのみ `ActiveChanged` 購読が残る）
- **時計は画面から独立**（段 B）: 確定は [`ZoneProgressionLogic`](../../Assets/Scripts/Streaming/ZoneProgressionLogic.cs) が
  dwell だけで行い、cue / インサート / override の凍結にも dip にも左右されない。**演出で画面が止まっていても、
  体験者が歩けば周回・区間追跡は進む**。逆にスタッフ手動 A・Web 固定・インサートの画面切替は時計を動かさない
- 純ロジック（`LapCounterLogic` / `CueScheduleLogic` / `ZoneProgressionLogic`）は MonoBehaviour から分離済みで EditMode テストがある
  （`Assets/Tests/Tracking/LapCounterTests.cs` / `Assets/Tests/Streaming/CueSchedulerTests.cs` /
   `Assets/Tests/Streaming/ZoneProgressionLogicTests.cs`）。セマンティクスを変えるときはテストを先に直す
- `PlayerZoneTracker` に `ZoneChanged` イベントを公開済み（旧: イベント無し）。ただし LapCounter の駆動は
  camera index キー（= course.order / schedule.camera と同一キー空間）のため Director 経由
- 起動時は既にスタート領域に居て確定イベントが出ないため、LapCounter が現在カメラを「進入」としてシードする
  （`seedInitialZone`。lap1 スタート領域の cue を発火可能にするため）

### 前後 (z) 方向の演出を入れる時
現在 z は全ゾーン共通 [-1.2, +1.2]。**前後で挙動を変えたいなら別軸のロジックを足す**（zone は左右専用にしておく）。`PlayerStateBus` のような中央集約は Phase 4（CG 合成）着手時に検討、それまでは Tracker と並列に小さな BehaviourScript で済ませる。
