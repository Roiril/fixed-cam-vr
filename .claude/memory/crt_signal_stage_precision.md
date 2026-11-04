# CRTのFloat RTを挟む診断出力の精度

2026-10-31、Unity 2022.3.62f2 Editorの`processing-signal-01`で確認した事実。
GPU backendをまたぐ一般則とは扱わない。

- Frame/Glassの前で一度Float RTへ出し、無処理filterを通して同じshaderへ戻すと、旧warpOnly Aに対し25,898 pixelsが最大1byteだけ違った。
- 無処理Float filterのRGBA readback差は0。SIGNAL keywordoffの単一passは旧warpOnly Aのhashへ完全一致した。
- Stage1 cameraのallowHDR true/falseは同じ2pass hashとなったため、HDR flag原因は棄却した。
- SIGNAL診断variantのfragとStage1出力の型だけをhalf4からfloat4へ変え、内部colのhalf3は保ったところ、旧warpOnly A/B/Cの3PNG hashが全て一致した。off variantの既定half4は保ち、旧28と旧warp単一passも各3枚一致した。

最大1byteだからと許容差へ変更せず、通る対照を維持して出力signatureと中間出力を分離する。中間RTがFloatでも、shaderの出力型は別に確認する。今回、HDR flag変更を再試行する根拠はない。

失敗出力は`processing-signal-01-invalid-identity01`、成功した12枚・校正・source26点の退避とXMLはDriveの`2026-10-30_crt-reference/processing-signal-01/`。sourceの絶対/相対pathをrepo相対に正規化した`source-archive.json`から照合できる。EditMode2288/2288とsource26前後hash差0を確認した時点の記録で、後続helper変更後の全件再試験とは区別する。
