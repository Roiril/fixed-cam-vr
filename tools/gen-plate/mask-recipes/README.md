# 合成マスクの再生成

手順は [composite-mask](../../../.claude/skills/composite-mask/SKILL.md)。
このディレクトリのJSONは画像座標で保持範囲を記録する。完成マスクは640×360の枠座標。

- `stain-a-20260919.json`: 無マスクの原動画から全コマのシミの範囲を指定。生成されたパイプも含めて連続した領域で合成する。
- `wall-b-20260919.json`: 壁全体を保持。手形の間を穴にしない。
- `baked/*.png`: 上記レシピの生成結果。配信先へ同じバイト列をコピーしたもの。
- `published-20260919.json`: 差し替え前後のURLと配信ハッシュ。
- `bleed-stain-a-20261113.json` / `bleed-hands-b-20261113.json`: 滲んで現れる・長押しで膨らむ合成（0349）の素材を作るレシピ。
  `py -3.11 tools/gen-plate/bleed_maps.py --recipe <レシピ>` が比の画・順の画・press マスク・人形の周りのマスクを
  `Logs/gen-plate/bleed/out/` へ出す。`cue` の秒は `tools/show-apply-bleed.py` が台本へ写す。
  契約は [screen-overlay.md](../../../.claude/rules/screen-overlay.md)。

元動画とプレートはローカル素材。原動画の場所と再生成コマンドは
[composite_mask_quality.md](../../../.claude/memory/composite_mask_quality.md) にある。
生成済み動画へ再びマスク合成を焼き込まない。表示時の1回だけにする。
