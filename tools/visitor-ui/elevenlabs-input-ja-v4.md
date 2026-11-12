# 博士の日本語音声 — Eleven v4 入力原稿（v4・2026-11-12）

2026-11-12 のユーザー依頼（`canon/LEDGER.md` 0327）で博士の台詞を書き直した。名を伏せた上司・管理番号 0514・
空き家の子供部屋のカーテンと人形・「特定の条件下でのみ、異常性が発現します」は、ユーザーが口にした骨子。残りの文はシュビーの案（`canon/OPEN.md`「博士の説明の書き直し」）。
下のコードブロックを一度に ElevenLabs へ入力する（0257）。`generate-doctor-audio.py` がこのファイルを読んで合成し、
1 字ごとの時刻から場面と文へ切る。このファイルは `export-briefing.py` では上書きしない。

演技の指定と間の取り方は、ユーザーが調整した v2（`elevenlabs-input-ja.md`）の書き方を使っている。
2026-11-12（LEDGER 0331）: 「装置」が「ちょうち」と読まれたので、原稿だけ「そうち」とかなで書く（字幕は漢字のまま）。研究所の名もかなで書く。
文の頭を変えたら `generate-doctor-audio.py` の `ANCHORS` も同じ字に直す。

```text
[deep, resonant male voice, calm and authoritative, restrained emotion, measured delivery, cinematic Japanese drama performance]

ようこそ。……にほんかいいけんきゅうじょへ。

[pause]

私は……あなたの上司、とでも……言っておきましょうか。

[pause]

[serious, quietly commanding]
あなたは、今日から……この研究所の、調査員です。

[short pause]

これより、調査対象について、説明します。

[long pause]

[flat, matter-of-fact, reading from a file]
調査対象は、管理番号、ゼロ・ゴー・イチ・ヨンに指定された、異常存在です。

[short pause]

[lower voice, deliberate]
ある空き家の、子供部屋から見つかった……カーテンと、人形です。

[pause]

[calm, precise]
外見上は、一般的な人形と、変わりません。
[lower voice, quiet warning]
しかし……特定の条件下でのみ、異常性が、発現します。

[pause]

[flat, matter-of-fact, no emotion]
その条件も、異常性の正体も……まだ、判明していません。
[serious, quietly commanding]
それを調査するのが、あなたの任務です。

[long pause]

[calm, precise, instructional]
かんそくそうちをつけ、このカーテンの周りを、三周してください。

[pause]

足元は、見えません。
右手で手すりを持ち、決して離さず、ゆっくり、歩いてください。

[long pause]

[calm, authoritative]
これが、怪異を記録し、解析するための、かんそくそうちです。

[short pause]

そうちには、室内のカメラが捉えた映像が、映ります。

[pause]

[serious, deliberate]
どのような異変が起きるかは……分かっていません。
映像に、少しでも違和感を覚えたら、報告してください。

[pause]

[calm, precise, instructional]
報告するときは、左コントローラーのボタンを、一秒間、長押ししてください。
エックスとワイ、どちらでも、構いません。

[short pause]

報告の内容は、そうちのエージェントが、解析します。
見間違いでも、構いません。

[long pause]

[calm, precise, instructional]
調査が終わったら、係の者がそうちを外すまで、動かずに、お待ちください。

[short pause]

続けられなくなったら、その場で立ち止まり、そうちを外してください。

[pause]

[quietly commanding, final instructions]
では。
左手にコントローラーを持って、かんそくそうちを、つけてください。

[pause]

[low, restrained, faintly ominous]
……健闘を、祈ります。
```
