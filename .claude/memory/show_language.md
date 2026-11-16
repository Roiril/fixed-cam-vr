---
name: show-language
description: タブレットで選ぶ日本語/English/FrançaisをHMDへ反映する境界。HMD内では選び直さない。翻訳面と静的フォントの注意。
metadata:
  node_type: memory
  type: project
  modified: 2026-09-14
---

# タブレットで選んだ言語をHMDへ反映する

2026-09-14 から、注意事項と言語選択とホラー軽減の設定は装着前のタブレットで行う。
HMD 内の `TitleNotice` は常に非表示。左コントローラーの入力で言語や軽減設定を変えない。

## 設定を受け取る場所

- 正は `VisitorPrefs` が受け取ったタブレットの選択。
- `TitleScreen.BeginTitle` が前の体験者の値を戻し、その直後に `VisitorPrefs.ApplyAtTitle()` を適用する。
- 導入中に新しい選択が届いた場合は、`TitleStage.Wait` のあいだだけ `ApplyPending()` で反映する。
- 題字の表示を始めて `Wait` を出たら選択を消費する。次の体験者へ持ち越さない。
- タブレットから値が届かなければ日本語と通常の音を使う。

## 翻訳する面

体験者が読む文面は日本語 / English / Français を持つ。

- 接続確認と報告練習を含む `CommsPanel`
- 題字下の開始案内を持つ `TitleScreen`
- 本編中の `VisitorMarkGuidance`
- 終幕の `OutroReportText`
- スクリーン左上の周回表示

スタッフ用の `StatusHud` と登録案内は日本語のままにする。
旧 `TitleNotice` の翻訳文は互換と検査のため残っているが、HMDには描画しない。

## 静的フォント

HMD の文字は `JapaneseHud SDF.asset` へ事前に焼く。新しい表示文を持つソースは
`JapaneseHudFontSetup.CollectHudCharset()` と `tools/unity.ps1` の `hud-font.Src` の両方へ追加する。
現在は `TitleScreen.cs` も収集対象。

文言を変えたら次を実行する。

```powershell
.\tools\unity.ps1 menu hud-font
.\tools\unity.ps1 menu text-audit -Set lang=ja
.\tools\unity.ps1 menu text-audit -Set lang=en
.\tools\unity.ps1 menu text-audit -Set lang=fr
```

Latin のアクセント付き文字は半角として幅を測る。`HmdTextStyle.CharWidth` と各文面テストがこの前提を持つ。
周回表示は独自のOSD版を使うため、訳語を変えた場合は `make-osd-font.py` 側も更新する。

## 英語版の残り（2026-11-16 調査）

ユーザー依頼「英語版がちゃんと機能しているのか、何か英語にし切れていないところがないかなど調査してきてほしい」。
英語の通しは `py -3.11 tools/twin-verify.py --lang en --fast`（PASS・走行 20261116_124951）。報告は Drive `reports/2026-11-16_fixed-cam-vr_english-audit.html`。

- HMD で日本語が残るのは 2-C の WARNING の下の 4 語だけ。`UnauthorizedAccessEffect` が焼いた PNG（`Assets/Art/Textures/UnauthorizedAccess/{subtitle,context,attempt,failed}-v5.png`）を言語に関係なく貼る。英語版の PNG と差し替えの口が無い
- タブレットの英仏は、報告の説明（`report` の 6・7 文目）でコントローラーの図が出ない。`isBriefingLoopCue` が歩く映像を続け、CSS が図を隠し、X・Y の輪と「Hold 1 s」だけが映像の上に浮く。日本語は口の動く映像なので出ない（映像が読めなかったときだけ同じになる）。`verify-runtime.cjs` は捕まえない
- 英仏には博士の声が無い（字幕だけ）。日本語と同じ声とモデルで作った英語の試作が Drive `audio/fixed-cam-vr/doctor-en-trial-v1.mp3`（131 秒・未採用）
- 人形の呼びかけ `sfx_doll_call` は日本語の声（0109 のユーザーの録音）。STT は短すぎて読めない
- `CommsNotice.Greeting` の英訳 "My apologies." は「申し遅れました」の意味から外れる
