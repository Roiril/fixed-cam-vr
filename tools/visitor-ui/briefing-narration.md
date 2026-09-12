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

開始 8.0 秒 / 尺 11.5 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.4 秒

こちらが調査対象の壁です。

文 02 / 場面内 2.4–6.0 秒

調査には、怪異を記録し、解析するための観測装置を使用します。

文 03 / 場面内 6.0–11.5 秒

装置を装着するとエージェントが調査経路を案内しますので、その指示に従って進んでください。

### 03 report — XかYを長押し

開始 19.5 秒 / 尺 8.5 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.0 秒

異変を見つけたら、左コントローラーのXかYを1秒間長押ししてください。

文 02 / 場面内 5.0–8.5 秒

報告はエージェントへ送られ、その内容が解析されます。

### 04 wear — 装着案内

開始 28.0 秒 / 尺 7.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.0 秒

では、コントローラーを左手に持ち、装置を装着してください。

文 02 / 場面内 4.0–7.0 秒

それでは、健闘を祈ります。

## en

### 01 introduction — Survey briefing

開始 0.0 秒 / 尺 8.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.4 秒

I study anomalous phenomena here at the institute.

文 02 / 場面内 3.4–8.2 秒

I would like you to investigate a wall where anomalies have been reported.

### 02 subject — Recovered wall

開始 8.2 秒 / 尺 12.7 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.7 秒

This is the wall you will investigate.

文 02 / 場面内 2.7–7.2 秒

You will use an observation device that records and analyses anomalies.

文 03 / 場面内 7.2–12.7 秒

Once you put on the device, an agent will guide you along the survey route, so follow its directions.

### 03 report — Hold X or Y

開始 20.9 秒 / 尺 8.8 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.6 秒

If you spot an anomaly, hold X or Y on the left controller for one second.

文 02 / 場面内 5.6–8.8 秒

The report will be sent to the agent for analysis.

### 04 wear — Putting on the device

開始 29.7 秒 / 尺 7.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

Now hold the controller in your left hand and put on the device.

文 02 / 場面内 4.2–7.2 秒

Good luck with your survey.

## fr

### 01 introduction — Présentation de la mission

開始 0.0 秒 / 尺 9.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.5 秒

J’étudie les phénomènes anormaux dans cet institut.

文 02 / 場面内 3.5–9.0 秒

Je vous confie une enquête sur un mur autour duquel des anomalies ont été signalées.

### 02 subject — Mur récupéré

開始 9.0 秒 / 尺 13.0 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.7 秒

Voici le mur que vous allez examiner.

文 02 / 場面内 2.7–7.4 秒

Vous utiliserez un dispositif d’observation qui enregistre et analyse les phénomènes anormaux.

文 03 / 場面内 7.4–13.0 秒

Une fois le dispositif en place, un agent vous guidera sur le parcours d’enquête ; suivez ses consignes.

### 03 report — Maintenez X ou Y

開始 22.0 秒 / 尺 9.4 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–6.0 秒

Si vous repérez une anomalie, maintenez X ou Y sur la manette gauche pendant une seconde.

文 02 / 場面内 6.0–9.4 秒

Le signalement sera envoyé à l’agent pour analyse.

### 04 wear — Mise en place du dispositif

開始 31.4 秒 / 尺 7.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

À présent, prenez la manette dans la main gauche et mettez le dispositif.

文 02 / 場面内 4.2–7.2 秒

Bonne chance pour votre enquête.
