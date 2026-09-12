# 博士の導入台本

案 / 2026-09-12 / シュビー。台詞と画面の演出は未判定の実装案。

正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

音声と動画は未制作。以下の時間はオート再生用の仮の尺。完成した音声に合わせて JSON の durationMs を調整する。
通常は一文ずつ全文表示してタップを待つ。オートを選んだときだけ次の文へ進む。
動画は既存博士画像と同じ人物と画角を使用する。映像の切替や字幕を動画に焼き込まない。

各場面を個別に音声化する。ファイル名は `場面ID-言語.mp3` または `場面ID-言語.mp4` を推奨する。
各文の開始は同じ場面にある前の文の durationMs の合計。手動では文の終端で媒体を止める。
一文送りは発話を待たず次の文の開始位置へ移る。文間の間も直前の文の尺へ含める。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 調査依頼

開始 0.0 秒 / 尺 8.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.5 秒

私はこの研究所で怪異の観測を担当しています。

文 02 / 場面内 3.5–8.0 秒

怪異が報告された壁を、あなたに調査員として調べていただきたい。

### 02 subject — 回収壁面

開始 8.0 秒 / 尺 8.5 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–5.0 秒

観測装置の映像を見ながら、エージェントの指示に従って歩いてください。

文 02 / 場面内 5.0–8.5 秒

映像はあなたの位置に応じて自動で切り替わります。

### 03 report — XかYを長押し

開始 16.5 秒 / 尺 8.0 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.0 秒

異変を見つけたら、左コントローラーのXかYを1秒間長押ししてください。

文 02 / 場面内 5.0–8.0 秒

報告を受けて、エージェントが解析します。

### 04 wear — 装着案内

開始 24.5 秒 / 尺 4.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.0 秒

コントローラーを左手に持ち、装置を装着してください。

## en

### 01 introduction — Survey briefing

開始 0.0 秒 / 尺 8.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.4 秒

I study anomalous phenomena here at the institute.

文 02 / 場面内 3.4–8.2 秒

I would like you to investigate a wall where anomalies have been reported.

### 02 subject — Recovered wall

開始 8.2 秒 / 尺 8.2 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–5.2 秒

Watch the images in the observation device and walk as directed by the agent.

文 02 / 場面内 5.2–8.2 秒

The view changes automatically as you move.

### 03 report — Hold X or Y

開始 16.4 秒 / 尺 8.1 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.6 秒

If you spot an anomaly, hold X or Y on the left controller for one second.

文 02 / 場面内 5.6–8.1 秒

The agent will analyse your report.

### 04 wear — Putting on the device

開始 24.5 秒 / 尺 4.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

Hold the controller in your left hand and put on the device.

## fr

### 01 introduction — Présentation de la mission

開始 0.0 秒 / 尺 9.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.5 秒

J’étudie les phénomènes anormaux dans cet institut.

文 02 / 場面内 3.5–9.0 秒

Je vous confie une enquête sur un mur autour duquel des anomalies ont été signalées.

### 02 subject — Mur récupéré

開始 9.0 秒 / 尺 9.0 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–5.5 秒

Observez les images dans le dispositif et marchez en suivant les consignes de l’agent.

文 02 / 場面内 5.5–9.0 秒

La vue change automatiquement selon votre position.

### 03 report — Maintenez X ou Y

開始 18.0 秒 / 尺 8.5 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–6.0 秒

Si vous repérez une anomalie, maintenez X ou Y sur la manette gauche pendant une seconde.

文 02 / 場面内 6.0–8.5 秒

L’agent analysera votre signalement.

### 04 wear — Mise en place du dispositif

開始 26.5 秒 / 尺 4.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

Prenez la manette dans la main gauche et mettez le dispositif.
