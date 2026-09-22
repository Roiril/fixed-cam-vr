# 廻リ視 Web

廻リ視の作品サイトです。黒い空間の立体題字 → キービジュアル → 固定視点 → 視点切り替え体験の紹介 → 調査依頼 → 記録 / ARCHIVE → ネタバレを含む実機映像 → クレジットの順で構成します。閲覧時の素材は自前配信です。フォームはありません。制作中の別サイトはまだ読み込みません。

2026-09-22のユーザー指示で、カメラ3枚と壁の写真をPC内の実際の撮影写真に差し替えました。ヒーローは「今のウェブのヒーローの見た目」を現すという追加指示に合わせ、既存の作品キービジュアルを維持しています。写真の撮影場所と出典は `assets/SOURCES.md` に記録しています。HMD装着者の写真としては扱いません。

## 入口

`entrance.js` と `entrance.css` が黒い背景の立体題字を表示します。既存の `assets/title.png` の表裏と輪郭に厚みを付けてWebGLで描画します。タップまたは「タイトルを割って入る」で60個の断片が約2秒で散り、ヒーローが現れます。追加ライブラリはありません。

入口は画像と描画の準備ができた場合にだけ開きます。スキップとEscapeで直ちに本文へ移動できます。動きを減らす設定やWebGL非対応では平面表示に切り替わります。タブ内の初回だけ表示し、ページ内リンクからの訪問では表示しません。フッターから再表示できます。終了時に描画資源とイベントを解放します。

## 視点切り替え体験の受け皿

`experience-config.js` の `gameUrl` は空です。完成前は「準備中」と3つの視点の紹介だけを表示します。空のiframeや起動できないボタンは出しません。

別タスクのサイトが完成したら `gameUrl` にHTTPS URLか同一サイト内の相対URLを設定します。ユーザーが「このページで体験する」を押してからiframeを生成します。読み込みに失敗した場合や埋め込みを拒否された場合も、別タブで開くリンクを使えます。閉じるとiframeを破棄します。カメラの選択状態とは連動させません。相手側は `frame-ancestors` / `X-Frame-Options` で `https://mawarimi.vercel.app` からの埋め込みを許可する必要があります。相手サイトをこの作業で変更してはいません。

## 実機映像

`#footage-gate` の折り畳みを自分で開くまで、映像本体とネタバレを含むポスターを読み込みません。開いても再生は始まりません。3本の映像を選べます。閉じると停止してURLを除去します。タブを離れた場合も停止し、復帰時には再開しません。

| 映像 | 尺 | 収録 |
|---|---:|---|
| 現実がスクリーンになる | 18秒 | Quest 3の片目 |
| エラーの演出 | 11秒 | Quest 3の右目 |
| 目の演出 | 11秒 | Quest 3の左目。視界の周囲を含む |

すべて実機の検証録画です。原本に音声はありません。出典と切り出し時刻は `assets/FOOTAGE.md`。再生成は `py -3.11 scripts/prepare-footage.py`。単独の再生成は `py -3.11 scripts/prepare-footage.py fracture` のようにIDを指定します。写真は `py -3.11 scripts/prepare-photos.py` で原本からWebPへ変換します。

記録は生成りの地に黒で刷った荷札2枚、クレジットは黒い金属の銘板1枚として組んでいます。受賞・展示予定は2026-09-15のユーザー指定に基づきます。展示日は月までを表示しています。荷札のバーコードは `IVRC2026` と `DCEXPO2026` を Code 128 B で符号化した本物、銘板の QR は公開URLです。

クレジットは2026-09-15のユーザー指定の10項目です。役割と「白石」「五島」「チーム」の表記をそのまま表示します。行番号01〜10はCSSのカウンタで描くので、本文の選択やコピーには混ざりません。

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
- `assets/camera-01.webp` から `camera-03.webp`: 実際のカメラ撮影写真
- `assets/investigation-request.webp`: 調査依頼書の原本1ページ
- `assets/wall-evidence.webp`: 実際の撮影場所の壁写真
- `assets/footage-*.mp4` / `*.webp`: 実機片目録画とポスター
- `assets/yuji-boku.woff2`: 書体見出し
- `assets/shippori-mincho.woff2`: 本文
- `assets/ibm-plex-mono-400.woff2` / `ibm-plex-mono-500.woff2`: データ表記
- `assets/chakra-petch.woff2`: 見出しの欧文と荷札の題
- `assets/fonts-manifest.json`: 各書体の収録文字。`npm run check` が本文との差を見る（配信しない）
- `scripts/codes/*.svg`: 荷札のバーコードと銘板のQR。`py -3.11 scripts/make-codes.py` で作り直し、`index.html` へ中身を貼る（配信しない）

カメラは1から3までを6秒ごとに順番に切り替えます。ページが非表示の間は止まり、表示へ戻ると6秒から再開します。3つのボタンとキーボードでも選択でき、操作後は同じカメラを選んだ場合も6秒から再開します。選択中のカメラはURLの `camera` クエリに保存され、ブラウザの戻る・進む操作にも追従します。動きを減らす設定では切替演出だけを止め、自動切替は続けます。自動切替のON/OFF操作は設けません。調査依頼書は画像を選ぶと原寸で開きます。音は使いません。

原稿のパスと素材の変更履歴は [assets/SOURCES.md](assets/SOURCES.md) にあります。画像と書体はサイト内から配信します。書体のOFLライセンスもビルドに含みます。旧CRTと旧3Dシーンは公開ビルドに含めません。ローカルサーバーもMP4の範囲リクエストに対応しています。
