---
name: wrong-room-sounds
description: Wrong Room（Parallel Minds）の音の扱い。ライセンスが再配布を禁じるので公開リポジトリへ入れない
metadata:
  type: project
---

2026-11-05 に Wrong Room から 2 本を採用した（`canon/LEDGER.md` 0312）。別れの `PMH_MusicBox_Am_MissingPins_01` と人形 End の `PMH_Drop_Swallowed_01`。

ライセンスは作品内での使用を認める。音単体の再配布（加工後を含む）、素材市場への掲載、Content ID での他者の遮断、AI の学習・試験・データセットへの使用を禁じる。クレジットは任意（`Sound design: Parallel Minds`）。

`Roiril/fixed-cam-vr` は公開で `Assets/Resources/Sound/` は追跡対象なので、焼いた wav と meta は `.gitignore` に入れた。原本と LICENSE は `G:/マイドライブ/shubie/fixed-cam-vr-assets/wrong-room/`。

**Why:** 追跡すると音単体を公開配布する形になる。APK への同梱は作品内の使用なので問題ない。
**How to apply:** 別の PC では `py -3.11 tools/ingest-sounds.py --only bed_farewell_music_box sfx_doll_end_fall` → `.\tools\unity.ps1 menu sound-import` を先に流す。焼かずにビルドしても APK は通るが、2 本が鳴らず `sndBuilt` の右が増える。このパックの音を生成 AI へ渡さない。

実機の 3D 定位と結果の曲との重なりは未確認。測った音量の包絡では、落ちる音は曲が入り始める 1.2 秒で -23dB、曲が立ち上がり切る 2.7 秒で -27dB。関連: [[ending_farewell_doll]]、[[ending_result]]。
