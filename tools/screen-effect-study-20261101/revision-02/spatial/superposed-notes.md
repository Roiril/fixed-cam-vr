# 世界空間の局所変形へ計測2D運動を重ねる

生成物は `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/superposed/`。入口は `superposed-preview.html`。P3の代理depth変換は採用せず、P3・pinned・元版と全出力を凍結したまま新規追加した。

原因はP3で手動の仮depthへ推定Tzを作用させたこと。近い仮frameに大きい伸びを作り、境界でglobal相似へ戻す補間が壁の段差と鋸歯を作った。この版はTz/Tx/Ty/Rzの代理推定もqflat/qworld混合も使わない。

既存pinned worldFを同じ固定カメラで投影し、gl_Position.xy/wへ共通の計測2D相似Sを作用させてからラスタライズする。gl_Position.zとwはlocalmeshのまま。global-onlyはF0+S、local-onlyはF+S0、combinedはF+S、noneは両方0。camera/calib、worldgeometry、元UV、field係数、12px境界拘束は変更しない。元の写真へ戻す画素処理はない。

globalStateの時間関数はP3で使った既存参照リズムをそのままコピーした。angleは画像y下の相似角。dx/dyは画像幅/高さに対する移動。全4modeの固定表示だけに同じ1.35viewport cropを適用する。raw640×480はcropなし。glass・開口・scanlineは後段のscreen座標に固定する。

実vertex shaderのGPU transform feedbackをflat2m面1000点×3時刻に流し、計測global相似との最大差0.000146px。rawzeroはMAE0.04034、max1、coverage99.99935%。strength0のcombined/global/localはnoneとPNGbyte一致。時刻returnもbyte一致。

元可視領域で重み0.75以上の各面3点、計15点のモデル軌跡を保存。globalSを逆変換で除くと既存localFとの差は最大8.3e-13px。相似除去残差は全8時刻でRMS4.78–9.44px、6.5/8.5/10.5秒は5.51/8.04/9.44px。P3の30px級の伸びは消えた。これはモデルの軌跡で、写真から追跡した特徴点ではない。

3.2/6.5/8.5/10.5秒のcombined raw穴は1013/9131/83811/65980px。同じ1.35viewport内の穴は397/428/318/384px。global-onlyの同viewport内にも263/230/164/196pxの穴がある。面ID境界をサブピクセルで再標本化するため点状の細い水平裂けが残る。P3の太い壁段差・近frameの巨大伸びと分けて評価する。穴埋め・係数探索はしていない。

空間の局所変形はworld座標と各面depthに基づくが、全体運動は計測した2D相似。12pxの拘束は元写真の境界に依存する。純粋な3D復元ではなく、仮物体の輪郭と奥行は推定のまま。
