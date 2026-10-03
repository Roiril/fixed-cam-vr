# 博士の日本語音声 — Eleven v4 用入力

正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。このファイルはそこから生成する。
Text to Speech で Eleven v4 と既存の博士の Voice を選ぶ。各章を別々に生成し、コードブロック内だけを貼り付ける。
4章とも Stability と Similarity を同じ値にする。v4 に Style と Speed のスライダーはない。
角括弧は発声指示。`[pause]` の秒数は固定されない。完成音声に合わせて JSON の `durationMs` を調整する。
数字とボタン名は読み間違いを避けるため、音声入力だけ漢字とカタカナで書く。字幕の表記は正本のまま。
章名は読み上げ文に含めない。音声は BGM や効果音を混ぜずに書き出す。
公式: [Eleven v4](https://elevenlabs.io/blog/eleven-v4) / [Text to Speech](https://elevenlabs.io/docs/eleven-creative/playground/text-to-speech) / [Audio Tags](https://elevenlabs.io/blog/elevenlabs-audio-tags-list)

## 01 調査依頼（introduction）

```text
[calm, measured] はじめまして。日本怪異研究所へようこそ。私はあなたの上司にあたる、博士です。
[pause] あなたに、ある呪われたオブジェクトの調査を依頼します。
```

## 02 探索方法（subject）

```text
[calm, measured] こちらのカーテンです。
[pause] 観測装置をつけ、このカーテンを三周してください。
[pause] その間は右手で手すりを持ち、離さないでください。
[pause] 異変を見逃さないよう、ゆっくり慎重に歩いてください。
```

## 03 XかYを長押し（report）

```text
[calm, measured] これが、怪異を記録・解析する観測装置です。
[pause] あなたの調査を支援するエージェントが搭載されています。
[pause] 異変に気づいたら、左コントローラーのエックスかワイを一秒、長押ししてください。
[pause] エージェントが解析してくれます。
```

## 04 装着案内（wear）

```text
[calm, measured] では、左手にコントローラーを持って観測装置をつけてください。
[pause] 健闘を祈ります。
```
