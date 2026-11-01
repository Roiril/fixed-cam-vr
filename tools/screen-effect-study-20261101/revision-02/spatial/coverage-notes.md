# 連続した元UVで可視性を判定する最終版

成果物は `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/coverage/`。入口は `coverage-preview.html`。終了指示に従い現版を凍結し、追加改善は行わない。

根因は、640×480の離散owner画像をnearestで引いた区分と、実際の連続した三角形の投影境界が一致しなかったこと。global-onlyでも元UVがサブピクセルに移るため、どちらの面も拒否する列が壁を横切る黒い点線になった。修正は、元UVの連続レイを既存の同じ平面と仮物体へ交差させ、各fragmentの元camera depthと照合する可視性判定への変更。仮物体は既存の3px格子の同じ三角形の逆depth補間を使い、新しい奥行は推定していない。境界上の数値的な同率はzbufferへ委ねる。画素の貼り戻し・inpaint・後段blurは使わない。

camera、元UV、worldgeometry、Fの係数、12pxの拘束、globalS、1.35viewport cropと固定displayは全て既存superposedのまま。全過去module/assets/出力はhash不変。

最終rawzeroは307199/307200px有効、coverage99.999674%、MAE0.040344、max1。strength0の全3modeはnoneとPNGbyte一致。時刻returnも一致。実vertex shaderのflat2m校正1000点×3時刻はglobalとの差max0.000146px。

global-onlyの1.35viewport内の穴は3.2/6.5/8.5/10.5秒で0/0/0/1px。noneは1px。元の黒い水平点線は実際の8.5/10.5秒の画から消えた。残り1pxの位置では、GPU補間された元camera depthとレイのdepthに約3.3e-5mの差を診断で確認した。数値許容や形状を追加調整せず制約として残す。

combinedの同viewport穴は141/200/161/182px。仮物体外周の細い欠落は残る。15可視点のS除去残差は既存localと変わらず4.78–9.44px、差は最大8.3e-13px。全4modeの比較と面別実出力数はverification.jsonに保存。

renderer-coverage.js SHA256: 23350a7da437b3f1336033c49f58aee814615418d6a328decc5efe432eb68e6d

coverage-ownership.js SHA256: 580a614e972cda766e83170261426ecdb8874a532ac645bab56269ddd1b00ee9

bundle順はassets→geometry→pinned-field→superposed-motion→coverage-ownership→renderer-coverage。createSpatialScene/renderAtのAPIは同一。combined/global/local/noneを維持する。
