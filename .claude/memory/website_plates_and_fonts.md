---
name: website-plates-and-fonts
description: 公式サイトの記録・クレジット（荷札と銘板）を触る前に。書体サブセットの門・Vercel の scope・ヘッドレス撮影の 2 つの罠
metadata: 
  node_type: memory
  type: project
  originSessionId: b65af3c9-ad9d-4f39-85fe-c62879e7b739
  modified: 2026-09-19T00:23:22.227Z
---

# 公式サイトの記録・クレジット（2026-09-19 再設計）と、その周辺の罠

`website/` の「記録 / ARCHIVE」は生成りの荷札 2 枚、「クレジット」は黒い銘板 1 枚（コミット 0f3b22e4）。
設計の判定と装飾文字列の規則は `website/AGENTS.md`（2026-09-19 のユーザー指示）と `website/README.md` が正本。

## 書体は「画面に出る文字だけ」のサブセット。文言を変えたら `npm run fonts`

- 和文 2 書体（Yuji Boku / Shippori Mincho）は Google Fonts の `text=` で必要な字だけ取る（`scripts/fetch-fonts.mjs`。fontTools 不要）。
  **1 字でも新しい漢字を刷ると代替書体に落ちる。** `scripts/check.mjs` が `assets/fonts-manifest.json` と本文を突き合わせ、足りない字を名指しで落とす
- どの要素がどの書体かは `scripts/font-text.mjs` の `headingText` / `latinText` が持つ。**CSS で font-family を変えたらここも変える**（対応を見る検査は無い）
- `npm run fonts -- mincho yuji` で絞れる。ネットワークが要る

**Why:** 2026-09-15 の初版は「必要文字を含む WOFF2」を手で取っていて、収録の再現手段が無かった。
**How to apply:** 文言を触るコミットには woff2 と manifest の差分が同時に入っているはず。入っていなければ fonts を走らせ忘れている。

## `check.mjs` の門（2026-09-19 に足したもの）

- html/css/js に `three` `webgl` `cdn.` の部分文字列があると落ちる（**英単語の一部でも**。英語コメントを書かない）
- html / css / webmanifest が参照するファイルが `dist/` にも入っているか（`build.mjs` の `publicFiles` への足し忘れ）
- `data-updated` と `sitemap.xml` の `<lastmod>` が同じ値か。**ページを更新したら両方を同じ日付にする**
- 通す側と止める側の両方で校正済み（publicFiles から 1 行抜いて落ちることを確認）

## Vercel の公開

- CLI は入っていない。`npx --yes vercel@latest deploy --yes --scope roilils-projects` で動く（認証は `%APPDATA%\com.vercel.cli\Data\auth.json` に残っている。値は出さない）
- ⚠ **`--scope roilils-projects` を付けないと "Not authorized"**（`.vercel/project.json` が team の orgId でも、既定 scope が個人アカウントになる）
- `--prod` 無しはプレビュー URL。**Deployment Protection が効いていて、開くには Vercel にログインが要る**（curl は `vercel.com/sso-api` へ 302）。ユーザー本人なら見える

## 描画の確認で踏んだ計器の罠 2 つ

1. **headless Chrome の `--window-size` は幅 512px より狭くできない**（`data:` ページで `innerWidth` を出して確認）。390 で撮ると 512 で描いた画を 390 に切り落とすので「スマホで右がはみ出す」の顔になる。**スマホ幅はアプリ内ブラウザの mobile プリセットで撮る**（在席のスクショが取れる）。512 以上は headless で等倍が撮れる（`--virtual-time-budget=6000`）
2. **アプリ内ブラウザのペインが隠れていると `document.hidden` が true で IntersectionObserver が発火しない。** スクロール出現（`data-reveal`）が「動いていない」ように見えるが、ページの不具合ではない。JS で `document.visibilityState` を先に見る

**Why:** どちらも「対象が壊れている」と読みそうになった。§2-3 の「別の観測手段で同じ場面を取る」でどちらも 2 分で判った。

## 関連

[[web-compositor]] / [[key-visual]]（キービジュアルの地）/ 設計案の比較は 3 体の design-critic（A ダイカット・B 監視装置・C 封印札）を並列に走らせ、収束した点（緑を捨てる・当て字を置かない・`dl` を残す・画像生成は不要）を土台に A と C を合わせた
