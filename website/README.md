# 廻リ視 Web

廻リ視の静的な作品サイトです。キービジュアル、固定視点、調査依頼、記録 / ARCHIVE の4セクションで構成します。外部通信やフォームはありません。

記録欄の受賞・展示予定は2026-09-15のユーザー指定に基づきます。展示日は月までを表示しています。

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

`npm run build` は公開に必要なファイルだけを `dist/` へ出力します。`dist/` は生成物です。

Vercelでは `vercel.json` により同じビルドを実行し、`dist/` だけを公開します。

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

カメラは3つのボタンで手動切替します。選択中のカメラはURLの `camera` クエリに保存され、ブラウザの戻る・進む操作にも追従します。動きを減らす設定では切替演出を止めます。調査依頼書は画像を選ぶと原寸で開きます。音は使いません。公開URLは未定です。

原稿のパスと生成プロンプトは [assets/SOURCES.md](assets/SOURCES.md) にあります。画像と書体はサイト内から配信します。書体のOFLライセンスもビルドに含みます。
