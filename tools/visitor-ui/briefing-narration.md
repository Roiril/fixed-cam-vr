# 博士の導入台本

台詞の正本。ユーザーが指定した文言と、それ以外の未判定の実装案を含む。

正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

日本語の4場面はリップシンク動画を使用する。各動画の音声はユーザー提供の Eleven v4 音声（65.567 秒）から切り出した同じ音源を使用する。英仏音声と英仏動画は未制作。以下の日本語時間は実音源の無音区間で測った尺。
通常は一文ずつ全文表示してタップを待つ。オートを選んだときだけ次の文へ進む。
博士動画は既存博士画像と同じ人物を中央配置で使用する。映像の切替や字幕を動画に焼き込まない。

日本語の元音源は `tools/visitor-ui/doctor-ja-v2-original.mp3`。`ingest-doctor-audio.py` で `場面ID-ja-v2.mp3.bytes` に切り出す。`ingest-doctor-video.py` は生成動画の音声を除き、同じ元音源から切り出した音声を AAC にして組み込む。英仏音声や動画を追加する場合は場面単位にする。
各文の開始は同じ場面にある前の文の durationMs の合計。手動では文の終端で媒体を止める。
一文送りは発話を待たず次の文の開始位置へ移る。文間の間も直前の文の尺へ含める。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 調査依頼

開始 0.0 秒 / 尺 15.6 秒

画像: `introduction-doctor-v1.jpg`

動画: `introduction-ja-v1.mp4`

文 01 / 場面内 0.0–9.8 秒

はじめまして。日本怪異研究所へようこそ。私はあなたの上司にあたる、博士です。

文 02 / 場面内 9.8–15.6 秒

あなたに、ある「呪われたオブジェクト」の調査を依頼します。

### 02 subject — 探索方法

開始 15.6 秒 / 尺 20.7 秒

画像: `camera-01-doll.webp`

動画: `subject-ja-v1.mp4`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–3.3 秒

こちらのカーテンです。

文 02 / 場面内 3.3–8.3 秒

観測装置をつけ、このカーテンを3周してください。

文 03 / 場面内 8.3–14.4 秒

その間は右手で手すりを持ち、決して離さないでください。

文 04 / 場面内 14.4–20.7 秒

異変を見逃さないよう、ゆっくり慎重に歩いてください。

### 03 report — XかYを長押し

開始 36.4 秒 / 尺 19.7 秒

画像: `briefing-device-v1.png`

動画: `report-ja-v1.mp4`

文 01 / 場面内 0.0–4.9 秒

これが、怪異を記録・解析する観測装置です。

文 02 / 場面内 4.9–9.3 秒

あなたの調査を支援するエージェントが搭載されています。

文 03 / 場面内 9.3–16.5 秒

異変に気づいたら、左コントローラーのXかYを1秒間、長押ししてください。

文 04 / 場面内 16.5–19.7 秒

エージェントが解析してくれます。

### 04 wear — 装着案内

開始 56.1 秒 / 尺 9.5 秒

画像: `wear-doctor-v1.jpg`

動画: `wear-ja-v1.mp4`

文 01 / 場面内 0.0–6.8 秒

では。左手にコントローラーを持って観測装置をつけてください。

文 02 / 場面内 6.8–9.5 秒

健闘を祈ります。

## en

### 01 introduction — Survey briefing

開始 0.0 秒 / 尺 11.3 秒

画像: `introduction-doctor-v1.jpg`

文 01 / 場面内 0.0–6.5 秒

Nice to meet you. Welcome to the Japan Institute of Anomalies. I am the doctor, your supervisor.

文 02 / 場面内 6.5–11.3 秒

I would like you to serve as an investigator and examine a cursed object.

### 02 subject — How to explore

開始 11.3 秒 / 尺 17.7 秒

画像: `camera-01-doll.webp`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–2.7 秒

This is the curtain you will investigate.

文 02 / 場面内 2.7–7.7 秒

Put on the observation device and walk around this curtain three times.

文 03 / 場面内 7.7–12.7 秒

Hold the handrail with your right hand and keep hold of it as you walk.

文 04 / 場面内 12.7–17.7 秒

Your footing will be hard to see, so walk very slowly and carefully.

### 03 report — Hold X or Y

開始 29.0 秒 / 尺 17.5 秒

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

開始 46.5 秒 / 尺 7.2 秒

画像: `wear-doctor-v1.jpg`

文 01 / 場面内 0.0–4.2 秒

Now hold the controller in your left hand and put on the device.

文 02 / 場面内 4.2–7.2 秒

Good luck with your survey.

## fr

### 01 introduction — Présentation de la mission

開始 0.0 秒 / 尺 12.7 秒

画像: `introduction-doctor-v1.jpg`

文 01 / 場面内 0.0–7.5 秒

Enchanté. Bienvenue à l'Institut japonais de recherche sur les phénomènes étranges. Je suis le docteur, votre supérieur.

文 02 / 場面内 7.5–12.7 秒

Je vous demande d’examiner, en tant qu’agent de terrain, un objet maudit.

### 02 subject — Méthode d'exploration

開始 12.7 秒 / 尺 19.2 秒

画像: `camera-01-doll.webp`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–2.7 秒

Voici le rideau que vous allez examiner.

文 02 / 場面内 2.7–8.2 秒

Mettez le dispositif d'observation et faites trois fois le tour de ce rideau.

文 03 / 場面内 8.2–13.7 秒

Tenez la main courante de la main droite et ne la lâchez pas en marchant.

文 04 / 場面内 13.7–19.2 秒

Vous distinguerez mal le sol à vos pieds : avancez très lentement et avec prudence.

### 03 report — Maintenez X ou Y

開始 31.9 秒 / 尺 18.4 秒

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

開始 50.3 秒 / 尺 7.2 秒

画像: `wear-doctor-v1.jpg`

文 01 / 場面内 0.0–4.2 秒

À présent, prenez la manette dans la main gauche et mettez le dispositif.

文 02 / 場面内 4.2–7.2 秒

Bonne chance pour votre enquête.
