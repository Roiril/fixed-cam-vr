# 博士の日本語音声 — Eleven v4 入力原稿（v3・2026-11-12）

2026-11-12 に博士の台詞を書き換えた（`canon/OPEN.md`「シナリオの組み直し」。シュビーの案で、ユーザーの判定はまだ無い）。
`doctor-ja-v2-original.mp3` は前の台詞の音声なので、タブレットの場面からは外してある。
下のコードブロックを一度に ElevenLabs へ入力して音声を作り、`ingest-doctor-audio.py` と `ingest-doctor-video.py` で
4 場面へ切り出してから、`briefing-v1.json` の各場面に `video.ja` を戻す。このファイルは `export-briefing.py` では上書きしない。

演技の指定と間の取り方は、ユーザーが調整した v2（`elevenlabs-input-ja.md`）の書き方をそのまま使っている。
変えた文は 6 つ。変えていない文（はじめまして／手すり／ゆっくり歩く／長押し／装着／健闘を祈ります）は v2 と 1 字も違わない。

| 場面 | 変えた文 | 置いた種 |
|---|---|---|
| 調査依頼 | あなたに、ある調査を依頼します。調査員は、あなたで三十七人目です。 | 前に 36 人いた |
| 探索方法 | こちらのカーテンです。ある家の子ども部屋から、回収しました。 | 相手は子ども |
| 探索方法 | …このカーテンの周りを三周してください。廻るほど、近づいてきます。 | 3 周する理由 |
| 報告 | これが、怪異を記録し、解析する観測装置です。 | （言い回しだけ） |
| 報告 | 怪異を直接見ると、見返されます。だから、装置があなたの代わりに見ます。 | 外から自分を見る理由 |
| 報告 | 装置のエージェントが解析し、取り除きます。 | 報告すると消える |

```text
[deep, resonant male voice, calm and authoritative, restrained emotion, slow deliberate delivery, cinematic Japanese drama performance]

はじめまして。

[pause]

日本怪異研究所へ、ようこそ。

[short pause]

私は、あなたの上司にあたる……博士です。

[long pause]

[serious, quietly commanding]
あなたに、ある調査を依頼します。

[pause]

[flat, matter-of-fact, no emotion]
調査員は……あなたで、三十七人目です。

[long pause]

[lower voice, ominous, deliberate]
……こちらの、カーテンです。

[pause]

ある家の、子ども部屋から……回収しました。

[long pause]

[calm, precise, instructional]
観測装置をつけ、このカーテンの周りを……三周してください。

[pause]

[lower voice, quiet warning]
廻るほど……近づいてきます。

[long pause]

[calm, precise, instructional]
その間は、右手で手すりを持ち……
決して、離さないでください。

[long pause]

[slightly tense, restrained warning]
異変を見逃さないよう……
ゆっくり。慎重に、歩いてください。

[long pause]

[calm, authoritative]
これが、怪異を記録し、解析する……観測装置です。

[pause]

[serious, slow, deliberate]
怪異を、直接見ると……見返されます。

[short pause]

だから、装置が……あなたの代わりに、見ます。

[long pause]

[calm, precise, instructional]
異変に気づいたら……
左コントローラーの、エックスかワイを、一秒間、長押ししてください。

[pause]

装置のエージェントが、解析し……取り除きます。

[long pause]

[quietly commanding, final instructions]
では。

[pause]

左手にコントローラーを持って……
観測装置を、つけてください。

[long pause]

[low, restrained, faintly ominous]
……健闘を、祈ります。
```
