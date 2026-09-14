---
name: visitor-sound-reset
description: 体験者交代で音がリセットされていなかった（ResetRun が本番から 1 度も呼ばれていない）。2 人目以降だけ壊れる型の見つけ方
metadata: 
  node_type: memory
  type: project
  originSessionId: aa629b9d-978a-41e5-b9f1-d22062359145
  modified: 2026-09-04T13:55:32.044Z
---

# 体験者が代わっても音が戻っていなかった（2026-09-04 修正）

`ShowSoundDirector.ResetRun()` は「**ラン開始（体験者交代）。前の体験者の音を持ち越さない**」と
自分で書いてあるのに、**本番コードから 1 度も呼ばれていなかった**。参照はテスト 3 本だけ。

## 何が起きていたか

1. **人形の笑いの方角が 1 人目のまま固定。** `SpatialAudio.PickLaughSpots` を呼ぶのは
   `Awake` と `ResetRun` の 2 か所で、`ResetRun` が死んでいたので**実質 `Awake` だけ** ＝
   `canon/LEDGER.md` 0130 の「ランダムな位置」が**1 人目にしか成立していなかった**
2. 前の体験者の一撃音・敷く音・頭出しラッチが落ちず、**次の体験者のタイトル画面へ流れ込む**

⚠⚠ **どちらも画にも録画にも 1 ビットも出ない**（音は録画に映らない）。展示は数十人が連続で
体験するので、**1 人目だけが設計どおり**という状態で会期を通すところだった。

## 直し方

`ShowControlClient` の**体験者交代の 2 経路**へ 1 行ずつ。すぐ隣に同じ形の
`ResolveBgmDirector()?.ResetRun()` が既にあるので、そこへ並べる。

- `TriggerRunReset()` — 卓の ▶ ラン開始（`control.runEpoch` の変化）
- `BeginNewVisitorRunLocal()` — 現地の右 A 2 秒長押し

⚠ **`BeginMainRun()` には入れない。** あれは「導入 → 本編」で体験者は代わっていない。
入れると走行の途中で笑いの方角が引き直され、**人形が歩いて聞こえる**。

## この型の見つけ方（同じものがまだあるかもしれない）

**「体験者交代で落ちるべき状態」を持つクラスは、`ResetRun` の呼び出し元を数える。**

```bash
grep -rn "ResetRun" Assets/Scripts/ --include=*.cs | grep -v /Tests/
```

`ShowControlClient` の 3 つのリセット経路が呼んでいる相手を並べると、**呼ばれていない実行体**が
そのまま浮く。2026-09-04 時点で呼ばれているのは cueScheduler / timelineDirector /
BgmDirector / SegmentRecorder / SoundDirector（今回追加）。

⭐ `canon/OPEN.md` に「**2 人目の体験を機械で確かめる手段が無い**」と既に書いてある
（自動走行 `quest-record.py --walk` はランを 1 回しか回さない）。**この穴がこのバグを生んだ**。
埋めるなら走行の最後にもう一度リセットして導入だけ流し、`analyze-xp-log.py` に
「2 回目のランでも `ev=sfx` が同じ本数出たか」を入れる。

## 確かめたこと / 確かめていないこと

**確かめた**（2026-09-04・Quest α 実機）:

- EditMode **1729/1729** 通過
- 卓から `control.runEpoch` を 2 回上げて、`[ShowControl] ラン開始（runEpoch=1 / 2）` が実機に 2 回出た
- **例外なし**（`poll error` も無し）。`sndBuilt=47/0` ＝ 音源 47 本すべて解決・欠けなし

**確かめていない**: **笑いの方角が実際に人ごとに変わること。** 呼びかけは体験が区間を進まないと
発火しないので、この検証では `sndCall=0/-1` のまま。⚠ 実機で通しの走行を 2 本続けて
`sndCall` の方角が変わることを見るまでは、**直ったとまでは言えない**。

⚠ ビルドのついでに踏んだもの: **`.cs` のコメントの日本語もフォント収集に入る**ので、
コメントを 1 行足しただけで `menu hud-font` の再ベイクが要る（[[hmd_text_style]]）。
再ベイクの `欠落 3 文字: '▯📍'` は**無害**（豆腐そのものの字と、卓 UI の絵文字。HMD には出ない）。

関連: [[onsite_day_ops]] / [[sound_pipeline]] / [[show_run_skeleton]] / [[hmd_text_style]]
