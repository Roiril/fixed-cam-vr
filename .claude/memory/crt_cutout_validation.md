---
name: crt-cutout-validation
description: 採用した開口の輪郭と深度を確認するときのCPU/GPU境界判定差。
type: project
---

2026-11-01、0302の枠なしスクリーンを本編へ移した際の事実。
CPUのGetPixelBilinearとGPUの半texel規約・補間量子化の違いで、輪郭付近の数pixelが閾値を跨いだ。
これを内部の背景漏れと数えると、正しいclipまで失敗と判定する。

形の正しさは承認PNGと独立照合する。今回の親照合は内側RGB差0、輪郭差1px以内。
採用した実alpha footprintの固定hashは、その後の退行検知に使う。
深度は実alpha footprintに対して背景を照合し、背景単独の全画面正例・枠ありの全面遮蔽負例・切り抜き後の外側背景/内側漏れ0を同じ門へ通す。
今回の背景正例は1,318,400pixel、全面遮蔽0、切り抜き外側背景368,363、内側漏れ0。

Camera曲面描画と同期Blitは輪郭のラスタと再sampleが違う。Endingの同座標対照はcommon processor.Renderで取り、採用Camera PNGとの一致とは別に確認する。
証拠は`G:/マイドライブ/shubie/reports/2026-11-01_screen-adoption/`。
