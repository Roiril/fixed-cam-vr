# ゾーン→カメラ切替の安定化改修（廻リ視 / FixedCam）

2026-07-22。実機（1.8m 四方・show.json layout.grid 12×12・tileM 0.15）で確定した「歩くとカメラ切替が
起きず、立ち止まった瞬間に dip-to-black 付きで遅れて切替わる」不具合の恒久修正。**ファイル編集のみ**
（Unity MCP / ビルド / 実機は別作業）。

## 症状 → 原因

| 原因 | 詳細 |
|---|---|
| dwell / クールダウンが部屋スケールに過大 | `CameraSwitchDirector` の `minDwellSec=2` / `switchCooldownSec=2`。1.8m 部屋のゾーン帯幅 ~0.45m は歩行 0.6〜1.5s で通過する → dwell 2s に永遠に満たず、止まって初めて満了して切替 |
| デッドバンド逆転 | show.json の `overlapM=0.08 < hysteresisM=0.12`。`PlayerZoneTracker.Pick` の shrink 保持（0.12m）が overlap 帯（±0.04m）の外まで及び、shrink AABB を出た地点で既に隣ゾーンの重なり帯も抜けている＝デッドバンド実質ゼロ |
| authored 値の焼き付き | dwell/cooldown は SerializeField で、シーン YAML に旧 2/2 が焼き付く。コード既定を変えても実機に効かない（unity-prefab-fields の罠） |

## 決定した意味論

1. **dwell / クールダウン既定 0.5s、非シリアライズ化**。`minDwellSec` / `switchCooldownSec` を
   `[SerializeField]` から非シリアライズ private（既定 `SwitchDirectorLogic.DefaultDwellSec/DefaultCooldownSec`=0.5f）へ。
   LongPressSec の const 化と同じ手法でシーン YAML の authored 2/2 を無効化。`manualHoldSec=8` と dip 70/100ms は現状維持。
   - 数値根拠: 帯幅 ~0.45m を歩行 0.6〜1.5s で通過 → dwell 0.5s なら通過中に満ちる。クールダウンも同スケールで 0.5s。
2. **保留キャンセル**（既存の RequestZone/Tick セマンティクスを維持・テストで固定）: 保留中の目標が現在表示カメラへ
   戻ったら pending クリア。境界のうろつき後に古い切替が突然 commit される事故を根絶。
3. **ゾーン外ガード**: 無効カメラ index（`target<0` は純ロジック、`registry` 範囲外は MonoBehaviour）の要求は無視。
   現カメラ表示継続・保留も触らない。tracker は keepLastWhenOutside=true で null を出さないが Director 単体でも安全側。
4. **dwell リセット意味論は「目標変化でリセット」を維持**（安定性として正しい。0.5s なら歩行通過中に満ちる）。
   クールダウン中は commit を遅延し前カメラを表示し続ける（現行どおり）。
5. **デッドバンド自動クランプ**: `ZoneLayoutApplier` が tracker へ渡す hysteresisShrink を
   `min(hysteresisM, overlapM/2)`（`ZoneLayoutSolver.ClampHysteresis` 純関数）へクランプ、逆転時 1 回警告。
   現 show.json（0.08/0.12）は編集なしで実効 0.04 になりデッドバンド復活。grid/cuts/default 全経路に効く。

## 現場調整（show.json control）

`control.minDwellSec` / `control.switchCooldownSec` を追加。present 判定 = **>0 で上書き / 0・未指定はコード既定 0.5s**
（`SwitchDirectorLogic.ResolveTiming`）。経路: `ShowControlClient`（ライブ long-poll / 端末キャッシュ / 焼き込み）→
`CameraSwitchDirector.ApplyTimingOverride`。`CachedConfig` へ往復（PC 不在でも生きる）。ShowControlClient→Director 参照は
既存シーン未配線でも `ResolveSwitchDirector`（GetComponent→FindObjectOfType）で遅延解決。Web 卓「ライブ運用」パネルに数値 2 入力。

## 変更ファイル

- `Assets/Scripts/Streaming/CameraSwitchDirector.cs` — 定数 + ResolveTiming、フィールド非シリアライズ化、ApplyTimingOverride、ゾーン外ガード
- `Assets/Scripts/Streaming/ShowControlClient.cs` — ControlState/CachedConfig に 2 フィールド、ApplySwitchTiming / ResolveSwitchDirector、Apply/InitializeAsync/ApplyBaked/SaveCache/LoadAndApplyCache に配線
- `Assets/Scripts/Tracking/ZoneLayoutSolver.cs` — `ClampHysteresis` 純関数
- `Assets/Scripts/Tracking/ZoneLayoutApplier.cs` — 全経路でクランプ + 1 回警告
- `Assets/Tests/Streaming/SwitchDirectorLogicTests.cs` / `Assets/Tests/Tracking/ZoneLayoutSolverTests.cs` — テスト追加
- `tools/web-compositor/index.html` + `app.js` — 数値 2 入力
- `.claude/rules/streaming.md` / `.claude/plans/2026-07-19_viewer-ux.md` — ドキュメント同期

## 実効パラメータ経路の確認

- Director は **シーン内コンポーネント**（`MainDemoSceneSetup` が Screen GameObject に AddComponent、Screen は prefab instance で
  シーンに永続）。よって dwell/cooldown が SerializeField のままだと Main.unity に authored 2/2 が焼き付き、コード既定変更が効かない。
  → 非シリアライズ化でシーン YAML の値は無視され、必ずコード既定 0.5s（または show.json control）で走る。
- ShowControlClient と Director は同じ Screen GameObject に同居 → `ResolveSwitchDirector` は GetComponent で解決可能。

## 残（別作業）

- ⚠ **実機未検証**。1.8m 部屋で歩行中に切替が追従するか、dip の頻度が過剰でないか、show.json control での調整反応を現地確認。
- コンパイル / EditMode テスト / Web UI は親が検証。
