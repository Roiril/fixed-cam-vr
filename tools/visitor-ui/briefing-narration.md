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

### 01 introduction — 調査をお願いします

開始 0.0 秒 / 尺 7.9 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

来てくれてありがとう。

文 02 / 場面内 2.4–4.9 秒

私はこの研究所で怪異の観測を担当しています。

文 03 / 場面内 4.9–7.9 秒

今日はあなたに調査員として、ある壁を調べていただきたい。

### 02 subject — 回収壁面

開始 7.9 秒 / 尺 7.9 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.4 秒

これが今回の調査対象です。

文 02 / 場面内 2.4–4.8 秒

この壁の周辺では怪異が報告されています。

文 03 / 場面内 4.8–7.9 秒

回収された場所と対象の詳細は、調査員には開示していません。

### 03 survey — 映像を確認しながら進む

開始 15.8 秒 / 尺 8.4 秒

画像: `briefing-survey-v1.png`

文 01 / 場面内 0.0–3.1 秒

装置に映る様子を確認しながら、所定の経路を進んでください。

文 02 / 場面内 3.1–6.0 秒

調査中はエージェントが装置を通じてあなたを支援します。

文 03 / 場面内 6.0–8.4 秒

指示が届いたら従ってください。

### 04 report — XかYを長押し

開始 24.2 秒 / 尺 11.4 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–4.0 秒

映像の中に異変を見つけたら、左コントローラーのXかYを1秒間押し続けてください。

文 02 / 場面内 4.0–6.4 秒

どちらか一つのボタンで報告できます。

文 03 / 場面内 6.4–8.8 秒

エージェントが解析します。

文 04 / 場面内 8.8–11.4 秒

異変が検出された場合は、装置が対処を試みます。

### 05 device — 観測装置

開始 35.6 秒 / 尺 8.2 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–3.4 秒

この装置は、通常は確認できない怪異を観測するために開発されました。

文 02 / 場面内 3.4–5.8 秒

映像はあなたの位置に応じて切り替わります。

文 03 / 場面内 5.8–8.2 秒

切り替える操作は必要ありません。

### 06 prepare — 左手にコントローラーを

開始 43.8 秒 / 尺 10.4 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–2.4 秒

コントローラーは左手で持ってください。

文 02 / 場面内 2.4–4.8 秒

ストラップを手首に通してください。

文 03 / 場面内 4.8–8.0 秒

気分が悪くなったら、その場で立ち止まって装置を外してください。

文 04 / 場面内 8.0–10.4 秒

スタッフに知らせてください。

### 07 wear — 装置を装着してください

開始 54.2 秒 / 尺 7.4 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

説明は以上です。

文 02 / 場面内 2.4–4.8 秒

では、装置を装着してください。

文 03 / 場面内 4.8–7.4 秒

装着後は装置に表示される案内に従ってください。

## en

### 01 introduction — A survey for you

開始 0.0 秒 / 尺 10.6 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

Thank you for coming.

文 02 / 場面内 2.4–5.8 秒

I study anomalous phenomena here at the institute.

文 03 / 場面内 5.8–10.6 秒

Today, I would like you to act as a surveyor and examine a particular wall.

### 02 subject — Recovered wall

開始 10.6 秒 / 尺 10.3 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.6 秒

This is the subject of your survey.

文 02 / 場面内 2.6–5.8 秒

Anomalies have been reported around this wall.

文 03 / 場面内 5.8–10.3 秒

The recovery site and further details are not disclosed to surveyors.

### 03 survey — Observe as you walk

開始 20.9 秒 / 尺 11.3 秒

画像: `briefing-survey-v1.png`

文 01 / 場面内 0.0–4.8 秒

Follow the designated route while observing the images shown in the device.

文 02 / 場面内 4.8–8.9 秒

An agent will assist you through the device during the survey.

文 03 / 場面内 8.9–11.3 秒

Follow its instructions.

### 04 report — Hold X or Y

開始 32.2 秒 / 尺 15.0 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.8 秒

Whenever you spot an anomaly in the image, hold X or Y on the left controller for one second.

文 02 / 場面内 5.8–8.3 秒

Either button will send a report.

文 03 / 場面内 8.3–10.7 秒

The agent will analyse it.

文 04 / 場面内 10.7–15.0 秒

If an anomaly is detected, the device will attempt to counter it.

### 05 device — Observation device

開始 47.2 秒 / 尺 11.2 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.4 秒

This device was developed to observe anomalous phenomena that cannot normally be seen.

文 02 / 場面内 5.4–8.4 秒

The view changes automatically as you move.

文 03 / 場面内 8.4–11.2 秒

You do not need to switch it yourself.

### 06 prepare — Controller in your left hand

開始 58.4 秒 / 尺 12.0 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–2.8 秒

Hold the controller in your left hand.

文 02 / 場面内 2.8–5.6 秒

Put the wrist strap around your wrist.

文 03 / 場面内 5.6–9.6 秒

If you feel unwell, stop where you are and remove the device.

文 04 / 場面内 9.6–12.0 秒

Let a staff member know.

### 07 wear — Please put on the device

開始 70.4 秒 / 尺 8.4 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

That concludes the briefing.

文 02 / 場面内 2.4–4.8 秒

Please put on the device.

文 03 / 場面内 4.8–8.4 秒

Once it is on, follow the instructions on the display.

## fr

### 01 introduction — Une mission pour vous

開始 0.0 秒 / 尺 9.9 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

Merci de votre présence.

文 02 / 場面内 2.4–5.9 秒

J’étudie les phénomènes anormaux dans cet institut.

文 03 / 場面内 5.9–9.9 秒

Aujourd’hui, je vous confie une mission d’enquête sur un mur.

### 02 subject — Mur récupéré

開始 9.9 秒 / 尺 10.8 秒

画像: `briefing-wall-v1.png`

文 01 / 場面内 0.0–2.4 秒

Voici l’objet de votre mission.

文 02 / 場面内 2.4–5.8 秒

Des anomalies ont été signalées autour de ce mur.

文 03 / 場面内 5.8–10.8 秒

Le lieu de récupération et les détails ne sont pas communiqués aux enquêteurs.

### 03 survey — Observer en avançant

開始 20.7 秒 / 尺 12.8 秒

画像: `briefing-survey-v1.png`

文 01 / 場面内 0.0–5.0 秒

Suivez le parcours prévu en observant les images affichées dans le dispositif.

文 02 / 場面内 5.0–9.9 秒

Un agent vous assistera par l’intermédiaire du dispositif pendant la mission.

文 03 / 場面内 9.9–12.8 秒

Suivez les consignes qu’il vous transmet.

### 04 report — Maintenez X ou Y

開始 33.5 秒 / 尺 17.5 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–6.6 秒

Lorsque vous repérez une anomalie dans l’image, maintenez X ou Y sur la manette gauche pendant une seconde.

文 02 / 場面内 6.6–10.7 秒

Un seul de ces deux boutons suffit pour envoyer un signalement.

文 03 / 場面内 10.7–13.2 秒

L’agent analysera le signalement.

文 04 / 場面内 13.2–17.5 秒

Si une anomalie est détectée, le dispositif tentera d’y remédier.

### 05 device — Dispositif d’observation

開始 51.0 秒 / 尺 11.4 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–5.0 秒

Ce dispositif a été conçu pour observer des phénomènes normalement invisibles.

文 02 / 場面内 5.0–8.5 秒

La vue change automatiquement selon votre position.

文 03 / 場面内 8.5–11.4 秒

Vous n’avez rien à faire pour la changer.

### 06 prepare — La manette dans la main gauche

開始 62.4 秒 / 尺 12.5 秒

画像: `briefing-device-v1.png`

文 01 / 場面内 0.0–2.7 秒

Tenez la manette dans la main gauche.

文 02 / 場面内 2.7–5.4 秒

Passez la dragonne autour du poignet.

文 03 / 場面内 5.4–10.1 秒

Si vous vous sentez mal, arrêtez-vous sur place et retirez le dispositif.

文 04 / 場面内 10.1–12.5 秒

Prévenez le personnel.

### 07 wear — Veuillez mettre le dispositif

開始 74.9 秒 / 尺 8.1 秒

画像: `doctor.jpg`

文 01 / 場面内 0.0–2.4 秒

Les consignes sont terminées.

文 02 / 場面内 2.4–4.8 秒

Veuillez mettre le dispositif.

文 03 / 場面内 4.8–8.1 秒

Une fois équipé, suivez les consignes affichées.
