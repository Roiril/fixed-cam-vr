# 博士の導入台本

台詞の正本。ユーザーが指定した文言と、それ以外の未判定の実装案を含む。

正本は `tablet/app/src/main/assets/web/asset/briefing-v1.json`。この文書と SRT はそこから生成する。
博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。

2026-11-12 に台詞を書き直した（`canon/LEDGER.md` 0327。0328 で「異常はまだ何も判明していない」前提に直した。名を伏せた上司・管理番号 0514・資料はテレビの中・絵はデジタルツイン。文の案は `canon/OPEN.md`「博士の説明の書き直し」）。
同じ日に、何も知らない読み手 3 人に読ませて直した（0338。装置に映る物を歩き方より先に言う・終わりを誰が知らせるか・手すりを持ったままどう外すか。`canon/OPEN.md`「博士の台詞を初見の読み手に読ませて直す」）。
日本語の声（v5）は `generate-doctor-audio.py` が `elevenlabs-input-ja-v5.md` を 1 本で合成し、1 字ごとの時刻から場面と文へ切る。切り出すときに 1.2 倍へ速める（0328）。日本語の時間はその実測。声は voice bEg9geTboP3ghzJ9Rt3L。日本語は、DomoAI の口の動画（30 本）を博士の部屋の試作に入れ、カメラの動きと声ごと焼いた 1 本（`doctor-room-ja-v1.mp4`）を各場面の `film.ja` から流す（0345）。英語と仏語は静止画のまま。
声の無い言語の時間は字数からの見積もり（オートの送りに使う）。英仏音声と英仏動画は未制作。
通常は一文ずつ全文表示してタップを待つ。オートを選んだときだけ次の文へ進む。
博士動画は既存博士画像と同じ人物を中央配置で使用する。映像の切替や字幕を動画に焼き込まない。

前の台詞の元音源は `tools/visitor-ui/doctor-ja-v2-original.mp3`（`場面ID-ja-v2.mp3` / `.mp4` として同梱したまま。場面からは参照しない）。新しい音源は `ingest-doctor-audio.py` で場面ごとに切り出し、`ingest-doctor-video.py` で生成動画へ組み込み、各場面の `video.ja` に書く。英仏音声や動画を追加する場合も場面単位にする。
各文の開始は同じ場面にある前の文の durationMs の合計。手動では文の終端で媒体を止める。
一文送りは発話を待たず次の文の開始位置へ移る。文間の間も直前の文の尺へ含める。
音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。

## ja

### 01 introduction — 着任

開始 0.0 秒 / 尺 15.5 秒

画像: `introduction-doctor-v2.jpg`

音声: `introduction-ja-v5.mp3`

文 01 / 場面内 0.0–3.7 秒

ようこそ。日本怪異研究所へ。

文 02 / 場面内 3.7–7.9 秒

私は……あなたの上司、とでも言っておきましょうか。

文 03 / 場面内 7.9–11.8 秒

あなたは今日から、この研究所の調査員です。

文 04 / 場面内 11.8–15.5 秒

これより、調査対象について説明します。

### 02 subject — 調査対象

開始 15.5 秒 / 尺 28.4 秒

画像: `twin-room-v2.jpg`

文 03 からの画像: `doll-file-v1.jpg`

音声: `subject-ja-v5.mp3`

文 01 / 場面内 0.0–6.1 秒

調査対象は、管理番号0514に指定された異常存在です。

文 02 / 場面内 6.1–11.1 秒

ある空き家の子供部屋から見つかった、カーテンと人形です。

文 03 / 場面内 11.1–20.4 秒

どちらも、外見上は一般的な物と変わりません。しかし、特定の条件下でのみ、異常性が発現します。

文 04 / 場面内 20.4–28.4 秒

その条件も、異常性の正体も、まだ判明していません。それを調査するのが、あなたの任務です。

### 03 report — 三周して、XかYで報告

開始 44.0 秒 / 尺 48.1 秒

画像: `device-quest3-v1.jpg`

文 03 からの画像: `twin-camera-v2.jpg`

文 04 からの画像: `twin-walk-v1.jpg`

文 07 からの画像: `equipment-controller-v1.png`（押すボタンの図）

音声: `report-ja-v5.mp3`

ループ動画: `twin-walk-v1.mp4`（文 04 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–4.9 秒

これが、怪異を記録し、解析するための観測装置です。

文 02 / 場面内 4.9–8.8 秒

装置をつけると、目の前に画面が現れます。

文 03 / 場面内 8.8–13.9 秒

そこに、室内のカメラが捉えた、あなた自身の姿が映ります。

文 04 / 場面内 13.9–19.0 秒

その映像を見ながら、このカーテンの周りを三周してください。

文 05 / 場面内 19.0–24.2 秒

右手で手すりを持ち、決して離さず、ゆっくり歩いてください。

文 06 / 場面内 24.2–29.0 秒

映像に少しでも違和感を覚えたら、報告してください。

文 07 / 場面内 29.0–39.3 秒

報告するときは、左コントローラーのボタンを、親指で1秒間、長押ししてください。XとY、どちらでも構いません。

文 08 / 場面内 39.3–48.1 秒

何を見たかは、装置に搭載されたエージェントが解析します。見間違いでも、何度報告しても構いません。

### 04 wear — 終了と装着

開始 92.1 秒 / 尺 23.2 秒

画像: `wear-doctor-v2.jpg`

音声: `wear-ja-v5.mp3`

文 01 / 場面内 0.0–9.2 秒

装置の合図で歩き始めてください。終わりの合図の後は、係の者が装置を外すまで、動かずにお待ちください。

文 02 / 場面内 9.2–15.4 秒

続けられないときだけは、その場で立ち止まり、手すりを離して、装置を外してください。

文 03 / 場面内 15.4–21.5 秒

では。係の者の案内に従って、観測装置をつけてください。

文 04 / 場面内 21.5–23.2 秒

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

### 02 subject — Subject

開始 17.0 秒 / 尺 24.0 秒

画像: `twin-room-v2.jpg`

文 03 からの画像: `doll-file-v1.jpg`

文 01 / 場面内 0.0–4.4 秒

The subject is an anomalous entity, designated Control Number 0514.

文 02 / 場面内 4.4–8.9 秒

A curtain and a doll, found in a child’s room in an abandoned house.

文 03 / 場面内 8.9–17.3 秒

In appearance, neither differs from an ordinary object. But only under certain conditions, their anomalous properties manifest.

文 04 / 場面内 17.3–24.0 秒

Neither those conditions nor the nature of the anomaly are known yet. Finding out is your assignment.

### 03 report — Three laps. Report with X or Y

開始 41.0 秒 / 尺 40.0 秒

画像: `device-quest3-v1.jpg`

文 03 からの画像: `twin-camera-v2.jpg`

文 04 からの画像: `twin-walk-v1.jpg`

文 07 からの画像: `equipment-controller-v1.png`（押すボタンの図）

ループ動画: `twin-walk-v1.mp4`（文 04 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–4.4 秒

This is the observation device. It records and analyses anomalies.

文 02 / 場面内 4.4–8.4 秒

When you put on the device, a screen appears in front of you.

文 03 / 場面内 8.4–12.2 秒

It shows you yourself, as seen by the cameras in the room.

文 04 / 場面内 12.2–16.2 秒

Watching that footage, walk around this curtain three times.

文 05 / 場面内 16.2–20.8 秒

Hold the handrail with your right hand, never let go, and walk slowly.

文 06 / 場面内 20.8–25.1 秒

If anything in the footage feels wrong, even slightly, report it.

文 07 / 場面内 25.1–31.9 秒

To report, hold a button on the left controller with your thumb for one second. X or Y, either will do.

文 08 / 場面内 31.9–40.0 秒

The agent built into the device will analyse what you saw. It does not matter if you are mistaken, or how often you report.

### 04 wear — Ending and equipment

開始 81.0 秒 / 尺 22.2 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–8.4 秒

Start walking when the device gives the signal. After it signals the end, stay still until a member of staff removes the device.

文 02 / 場面内 8.4–14.8 秒

Only if you cannot continue: stop where you are, let go of the handrail, and take off the device.

文 03 / 場面内 14.8–19.2 秒

Now. Follow the staff’s guidance and put on the observation device.

文 04 / 場面内 19.2–22.2 秒

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

### 02 subject — Objet

開始 18.4 秒 / 尺 25.0 秒

画像: `twin-room-v2.jpg`

文 03 からの画像: `doll-file-v1.jpg`

文 01 / 場面内 0.0–4.6 秒

L’objet est une entité anormale, désignée sous le numéro de contrôle 0514.

文 02 / 場面内 4.6–10.1 秒

Un rideau et une poupée, trouvés dans la chambre d’un enfant, dans une maison abandonnée.

文 03 / 場面内 10.1–18.6 秒

En apparence, rien ne les distingue d’objets ordinaires. Mais leurs propriétés anormales ne se manifestent que dans certaines conditions.

文 04 / 場面内 18.6–25.0 秒

Ni ces conditions ni la nature de l’anomalie ne sont encore connues. Les découvrir, voilà votre mission.

### 03 report — Trois tours. Signalez avec X ou Y

開始 43.4 秒 / 尺 41.0 秒

画像: `device-quest3-v1.jpg`

文 03 からの画像: `twin-camera-v2.jpg`

文 04 からの画像: `twin-walk-v1.jpg`

文 07 からの画像: `equipment-controller-v1.png`（押すボタンの図）

ループ動画: `twin-walk-v1.mp4`（文 04 から章末まで。文送りとは別に再生）

文 01 / 場面内 0.0–4.6 秒

Voici le dispositif d’observation. Il enregistre et analyse les anomalies.

文 02 / 場面内 4.6–8.5 秒

Quand vous mettez le dispositif, un écran apparaît devant vous.

文 03 / 場面内 8.5–12.0 秒

Il vous montre vous-même, vu par les caméras de la pièce.

文 04 / 場面内 12.0–16.0 秒

En regardant ces images, faites trois fois le tour de ce rideau.

文 05 / 場面内 16.0–21.1 秒

Tenez la main courante de la main droite, ne la lâchez jamais et avancez lentement.

文 06 / 場面内 21.1–26.2 秒

Si quelque chose vous semble anormal dans ces images, même légèrement, signalez-le.

文 07 / 場面内 26.2–33.1 秒

Pour signaler, maintenez avec le pouce un bouton de la manette gauche pendant une seconde. X ou Y, peu importe.

文 08 / 場面内 33.1–41.0 秒

L’agent intégré au dispositif analysera ce que vous avez vu. Peu importe si vous vous trompez, ou combien de fois vous signalez.

### 04 wear — Fin et équipement

開始 84.4 秒 / 尺 23.8 秒

画像: `wear-doctor-v2.jpg`

文 01 / 場面内 0.0–8.5 秒

Commencez à marcher au signal du dispositif. Après le signal de fin, restez immobile jusqu’à ce qu’un membre du personnel vous le retire.

文 02 / 場面内 8.5–15.8 秒

Seulement si vous ne pouvez plus continuer : arrêtez-vous sur place, lâchez la main courante et retirez le dispositif.

文 03 / 場面内 15.8–20.8 秒

Bien. Suivez les indications du personnel et mettez le dispositif d’observation.

文 04 / 場面内 20.8–23.8 秒

Bonne chance pour votre enquête.
