# 合成マスクの再生成

手順は [composite-mask](../../../.claude/skills/composite-mask/SKILL.md)。
このディレクトリのJSONは画像座標で保持範囲を記録する。完成マスクは640×360の枠座標。

- `stain-a-20260919.json`: 無マスクの原動画から全コマのシミの範囲を指定。生成されたパイプも含めて連続した領域で合成する。
- `wall-b-20260919.json`: 壁全体を保持。手形の間を穴にしない。
- `baked/*.png`: 上記レシピの生成結果。配信先へ同じバイト列をコピーしたもの。
- `published-20260919.json`: 差し替え前後のURLと配信ハッシュ。

元動画とプレートはローカル素材。原動画の場所と再生成コマンドは
[composite_mask_quality.md](../../../.claude/memory/composite_mask_quality.md) にある。
生成済み動画へ再びマスク合成を焼き込まない。表示時の1回だけにする。
