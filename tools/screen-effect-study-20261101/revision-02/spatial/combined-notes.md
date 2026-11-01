# global 運動と局所変形を合成した代理投影

生成物は `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/combined/`。入口は `combined-preview.html`。既存のfree/pinned/source/出力は変更していない。

参照のmotion-trackのangle/scale/dx/dyと旧globalの導入・移行・復帰の時間関数を再利用する。これは計測した2D相似であり、真のカメラ軌跡ではない。dx/dyは画像幅/高さに対する正規化移動。画像y下方向のangleをcamera y上方向のroll=-angleへ写す。基準奥行2mでTz=2*(1/scale-1)、Tx=dx*640/fx*(2/scale)、Ty=-dy*480/fy*(2/scale)。固定camera座標内でrollしてからtranslationを加える。

元のworldfieldは振幅を変えていない。既存pinweightで拘束して加え、その後proxy rigid運動を適用する。境界付近では、元UVをglobal相似で前方変換した座標をqworld.zで逆投影したqflatとqworldをpinweightで混ぜる。純粋な3D再投影ではない。元の写真を貼り戻して穴を埋める処理はない。

display=true時はcombined/global/local/noneへ同じ固定1.35倍のviewport cropを適用する。走査線・開口・glassはcrop後の画面座標へ固定。raw640×480のdisplay=falseにはcropをかけない。外側をclampして補間する処理はない。

実vertex shaderのtransform feedbackでflat2m面1000点×3時刻を測り、global相似との最大差0.000136px、最大RMS0.0000502px。sourcecamera、fx、calibは変更していない。rawゼロ変形はMAE0.04034、max1、coverage99.99935%。strength0のcombined/global/localはnoneとPNG byte一致。時刻を戻したPNGも一致。

6.5/8.5/10.5秒の15点の相似変換を除いたRMSは22.38/30.34/38.55px。combinedとglobalの差は30.87/41.15/51.25px。各面で元画像の可視領域から3点ずつ選んだモデル軌跡。原写真から追跡した特徴点ではない。

raw画面の穴は15524/80462/68501px。同じ1.35viewport内では233/974/1632px。globalのみの同viewport内は0px。viewport cropの欠落と、主絵の内部の裂けを分けて保存している。面IDごとの実出力画素数、raw/crop穴数、全条件の原寸比較はverification.jsonとcomparison-native/fullの各PNG。

近い仮frameの0.72mとglobalのTzの組合せにより、8.5/10.5秒では局所的な伸びが強い。境界ではglobalへ戻るので内部との差が見える。形状・振幅・拘束幅を追加調整していない。採用する絵としての判定は未実施。
