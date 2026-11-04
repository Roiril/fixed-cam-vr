---
name: intro-title
description: 装着後の会話・題字・歩行誘導・現実が割れてスクリーンになる導入の現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Diagnostics/VeilAlignmentProbe.cs"
  - "Assets/Scripts/Diagnostics/StartupFader.cs"
  - "Assets/Scripts/Diagnostics/TitleNotice.cs"
  - "Assets/Scripts/Streaming/Intro*.cs"
  - "Assets/Scripts/Streaming/Title*.cs"
  - "Assets/Scripts/Streaming/ContainmentShell.cs"
  - "Assets/Scripts/Streaming/CrtScreenMesh.cs"
  - "Assets/Scripts/Streaming/Attic/**"
  - "Assets/Scripts/Streaming/Editor/Intro*.cs"
  - "Assets/Scripts/Streaming/Editor/Title*.cs"
  - "Assets/Scripts/Streaming/Editor/Attic/**"
  - "Assets/Scripts/Input/HmdOnboardingLogic.cs"
  - "Assets/Scripts/OvrBridge/IntroPassthroughCapture.cs"
  - "Assets/Tests/Streaming/Intro*Tests.cs"
  - "Assets/Tests/Streaming/Title*Tests.cs"
  - "Assets/Tests/Streaming/ContainmentShell*Tests.cs"
  - "Assets/Tests/Streaming/CrtScreenMesh*Tests.cs"
  - "Assets/Tests/Streaming/Attic/**"
  - "Assets/Tests/Input/HmdOnboardingLogicTests.cs"
  - "Assets/Art/Shaders/Intro/**"
  - "Assets/Art/Shaders/Title/**"
  - "Assets/Resources/Intro*.shader"
---

# タイトルと導入（装着 → 題字 → 歩行誘導 → 割れてスクリーンへ）

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
### 装着後の会話とタイトル
- 順は 練習（X/Y 1 秒長押し）→ 成功文 → 名乗り 3 秒 → 念押し 4 秒 → 0.5 秒で面を畳む → 題字。秒は全文表示から数え、練習の報告は本編の件数へ渡さない — `HmdOnboardingLogic.Tick` / `TutorialComesFirst_ThenAcknowledgementGreetingAndReminderWaitForReading` 〔出典: memory/experience_fixes_2026_09_24.md〕
- 題字は `ShowTitle` で出し、新しい押下を 0.5 秒以内に離したら閉じる。持ち越しと長押しは受けない — `HmdOnboardingLogic.TapMaxSec` / `TitleNeedsANewShortPressAndNeverAcceptsAHold` 〔出典: memory/hmd_onboarding.md〕
- 段は Off/Wait/In/Hold/Out/Done。Wait は黒だけ、Hold は時間で閉じない — `TitleLogic.Tick` / `Hold_WaitsUntilTheVisitorIsReady` 〔出典: memory/title_screen.md §3〕
- 出は筆順の書き出し 0.9 秒（時間比例・緩急は `_Write` の図）、乾き 1.6 秒 — `TitleLogic.InSec` / `SettleSec` / `TitleWriteOnTests` 〔出典: LEDGER 0260〕
- 閉じは光 0.42 → 焼け 0.20+1.40 秒 → 字が消え切ってから黒が 1.25 秒で開く。隠すもの待ちは最大 0.5 秒でラッチにしない — `TitleLogic.GlyphGoneSec` / `ConcealWaitMaxSec` / `Open_WaitsForConcealment_ButNeverLatches` 〔出典: memory/title_screen.md §4.5〕
- 段 0 に箱も殻も立たないので `ConcealReady` は false。毎回 0.5 秒待って会場のパススルーへ開く — `TitleScreen.ConcealReady` 〔出典: コード。title_screen.md §1 は箱の退避で失効〕
- 奥へ退かない（押した瞬間 0.06m の山だけ）。渦は外→内の前線、焼ける順は巻かれた量 — `TitleLogic.PushM` / `Push_MovesTowardTheViewer_AndNeverRecedes` / `TitleGlyph.shader` `SpoolWound` 〔出典: memory/title_screen.md §4.5〕
- 版 1 枚の 4ch マスク（R 主 / G 朱 / B 溶け順 / A 添え）を平らなまま +0.10 / 0 / −0.06m へ。押し出さない。朱は「視」1 字、灯りは橙 — `TitleScreen.LayerZ*` / `TitleArtImporter` / `_AccentColor` / `_GlowColor` 〔出典: memory/title_screen.md §2, §5.1〕
- 追従はヨーだけ（`HeadYawFollow` を `ScreenAnchor` と共有）。黒の面は追従させない — `TitleScreen.FollowHead` 〔出典: memory/title_screen.md §4.6〕
- 譲るのは位置合わせ中とスタッフ用準備中だけで、`IsYielding` を `IsBlocking` に混ぜない。組めなければ素通し、導入が段 0 を出たら `ForceClose` — `TitleScreen.Update` / `MissingTitleFailsSoftAfterTheTutorial` 〔出典: memory/title_screen.md §4〕

### 導入（IntroStage）
- `ShowPhase.Intro` 内の段 Black→Real→Degrade→Structure→Frame→Swap→Done。相も段も増やさない。重みは `IntroLogic.Weights` 1 本 — `IntroStage` 〔出典: streaming:940-944〕
- 尺の既定 1.5 / 3.5 / 2.5 / 5.0 / 1.6 秒・max 20、合計 12.7 秒（段 3 は段 2 の .6 から重なる）。4 か所一致 — `IntroTiming.Default` / `ShowIntroDef` / 卓 2 か所 / `TotalSec_AccountsForTheStructureOverlap` 〔出典: streaming:933, 1000〕
- 段 0 は現実が見え、時間で進まず maxSec に数えない。自動の出口はすべて `startAuthorized`（練習と題字の完了 ∧ スタッフ用準備の開始）を通る。⏭ は通さない。未配線は true — `IntroInput.startAuthorized` / `OvrControllerBridge` / `Black_StaffOverrideStillWorksBeforeTheAPress` 〔出典: show-design:487-505〕
- 誘導が予約・指示中は出口が「円に着いた」1 つ。30 秒で諦めて従来判定へ — `IntroInput.guidingToSpot` / `WalkGuideLogic.GatesStart` / `HoldMaxSec` 〔出典: show-design:578-593〕
- 従来判定は接近（境界から 1.0m・0.4 秒）・安全網（外を見てから中へ）・救済（中に立って 1.0 秒）。位置が解けない間は中／外を更新しない。武装し直しは 1 か所 — `ConcealStartSec` / `IntroDirector.TryOutsideBoxM` / `RearmStartSignal` 〔出典: show-design:459-535〕
- 位置合わせ中は凍結して覆い・殻・誘導を畳み ignite を戻す。recenter は中止、復帰は位置合わせの確定 1 段。本編へ入るのは完走 — `IntroDirector.Aborted` / `TryRecoverFromAbort` / `Completed` 〔出典: show-design:409-444, 963〕
- 段 2 は彩度だけを滑らかに抜く。輪郭・粒・乱れ・線は使わない。段 3 は終端の静止。構造線は既定 OFF — `Degrade_OnlyDrainsColourSmoothly` / `IntroStructureWire.SetSources` 〔出典: コード（streaming:838 の「色→コントラスト→輪郭→粒」は旧）・streaming:974〕
- 段 4 は頭の角速度 45°/s 以下を待つ（最大 3 秒）。`shatter` = p を破片・粒・音が読む。frame = SmoothStep(.84,.90)、reveal = (.68,.70)、live = (.895,.905) — `IntroLogic.MaxHeadTurnForFrame` / `ShatterClock_EqualsFrameProgress_ForDrawingAndSound` 〔出典: memory/intro_fracture_cinematic.md〕
- 振り付けの時計の正は `IntroFracture.shader` 先頭の表と `IntroFractureTime.hlsl`（PullStart .56 / ArriveFirst .70 / ArriveSpan .17 / CloserArrive .90） 〔出典: memory/intro_fracture_cinematic.md〕
- 中央も含めて全体を割り、同じ片が映像の出ている矩形へ組み上がる。終端の矩形を先に別の窓で描かない — `IntroFractureMesh` / `IntroVeil.ContentScale` 〔出典: show-design:62-64〕
- 参照CRTの到着矩形は`CrtReferenceScreen.VisibleScale`と`ApertureCenter`で取る。論理containの0.75を二重に掛けない。覆いの全開は筐体を含むQuad全体、破片の到着先は映像の開口 — `IntroVeil.ResolveContentScale` / `CrtReferenceScreenTests`。
- 像は割れ始めに 1 回だけ凍結し、撮影時の姿勢にワールド固定。片は撮影時の部屋の面へ置き（無ければ 1.6m の殻）、剥がれたら両眼とも左カメラの写真。静止画が無ければ旧 alpha 窓へ退避して再試行しない — `IntroVeil.ShatterAnchored` / `ApplyRoomDepth` / `IntroVeil_ShatterAnchorStaysAtStartPoseWhileHeadMoves` / `IntroVeil_NullFrozenFrameFallsBackWithoutRetrying` 〔出典: memory/intro_fracture_cinematic.md / presentation_redesign.md〕
- `_IntroLive` は `live>0 || reveal>0` の 0/1。混合量は覆いの `_ScreenFade` だけ。`ignite` は全段 1 で、畳むすべての経路で 1 へ戻す — `IntroDirector.WriteIgnite` / `ResetIgnite` / `Ignite_IsAlwaysOne` 〔出典: streaming:851, 889-897〕
- 段 5 は live 1 の静止 1.6 秒、乱れなし。映像が来なくても最大 3 秒で進み砂嵐 — `IntroLogic.MaxHoldSec` / `Swap_StillRunsWithStaticWhenNoCameraFrameArrives` 〔出典: streaming:866 / LEDGER 0060〕
- 殻は導入で描かない（終幕だけ）。封印の箱は全段 0 で毎フレーム `SetHidden` — `ContainmentShellLogicTests.Intro_NeverPaintsTheShellAtAll` / `SealedBox_NeverAppears_InAnyStage` 〔出典: streaming:858, 898〕
- 開口は「眼とスクリーンの 4 辺を通る平面」。全開は厳密に全開、`AnchorK` .15 まで頭の正面から寄せる。C# と shader が同じ式。ブラウン管の曲面は角を動かさない — `IntroVeil.SignedDistance` / `Open_CoversWholeFov_AtEveryHeadPitch` / `CrtScreenMesh` 〔出典: streaming:948-971〕

## 罠（破ると壊れる）
- 描画順 4900 覆い（`Blend Zero SrcColor, Zero SrcAlpha`）→ 4901 破片深度（ステンシル bit 32）→ 4902 隙間 → 4903 破片色 → 4904 周辺窓 → 4906 周辺色 → 4907 粒 → 4910 殻 → 4950 題字の黒 → 4960 題字 → 4995 開始案内。5000 を超えると描かれない — `IntroVeilOrderTests` / `IntroVeil.FractureStencilBit` 〔出典: memory/intro_fracture_cinematic.md / show-design:707〕
- ステンシルは URP が使っていない前提（`overrideStencilState: 0`）。殻 ref 1・破片 bit 32 〔出典: show-design:711〕
- 覆いの下のスクリーンは `forceRenderingOff`、段 4 は `IntroVeil.ShatterViewportScale` 0.5。畳むすべての経路で戻す 〔出典: memory/intro_fracture_cinematic.md〕
- `run.intro` 欠落は全部 0 になる → `ShowIntroDef.LooksUnset` で既定へ 〔出典: streaming:972〕
- `IntroPreview` は `_IntroLive` の式を写している。変えたら対で直す 〔出典: memory/intro_fracture_cinematic.md〕
- 名目の殻 1.6m は 3 か所 — `IntroRoomDepth.ShellRadius` / `IntroPeripheralFractureMesh.Radius` / `IntroFractureTime.hlsl` / `ShellRadius_MatchesTheShaderAndThePeripheralMesh`
- 起点は shader の tan(−0.16, 0.12) と `IntroFractureMesh.ImpactX/Y` が対 〔出典: memory/intro_fracture_cinematic.md〕
- 未装着ではパススルーカメラの姿勢が使えない → `IntroPassthroughCapture.TryHeadPoseFallback`（`shatProj=…(head)`） 〔出典: memory/intro_fracture_cinematic.md〕
- 観測は「画に出た」側。`shatCam` / `shatAnchor` / `shatDepth` / `shatWarm` / `ignite=` は `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す（`ilive=` / `sig=` / `rvs=` は C# だけ）
- 導入の音は shatter ≥ .02 で `sfx_shatter` 1 本。ScreenOn は鳴らさない。`ingest-sounds.py` の `SWARM_*`（ArriveSpan .13）は旧レシピで写しではない — `SoundCueLogic.ShatterFireAt` 〔出典: rules/sound-design.md 2026-09-25 節〕
- 題字の版を焼き直したら `ORDER_LO/HI` を測り直す 〔出典: memory/title_screen.md §4.5〕 〔未照合〕
- `menu title` / `menu intro` は追従を映さない。`menu title` は `TitlePreview.Summoned` 経由で、hold の最大輝度 120 未満は LogError 〔出典: memory/title_screen.md §5〕

## 深く読むとき
- 割れる遷移の数値・質感・fps → memory/intro_fracture_cinematic.md（R065 / R060 / R061 / 0235 の節）
- 凍結像と投影・撮影権限 → memory/presentation_redesign.md の 1〜100 行
- 題字の版・渦・焼け → memory/title_screen.md
- 開始判定の経緯 → archive/show-design-2026-10-22.md:395-660
- 段の旧構成と覆いの開口 → archive/streaming-2026-10-22.md:819-1025
- 封印の箱を戻す → reference/attic-sealed-box.md / memory/sealbox_surface.md
- 隔離殻（終幕で使う） → archive/show-design-2026-10-22.md:661-713
