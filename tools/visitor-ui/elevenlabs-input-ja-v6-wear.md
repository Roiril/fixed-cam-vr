# 博士の日本語音声 — 入力原稿（v6・最後の場面だけ・2026-11-13）

2026-11-13 のユーザー依頼（`canon/LEDGER.md` 0348）で、最後の場面「終了と装着」の係の者の 2 か所を、
装置内のエージェントの指示に従う形へ替えた。この場面だけを合成する（ほかの場面は v5 の合成のまま）。
`generate-doctor-audio.py` の `TAKES["v6-wear"]` がこのファイルを読む。演技の指定は v5 の同じ場面と同じ。

「装置内」は、内を「うち」と読まれないよう、かなで「そうちない」と書く（字幕は漢字のまま）。
文の頭を変えたら `generate-doctor-audio.py` の `TAKES["v6-wear"]["anchors"]` も同じ字に直す。

```text
[deep, resonant male voice, calm and authoritative, restrained emotion, measured delivery, cinematic Japanese drama performance]

[calm, precise, instructional]
そうちをつけたら、そうちないのエージェントの指示に従って、歩き始めてください。
調査が終わった後も、そのまま、エージェントの指示に従ってください。

[short pause]

続けられないときだけは、その場で立ち止まり、手すりを離して、そうちを外してください。

[pause]

[quietly commanding, final instructions]
では。
かんそくそうちを、つけてください。

[pause]

[low, restrained, faintly ominous]
……健闘を、祈ります。
```
