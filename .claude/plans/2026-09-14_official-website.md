# 廻り視 公式Webサイト

2026-09-14 / Web表現としての制作判断。作品の正典への追記ではない。

## 主題

「視点を、置き去りにする。」
装飾として監視映像を真似るのではなく、操作する位置と観測される位置の時間差を体験にする。
主画面の操作は即時。遅れるのは入力を持たない映像窓だけ。
Loadingから窓が開き、3つの固定視点を経て、現在の自分と少し前の自分が同時に映る。
批判検討を受け、ただの光点や無人の軌跡ではなく、同じ部屋の小さな人物として自分を表す。

## 制作範囲

website/ に独立した静的サイト。HTML / CSS / ES modules / Canvas 2D / Web Animations。
追加のランタイム依存は使わない。Node組み込み機能で開発サーバーと成果物コピーを用意する。
既存の題字と画像を利用。日程・予約・実機の映像など未確認の事実は作らない。
公開はまだ行わず、ローカルで完成状態を検証する。

## 画面

1. 読込: 一本の光と画角。実際の画像準備を待つ。スキップ可能。エラー時にも本文へ進む。
2. First View: 既存の暗い舞台画像を全面に固定。骨白の大きな文字と真鍮色の細い観測窓。
   窓だけが450ms遅れて入力位置を追い、同じ画像の別画角を見せる。
   立体的な部屋と身体は後段に置き、冒頭は作品素材の質感と静けさを優先する。
3. 作品: 淡い紙色の面。自分を外から見る体験を短い文章で伝える。
4. 視点: 3つの視点ボタン / 現在の自分 / 遅れた自分。Web上の表現であると明示。
5. 展示: DCEXPOへの出展準備という既知の状態。公式イベント情報への実リンク。
   2026-09-14に https://www.dcexpo.jp/ でイベント会期2026-11-18〜20と幕張メッセ開催を確認。
   作品の体験枠や予約は未確定。イベント全体の概要と区別する。
6. 余韻: 現在像を保ったまま過去像が短く再演される。Roil Studioの署名。

## 共通契約

HTMLとCSS担当: website/index.html / website/styles.css。
動作担当: website/main.js。scene.jsのcreateSceneをimport。
描画担当: website/scene.js / website/assets/ / website/scripts/prepare-assets.ps1。
createScene(canvas,{reducedMotion}) は {ready:Promise,setView(index),setPointer(x,y,active),setProgress(p),setReducedMotion(bool),setPaused(bool),replay(),dispose()} を返す。
x,yは0..1。setProgressはセクション内の0..1。カメラindexは0..2。
readyはロード失敗でもsettle。画像がなくても美しい図形の窓を描けること。

主要DOM: #loading #loading-count #loading-line #skip-intro #site-header #menu-toggle #site-menu #motion-toggle #sound-toggle #hero #hero-canvas #hero-stage #about #perspective #perspective-canvas #view-title #view-copy #visit #return #replay #progress-fill #current-chapter #cursor-echo。
カメラボタン: [data-view="0|1|2"]。再生ボタン: #replay。リビール: [data-reveal]。
ローダー終了はbody.is-ready、メニューはbody.menu-open。reduced motionはbody.motion-reduced。
画像: assets/room.jpg / assets/dolls.jpg / assets/title.png / assets/studio.png / assets/pattern.jpg / assets/favicon.png。
音は任意のクリックで有効化する。自動再生しない。
スマホ・キーボード・動きを減らす設定・JS無効・画像失敗でも作品説明と展示情報へアクセス可能。

## 検証

PC / モバイル / 幅の広い画面の描画確認。
Loading完了とスキップ。カメラ3方向。メニューとEscape。音と動きの設定。スクロールと内部リンク。
Browser errors / 横はみ出し / 画像の実在 / build成功 / 通常と低モーション / キーボード。
成果物と起動法をREADMEへ記載し、今回のパスのみコミット。

## 実測記録

2026-09-14。Codex内ブラウザーで320 / 375 / 1280 / 1920px幅を確認。
375pxではヘッダーの操作名、メニューのフォーカス移動、部屋全景、画像ダイアログを確認。
画像ダイアログは画像177.6pxに対して全体270.8px。本文画像4要素の読み込みを確認。
停止後の矢印キー1入力で描画14→20、入力0→1。現在X=0.1785、過去X=0を同時に描画。
視点1→2への切替で現在位置を保持。再演中も現在像と過去像が別座標で描画された。
Canvas表示率80%以上で再演開始。画面外ではpaused=trueになった。
音は初期uninitialized、操作後running、停止後suspended。低モーション設定は再読込後も保持。
初回スキップでloaderHidden=true。モジュールを意図的に404にした別サーバーでも3.5秒後に本文を表示。
scriptタグを除いた確認ページでは本文opacity=1、Loading非表示、舞台の静止画を表示。
確認専用サーバーは停止。通常のローカルプレビューは4173番で起動を維持。
