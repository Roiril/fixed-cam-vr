---
name: ending-farewell-doll
description: 帰還の別れの人形。L字壁の内側で対面し、写真成功後に退場する。LEDGER 0291・0293
metadata:
  node_type: memory
  type: project
  originSessionId: 2b89953c-e90d-428a-bc15-5060ba8fefc5
  modified: 2026-10-30
---

2026-10-30 のユーザー図（0293）に合わせ、帰還後の人形を L 字壁の内側の固定位置へ立てる。
0291 で台車（Romi）は不使用になった。写真条件は人形が左・体験者が右で互いに向き合うこと。

**本番の順序**: 報告 → 入れ替わり（人形→人）→ CG 層が空く → `BeginFarewell` →
現れる0.35秒 → 見つめる最短1.15秒（写真未成立なら保持）→ GPUへ写真を撮れた通知 →
後ろを向く0.75秒 → 壁端を回って遠ざかる6.0秒（最後4.5秒で薄くなる）→ カット自然終了 → 終幕。
`EndingFarewellLogic.Tick` を通さず `SampleAt` だけで描くと写真待ちが抜ける。

**立ち位置**: `TrySolveAtWall` は床上の共有頂点と2辺の端点を受ける。
内側と開いた側を2.5cm刻みで探索し、実カメラに投影して左を人形・右を体験者にする。
頭・胴・足を対面yawで回して投影し、画の縁から0.5%以上空くことを確認する。
一般人体近似として頭は半幅0.10m/厚み0.11m、胴は半幅0.25m/厚み0.13m、足は半幅0.11m/前後0.20m（身長比例）。
全身の最大半径を足の前後長へ転用しない。写真の人物・人形の表面間に4cmの離隔を取る。
身体の表面と壁厚の間に4cmの余白が必要。身体の各断面と壁の交差も拒否する。
頭や足まで最大胴幅を使うworld AABBや円柱の投影は、空の隅で候補を落とすため使わない。
`Anchor.visitorTargetFoot` が体験者の停止位置。全身が入る組合せが無ければfalse。
最初の固定比率0.30/0.78では、保存プレートAのRemyの頭が切れ、全身凸包が壁へ1.5cm入ったため廃止した。
退場は `Anchor.retreatCorner` を通り `retreatEnd` へ進む2区間。
壁の端を人形の半径と壁厚に加え4cm以上空けて回り、人との距離が単調増加する経路だけを選ぶ。
終点ではカメラとの距離も増え、投影した背丈が撮影時より小さくなることを条件にする。
向きは現在位置から人とカメラへの平均方向の逆。両方へ背を向ける。
`PoseAt` は本番の `PlaceFarewell` と保存プレートのgateが共有する。
`sample.retreatM / RetreatM` を道のりの進行率へ写す。Doneのsampleが距離0でも終点を保つ。
`RetreatM=.45` は名目進行パラメータで実移動距離ではない。最大速度は実折れ線長と6秒の時計で計算し0.35m/s以下に限る。
頂点からの放射方向へ直進するだけではAでカメラへ近づき白い顔を見せたため廃止した。
本番Toyの45,523 CPU頂点のboundsはx=[-.14244622,.15675569], z=[-.07251731,.07268838]m。
全回転を包む半径上限はsqrt(.15675569²+.07268838²)=.17278873m。既定の回転半径は0.174m（背丈.4m時）。
`dollBodyRadiusM` にこの半径を渡し、写真の位置から回転・退場の全期間に同じ壁余白を使う。
`trajectoryAccepted(Anchor)` は回転と退場の実CPU全身の壁余白を事前に測り、NGなら別候補を探す。
`ShowCgLayer.TryAnchorFarewell` は `room.walls` の共有頂点を探し、`room.floorY` と
course→world変換を通す。枠UVから決める `TrySolve` は既存プレビューとの互換用。
本番の人物身長は追跡した頭の床上高+0.12m（1.2〜2.1m）。
`canStand` は床範囲を制約するoptionalで、本番はroom.floorの外側0.5mまでを探索範囲にする。
`pairAccepted(visitorWorld,dollWorld)` は保存プレート検証で実CPU頂点・全身凸包の不合格候補を
拒否するoptional。NGなら次候補を調べる。候補の人体近似だけで実Remyの合格を宣言しない。

**撮影条件**: `IsPhotoReady` は体験者が右の点から0.20m以内かつ、人と人形のyawが
互いへの方向から35°以内であること。`FarewellPhotoReady` は追跡済みの頭の向きも要求する。
`EndingFrameCapture` が合成GPU転写に成功すると `NotifyEndingFarewellCaptured` を返す。
成功した1枚は上書きしない。人形なしの仮写真は本番では撮らない。
立ち位置が解けない場合は見えないまま1秒ごとに再試行する。

**待機と終了**: 帰還ライブの別れが未完了の間は `TakeRunnerLogic.Tick(holdCurrentStep:true)` を使い、
カット時限とwatchdogで終わらせない。写真待ちと退場が済むまでライブが続く。
報告待ちカットは保持しない（保持するとライブへ移る前に停止してしまう）。
固定7.1秒より写真と退場の成功を優先する。中断やランリセットは既存の終了処理を通る。

**人形の器**: `ShowCgLayer` の1体を使う。入れ替わり中に出すと取り合うのでCGが空くまで待つ。
位置が解けるまで強さ0。腕入力は `ShowBodyInput.None`。笑わない。
床への投影影と接地影は通常の人形と同じ。`room.floorY` に足元を合わせる。

**検証**: `EndingFarewellLogicTests` に写真待ち・L字配置・位置と向きの条件を置き、
`TakeRunnerLogicTests` にwatchdog時間を超える待機後の自然終了を置く。
保存プレート検証は `G:\マイドライブ\shubie\renders\plate-validation\`。
2026-10-30の保存プレートでは通常9配置・歩行3配置・遮蔽12配置・終幕3視点を実行。
Aの対面写真は人が右・人形が左で全身が入り、実撮影の同じRenderTextureをOutroReportへ渡した。
回転/退場の全身壁余白最小0.069m、体験者距離0.426→1.338m、カメラ距離1.803→2.254m、像高123.0→103.6px。
実移動1.286m、最大0.246m/s、最後のalpha=0。30fps連番と動画で左へ背を向けて消える順を確認する。
実機Aは移動済みなので保存プレートが今回の完了条件。最終の出力導線は [virtual_walk.md](virtual_walk.md)。

**台車を外した範囲**: Quest側の `RomiCartLink` / `RomiCartLinkLogic` / テストとテレメトリ。
タブレットの台車パネルと `tools/romi/` とファームは残っている（使わない）。

関連: [[ending-roll]] [[romi-cart-esp32]] [[swap-veil-mask]]
