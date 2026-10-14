---
name: virtual_walk
description: 2026-10-14。PC の仮想体験（NPC が歩き、実プレート＋CG 合成の映像が実シェーダ・実 Director を通る）で演出を前後比較する手順と、その計器の限界
metadata:
  type: project
---

2026-10-14 ユーザー指示「動作検証は、PCで仮想的な体験環境を作って、その中でNPCを歩かせてその視点から見てチェックするように」で作った。

**回し方**: `.\tools\unity.ps1 menu virtualwalk -Set laps=1,tag=before`（PowerShell では `-Set` を**引用しない**。
`"a=1,b=2"` と引用すると 1 本の文字列になり、`laps` が読めず既定に落ちる）→ `py -3.11 tools/virtualwalk-video.py <dir>`
→ 変えたら `tag=after` で同じく → `--compare <before> <after>`。1 周＋終幕で約 45 秒（20 秒ぶん・613 コマ）。

**何が本物か**: 背景は各カメラの実プレート（`StreamingAssets/show/assets/plate_*.jpg`）、人は Remy を本番の CG 層
（較正カメラ・壁の遮蔽・contain 枠・レンズ）で重ねる。ScreenComposite・CameraSwitchDirector・GlitchFx・ZonePickLogic・
YawFollowLogic・OutroLogic・OutroReport は実物。刻みは 90Hz（0.07s の暗転は 30fps だと 2 コマで緩急が写らない）。

**限界**: 圧縮・伝送の痩せ・遅延・両眼視差・実機ディスプレイは入らない。シェーダの `_Time.y`（砂嵐・点灯）は進まない。
0236 でユーザーは「Editor はすごくいい、Quest の生録画は微妙」と判定している — **見た目の合否は実機で出す**。
ここで見るのは時刻・順序・曲線・重なり（why.md「代理を厚くしない」）。

**計器の罠**:
- `OutroLogic.ScreenCollapse` は潰れ終わると 0 に戻る（電力 0 が隠す設計）。記録をそのまま見ると「逆戻り」に見える。
  記録は段を過ぎたら 1 にしてある
- 1 周目のゾーン切替の乱れは強さ 0.113（0.25 × 段階の倍率）で、乱れの音の閾値 0.15 に届かない＝鳴らない。設計どおり
- 乱れの見た目を変えるなら `glitchstills=1` で強さ 0.32 / 0.6 を揃えた静止画を前後で撮る（0055 の育ち方を崩さないか見る）

関連: [[sound_pipeline]] / [[sim_device_divergence]]
