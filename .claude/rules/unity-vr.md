---
name: unity-vr
description: Unity / VR 共通規約。Assets/ 編集前に読む
globs:
  - "Assets/**/*.cs"
  - "Assets/**/*.unity"
  - "Assets/**/*.prefab"
  - "Assets/**/*.asset"
  - "ProjectSettings/**"
---

# Unity / VR 共通規約

## ディレクトリ構成（Assets/ 配下）

```
Assets/
├── Scenes/                  # *.unity（命名: PascalCase）
├── Scripts/
│   ├── Streaming/           # MJPEG 取り込み（asmdef: FixedCamVr.Streaming）
│   ├── Diagnostics/         # HUD / ログ / StartupFader
│   ├── Tracking/            # PlayerZone（HMD 座標駆動）
│   ├── Fx/                  # ポスト FX（Blit/Compute/Particles/Source）
│   ├── OvrBridge/           # OVRInput 接点（asmdef なし = Assembly-CSharp。意図的）
│   └── <Feature>/Editor/    # 各機能の Editor 拡張（別 asmdef）
├── Prefabs/<Feature>/
├── Settings/                # ScriptableObject 設定（Cameras/ 等）
├── TableDuo/                # 同居サブプロジェクト（CLAUDE.md 参照、相互参照禁止）
└── ThirdParty/              # 外部アセット（Meta XR SDK は除く）
```

Passthrough/・UI/・Common/・Art/ は後続フェーズで必要になったら切る（先回りで作らない）。

## C# 規約

- `namespace FixedCamVr.<Feature>`（例: `FixedCamVr.Streaming`）
- 機能単位で `.asmdef` を切る（ビルド時間短縮）
- `[SerializeField] private` を基本。`public` フィールド禁止
- `#nullable enable` を新規 .cs に付与
- 非同期処理は `async Task` + `CancellationToken`。コルーチンは VR ループ的に避ける（フレーム同期しないため）
- Update 内で `new`（特にバイト配列・文字列連結）禁止 → 90Hz 維持

## VR パフォーマンス

- **目標**: 90 FPS 維持（Quest 3 デフォルト 90Hz）
- **ドローコール**: 100 以下推奨。Static Batching / SRP Batcher 活用
- **テクスチャ**: ASTC 6x6 圧縮、ストリーミング用は別途 RGBA32 で動的更新
- **シェーダ**: 複雑なピクセルシェーダはポスト FX のみに限定。マテリアルは Lit ではなく **URP/Unlit** 基本
- **GC**: フレーム内アロケーション 0 を目標。`Profiler` で確認

## シーン管理

- Main シーンは `Assets/Scenes/Main.unity`
- デバッグ用は `Assets/Scenes/Debug/<Name>.unity`
- シーン切替は `SceneManager.LoadSceneAsync` のみ（同期版禁止：VR で長時間ブロックすると酔う）

## 入力

- 現状は `OvrBridge/OvrControllerBridge.cs` で OVRInput を直接参照（マッピングは Inspector で変更可）
- XRI の `InputActionReference` + `Assets/Settings/InputActions.inputactions` への集約は、入力が増えた段階で導入（現状ファイル未作成）

## ビルド

- ターゲット: Android / IL2CPP / ARM64 単独
- **2 アプリ並存**: `Tools/FixedCamVr/Build FixedCam APK（廻リ視）` / `Build TableDuo APK`
  （[BuildVariants.cs](../Assets/Editor/BuildVariants.cs)）。productName / パッケージ ID
  （com.roiril.mawarimi / com.roiril.tableduo）をビルド時のみ切り替え → Quest 上で別アプリとして同居。
  ProjectSettings は終了後に必ず復元される（手動の Build Settings ではどちらも同名・同 ID になるので使わない）
- `Development Build` 有効でデプロイし、初期は `adb logcat` でログ確認
- リリースビルドは `Build ...（Release）` メニューを使う（Development なし、出力名 -release）

## 座標駆動の体験設計（PlayerZone / カメラ自動切替）

体験設計で **HMD 座標は最重要の入力**。Quest 3 の標準ガーディアンを前提に以下を守る：

### 前提
- **実プレイレンジは ±1.3m 程度**（4 畳半 / 2.5–3m 四方の典型値）。これを超える前提でゾーンを置かない。
- HMD 高さは ≈1.6m。`PlayerZone.centerOffset` を 0 のまま運用するなら、ゾーン Transform.y を 1.0–1.2m に置いて `halfExtents.y=2` で全身を含める。
- **ゾーン形状は推測で決めず、実機で `[HudDump]` / `[HmdTrace]` を取って `pos` の min/max からプレイレンジを逆算してから決める**（[`HmdTrajectoryRecorder`](../Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs) 参照）。

### Pick() の挙動を踏まえた設計
[`PlayerZoneTracker.Pick`](../Assets/Scripts/Tracking/PlayerZoneTracker.cs) は次の順で評価する：
1. 直近ゾーンが `shrink` 後 AABB に含まれるなら維持（再評価スキップ）
2. それ以外は全ゾーンで full AABB 判定 → `priority` 大が勝ち、同値なら **配列先頭が勝つ**

これを踏まえると：

- **隣接ゾーンを 0.05–0.10m オーバーラップさせる** — `keepLastWhenOutside` に頼って間を埋めるとデッドゾーンで前ゾーン張り付きが起き、ユーザーが「切り替わらない」と感じる
- **Center を配列の先頭に置く** — オーバーラップ帯では Center が勝ち、中央寄りに自然に張り付く
- **`hysteresisShrink` は halfExtents の 20–30% を目安**（hx=0.5m なら 0.10–0.15m）。これより小さいと境界 jitter、大きいと「shrink から出ても full では現ゾーンが勝つ」状態が続いて切替が鈍くなる
- ヒステリシス幅 ≒ `hx_overlap` + `shrink` の効き方の組み合わせで決まる。**手で計算するか、Pick の事前条件をテストで固定する**（[`PlayerZoneSelectionTests`](../Assets/Tests/Tracking/PlayerZoneSelectionTests.cs) を増やす）

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
再実行は [Tracker] を作り直すため、**Screen の ShowControlClient.zoneTrackerToDisable を再アサイン**すること。

### ゾーン校正の再設計（2026-07-16〜・形状は PC / 位置合わせは HMD 2 点登録）

**旧 ZoneCalibrator（ゾーンを HMD 内でドラッグ・リサイズ・回転）は廃止**。校正を 2 つに分解した（設計 [.claude/plans/2026-07-16_zone-authoring-redesign.md](../plans/2026-07-16_zone-authoring-redesign.md)）：

1. **形状・カメラ割当 = 純データ**（物理レイアウトは固定）。PC の Web オペレータ卓が show.json `layout` を配り、[`ZoneLayoutSolver`](../Assets/Scripts/Tracking/ZoneLayoutSolver.cs) が矩形 OBB へ決定的に展開 → [`ZoneLayoutApplier`](../Assets/Scripts/Tracking/ZoneLayoutApplier.cs) が [GeneratedZones] へ生成し tracker.zones を差し替える。**HMD では形状を一切いじらない**。
   - **v2: タイルペイント（grid）モデル**（2026-07-16〜・既定の編集モデル）: フロアを正方タイル（`layout.grid`: tileM/cols/rows/cells）に分割し、各タイルをカメラ index で塗る。Solver は**カメラ毎のタイル集合を貪欲矩形分解**（行方向マージ→行間マージ・決定的）→ 各矩形を `overlapM/2` 全方向拡張して隣接カメラ境界で計 `overlapM` 重ねる。cells は rows 本の文字列（row0=北端 z=+d/2 / col0=西端 x=-w/2、`'0'..'8'`=カメラ / `'.'`=未割当）。不正 cells は警告 + 未割当扱いで例外にしない。
   - **v1: cuts（ループ切れ目）モデル**は後方互換で残す（`grid` があれば grid 優先。端末キャッシュに古い cuts しか無くても動く）。選択は `ZoneLayoutSolver.ChooseSource(hasGrid, hasCuts)`（grid 優先）。show.json layout present 判定は `ShowLayoutDef.HasData()`（grid か cuts）。
2. **位置合わせ = 剛体 3 DOF**（XZ 平行移動 + yaw）だけを [`CourseFrame`](../Assets/Scripts/Tracking/CourseFrame.cs) が持ち、[`CourseRegistrationController`](../Assets/Scripts/Tracking/CourseRegistrationController.cs) の **HMD 2 点登録**で解く（[Tracker] 上、`Setup Main Demo Scene` が自動配線）。

**フロアマップ（卓）の 5 モード**: 🖌 塗る（ゾーン）/ 📍 位置合わせ点（`layout.regPoints`）/ 🚶 歩かせる（シミュレータ）/
📐 カメラ姿勢（`cameras[].pose`・CG 人形の視点）/ **🎯 位置トリガー（`layout.spots` = 演出の発火点となる床の円。
2026-07-27〜・契約は [streaming.md](streaming.md) の「開始規則『この位置に来たら』」）**。
どのモードでも円・カメラ印は薄く描かれる（塗りながら位置関係が見える）。

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
- **登録ビュー（ZoneGridFootprint + ワイヤーフレーム）の検証ハーネス**（2026-07-22）: (1) `Tools/FixedCamVr/Diagnostics/Preview Registration Viz`（Editor 多角度 PNG・Play 不要。show.json layout を注入し identity/登録後 × 真上/斜め/目線 を `Assets/Screenshots/regviz/` へ焼く。南北反転・変換ズレを机上で確認）、(2) `RegVizDebugDriver`（Development ビルド起動フック。`adb shell am start -e regviz 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity` で起動 5 秒後に登録モードへ自動入場 → `adb exec-out screencap` で見た目確認。`[RegVizDriver]` タグでログ）。どちらも registration.json / show.json を書かない（`SetRegistration(save:false)` + `SetLayoutForPreview` 注入）。**2026-07-22 実機 screencap で grid タイルの両眼描画・配色を確認済み**（Editor プレビューでは配色/南北/変換追従も確認済み。立体視のスケール感・ワイヤーの重なり精度のみ現地の人間確認が残り）。`exec-out screencap` が connection reset する時は `shell screencap -p /sdcard/x.png` → `pull` 方式で。
  - **HMD 内テキスト StatusHud の位置・サイズ感も検証対象に追加**（2026-07-22）: 見た目構築は `MainDemoSceneSetup.CreateStatusHudVisual`（本番 `CreateStatusHud` と共有・数値定義は 1 箇所）。(1) の Editor プレビューは eye アングルのみ HUD を写して `regviz_<identity/registered>_eye_hud.png` を +2 枚焼き（既存 6 枚は不変）、パネル/1 行の見かけ角を `[RegVizPreview] hud angular: panel=…°x…° line=…°` でログ。Edit Mode は Update/LateUpdate が走らないので `SendMessage("Update"/"LateUpdate")` で内容解決・配置を手動駆動する。(2) の実機フックは **フェーズ 2**（`[RegVizDriver] viz ready` の 12 秒後）で登録退場 → ステータス表示を ON にし `[RegVizDriver] phase2 status hud shown` をログ（親がこのマーカーで 2 枚目の screencap を撮る）。Tracking→Diagnostics の asmdef 参照禁止を守るため実機フックは `GameObject.Find("StatusHud").SendMessage("SetVisible", true)` で ON にする。
  - **日本語フォント（このハーネスが即日拾った実バグと恒久修正・2026-07-22）**: TMP 既定 LiberationSans SDF に CJK グリフが無く、**登録ガイダンス・ステータス行の日本語が実機で全て豆腐**だった（過去の「実機で文字が出てる風」スクショは ASCII 断片だけが見えていた）。修正 = `Assets/Art/Fonts/SourceHanSansJP-Normal.otf`（SIL OFL・同梱可）から `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` で **使用文字を事前ベイクした Static アトラス** `Assets/Resources/Fonts/JapaneseHud SDF.asset` を生成し、`JapaneseHudFont.TryGet()`（Diagnostics）が Resources から供給、`StatusHud.Awake` が差し替える。**HUD の文言（RegistrationGuidance / StatusHud / CourseRegistrationController）に新しい漢字・記号を足したらメニュー再実行**（文字集合はソースから自動収集）。実機 screencap でガイダンス・ステータスとも日本語可読を確認済み。罠 3 つ: ①可変フォント（NotoSansJP-VF 等）は CreateFontAsset が通ってもグリフラスタライズ全滅 → **静的フォント必須** ②この Unity/TMP 世代はランタイム OS フォント→CreateFontAsset が全滅（58 候補実測）→ 同梱ベイクが唯一の確実経路 ③Editor プレビューの HUD ミラー（TMP 3D）は Edit Mode 制約で CJK が豆腐のまま = **配置・サイズ検証専用**。可読性の一次証拠は実機 screencap で取る。

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
  StatusHud への push は撤去）。「押しても振動しない」時はまずガイドパネルが出ているかを見る。
  StatusHud 本文は 2026-07-23 に○×表記・cm 化へ改稿（カメラ健全性は `カメラ1○ カメラ2×`＝○届いてる/×届いてない・
  半角スペース 2 個区切り、位置残差は `位置合わせOK（ずれ 5cm）` の整数 cm。`[NORMAL]` 行頭・`信号 ●●○`・丸数字・接続行は廃止）。
  スタッフ向け操作早見表は右コントローラ追従の [`ControllerGuidePanel`](../../Assets/Scripts/Diagnostics/ControllerGuidePanel.cs)
  が常時表示（Bridge が `PushModeLabel` で NORMAL/REG 本文を切替）。**インサート演出中に A を押すと手動切替は破棄**され、
  ガイドパネル上部に赤 1 行 `演出中は切り替えできません` を数秒出す（[`ControllerGuidePanel.ShowTransient`] + 失敗振動）。
- **⚠ 実機未検証**（2026-07-21）。振幅・波形の体感、コントローラ未接続時のガイドパネル消灯は現場調整前提

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

## 起動時の視界保護（StartupFader）

VR では **Play 開始から最初の安定フレームまで** の間、以下が同時に起こり「不安定な絵」が露出する：
- Quest システムの砂時計表示（OS レイヤ）
- OVRCameraRig がヘッドポーズを取得するまで数フレーム
- MJPEG ストリームの接続待ち（数百 ms 〜 数秒）
- URP の RT 確保 / ポストプロセス初期化の最初のフレーム

これを直接ユーザーに見せると **目に悪い + 体験の質を下げる**。原則：

- CenterEyeAnchor 配下に **head-locked な黒 Canvas** を Awake 時に生成し、最初から視界を覆う
- 解除条件は `(min_hold) AND (any_stream_connected OR max_wait_timeout)` の AND/OR
  - `min_hold`（既定 0.5s）: 早すぎる解除での pop-in 防止
  - `max_wait_timeout`（既定 4s）: スマホがスリープ等で永遠に繋がらない時のハードガード
- フェードアウトは 0.5s 程度の線形 alpha 1→0
- 完了したら GameObject ごと破棄（Update を残さない）

実装は [`StartupFader`](../Assets/Scripts/Diagnostics/StartupFader.cs)。`MainDemoSceneSetup` で自動配置される。

## Editor 拡張メニュー設計

カスタムメニューは **`Tools/FixedCamVr/`** 配下のみ（トップレベル `FixedCamVr/` を切らない）。サブメニューは 4 階層に固定：

| サブメニュー | priority 範囲 | 用途 |
|---|---|---|
| 直下（ショートカット可） | 0〜49 | ユーザー常用（Open Main Scene 等） |
| `Setup/` | 50〜99 | シーン構築・アセット生成（Setup Main Demo Scene 等） |
| `Layout/` | 100〜199 | Editor レイアウト管理（ユーザー初期設定） |
| `Diagnostics/` | 200〜299 | シュビーが叩く検証ツール（Ping / Run Tests / Preview Registration Viz / Preview Show Actor 等） |

**CG 人形のレイヤ規約**（2026-07-27）: 人形は専用レイヤ **`ShowCg`(slot 9)** に置き、
**HMD カメラの cullingMask からは外す**（`Setup Main Demo Scene` が自動で外す）。外すのを忘れると
人形が VR 空間にそのまま浮いて見え、「映像の中に居る」という前提が壊れる。
レイヤの追加はファイル直編集ではなく MCP `manage_editor action=add_layer` で行う
（外部編集は Unity のメモリ側に無視され、次の保存で消える — [[cg_actor_and_layer_traps]]）。

新規メニュー追加時は **どの階層に置くべきか判断**してから書く。階層基準が曖昧なら直下に置かない（ゴチャゴチャ化を防ぐ）。MenuItem パスを README / TROUBLESHOOTING / docs/ で参照している箇所も同時更新する。
