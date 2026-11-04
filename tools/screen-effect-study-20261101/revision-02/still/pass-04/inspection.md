# pass04・B専用の1候補

raw current Bを全域のunsharpと単調LUTへ通し、その下地に手動指定の縦境界だけ横方向の局所加工を加えた。pass03の2本に、原寸で見た布の右側境界y235〜380を1本追加した。右境界は金属の横棒へ達する前で止めた。旧03の大きなhanger polygonは使わず、コードと金属は幅14pxのpolyline、床はy630以降のpolygonでspatialdeltaだけを止めた。unsharpとLUTはその範囲でも適用する。overlayに観察上の区別を明記した。

出力はsigma2の平均画像へ置き換えていない。指定の式で高周波差分を加え、その輝度を指定LUTへ通した。RGBは共通luma deltaで加算する。最終gamutclipはpoint段24266px、spatial段1896pxで発生し、色域端では色の比が変わる可能性がある。処理の種類を分けるためsharp-only、tone-only、pointbase、spatialdelta単独PNG/NPZを残した。

raw/03/04/old23のfullと原寸vertical/cable/floor/rightを開いた。04では白い壁と黒い衣類の明暗差が増え、床の線も強くなる。コードへ横方向のぼかしは入っていない。一方、原画より床の粒と線が強く、白壁の粒も増えて見える。高周波RMS比は壁1.290倍、床1.612倍、コードROI1.975倍。これらは粒だけを測る指標ではなく、線の強調も含む。広いgrainが増えていない条件を満たしたとは言わない。ROI内の輝度>.99画素は元と新の双方0だが、これは局所haloが無い証明にはならない。

主境界511/511行、衣類左271/271行、新しい布右146/146行でspatial変化が実在し、連続spanも同じ。全域の明暗処理により可視画素の98.823%が元画像と異なり、RGB RMSは24.962byte。旧03の細線RGBA exactの主張は引き継がない。

2/4/6/12px・水平/垂直/斜め・明線/暗線の24fixtureで、コントラスト比とFWHMを数値化した。FWHM増大0件。flatはtone-onlyと完全一致。LUT順序4097点は単調。OFF・alpha・hiddenRGB・内側2px・repeatは一致する。範囲外と保護範囲ではspatialdeltaが実際に0で、最終RGBAはpointbaseと完全一致する。これは全域のpoint/localcontrast処理を止めたという意味ではない。

実コードの手動断面3点も計測した。中心[815,267]ではcontrast比.775、暗部FWHM10.25→17.0px。[850,202]はcontrast比.210、17.0→3.5px。[906,133]はcontrast比.558、16.25→16.5px。背景とほかの形も含む手動断面で、コードの物理的な幅だけを分離した値ではない。それでも、実画像の暗部がつぶれてcontrastが下がる問題とFWHMが増す断面があるため、syntheticが通ったことを全体の合格にしない。verificationのall_numeric_checks_passedはfalseにして、この不足を保存した。

見えの採否は未判定。指定どおり1候補だけを作り、追加探索はしていない。A/Cや本編には適用していない。ブラウザのボタン操作は未検証。
