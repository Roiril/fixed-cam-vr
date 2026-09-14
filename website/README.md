# 廻リ視 Web

廻リ視の静的な作品サイトです。外部通信やフォームはありません。

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
- `assets/camera-01.webp` から `camera-03.webp`: カメラ映像の生成イメージ原本
- `assets/yuji-boku.woff2`: 書体見出し
- `assets/shippori-mincho.woff2`: 本文

カメラ映像は実機映像ではなく生成イメージです。音は使いません。自動切替は初期状態で停止しています。動きを減らす設定では切替演出を止めます。閲覧中にこの設定へ切り替えた場合は自動切替も停止します。企画本文は提供された原文をそのまま掲載します。新しい説明文や物語は追加しません。公開URLは未定です。

原稿のパスと生成プロンプトは [assets/SOURCES.md](assets/SOURCES.md) にあります。画像と書体はサイト内から配信します。書体のOFLライセンスもビルドに含みます。
