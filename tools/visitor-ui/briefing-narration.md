# 博士の導入台本

案 / 2026-09-12 / シュビー。台詞と画面の演出は未判定の実装案。

正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

音声と動画は未制作。以下の時間は静止画版の仮の尺。完成した音声に合わせて JSON の durationMs を調整する。
動画は既存博士画像と同じ人物と画角を使用する。映像の切替や字幕を動画に焼き込まない。

各場面を個別に音声化する。ファイル名は `場面ID-言語.mp3` または `場面ID-言語.mp4` を推奨する。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 調査をお願いします

開始 0.0 秒 / 尺 8.7 秒

画像: `doctor.jpg`

来てくれてありがとう。私はこの研究所で怪異の観測を担当しています。

今日はあなたに、ある壁の調査をお願いしたい。

### 02 subject — 回収壁面

開始 8.7 秒 / 尺 9.0 秒

画像: `briefing-wall-v1.png`

これが今回の調査対象です。この壁の周辺では怪異が報告されています。

回収された場所と対象の詳細は、調査者には開示していません。

### 03 survey — 映像を確認しながら進む

開始 17.7 秒 / 尺 9.8 秒

画像: `briefing-survey-v1.png`

装置に映る様子を確認しながら、所定の経路を進んでください。

調査中は装置の中のエージェントがあなたを支援します。指示が届いたら従ってください。

### 04 report — 異変を見つけたら長押し

開始 27.5 秒 / 尺 10.0 秒

画像: `briefing-device-v1.png`

映像の中に異変を見つけたら、その都度、手元のボタンを長押ししてください。

エージェントが解析します。異変が検出された場合は、装置が対処を試みます。

### 05 device — 観測装置

開始 37.5 秒 / 尺 9.9 秒

画像: `briefing-device-v1.png`

この装置は、通常では確認できない怪異を観測するために開発されました。

映像はあなたの位置に応じて切り替わります。切り替える操作は必要ありません。

### 06 prepare — 右手にコントローラーを

開始 47.4 秒 / 尺 10.9 秒

画像: `briefing-device-v1.png`

コントローラーは右手で持ってください。ストラップを手首に通してください。

気分が悪くなったら、その場で立ち止まって装置を外してください。スタッフに知らせてください。

### 07 wear — 装置を装着してください

開始 58.3 秒 / 尺 8.0 秒

画像: `doctor.jpg`

説明は以上です。では、装置を装着してください。

装着後の案内は、装置の中に表示されます。

## en

### 01 introduction — A survey for you

開始 0.0 秒 / 尺 9.7 秒

画像: `doctor.jpg`

Thank you for coming. I study anomalous phenomena here at the institute.

Today, I would like you to survey a particular wall.

### 02 subject — Recovered wall

開始 9.7 秒 / 尺 11.3 秒

画像: `briefing-wall-v1.png`

This is the subject of your survey. Anomalies have been reported around this wall.

The recovery site and further details are not disclosed to surveyors.

### 03 survey — Observe as you walk

開始 21.0 秒 / 尺 11.6 秒

画像: `briefing-survey-v1.png`

Follow the designated route while observing the images shown in the device.

An agent in the device will assist you during the survey. Follow its instructions.

### 04 report — See an anomaly? Press and hold.

開始 32.6 秒 / 尺 12.9 秒

画像: `briefing-device-v1.png`

Whenever you spot an anomaly in the image, press and hold the button on your controller.

The agent will analyse it. If an anomaly is detected, the device will attempt to counter it.

### 05 device — Observation device

開始 45.5 秒 / 尺 12.2 秒

画像: `briefing-device-v1.png`

This device was developed to observe anomalous phenomena that cannot normally be seen.

The view changes automatically as you move. You do not need to switch it yourself.

### 06 prepare — Controller in your right hand

開始 57.7 秒 / 尺 12.0 秒

画像: `briefing-device-v1.png`

Hold the controller in your right hand. Put the wrist strap around your wrist.

If you feel unwell, stop where you are and remove the device. Let a staff member know.

### 07 wear — Please put on the device

開始 69.7 秒 / 尺 8.7 秒

画像: `doctor.jpg`

That concludes the briefing. Please put on the device.

Further instructions will appear inside the device.

## fr

### 01 introduction — Une mission pour vous

開始 0.0 秒 / 尺 9.9 秒

画像: `doctor.jpg`

Merci d’être venu. J’étudie les phénomènes anormaux dans cet institut.

Aujourd’hui, je voudrais vous confier l’examen d’un mur.

### 02 subject — Mur récupéré

開始 9.9 秒 / 尺 11.7 秒

画像: `briefing-wall-v1.png`

Voici l’objet de votre mission. Des anomalies ont été signalées autour de ce mur.

Le lieu de récupération et les détails ne sont pas communiqués aux enquêteurs.

### 03 survey — Observer en avançant

開始 21.6 秒 / 尺 12.4 秒

画像: `briefing-survey-v1.png`

Suivez le parcours prévu en observant les images affichées dans le dispositif.

Un agent intégré vous assistera pendant la mission. Suivez les consignes qu’il vous transmet.

### 04 report — Une anomalie ? Maintenez le bouton.

開始 34.0 秒 / 尺 13.3 秒

画像: `briefing-device-v1.png`

Lorsque vous repérez une anomalie dans l’image, maintenez le bouton de la manette enfoncé.

L’agent analysera le signalement. Si une anomalie est détectée, le dispositif tentera d’y remédier.

### 05 device — Dispositif d’observation

開始 47.3 秒 / 尺 12.4 秒

画像: `briefing-device-v1.png`

Ce dispositif a été conçu pour observer des phénomènes normalement invisibles.

La vue change automatiquement selon votre position. Vous n’avez rien à faire pour la changer.

### 06 prepare — La manette dans la main droite

開始 59.7 秒 / 尺 12.4 秒

画像: `briefing-device-v1.png`

Tenez la manette dans la main droite. Passez la dragonne autour du poignet.

Si vous vous sentez mal, arrêtez-vous sur place et retirez le dispositif. Prévenez le personnel.

### 07 wear — Veuillez mettre le dispositif

開始 72.1 秒 / 尺 9.4 秒

画像: `doctor.jpg`

Les consignes sont terminées. Veuillez mettre le dispositif.

La suite des instructions s’affichera dans le dispositif.
