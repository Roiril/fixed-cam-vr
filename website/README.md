# 廻リ視 Web

廻リ視の作品サイトです。黒い空間の立体題字 → キービジュアル → 固定視点とは → 実世界×固定視点 → ストーリー → 展示履歴とトレイラー → こだわりのVR演出 → クレジットの順で構成します。トレイラーはYouTube埋め込み、ほかの素材は自前配信です。フォームはありません。

2026-09-22のユーザー指示で、カメラ3枚と壁の写真をPC内の実際の撮影写真に差し替えました。ヒーローは「今のウェブのヒーローの見た目」を現すという追加指示に合わせ、既存の作品キービジュアルを維持しています。写真の撮影場所と出典は `assets/SOURCES.md` に記録しています。HMD装着者の写真としては扱いません。

## 入口

`entrance.js` と `entrance.css` が黒い背景の立体題字を表示します。既存の `assets/title.png` の表裏と輪郭に厚みを付けてWebGLで描画します。案内はタッチ端末で「タップ」、マウス端末で「クリック」。画面全体が開始操作を受け付けます。追加ライブラリはありません。

`entrance-geometry.js` は `Assets/Scripts/Streaming/IntroFractureMesh.cs` の分割を移植したものです。15放射と10輪をもとにした267片（三角178片・四角89片）を使います。`entrance-glass.js` は `IntroFractureTime.hlsl` と `IntroFracture.shader` の5秒の時間式を移植しています。亀裂が走り、一撃で飛び、漂い、重さに応じて加速して戻ります。着地は進行率 .70〜.87、最後の3片は .90。現行HLSLの着地幅 .17 を正とします。旧記録の .13 は使いません。

凍結する像は入口の実際の3D題字と黒背景です。飛んでいる破片にも元の白い筆跡と赤い印を保ちます。本体の暗いガラス材質をそのまま掛けると題字が黒く潰れるため、ウェブ版は画像の色と明るさを保ち、反射を弱く重ねます。破片には本体と同じ薄い厚み、研磨した縁、反射帯、屈折を付けます。ウェブでは両眼の投影と周辺球殻を使わず、画面の縦横比に合わせた投影にします。帰還中も題字の断片を保ち、着地した部分から120msで背後の実HTMLへ変わります。最後の3片は進行率 .90 で中央へ着地し、その部分のウェブ表示も現れます。最後は縁が中央から消えます。別のヒーロー画像への置き換えや拡大フェードで終わらせません。

入口は画像と描画の準備ができた場合にだけ開きます。スキップとEscapeで直ちに本文へ移動できます。動きを減らす設定やWebGL非対応では平面表示に切り替わります。タブ内の初回だけ表示し、ページ内リンクからの訪問では表示しません。フッターから再表示すると先頭へ戻って開始します。終了時に描画資源とイベントを解放します。

## 携帯ゲーム機の視点切り替え体験

`experience-config.js` の `gameUrl` は `perspective-game/?embedded=1`。ユーザー指定のローカル体験を同サイト内へ収めています。コピー元と版は `perspective-game/README.md`。元のゲームは編集していません。架空の古い携帯ゲーム機は画像生成した `assets/handheld.webp`。生成条件は `assets/handheld-prompt.md`。

ページ表示時にiframeを生成してゲーム機を起動します。固定視点への切り替えが終わるまで画面を隠し、固定視点から遊べる状態で表示します。実描画が始まってから本体の十字キーと左右のスティックを有効にします。下の3ボタンで視点を選べます。拡大表示はEscapeで戻れます。「電源を切る」でiframeを破棄し、画面内の「電源を入れる」で再起動します。別タブでは元の操作画面で遊べます。`handheld.js` と埋め込み側の `embed.js` は同一originと送信元を確認して操作を渡します。埋め込み中は隠した設定画面のショートカットを止めます。

展示履歴の直下にYouTubeトレイラー `ipoU4gU9G4k` を置きます。映像には「2026.09.01時点で作成した古いバージョン」と明記します。YouTube埋め込みが使えない環境向けに元動画へのリンクも置きます。

## 実機映像

`#footage-gate` の折り畳みを自分で開くまで、映像本体とネタバレを含むポスターを読み込みません。開いても再生は始まりません。3本の映像を選べます。閉じると停止してURLを除去します。タブを離れた場合も停止し、復帰時には再開しません。

| 映像 | 尺 | 収録 |
|---|---:|---|
| 現実がスクリーンになる | 14秒 | Quest 3の片目。冒頭4秒をカット |
| エラーの演出 | 11秒 | Quest 3の右目 |
| 目の演出 | 11秒 | Quest 3の左目。視界の周囲を含む |

すべて実機の検証録画です。原本に音声はありません。出典と切り出し時刻は `assets/FOOTAGE.md`。再生成は `py -3.11 scripts/prepare-footage.py`。単独の再生成は `py -3.11 scripts/prepare-footage.py fracture` のようにIDを指定します。写真は `py -3.11 scripts/prepare-photos.py` で原本からWebPへ変換します。

記録は生成りの地に黒で刷った荷札2枚、クレジットは黒い金属の銘板1枚として組んでいます。受賞・展示予定は2026-09-15のユーザー指定に基づきます。展示日は月までを表示しています。荷札のバーコードは `IVRC2026` と `DCEXPO2026` を Code 128 B で符号化した本物、銘板の QR は公開URLです。

クレジットは2026-09-15のユーザー指定の10項目です。役割と「白石」「五島」「チーム」の表記をそのまま表示します。行番号01〜10はCSSのカウンタで描くので、本文の選択やコピーには混ざりません。

2026-09-22の追加指示でスマホでも役割を左、氏名を右に並べています。制作名は `Roil Studio`、連絡先は `rinkyouaoi@gmail.com` のメールリンクです。

固定視点の写真は既存の `crt.webp` 内に表示します。本体の `ScreenMjpeg.mat` にある露出・コントラスト・彩度・周辺減光を参照し、約50%の強さにします。写真原本を加工せずCSSで明度 .72・コントラスト1.06・彩度 .76と軽い暖色、周辺減光を付けます。調査依頼は `camera-01.webp` を共用します。

装飾に見える文字列は実在の事実から導いたものだけです。`REC 01` は記録の通し番号、`ivrc.net` と `dcexpo.jp` はリンク先のホスト名、`5EFB-30EA-8996` は「廻リ視」3字のUnicodeコードポイント、`MAWARIMI` は構造化データの別表記です。

見出しの右に出る更新日 `data-updated` は `sitemap.xml` の `<lastmod>` と同じ値にします。片方だけ直すと `npm run check` が落ちます。

## 起動

```powershell
npm run dev
```

`http://127.0.0.1:4173/` で開きます。`PORT` 環境変数でポートを変更できます。

## 検証とビルド

```powershell
npm run check
npm run build
npm start
```

## 書体

```powershell
npm run fonts
```

日本語の2書体は `index.html` の画面に出る文字だけを収録したサブセットです。**文言を変えたら走らせ直します。** 走らせないと新しい字が抜けたままになり、`npm run check` が「どの字が足りないか」を名指しで落とします。`npm run fonts -- mincho yuji` のように書体名で絞れます（キーは `mincho` `yuji` `plex` `chakra`）。欧文の2書体は印字できるASCII全部を持つので取り直しは不要です。

| 書体 | 使う場所 |
|---|---|
| Yuji Boku | 見出しの和文（記録・クレジット）、サイト名、銘板の背の「廻リ視」 |
| Shippori Mincho | 本文、役割名、人名、受賞と展示予定 |
| Chakra Petch 600 | 見出しの欧文（`/ ARCHIVE` `/ CREDITS`）、荷札の題 |
| IBM Plex Mono 400 / 500 | データ表記（`REC 01` `2026-11` `ivrc.net` ほか）、クレジットの行番号 |

`npm run build` は公開に必要なファイルだけを `dist/` へ出力します。`dist/` は生成物です。

Vercelでは `vercel.json` により同じビルドを実行し、`dist/` だけを公開します。

Vercelプロジェクト `roilils-projects/mawarimi` はGit連携なしです。公開はこのディレクトリで `vercel deploy --prod --scope roilils-projects` を実行します。`--scope` を省くと "Not authorized" で止まります（2026-09-19 実測）。CLI を入れていなければ `npx --yes vercel@latest deploy --prod --scope roilils-projects` で同じことができます。`--prod` を付けなければプレビュー URL に出ます。プレビューは Deployment Protection が効いていて、Vercel にログインしたブラウザでしか開けません。初回は `vercel link --project mawarimi --scope roilils-projects` で既存プロジェクトへ接続します。`.vercel/` と `.env*` はローカル専用です。

Google Search Console の所有権確認には `google5081a8a413a7871f.html` を使います。確認状態を維持するため、公開後も削除しません。

公開URLは `https://mawarimi.vercel.app/` です。検索エンジン向けの正規URL・OGP・構造化データは `index.html`、巡回設定は `robots.txt` と `sitemap.xml` で管理します。構造化データでは「まわりみ」「廻り視」「マワリミ」「Mawarimi」を別表記として示します。

## 素材

- `assets/hero-desktop.webp`: デスクトップ用キービジュアル原本
- `assets/hero-small.webp`: 小さい画面用キービジュアル
- `assets/hero-mobile.webp`: モバイル用キービジュアル原本
- `assets/title.png`: 入口の立体題字に使う既存の題字
- `assets/crt.webp`: 固定視点のレトロテレビ
- `assets/camera-01.webp` から `camera-03.webp`: 実際のカメラ撮影写真
- `assets/investigation-request.webp`: 調査依頼書の原本1ページ
- `assets/footage-*.mp4` / `*.webp`: 実機片目録画とポスター
- `assets/yuji-boku.woff2`: 書体見出し
- `assets/shippori-mincho.woff2`: 本文
- `assets/ibm-plex-mono-400.woff2` / `ibm-plex-mono-500.woff2`: データ表記
- `assets/chakra-petch.woff2`: 見出しの欧文と荷札の題
- `assets/fonts-manifest.json`: 各書体の収録文字。`npm run check` が本文との差を見る（配信しない）
- `scripts/codes/*.svg`: 荷札のバーコードと銘板のQR。`py -3.11 scripts/make-codes.py` で作り直し、`index.html` へ中身を貼る（配信しない）

カメラは1から3までを6秒ごとに順番に切り替えます。ページが非表示の間は止まり、表示へ戻ると6秒から再開します。3つのボタンとキーボードでも選択でき、操作後は同じカメラを選んだ場合も6秒から再開します。選択中のカメラはURLの `camera` クエリに保存され、ブラウザの戻る・進む操作にも追従します。動きを減らす設定では切替演出だけを止め、自動切替は続けます。自動切替のON/OFF操作は設けません。調査依頼書は画像を選ぶと原寸で開きます。音は使いません。

原稿のパスと素材の変更履歴は [assets/SOURCES.md](assets/SOURCES.md) にあります。画像と書体はサイト内から配信します。書体のOFLライセンスもビルドに含みます。未使用の壁写真と旧3Dシーンは公開ビルドに含めません。ローカルサーバーもMP4の範囲リクエストに対応しています。
