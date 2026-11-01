# 空間を変形する独立試作

生成物と検証出力は `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/`。入口は `spatial-preview.html`。ネット接続やサーバなしで file URL から開ける。

`renderer.js` は `createSpatialScene({canvas?, assetBase?})` を export する。`await` 後に `renderAt(t, {mode:'local'|'global'|'none', strength:1, diagnostic:'beauty'|'surface'|'depth'|'wire'|'holes', display:true, showHoles:false})`。`display:false` は表示処理前。`assetBase` は呼び出し互換用で、資産は `assets.js` に埋め込んである。`readRaw()` は640×480のWebGL下原点RGBA。

元写真を各面へカメラ投影し、元の camera XYZ を varying で保持して fragment で z 除算する。変形するのは world XYZ。カメラは固定。床と仕切りは同じ滑らかな XYZ 変位場を使う。走査線・開口・ガラスの近似は別 pass で表示座標へ固定した。

室内仮面は VirtualRoom.cs の9×9×2.7m。L字の仕切りとカメラはshow.jsonの手動設定。写真の右端の棚・モニタ群と金属フレームは手で囲った輪郭と仮の奥行。実測復元ではない。三角形は各面内で分割し、深度検査する。元写真で最前に見えていた面IDを640×480で固定し、別の面がその画素を流用することを拒否する。露出した未撮影領域は補間も拡大もせず穴として残す。

global 比較は旧 motion.js のsin運動とmotion-track.jsonを用いた相似運動。旧プレビューの1.42倍拡大・射影調整・行揺れは再現していない。比較3条件は同じ新しい固定表示を通る。旧CRTとの一致を主張しない。開口だけ旧base-b.pngのalphaから抽出し、RGBはワールドへ貼っていない。

実renderのゼロ変形は307198/307200画素有効。RGB MAE 0.040343、max 1。2画素は数値境界で欠けた。有効画素で誤差1超は0。none と local strength0 はPNGのbyteが一致。時刻を戻したPNGも一致。

3.2秒の未撮影領域は17098画素（5.57%）。床94878画素、カーテンw1 6187画素、w2 26323画素、推定フレーム11330画素、推定棚20886画素が描かれた。各面から実際に元画像で見えている3点ずつを選び、15点の相似変換を最小二乗で除いたRMSは8時刻で5.90–10.95px。カメラ投影は凍結したcalib.jsの1000点と最大1.18e-13px差。

残る見た目の問題は、仮カードのまわりの大きい穴と、仮の天井・外室・床の境界の細い欠落。穴を未撮影領域の診断で確認できる。仕組みの試作で、採用する絵としての判定はまだない。

再生成は `build-assets.py` → `package.py` → `capture.mjs` → `finalize.py`。独立のheadless chromeをfile URLで開く。既存サーバ・ユーザーのブラウザ・Unityを使わない。`verify-projection.mjs` が凍結したcalib.jsと照合する。
