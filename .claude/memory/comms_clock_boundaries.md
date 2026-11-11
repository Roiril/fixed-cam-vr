# CommsTakeover の時刻境界

⚠ 対象の `CommsTakeoverLogic`（と `LieReveal` / `Seized` の段）は削除済み。float の時刻境界の教訓としてだけ残す。
現在これに当たる段境界の判定は `CommsPossessionLogic.Sample`（`BoundaryToleranceSec` で 1 マイクロ秒の許容差を持つ）。
乗っ取り全体の共通時計は `CommsPanelLogic`（`TakeoverFillStartSec` など）。

2026-09-14。終盤演出を二度組み替えた際、境界時刻で期待した次の段へ進まないテスト失敗が再発した。
前版は順次減算した時刻でLieRevealに残った。追跡版はDurationForの戻り値をSampleへ渡してもSeizedに残った。

- Sampleの区間判定では境界を共有する。同じ秒数を呼び手で別に組み立てない。
- 完了と捕捉の比較には1マイクロ秒の許容差を設けた。演出の尺を一コマ延ばして対処しない。
- 境界ぴったりを渡すテストを残す。通常の30fpsサンプリングだけでは境界の比較を踏まない。
- 低FPSでも進捗が逆行せず、捕捉後に字が戻らないことを別に確認する。
- コンパイル失敗時に残った古いXMLを成功証拠にしない。新しい開始時刻と終了時刻を読む。

旧の検証先は Assets/Tests/Streaming/CommsTakeoverLogicTests.cs（削除済み）。現行の境界テストは Assets/Tests/Streaming/CommsPossessionLogicTests.cs の `SamplesEveryAuthoredBoundary`。
Unity 2022.3.62f2のEditModeで1,885件通過（当時）。正確なランタイム内の丸め経路は未特定。
