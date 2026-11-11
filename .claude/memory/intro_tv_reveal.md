---
name: intro-tv-reveal
description: 導入のテレビ方式（0309）。パススルー → 暗転 → テレビが出る → 電源 → 映像。筐体は参照の写真を灯りで出し、段 4 から体験の終わりまで出したまま（0321）。割れる遷移は transition=shatter で残した（本編は枠なし）。
metadata:
  node_type: memory
  type: project
  originSessionId: 5695c35d-4095-4436-b8a2-1e906bf003c1
  modified: 2026-11-11T01:32:55.036Z
---

依頼は LEDGER 0309。段も相も増やさず、`IntroLogic.TvWeights` の重みだけで作った。経路は [[intro-fracture-cinematic]] と同じ `IntroStage`。

## 重みと材質

| 段 | 見えるもの | 重み |
|---|---|---|
| 2 暗転 | 現実が暗くなる | `passthrough` = 1 - `Darken(p)` |
| 3 テレビが出る | 黒の後、テレビが 7m 奥から定位置へ寄る。筐体とガラスの灯りは同じ量で、霧の式で上がる | `tvDepthM` = 7 × (1-q)²、`tvCase` = `ScreenFogLogic.Transmit(2 + tvDepthM)` × `Reveal(q/.22)`、`tvDark` = 1 - `tvCase` |
| 4 電源 | 既存の管の点灯 → 映像 | `ignite` = p/.70、`live` = `Reveal((p-.62)/.38)`、`tvCase` = 1 |
| 5 静止 | 映像のまま静止。筐体は残る | `tvCase` = `MainShowTvCase`（1） |
| 本編（Off / Done） | テレビの枠がついたまま映る | `tvCase` = `MainShowTvCase`、`tv` = false |

- `_CrtTvCase`（輪郭の外の筐体）と `_CrtTvDark`（消えた管の反射と輪郭内の筐体を落とす）は `ScreenComposite` の HideInInspector。材質の既定は 0（割れる遷移の本編 ＝ 輪郭で clip）
- 筐体の色は参照写真 `CaseColor`。写真の明るさ（樹脂で sRGB 25〜45）のままだと闇に沈み、縁だけ光る黒い板に見えたので 2.2 倍し、外周を闇へ溶かす
- 輪郭の判定は clip と同じ .5（滑らかな境界だと輪郭の 58 画素が最大 52 階調ずれた）

## 本編も枠つき（0321・2026-11-11）

ユーザー「テレビの枠が急になくなっている気がするので、枠は常時つけてください。」。それまでは段 5 で筐体を沈め、0302 の枠なしの形で本編へ入っていた。

- 重み: `TvWeights` の `Swap` / `Off` / `Done` / `default` が `MainShowTvCase`。`IntroWeights.Inactive` は割れる遷移の本編のまま（`tvCase` 0）で、テレビ方式は `TvMainShow` が筐体だけ差し替える
- 材質: `IntroDirector.ResetIgnite` が方式で書き分ける（`_logic.Transition` が Tv なら 1、Shatter なら 0）。**畳むすべての経路がここを通るので、0 を直書きすると本編で枠が消える**
- 導入を通らずに本編へ入る経路も塞いだ: `OnEnable` で相が Intro でないとき、`OnIntroDefChanged` で演出の外にいるとき（方式が変わったら書き直す）にも `ResetIgnite`。⏭・`!enabled`・中止・位置合わせ中・相の変化は元から `ResetIgnite` を通る。2 人目以降は `BeginIntro` → 段 0 の重み（`tvCase` 0）で、導入の中でまた上がる
- シェーダ: 筐体の色は `_SwitchDim` / `_ScreenPower` を掛けた後に上書きされるので、`_ScreenPower` だけ掛け直した（終幕の電源断で筐体だけ光って残らないように）。`_SwitchDim` は掛けない — カメラ切替の暗転は映像の出来事で、筐体はその間も見え続ける
- 観測: `analyze-xp-log.py` はテレビ方式で導入後の最後の `tvCase` が 1 未満なら FAIL「本編で筐体が消えている」
- 3-C の増えるスクリーン（`ScreenMultiply`）はメインスクリーンを筐体ごと取り込み、面の枠はメインの筐体そのもの（0324）。管と筐体の境はメインの輪郭マスクで切る（[[screen_multiply]]）
- HMD と実機は未確認。`menu intro` / `menu screen-wall` の絵も撮り直していない

## 奥から寄る（0318・2026-11-10）

- 奥行きは `ScreenAnchor.SetIntroDepth` が据えた位置から前方へ足す。据えていない間は何もしない。解除で定位置へ戻す
- 大きさは変えない。遠いので小さく見える。見えはじめは距離 7.4m で、止まるまでに見かけの幅が 3.7 倍になる
- 2 乗の減速にした。3 乗で 12m から寄せると、見えている間の速さが 7m/s になる
- `IntroPreview` は同じ値を画面の z へ足す。帯に `tvZ=` が出る
- 被っていない。寄ってくる速さと圧迫感は HMD で見る

## 踏んだ罠

- **覆いは矩形の内側だけ alpha を `_ScreenFade` へ置き換える。** 全画素を同じ係数で落とすには `_ScreenFade` = `1 - passthrough`。`live` を渡すと暗転の途中で矩形が浮く
- **テレビが出る前にスクリーンを描くと、暗転の途中に矩形が 2〜3 階調浮く。** 消えた管でもわずかに光が残るため。`IntroVeil.TvScreenHidden` で描かない。プレビューは覆いが手を出さない約束なので `IntroPreview` が同じ関数を読む
- **段 3 の頭で覆いが要らなくなると `Apply` は `SetHidden` へ落ち、描画の止めを解く。** 黒の保持にガラスの縁が sRGB 12 で浮いた。`IntroPreview` は描画の可否を自分で書くので `menu intro` には出ず、ツインの動画で見つかった。`!needed` の経路でも止めを掛け直す。観測は `ev=intro` の `scrHid=`（段 2・段 3 の行が 1）
- **消えた管のガラスにも `(1 - transmission) × 0.006` の光がある。** 描き始めの瞬間に縁が跳ねるので、これも `tvLit` で落とす
- 位置の据え置きは `ScreenAnchor` の専用の口。`SignalLostFx` が `SetFollowFrozen` を書くので相乗りできない
- `IntroPassthroughCapture` はテレビ方式でカメラを準備しない（静止画を使わない）。起動時の権限要求は割れる遷移へ戻せるよう残した

## 実走行の尺

段 4 は `frameSec` の後で中央の保持 0.5 秒を数えるので、走行では 3.5 秒になり、導入の合計は 11.3 秒（ツイン 2 回の実測 3.47 / 3.51 秒）。割れる遷移と同じ判定で、映像が見えている時間が延びるだけ。

## show.json

`run.intro` の尺は現場設定で、卓の通常起動は演出の変更を 403 で拒否する。変えるのは `FIXEDCAM_AUTHORING=1` で起動した卓から（[[show-json-is-live-config]]）。
旧尺のままでもテレビ方式で走る（段 2 が 3.5 秒、段 4 が 5.0 秒に伸びる）。`menu intro -Set style=tv` はコード既定の尺で撮る。
