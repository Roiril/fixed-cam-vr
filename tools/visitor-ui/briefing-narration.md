# 博士の導入台本

台詞の正本。ユーザーが指定した文言と、それ以外の未判定の実装案を含む。

正本は `tablet/app/src/main/assets/web/asset/briefing-v1.json`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

2026-11-12 に台詞を書き換えた（種を 4 つ置いた。`canon/OPEN.md`「シナリオの組み直し」）。日本語の声と口の動画（v2）は前の台詞のものなので、場面から外してある。新しい声は `tools/visitor-ui/elevenlabs-input-ja-v3.md` から作る。それまで日本語も英仏と同じく字幕と静止画で進む。
声の無い言語の時間は字数からの見積もり（オートの送りに使う）。声を入れたら、実音源の無音区間で測った尺へ直す。英仏音声と英仏動画は未制作。
通常は一文ずつ全文表示してタップを待つ。オートを選んだときだけ次の文へ進む。
博士動画は既存博士画像と同じ人物を中央配置で使用する。映像の切替や字幕を動画に焼き込まない。

前の台詞の元音源は `tools/visitor-ui/doctor-ja-v2-original.mp3`（`場面ID-ja-v2.mp3` / `.mp4` として同梱したまま。場面からは参照しない）。新しい音源は `ingest-doctor-audio.py` で場面ごとに切り出し、`ingest-doctor-video.py` で生成動画へ組み込み、各場面の `video.ja` に書く。英仏音声や動画を追加する場合も場面単位にする。
各文の開始は同じ場面にある前の文の durationMs の合計。手動では文の終端で媒体を止める。
一文送りは発話を待たず次の文の開始位置へ移る。文間の間も直前の文の尺へ含める。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 調査依頼

開始 0.0 秒 / 尺 17.8 秒

画像: `introduction-doctor-v2.jpg`

文 01 / 場面内 0.0–9.8 秒

はじめまして。日本怪異研究所へようこそ。私はあなたの上司にあたる、博士です。

文 02 / 場面内 9.8–17.8 秒

あなたに、ある調査を依頼します。調査員は、あなたで三十七人目です。

### 02 subject — 探索方法

開始 17.8 秒 / 尺 28.9 秒

画像: `camera-01-doll.webp`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–7.2 秒

こちらのカーテンです。ある家の子ども部屋から、回収しました。

文 02 / 場面内 7.2–16.4 秒

観測装置をつけ、このカーテンの周りを三周してください。廻るほど、近づいてきます。

文 03 / 場面内 16.4–22.5 秒

その間は右手で手すりを持ち、決して離さないでください。

文 04 / 場面内 22.5–28.9 秒

異変を見逃さないよう、ゆっくり慎重に歩いてください。

### 03 report — XかYを長押し

開始 46.7 秒 / 尺 24.9 秒

画像: `briefing-device-v3.jpg`

文 01 / 場面内 0.0–5.2 秒

これが、怪異を記録し、解析する観測装置です。

文 02 / 場面内 5.2–13.0 秒

怪異を直接見ると、見返されます。だから、装置があなたの代わりに見ます。

文 03 / 場面内 13.0–20.1 秒

異変に気づいたら、左コントローラーのXかYを1秒間、長押ししてください。

文 04 / 場面内 20.1–24.9 秒

装置のエージェントが解析し、取り除きます。

### 04 wear — 装着案内

開始 71.6 秒 / 尺 9.5 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–6.8 秒

では。左手にコントローラーを持って観測装置をつけてください。

文 02 / 場面内 6.8–9.5 秒

健闘を祈ります。

## en

### 01 introduction — Survey briefing

開始 0.0 秒 / 尺 12.1 秒

画像: `introduction-doctor-v2.jpg`

文 01 / 場面内 0.0–6.5 秒

Nice to meet you. Welcome to the Japan Institute of Anomalies. I am the doctor, your supervisor.

文 02 / 場面内 6.5–12.1 秒

I have a survey for you. You are our thirty-seventh investigator.

### 02 subject — How to explore

開始 12.1 秒 / 尺 23.8 秒

画像: `camera-01-doll.webp`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–5.6 秒

This is the curtain. We recovered it from a child’s room in a certain house.

文 02 / 場面内 5.6–13.8 秒

Put on the observation device and walk around this curtain three times. The more you circle it, the closer it comes.

文 03 / 場面内 13.8–18.8 秒

Hold the handrail with your right hand and keep hold of it as you walk.

文 04 / 場面内 18.8–23.8 秒

Your footing will be hard to see, so walk very slowly and carefully.

### 03 report — Hold X or Y

開始 35.9 秒 / 尺 21.2 秒

画像: `briefing-device-v3.jpg`

文 01 / 場面内 0.0–4.8 秒

This is the observation device. It records and analyses anomalies.

文 02 / 場面内 4.8–11.6 秒

If you look at an anomaly directly, it looks back. So the device will look in your place.

文 03 / 場面内 11.6–17.2 秒

If you spot an anomaly, hold X or Y on the left controller for one second.

文 04 / 場面内 17.2–21.2 秒

The agent in the device will analyse it and remove it.

### 04 wear — Putting on the device

開始 57.1 秒 / 尺 7.2 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–4.2 秒

Now hold the controller in your left hand and put on the device.

文 02 / 場面内 4.2–7.2 秒

Good luck with your survey.

## fr

### 01 introduction — Présentation de la mission

開始 0.0 秒 / 尺 13.5 秒

画像: `introduction-doctor-v2.jpg`

文 01 / 場面内 0.0–7.5 秒

Enchanté. Bienvenue à l'Institut japonais de recherche sur les phénomènes étranges. Je suis le docteur, votre supérieur.

文 02 / 場面内 7.5–13.5 秒

J’ai une enquête à vous confier. Vous êtes notre trente-septième enquêteur.

### 02 subject — Méthode d'exploration

開始 13.5 秒 / 尺 25.4 秒

画像: `camera-01-doll.webp`

ループ動画: `kabe-one-lap-doll-v1.mp4`（文 02 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–5.6 秒

Voici le rideau. Nous l’avons récupéré dans la chambre d’un enfant.

文 02 / 場面内 5.6–14.4 秒

Mettez le dispositif d'observation et faites trois fois le tour de ce rideau. Plus vous tournez, plus cela se rapproche.

文 03 / 場面内 14.4–19.9 秒

Tenez la main courante de la main droite et ne la lâchez pas en marchant.

文 04 / 場面内 19.9–25.4 秒

Vous distinguerez mal le sol à vos pieds : avancez très lentement et avec prudence.

### 03 report — Maintenez X ou Y

開始 38.9 秒 / 尺 23.0 秒

画像: `briefing-device-v3.jpg`

文 01 / 場面内 0.0–5.4 秒

Voici le dispositif d’observation. Il enregistre et analyse les phénomènes anormaux.

文 02 / 場面内 5.4–12.8 秒

Si vous regardez une anomalie en face, elle vous regarde aussi. Le dispositif regardera donc à votre place.

文 03 / 場面内 12.8–18.8 秒

Si vous repérez une anomalie, maintenez X ou Y sur la manette gauche pendant une seconde.

文 04 / 場面内 18.8–23.0 秒

L’agent du dispositif l’analysera et l’éliminera.

### 04 wear — Mise en place du dispositif

開始 61.9 秒 / 尺 7.2 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–4.2 秒

À présent, prenez la manette dans la main gauche et mettez le dispositif.

文 02 / 場面内 4.2–7.2 秒

Bonne chance pour votre enquête.
