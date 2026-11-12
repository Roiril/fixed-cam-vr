# 博士の導入台本

台詞の正本。ユーザーが指定した文言と、それ以外の未判定の実装案を含む。

正本は `tablet/app/src/main/assets/web/asset/briefing-v1.json`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

2026-11-12 に台詞を書き直した（`canon/LEDGER.md` 0327。0328 で「異常はまだ何も判明していない」前提に直した。名を伏せた上司・管理番号 0514・資料はテレビの中・絵はデジタルツイン。文の案は `canon/OPEN.md`「博士の説明の書き直し」）。
日本語の声（v4）は `generate-doctor-audio.py` が `elevenlabs-input-ja-v4.md` を 1 本で合成し、1 字ごとの時刻から場面と文へ切る。切り出すときに 1.2 倍へ速める（0328）。日本語の時間はその実測。声は voice bEg9geTboP3ghzJ9Rt3L。口の動画は未制作で、博士は静止画のまま声が流れる。
声の無い言語の時間は字数からの見積もり（オートの送りに使う）。英仏音声と英仏動画は未制作。
通常は一文ずつ全文表示してタップを待つ。オートを選んだときだけ次の文へ進む。
博士動画は既存博士画像と同じ人物を中央配置で使用する。映像の切替や字幕を動画に焼き込まない。

前の台詞の元音源は `tools/visitor-ui/doctor-ja-v2-original.mp3`（`場面ID-ja-v2.mp3` / `.mp4` として同梱したまま。場面からは参照しない）。新しい音源は `ingest-doctor-audio.py` で場面ごとに切り出し、`ingest-doctor-video.py` で生成動画へ組み込み、各場面の `video.ja` に書く。英仏音声や動画を追加する場合も場面単位にする。
各文の開始は同じ場面にある前の文の durationMs の合計。手動では文の終端で媒体を止める。
一文送りは発話を待たず次の文の開始位置へ移る。文間の間も直前の文の尺へ含める。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 着任

開始 0.0 秒 / 尺 17.2 秒

画像: `introduction-doctor-v2.jpg`

音声: `introduction-ja-v4.mp3`

文 01 / 場面内 0.0–4.4 秒

ようこそ。日本怪異研究所へ。

文 02 / 場面内 4.4–9.2 秒

私は……あなたの上司、とでも言っておきましょうか。

文 03 / 場面内 9.2–13.3 秒

あなたは今日から、この研究所の調査員です。

文 04 / 場面内 13.3–17.2 秒

これより、調査対象について説明します。

### 02 subject — 調査対象と手順

開始 17.2 秒 / 尺 40.0 秒

画像: `twin-room-v1.jpg`

文 03 からの画像: `doll-file-v1.jpg`

文 05 からの画像: `twin-walk-v1.jpg`

音声: `subject-ja-v4.mp3`

ループ動画: `twin-walk-v1.mp4`（文 05 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–6.5 秒

調査対象は、管理番号0514に指定された異常存在です。

文 02 / 場面内 6.5–11.7 秒

ある空き家の子供部屋から見つかった、カーテンと人形です。

文 03 / 場面内 11.7–20.9 秒

外見上は、一般的な人形と変わりません。しかし、特定の条件下でのみ、異常性が発現します。

文 04 / 場面内 20.9–29.7 秒

その条件も、異常性の正体も、まだ判明していません。それを調査するのが、あなたの任務です。

文 05 / 場面内 29.7–34.6 秒

観測装置をつけ、このカーテンの周りを三周してください。

文 06 / 場面内 34.6–40.0 秒

右手で手すりを持ち、決して離さず、ゆっくり歩いてください。

### 03 report — XかYを長押し

開始 57.2 秒 / 尺 33.9 秒

画像: `device-quest3-v1.jpg`

文 02 からの画像: `twin-camera-v1.jpg`

文 03 からの画像: `twin-camera-view-v1.jpg`

文 04 からの画像: `equipment-controller-v1.png`（押すボタンの図）

音声: `report-ja-v4.mp3`

文 01 / 場面内 0.0–5.1 秒

これが、怪異を記録し、解析するための観測装置です。

文 02 / 場面内 5.1–9.6 秒

装置には、室内のカメラが捉えた映像が映ります。

文 03 / 場面内 9.6–17.8 秒

どのような異変が起きるかは、分かっていません。映像に少しでも違和感を覚えたら、報告してください。

文 04 / 場面内 17.8–27.3 秒

報告するときは、左コントローラーのボタンを1秒間、長押ししてください。XとY、どちらでも構いません。

文 05 / 場面内 27.3–33.9 秒

報告の内容は、装置のエージェントが解析します。見間違いでも構いません。

### 04 wear — 終了と装着

開始 91.0 秒 / 尺 19.2 秒

画像: `wear-doctor-v2.jpg`

音声: `wear-ja-v4.mp3`

文 01 / 場面内 0.0–6.1 秒

調査が終わったら、係の者が装置を外すまで、動かずにお待ちください。

文 02 / 場面内 6.1–11.2 秒

続けられなくなったら、その場で立ち止まり、装置を外してください。

文 03 / 場面内 11.2–17.1 秒

では。左手にコントローラーを持って、観測装置をつけてください。

文 04 / 場面内 17.1–19.2 秒

健闘を祈ります。

## en

### 01 introduction — Assignment

開始 0.0 秒 / 尺 17.0 秒

画像: `introduction-doctor-v2.jpg`

文 01 / 場面内 0.0–3.6 秒

Welcome... to the Japan Institute of Anomalies.

文 02 / 場面内 3.6–8.4 秒

I am... your supervisor. Let us leave it at that.

文 03 / 場面内 8.4–12.8 秒

As of today, you are an investigator of this institute.

文 04 / 場面内 12.8–17.0 秒

I will now brief you on the subject of your survey.

### 02 subject — Subject and procedure

開始 17.0 秒 / 尺 38.4 秒

画像: `twin-room-v1.jpg`

文 03 からの画像: `doll-file-v1.jpg`

文 05 からの画像: `twin-walk-v1.jpg`

ループ動画: `twin-walk-v1.mp4`（文 05 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–5.2 秒

The subject is an anomalous entity, designated Control Number 0514.

文 02 / 場面内 5.2–10.4 秒

A curtain and a doll, found in a child’s room in an abandoned house.

文 03 / 場面内 10.4–19.0 秒

In appearance, it is no different from an ordinary doll. But only under certain conditions, its anomalous properties manifest.

文 04 / 場面内 19.0–26.2 秒

Neither those conditions nor the nature of the anomaly are known yet. Finding out is your assignment.

文 05 / 場面内 26.2–31.4 秒

Put on the observation device and walk around this curtain three times.

文 06 / 場面内 31.4–38.4 秒

Hold the handrail with your right hand, never let go, and walk slowly.

### 03 report — Hold X or Y

開始 55.4 秒 / 尺 30.4 秒

画像: `device-quest3-v1.jpg`

文 02 からの画像: `twin-camera-v1.jpg`

文 03 からの画像: `twin-camera-view-v1.jpg`

文 04 からの画像: `equipment-controller-v1.png`（押すボタンの図）

文 01 / 場面内 0.0–4.8 秒

This is the observation device. It records and analyses anomalies.

文 02 / 場面内 4.8–9.4 秒

The device shows you footage captured by cameras in the room.

文 03 / 場面内 9.4–17.4 秒

We do not know what kind of anomaly will occur. If anything in the footage feels wrong, even slightly, report it.

文 04 / 場面内 17.4–24.0 秒

To report, hold a button on the left controller for one second. X or Y, either will do.

文 05 / 場面内 24.0–30.4 秒

The agent in the device will analyse your report. It does not matter if you are mistaken.

### 04 wear — Ending and equipment

開始 85.8 秒 / 尺 18.8 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–5.4 秒

When the survey ends, stay still until a member of staff removes the device.

文 02 / 場面内 5.4–10.4 秒

If you cannot continue, stop where you are and take off the device.

文 03 / 場面内 10.4–15.8 秒

Now. Hold the controller in your left hand and put on the observation device.

文 04 / 場面内 15.8–18.8 秒

Good luck with your survey.

## fr

### 01 introduction — Affectation

開始 0.0 秒 / 尺 18.4 秒

画像: `introduction-doctor-v2.jpg`

文 01 / 場面内 0.0–4.2 秒

Bienvenue… à l’Institut japonais de recherche sur les phénomènes étranges.

文 02 / 場面内 4.2–9.0 秒

Je suis… votre supérieur. Disons-le ainsi.

文 03 / 場面内 9.0–13.8 秒

À partir d’aujourd’hui, vous êtes enquêteur de cet institut.

文 04 / 場面内 13.8–18.4 秒

Je vais maintenant vous présenter l’objet de votre enquête.

### 02 subject — Objet et procédure

開始 18.4 秒 / 尺 42.6 秒

画像: `twin-room-v1.jpg`

文 03 からの画像: `doll-file-v1.jpg`

文 05 からの画像: `twin-walk-v1.jpg`

ループ動画: `twin-walk-v1.mp4`（文 05 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–5.6 秒

L’objet est une entité anormale, désignée sous le numéro de contrôle 0514.

文 02 / 場面内 5.6–12.0 秒

Un rideau et une poupée, trouvés dans la chambre d’un enfant, dans une maison abandonnée.

文 03 / 場面内 12.0–21.4 秒

En apparence, rien ne la distingue d’une poupée ordinaire. Mais ses propriétés anormales ne se manifestent que dans certaines conditions.

文 04 / 場面内 21.4–29.0 秒

Ni ces conditions ni la nature de l’anomalie ne sont encore connues. Les découvrir, voilà votre mission.

文 05 / 場面内 29.0–34.6 秒

Mettez le dispositif d’observation et faites trois fois le tour de ce rideau.

文 06 / 場面内 34.6–42.6 秒

Tenez la main courante de la main droite, ne la lâchez jamais et avancez lentement.

### 03 report — Maintenez X ou Y

開始 61.0 秒 / 尺 32.4 秒

画像: `device-quest3-v1.jpg`

文 02 からの画像: `twin-camera-v1.jpg`

文 03 からの画像: `twin-camera-view-v1.jpg`

文 04 からの画像: `equipment-controller-v1.png`（押すボタンの図）

文 01 / 場面内 0.0–5.2 秒

Voici le dispositif d’observation. Il enregistre et analyse les anomalies.

文 02 / 場面内 5.2–10.2 秒

Le dispositif affiche les images filmées par les caméras de la pièce.

文 03 / 場面内 10.2–19.4 秒

Nous ignorons quel type d’anomalie se produira. Si quelque chose vous semble anormal dans ces images, même légèrement, signalez-le.

文 04 / 場面内 19.4–26.4 秒

Pour signaler, maintenez un bouton de la manette gauche pendant une seconde. X ou Y, peu importe.

文 05 / 場面内 26.4–32.4 秒

L’agent du dispositif analysera votre signalement. Peu importe si vous vous trompez.

### 04 wear — Fin et équipement

開始 93.4 秒 / 尺 21.6 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–7.0 秒

À la fin de l’enquête, restez immobile jusqu’à ce qu’un membre du personnel vous retire le dispositif.

文 02 / 場面内 7.0–12.8 秒

Si vous ne pouvez plus continuer, arrêtez-vous sur place et retirez le dispositif.

文 03 / 場面内 12.8–18.6 秒

Bien. Prenez la manette dans la main gauche et mettez le dispositif d’observation.

文 04 / 場面内 18.6–21.6 秒

Bonne chance pour votre enquête.
