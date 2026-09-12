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

開始 0.0 秒 / 尺 8.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–3.6 秒

私はこの研究所で、怪異調査を統括している博士です。

文 02 / 場面内 3.6–8.2 秒

あなたには調査員として、呪われたオブジェクトを調べていただきたい。

### 02 subject — 回収壁面

開始 8.2 秒 / 尺 14.8 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.4 秒

こちらが調査対象の壁です。

文 02 / 場面内 2.4–5.4 秒

動画のように3周してください。

文 03 / 場面内 5.4–9.6 秒

右手で手すりを持ち、手を離さずに進んでください。

文 04 / 場面内 9.6–14.8 秒

足元が見づらいので、ゆっくりゆっくり、慎重に歩いてください。

### 03 report — XかYを長押し

開始 23.0 秒 / 尺 15.9 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–3.6 秒

調査には怪異の記録と解析を行う観測装置を使います。

文 02 / 場面内 3.6–7.4 秒

観測中は、エージェントがあなたの調査を支援します。

文 03 / 場面内 7.4–12.4 秒

異変を見つけたら、左コントローラーのXかYを1秒間長押ししてください。

文 04 / 場面内 12.4–15.9 秒

報告はエージェントへ送られ、その内容が解析されます。

### 04 wear — 装着案内

開始 38.9 秒 / 尺 7.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.0 秒

コントローラーを左手に持ち、観測装置を装着してください。

文 02 / 場面内 4.0–7.0 秒

健闘を祈ります。

## en

### 01 introduction — Survey briefing

開始 0.0 秒 / 尺 9.0 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

I am the doctor overseeing anomalous investigations at this institute.

文 02 / 場面内 4.2–9.0 秒

I would like you to serve as an investigator and examine a cursed object.

### 02 subject — Recovered wall

開始 9.0 秒 / 尺 16.5 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.7 秒

This is the wall you will investigate.

文 02 / 場面内 2.7–6.5 秒

Walk around it three times as shown in the video.

文 03 / 場面内 6.5–11.5 秒

Hold the handrail with your right hand and keep hold of it as you walk.

文 04 / 場面内 11.5–16.5 秒

Your footing will be hard to see, so walk very slowly and carefully.

### 03 report — Hold X or Y

開始 25.5 秒 / 尺 17.5 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–4.5 秒

You will use an observation device that records and analyses anomalies.

文 02 / 場面内 4.5–8.7 秒

While you are observing, an agent will support your investigation.

文 03 / 場面内 8.7–14.3 秒

If you spot an anomaly, hold X or Y on the left controller for one second.

文 04 / 場面内 14.3–17.5 秒

The report will be sent to the agent for analysis.

### 04 wear — Putting on the device

開始 43.0 秒 / 尺 7.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

Now hold the controller in your left hand and put on the device.

文 02 / 場面内 4.2–7.2 秒

Good luck with your survey.

## fr

### 01 introduction — Présentation de la mission

開始 0.0 秒 / 尺 10.4 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–5.2 秒

Je suis le docteur chargé de superviser les enquêtes sur les phénomènes anormaux dans cet institut.

文 02 / 場面内 5.2–10.4 秒

Je vous demande d’examiner, en tant qu’agent de terrain, un objet maudit.

### 02 subject — Mur récupéré

開始 10.4 秒 / 尺 17.9 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.7 秒

Voici le mur que vous allez examiner.

文 02 / 場面内 2.7–6.9 秒

Faites-en trois fois le tour, comme dans la vidéo.

文 03 / 場面内 6.9–12.4 秒

Tenez la main courante de la main droite et ne la lâchez pas en marchant.

文 04 / 場面内 12.4–17.9 秒

Vous distinguerez mal le sol à vos pieds : avancez très lentement et avec prudence.

### 03 report — Maintenez X ou Y

開始 28.3 秒 / 尺 18.4 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–4.7 秒

Vous utiliserez un dispositif d’observation qui enregistre et analyse les phénomènes anormaux.

文 02 / 場面内 4.7–9.0 秒

Pendant l’observation, un agent vous assistera dans votre enquête.

文 03 / 場面内 9.0–15.0 秒

Si vous repérez une anomalie, maintenez X ou Y sur la manette gauche pendant une seconde.

文 04 / 場面内 15.0–18.4 秒

Le signalement sera envoyé à l’agent pour analyse.

### 04 wear — Mise en place du dispositif

開始 46.7 秒 / 尺 7.2 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–4.2 秒

À présent, prenez la manette dans la main gauche et mettez le dispositif.

文 02 / 場面内 4.2–7.2 秒

Bonne chance pour votre enquête.
