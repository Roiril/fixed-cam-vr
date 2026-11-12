# 博士の日本語音声 — 入力原稿（v5・2026-11-12）

2026-11-12 のユーザー依頼（`canon/LEDGER.md` 0338）で、何も知らない読み手 3 人に台詞を読ませ、重なった指摘から直した版。
名を伏せた上司・管理番号 0514・空き家の子供部屋のカーテンと人形・「特定の条件下でのみ、異常性が発現します」は、ユーザーが口にした骨子（0327）。
直した所と見送った所は `canon/OPEN.md`「博士の台詞を初見の読み手に読ませて直す」。
下のコードブロックを一度に ElevenLabs へ入力する（0257）。`generate-doctor-audio.py` がこのファイルを読んで合成し、
1 字ごとの時刻から場面と文へ切る。このファイルは `export-briefing.py` では上書きしない。

演技の指定と間の取り方は、ユーザーが調整した v2（`elevenlabs-input-ja.md`）の書き方を使っている。
読み違えやすい語は、原稿だけかなで書く（字幕は漢字のまま）。「装置」は「ちょうち」と読まれた（0331）ので「そうち」。
研究所の名は「にほんかい」（日本海）と続けて読まれないよう「・」で区切る（0332）。「物」もかなにした（読みを確かめる前の用心）。
文の頭を変えたら `generate-doctor-audio.py` の `TAKES["v5"]` も同じ字に直す。

⚠ 最後の場面（「そうちの合図で…」から後ろ）は、0348 で `elevenlabs-input-ja-v6-wear.md` の合成に替わった。
このファイルの最後の場面は、v5 の合成に入っている文の記録として残す（速さの基準がこの 20 文の中央値なので消さない）。

```text
[deep, resonant male voice, calm and authoritative, restrained emotion, measured delivery, cinematic Japanese drama performance]

ようこそ。……にほん・かいい・けんきゅうじょへ。

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
どちらも、外見上は、一般的なものと、変わりません。
[lower voice, quiet warning]
しかし……特定の条件下でのみ、異常性が、発現します。

[pause]

[flat, matter-of-fact, no emotion]
その条件も、異常性の正体も……まだ、判明していません。
[serious, quietly commanding]
それを調査するのが、あなたの任務です。

[long pause]

[calm, authoritative]
これが、怪異を記録し、解析するための、かんそくそうちです。

[short pause]

[calm, precise, instructional]
そうちをつけると、目の前に画面が、現れます。

[short pause]

そこに、室内のカメラが捉えた、あなた自身の姿が、映ります。

[pause]

その映像を見ながら、このカーテンの周りを、三周してください。

[short pause]

右手で手すりを持ち、決して離さず、ゆっくり、歩いてください。

[pause]

[serious, deliberate]
映像に、少しでも違和感を覚えたら、報告してください。

[pause]

[calm, precise, instructional]
報告するときは、左コントローラーのボタンを、親指で、一秒間、長押ししてください。
エックスとワイ、どちらでも、構いません。

[short pause]

何を見たかは、そうちに搭載されたエージェントが、解析します。
見間違いでも、何度報告しても、構いません。

[long pause]

[calm, precise, instructional]
そうちの合図で、歩き始めてください。
終わりの合図の後は、係の者がそうちを外すまで、動かずに、お待ちください。

[short pause]

続けられないときだけは、その場で立ち止まり、手すりを離して、そうちを外してください。

[pause]

[quietly commanding, final instructions]
では。
係の者の案内に従って、かんそくそうちを、つけてください。

[pause]

[low, restrained, faintly ominous]
……健闘を、祈ります。
```
