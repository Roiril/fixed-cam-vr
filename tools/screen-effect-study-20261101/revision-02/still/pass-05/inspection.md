# pass05・同じ画素の差分だけを減らす1候補

pass03の元コード原寸と手動保護overlayを先に開いた。目で追えるコード・金属の経路と周囲を引き継ぎ、nativecable [735,85,1010,560] と床全域y630以降もraw保持coreへ明示した。保護範囲はB専用の手動指定。コードの3断面だけを狭く保護する方式ではない。

pass04のpurefilterを読み取りimportし、手動範囲の生成と全処理を再計算した。再計算RGBAは凍結したpass04と完全一致。新しい処理はraw RGB + smoothstep(distance_to_core / 12) × (pass04 RGB − raw RGB)という同じ画素の差分の減衰だけ。原RGBの空間ぼかしや新しい階調処理は追加していない。core外12pxで加工量が0から1へ移り、重み1の画素はpass04と完全一致する。

raw/03/04/05のfullと原寸vertical/cable/floor/right、および保護core下端と床境界の原寸を開いた。protect図・mixweight図・04→05差分も保存した。コードと床は原画と全RGBA完全一致になり、線のつながりもrawへ戻る。3断面はcontrast .142190/.017362/.140008、暗部FWHM10.25/17.0/16.25pxで、それぞれrawと完全一致。旧04の黒つぶれはこの範囲では残らない。

主境界511行と衣類左271行のspatialcoverageは連続したまま。一方、nativecable全ROIの保持は右布境界の範囲を含むため、その3本目は146→0行となる。全ROIのraw完全保持と同じ画素の加工維持は同時には成立しない。この不足を隠さずcoverageへ残した。

core下端y560付近には暗い矩形の濃度差が見え、床境界付近にも12px移行の明暗帯が見える。距離smoothstepは元RGBをぼかさないが、異なる明暗処理の境界自体は消せていない。追加のRGBぼかしやパラメータ探索はしていない。見た目の継ぎ目と3本目のcoverageは未達。

保護外の壁はpass04のままで高周波RMS比1.290倍、粒の増幅が残る。コードと床の比は1.000、RGB RMS0。可視変更44.845%、全可視RGB RMS20.164byte。OFF・alpha・hidden・内側2px・repeat・core・weight1・範囲外spatial契約は直接照合した。旧pass01/02/03/04は全artifact SHAが不変。本編へは統合していない。
