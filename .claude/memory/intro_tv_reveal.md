---
name: intro-tv-reveal
description: 導入のテレビ方式（0309）。パススルー → 暗転 → テレビが出る → 電源 → 映像。筐体は参照の写真を灯りで出し、段 5 で沈めて本編の枠なしへ戻す。割れる遷移は transition=shatter で残した。
metadata:
  node_type: memory
  type: project
  originSessionId: 5695c35d-4095-4436-b8a2-1e906bf003c1
  modified: 2026-11-05T12:03:15.118Z
---

依頼は LEDGER 0309。段も相も増やさず、`IntroLogic.TvWeights` の重みだけで作った。経路は [[intro-fracture-cinematic]] と同じ `IntroStage`。

## 重みと材質

| 段 | 見えるもの | 重み |
|---|---|---|
| 2 暗転 | 現実が暗くなる | `passthrough` = 1 - `Darken(p)` |
| 3 テレビが出る | 黒の後、筐体とガラスの灯りが同じ量で上がる | `tvCase` = `Reveal`、`tvDark` = 1 - `tvCase` |
| 4 電源 | 既存の管の点灯 → 映像 | `ignite` = p/.70、`live` = `Reveal((p-.62)/.38)` |
| 5 静止 | 映像のまま筐体だけ闇へ沈む | `tvCase` = 1 - `Darken(p)` |

- `_CrtTvCase`（輪郭の外の筐体）と `_CrtTvDark`（消えた管の反射と輪郭内の筐体を落とす）は `ScreenComposite` の HideInInspector。既定 0 で本編は変わらない
- 筐体の色は参照写真 `CaseColor`。写真の明るさ（樹脂で sRGB 25〜45）のままだと闇に沈み、縁だけ光る黒い板に見えたので 2.2 倍し、外周を闇へ溶かす
- 輪郭の判定は本編の clip と同じ .5。沈み切った時に本編と差が出ない形にしてある（滑らかな境界だと輪郭の 58 画素が最大 52 階調ずれた）

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
