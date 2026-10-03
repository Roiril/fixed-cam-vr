# 博士の日本語音声 — Eleven v4 用入力

正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。このファイルはそこから生成する。
Text to Speech で Eleven v4 と既存の博士の Voice を選ぶ。全台詞を一度に生成し、下のコードブロック全体を貼り付ける。
v4 の設定項目は Stability と Similarity。Style と Speed のスライダーはない。
角括弧は発声指示。`[pause]` の秒数は固定されない。完成音声に合わせて JSON の `durationMs` を調整する。
数字とボタン名は読み間違いを避けるため、音声入力だけ漢字とカタカナで書く。字幕の表記は正本のまま。
章名は読み上げ文に含めない。音声は BGM や効果音を混ぜずに書き出し、生成後に4場面へ切り分ける。
公式: [Eleven v4](https://elevenlabs.io/blog/eleven-v4) / [Text to Speech](https://elevenlabs.io/docs/eleven-creative/playground/text-to-speech) / [Audio Tags](https://elevenlabs.io/blog/elevenlabs-audio-tags-list)

```text
[calm, measured] はじめまして。日本怪異研究所へようこそ。私はあなたの上司にあたる、博士です。
[pause] あなたに、ある呪われたオブジェクトの調査を依頼します。
[long pause] こちらのカーテンです。
[pause] 観測装置をつけ、このカーテンを三周してください。
[pause] その間は右手で手すりを持ち、離さないでください。
[pause] 異変を見逃さないよう、ゆっくり慎重に歩いてください。
[long pause] これが、怪異を記録・解析する観測装置です。
[pause] あなたの調査を支援するエージェントが搭載されています。
[pause] 異変に気づいたら、左コントローラーのエックスかワイを一秒、長押ししてください。
[pause] エージェントが解析してくれます。
[long pause] では、左手にコントローラーを持って観測装置をつけてください。
[pause] 健闘を祈ります。
```
