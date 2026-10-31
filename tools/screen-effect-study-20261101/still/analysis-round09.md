# 追加加工・9版目：OpenCVによるオフライン処理

8版目の独立比較は48%。前の候補と記録は全て維持した。今回の出力はオフラインのPythonフィルタから作る。JavaScriptでのリアルタイム計算は要件にしない。

## 使った環境

既存のOpenCV 5.0.0を実際にimportし、`edgePreservingFilter` と `detailEnhance` と `fastNlMeansDenoising` と `stylization` が使えることを確認した。新しい依存は入れていない。呼出形式は導入済みモジュールのdocstringと公式資料を照合した。

公式資料: [Denoising](https://docs.opencv.org/4.x/d1/d79/group__photo__denoise.html)、[Non-Photorealistic Rendering](https://docs.opencv.org/4.x/df/dac/group__photo__render.html)。実行時は5.0.0の公開Python関数を使う。

## 明確に違う4候補

- AはNon-local Meansで粒を整理し、Gaussian PSFを仮定したWiener処理でぼけを補正する。その後、横方向の輪郭信号を加える。
- BはNon-local Meansの後、境界を保つ平滑化とdetailEnhanceを使う。
- CはNon-local Meansとstylizationを使い、面と輪郭を単純にする。
- DはAと同じ光学処理の後、横へ相関を伸ばし、上下差分と上下のmin/maxで薄い水平の線芯を作る。

WienerはGaussianの広がりを仮定した正則化逆フィルタ。元の光学系を同定したものではなく、失われた細部を真値として復元したとも扱わない。PSF、K、相関の値は全て処理JSONへ残した。

## 水平線と差分の軸

以前の左右差分は縦の境界を選ぶ処理だった。水平に長い線片そのものを作るには、横へ相関を伸ばし、上下の差を強める必要がある。Dは7×1の低域処理で横へ伸ばし、5×1の上下min/maxと上下の高域から1〜2pxの高さの芯を作る。

dxによる縦境界の横ずれと、dyによる水平線の芯を分けた。局所的な等方性の標準偏差とtanhで丸い穴を作る処理は使わない。平坦面では上下の幅が小さくなり、芯の処理は弱まる。

参照から測った稀な変位と実測run長を使う。行全体を毎行動かさず、実測変位の大きい行だけをずらす。大半の行は変位0。

## 実画像での比較

元参照全体とA/B/C/Dの全体を実際に開いた。4候補の床の原寸切り出しと参照の床の原寸切り出しも開いた。

自分の観察では、Cは輪郭を描いた絵に寄り、今回の優先候補から外れる。AとBは元の粒が整理され、Dでは床の筋が白く硬くなる。水平の芯を作る軸を変えたDを優先候補として親へ渡した。これは独立評価の採点ではない。参照にある机と床の細片の密度まで一致したとは扱わない。

元参照の床切り出しは比較用に保存しただけで、候補計算へ使わない。生成目標画のRGBと固定した輝度fieldも使用しない。画素の入力は固定したベースだけ。

## 再現と検証

固定PNGのSHA-256を照合し、PNGを再デコードしたRGBAと固定生RGBAの一致も検証した。全候補で同条件の再計算一致、alpha一致、全透明RGBA一致、OFF一致を確認した。`--off` は元PNGのバイト列をそのまま複製するため、PNGファイル全体でも一致する。

`py -3.11 tools/screen-effect-study-20261101/still/offline-round09.py` で全4候補を再生成できる。`--candidate D` はDだけを再計算する。`--off` は加工前のバイトコピーを保存する。既存候補を画面へ載せるときはhashを検証する。

## 出力

優先候補は `round-09-D.png`。独立比較用の不透明版は `round-09-D-on-black.png`。A/B/Cも同じ命名で保持する。光学処理だけの中間段階は `round-09-*-optical-stage.png`。原寸全体と布と床と壁の前後比較もある。

全候補の処理値とhashと検証結果は `round-09-processing.json`。自己完結の `round-09-preview.html` は、事前計算した4枚のPNGと加工前を切り替える。ブラウザ実表示は先のURL policy拒否により未検証。本編Unityと共用台帳は変更していない。独立評価と採用判断は未実施。
