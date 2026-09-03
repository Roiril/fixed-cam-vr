---
name: show-language
description: 体験者が選ぶ言語（日本語/English/Français）を触る前に。訳す面と訳さない面の線／文言を置く場所がフォントのベイクで決まる話／「非ASCII＝全角」ではない／打鍵の速さは言語で変えられない
metadata: 
  node_type: memory
  type: project
  originSessionId: 812d83b0-0fea-4ffe-a970-ad213de45d31
  modified: 2026-09-03T06:35:39.446Z
---

# 言語選択（2026-09-03・`canon/LEDGER.md` 0127）

体験者が**体験前の注意書きが出ているあいだだけ**、手元（左）の**どれかのボタン**で
日本語 → English → Français → 日本語 と巡らせる（0128）。正は
[`ShowLanguage`](../../Assets/Scripts/Streaming/ShowLanguage.cs)（static）。

契約は **`rules/show-design.md`「体験者が読む面には言語が 3 つある」が正本。**
ここは実装の経緯と、次のシュビーが踏みそうな罠だけ。

## 訳す面と訳さない面

**訳すのは体験者が読む 4 つだけ** — 注意書き（`TitleNotice`）/ AIエージェントの連絡 8 通
（`CommsPanel`）/ 手元の「解析中」（`VisitorMarkGuidance`）/ 終幕の報告 4 行（`OutroReportText`）。

**スタッフが読む面は日本語のまま**（`StatusHud` / `ControllerGuidePanel` /
`RegistrationGuidance` / `RecoveryGuidance` / 黒の上の 1 行）。読み手が日本語のスタッフなので、
訳すと現場が読めなくなる。⭐ この線は `rules/show-design.md` の
「HMD に出す文言の規約」の面の表と**同じ線**（門は `StatusHud.StaffViewing`）。

## ⚠⚠ 訳文は「その面を持つ .cs の中」に置く

フォントは**静的ベイク**で、収集元は `JapaneseHudFontSetup.CollectHudCharset()` の配列
（＝ `tools/unity.ps1` の `hud-font` の `Src` と 1:1）。上の 4 つは**もともと全部そこに入っている**ので、
訳文をその中に書く限り**フォントの配線を 1 行も触らなくてよい**。

⇒ **`ShowLanguage` は ASCII しか持たない**（`Code()` の "ja"/"en"/"fr" だけ）。
言語の名前「日本語 / English / Français」は **`TitleNotice` が持っている** — そこが唯一
画に出す場所だから、というより**そこが収集元だから**。
⚠ 新しいファイルへ非 ASCII を出して収集元の追加を忘れると、
**実機で豆腐になるのに警告が 1 件も出ない**（0035 の「声」と同じ型）。

## ⚠⚠ 「非 ASCII ＝ 全角」ではない

行が枠に収まるかを机上で見る物差しを `c < 0x80 ? 0.5 : 1` と書いていて、
**フランス語のアクセント付き（é è à ç …）が全角 1 と数えられた**。
« L'anomalie a été supprimée. » が 15/14 で落ちて、収まっているのに文言を削りかけた。

⇒ 物差しは [`HmdTextStyle.LineWidth`](../../Assets/Scripts/Diagnostics/HmdTextStyle.cs) **1 か所**
（U+0370 未満と U+2010〜201F が半角）。テストも `CommsPanel` の折り返し幅もここを読む。
⚠ **これは目安。実測は `menu text-audit -Set lang=ja|en|fr`**（3 言語ぶん通す）。
絵は言語ごとに別名で残る（`_en` / `_fr` の接尾辞）。

## ⚠ 打鍵の速さは言語で変えられない

装置の印字は 12 文字/秒の 1 つだけ（`CommsPanelLogic.CharsPerSec`）。
Latin は同じ内容で**日本語の約 2.4 倍の文字数**になる（①b が 37 → 75 文字）。
`MaxTypeSec` を 3.5 のままにすると上限が効いて**English のときだけ速く打つ** ＝
実機で 1 言語だけ壊れる。⇒ **3.5 → 7.0 へ上げた。日本語の見え方は 1 ビットも変わらない**
（日本語の最長 37 文字 = 3.08 秒で、旧上限でも一度も効いていない）。

終幕の報告も同じ理屈で伸びる（日本語 3.4 秒 / English 6.8 / Français 7.3）。
テストの上限は言語で分けてある（日本語 6 秒 / Latin 8 秒）— ここを日本語の値で縛ると
**4 行を削るしかなくなる** ＝ 言語で終わり方が変わる。

## ⚠ 入力は増えていない

左で読むのは同じ集合のままで、**注意書きが出ている段だけ意味が変わる**
（A が 0043 で「カメラ送り」→「タイトルを閉じる」へ変わったのと同じ形。
2026-07-23 の入力凍結は生きている）。

- **門は「面が画に出ているか」**（`TitleNotice.IsShowing`）。段（`TitleStage.Wait`）だけを見ると、
  フォントが解決できず面が組めていない現場で**見えない切り替えが起きる**
- ⚠ **そのあいだ報告のゲージを進めない。** 進めると**言語を選んだだけで異変の報告が 1 件立つ**
  （終幕の報告の数が、まだ始まってもいないのに 1 から始まる）
- 選び方が巡回なのは、**被った体験者に手元が見えない**から（0050 で X と Y を同じにし、
  0128 で左の全ボタンへ広げた ＝ どれを押しても「次へ」）。
  だから 3 つの名前を常に全部出し、**いま選んでいるものを角括弧で囲む** —
  この面は `richText` を切ってあり白 1 色しか出せないので、囲みが唯一の手掛かり

## ⚠ 戻すのは 1 か所

`TitleScreen.BeginTitle`（タイトルの出し直し）。相の遷移からもランリセットからも卓の ⏭ からも
必ずここを通る。**置き場所を増やすと「戻し忘れ」ではなく「二重に戻して体験者の選択が消える」**側の
事故になる。⚠ 実体を組めていない現場（早期 return の手前）でも戻す。

## 観測

`ev=sum` の **`lang` / `langN`（対で出す）**。**画にも音にも出ない** — 走行の PNG に写るのは
「文字が出ている」ことだけで、それが日本語だったのか English だったのかは読めない。
`lang` だけだと「選ばれなかった」と「押しても切り替わらない」がどちらも `ja` で区別できない。

`analyze-xp-log.py` は **「注意書きが引っ込んだ後にも `langN` が増えた」を FAIL** にする
（＝ `IsShowing` の門が漏れている。体験者が異変を報告するたびに文面の言語が変わる）。

## 触ったら通すもの

```powershell
.\tools\unity.ps1 test                      # 文面の幅・行数・打鍵の尺（3 言語ぶん機械が測る）
.\tools\unity.ps1 menu hud-font             # 静的ベイク（Latin のアクセントもここで焼く）
.\tools\unity.ps1 menu scene                # 面の寸法はシーンに焼かれる
.\tools\unity.ps1 menu text-audit -Set lang=en   # ja / en / fr の 3 回
```

⚠ フォントの元（`SourceHanSansJP-Normal.otf`）に **Ÿ（U+0178）だけが無い**（実測）。
フランス語で使う字ではないので実害は無いが、文言に入れたらベイクの「欠落 N 文字」に出る。
