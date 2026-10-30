---
name: ending-farewell-doll
description: 2026-10-30。帰還の別れの人形（LEDGER 0291）の作り。CG 層の 1 体の器を別れのモードで使う・立ち位置は枠 UV から床へ戻す・写真は見つめる段で撮る・台車を外した
metadata:
  node_type: memory
  type: project
  originSessionId: 2b89953c-e90d-428a-bc15-5060ba8fefc5
  modified: 2026-10-30T07:14:36.967Z
---

2026-10-30 ユーザー指示（LEDGER 0291）で台車（Romi）をやめ、帰還の後に CG 人形が 1 体だけ体験者の左に立って去る形にした。

**流れ**: 報告 → 入れ替わり（人形 → 人・2.6 秒）→ `SwapMorphFx` が人形を畳んで CG 層が空く → `EndingFarewellDoll` が
`ShowCgLayer.BeginFarewell` で締めの人形を出す → 現れる 0.35 / 見つめる 1.15 / 後ろを向く 0.75 / 下がって消える 2.0 秒 →
報告から約 6.9 秒で消え切り、7.1 秒で電源断。

**決め所**:
- 器は `ShowCgLayer` の 1 体だけ。入れ替わりの人形が消える前に出すと器を取り合うので、`EndingReleasedLiveReady`
  （ライブ・`SwapActive` でない・乱れの遷移でない）かつ CG 層が空いた縁で始める
- `Apply` は通常の人形へ戻す。別れのモードは `BeginFarewell` が `_farewellPending` を立てて `Apply` を呼んだときだけ立つ。
  `Apply` の中の `Tick` が 1 枚描くので、**立ち位置が解けるまでは強さ 0**（体験者の位置に一瞬立つのを防ぐ）
- 立ち位置 `EndingFarewellLogic.TrySolve`: 体験者の足元を枠 UV へ投げ、左へ 0.18 ずらして同じ行のまま床へ戻す
  （`TryFloorAtFrameUv` は `TryFrameUv` の逆）。床の上で 0.5〜1.2m に収まらなければカメラの左右方向で置き直す。
  画面で決めるので「画の中で左」は必ず成り立つ
- 腕は体験者に合わせない（`ShowBodyInput.None`）。笑わない（`ShowSoundDirector` の `dollPresent` から外す）
- 写真 `ResolveEndingShotWindow(..., farewellFacing, plainReleasedAllowed)`: 人形が出る前は CG 無しの帰還を仮に撮り、
  見つめる段で上書き、後ろを向いた後と消えた後は撮らない。`EndingFrameCapture` は最後に撮れた画を残すので、
  写真は見つめる段の最後のコマになる
- 観測 `ev=farewell phase= cg= anchored= dollU= visU=` と `ev=sum farewell=<見つめた>/<消え切った>`。
  `analyze-xp-log.py` は帰還で見つめる段が無ければ FAIL、`dollU >= visU` なら FAIL

**台車を外した範囲**: Quest 側（`RomiCartLink` / `RomiCartLinkLogic` / テスト / テレメトリ `ev=romi` / タブレットへの `cartKind`）。
タブレットの「台車（Romi）の自動走行」パネルと `tools/romi/` とファームは残っている（使わない）。

**確認の道具**: `.\tools\unity.ps1 menu farewell` → `Logs/ending-farewell-preview/`（実プレート A × 実合成シェーダ ×
本番の `TrySolve` と `SampleAt`。体験者は actors[] の visitor をゾーン重心に立てて焼き込む代役）。
帰還End の見本写真（`menu ending-record`）も同じ絵を使う。

**未確認（2026-10-30 時点）**: Unity のライセンスが切れていて EditMode テストとプレビューを回せていない。
コンパイルは Library/Bee の `.rsp` を流用して Unity 同梱の Roslyn で通した（Editor / Player とも 0 エラー）。
実機の走行も未実施。

関連: [[ending-roll]] [[romi-cart-esp32]] [[swap-veil-mask]]
