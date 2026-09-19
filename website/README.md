# 廻リ視 Web

廻リ視の静的な作品サイトです。キービジュアル → 固定視点 → 調査依頼 → 記録 / ARCHIVE → クレジットの順で構成します。外部通信やフォームはありません。

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
- `assets/crt.webp`: ブラウン管テレビ原本
- `assets/camera-01.webp` から `camera-03.webp`: カメラ映像素材
- `assets/investigation-request.webp`: 調査依頼書の原本1ページ
- `assets/wall-evidence.webp`: 調査対象の壁写真
- `assets/yuji-boku.woff2`: 書体見出し
- `assets/shippori-mincho.woff2`: 本文
- `assets/ibm-plex-mono-400.woff2` / `ibm-plex-mono-500.woff2`: データ表記
- `assets/chakra-petch.woff2`: 見出しの欧文と荷札の題
- `assets/fonts-manifest.json`: 各書体の収録文字。`npm run check` が本文との差を見る（配信しない）
- `scripts/codes/*.svg`: 荷札のバーコードと銘板のQR。`py -3.11 scripts/make-codes.py` で作り直し、`index.html` へ中身を貼る（配信しない）

カメラは1から3までを6秒ごとに順番に切り替えます。ページが非表示の間は止まり、表示へ戻ると6秒から再開します。3つのボタンとキーボードでも選択でき、操作後は同じカメラを選んだ場合も6秒から再開します。選択中のカメラはURLの `camera` クエリに保存され、ブラウザの戻る・進む操作にも追従します。動きを減らす設定では切替演出だけを止め、自動切替は続けます。自動切替のON/OFF操作は設けません。調査依頼書は画像を選ぶと原寸で開きます。音は使いません。

原稿のパスと生成プロンプトは [assets/SOURCES.md](assets/SOURCES.md) にあります。画像と書体はサイト内から配信します。書体のOFLライセンスもビルドに含みます。
