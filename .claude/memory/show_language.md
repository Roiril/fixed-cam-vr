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

同じ日に推奨どおり直した（LEDGER 0394「シュビーの推奨で作って。博士に関しては、吹き替えという体で、口パクは日本語のままで、再生は一旦なしで」）。いま残っているのは次の 2 つ。

- 人形の呼びかけ `sfx_doll_call` は日本語の声のまま（0109 のユーザーの録音。替えない推奨を `canon/OPEN.md` に置いた）
- 予告動画の英語版（`mawarimi-trailer-en-v2.mp4`）は、後半の画が日本語の走行から撮ってあるので、画の中のスイの文面・WARNING・画面の時計が日本語のまま。`twin-verify.py --lang en` の走行から撮り直せば英語で映る

直したもの（罠として残す分）:

- **WARNING の下の 4 語**は言語ごとの版。`UnauthorizedAccessEffect.UseLanguage(ShowLanguage.Current)` を `CommsTakeoverError.StartEffect` が `Play` の前に呼ぶ。英仏の版は prefab の `englishWords` / `frenchWords`。4 枚そろっていない言語は日本語で出る。版を焼き直したら、出力の `cap` を `UnauthorizedAccessEffect.LatinCapBoxes` と試験の `EnglishWords` / `FrenchWords` へ写す（本番の版は Read/Write 無効で、実行時に不透明の範囲を測れない）
- **報告の説明のコントローラーの図**: 歩く映像は `loopVideo.startCue` から次の図の文の手前まで。英仏で映像が図を覆い、X・Y の輪と「Hold 1 s」だけが浮いていた（日本語は口の動画なので出ず、動画が読めないときだけ同じ形になっていた）。`verify-runtime.cjs` の試験が図の文で映像が止まることを見る
- **英仏の博士**は吹き替えの版の動画（`room-proto/README.md`）。字幕は `film.<lang>.windowsMs` で切る。動画が読めないと英仏は声が無いので、媒体なしで文の長さで進む
- 試作した英語の声は Drive `audio/fixed-cam-vr/doctor-en-trial-v1.mp3`（131 秒・流さない）
- `CommsNotice.Greeting` の英仏は名乗りの前置き（"Let me introduce myself." / "Je me présente."）
