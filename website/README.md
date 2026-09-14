# 廻リ視 Web

廻リ視の静的な作品サイトです。キービジュアル、固定視点、調査依頼の3セクションで構成します。外部通信やフォームはありません。

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
