---
name: composite-mask-quality
description: シミの直線切れと二重マスクを直した手順。原動画への復帰、生成されたパイプも含む連続したマスク、全コマ比較。
type: project
updated: 2026-09-19
---

# シミと壁の合成マスク

## ユーザーの指示

2026-09-19:

> 人だけくりぬくのは、どう頑張っても破綻や人の周囲が変になるという問題がある。
> なので合成をちゃんとやるのがいい。
> 壁のしみも、なぜかマスクが直線的に区切られている。ちゃんとシミの範囲をすべてカバーしたものを合成すれば違和感はあまりなくなると思う。マスクのやり方を工夫してみて。
> それで、スキルにしてハイクオリティなマスク手法を再現できるようにして。

候補の修正中にも指摘を受けた:

> 変更後もしたが直線的に不自然に切り取られていない？

その後の指定:

> 動画/画像生成するとき、パイプがあるうえで生成するんで、べつにパイプだけ消さなくてもいいと思うよ

人物だけを切り抜く提案は取り下げた。固定マスクで人物の重なりまで解決したとは扱わない。

## 原因と今回の変更

- 旧カーテン素材は素材座標の矩形 `[100,8,308,190]` でシミを切っていた。`fitvideo.py` で動画にも合成を焼き込んでいた。表示時も同じマスクが掛かるため、マスクだけの拡張では直らない。
- 原動画は `C:/Users/kouga/Downloads/Curtain_stain_animation_202609071116 (1).mp4`。640×360、24fps、96コマ。左右各80pxを除去して640×480へ拡大した。色合わせやマスク合成は焼かない。音声は除外。
- `logs/gen-plate/seedance_stain_first_20260907.png` と末尾の画像は動画生成の入力。実際の動画の先頭・末尾と混同しない。
- 布も生成時に描き直されていたため、単純な差分はシミ以外を拾った。今回は全コマでシミの広がりを確認し、保存した多角形で指定する `region` を使用。中心を不透明に保ち、2pxの余白の外側8pxで透明へ移す。
- 最初の修正候補でも `protect` が広すぎた。パイプの上下に無人画像の帯を作り、シミを切っていた。いったんパイプ本体の輪郭へ絞ったが、ユーザーが保護自体を不要とした。最終版は `protect` を撤去。生成されたパイプもシミと一緒に合成する。中を棒状に抜かない。
- 壁は面全体を不透明にする `surface` を使用。色の似た部分を穴にしない。右と下の物理的な壁の輪郭だけ内側2pxで透明へ移す。

手順の正本は [composite-mask](../skills/composite-mask/SKILL.md)。Codexからも `.agents/skills/composite-mask` を通して同じものを読む。

## 再生成

必要な既存Pythonモジュール: `numpy`、`Pillow`、`opencv-python`、`imageio-ffmpeg`。今回の作業で依存の追加はしていない。

```powershell
py -3.11 .claude/skills/composite-mask/scripts/prepare_video.py --input 'C:/Users/kouga/Downloads/Curtain_stain_animation_202609071116 (1).mp4' --crop 80,0,480,360 --size 640,480 --out tools/web-compositor/captures/gen_stainA_full_20260919.mp4 --still tools/web-compositor/captures/gen_stainA_full_still_20260919.png
py -3.11 .claude/skills/composite-mask/scripts/build_mask.py --recipe tools/gen-plate/mask-recipes/stain-a-20260919.json --out-dir .codex-tmp/mask-stain
py -3.11 .claude/skills/composite-mask/scripts/build_mask.py --recipe tools/gen-plate/mask-recipes/wall-b-20260919.json --out-dir .codex-tmp/mask-wall
py -3.11 -m unittest discover -s .claude/skills/composite-mask/scripts -p test_build_mask.py
```

生成済みマスクの保存先は `tools/gen-plate/mask-recipes/baked/`。レシピと同じ名前のPNG。
動画と元プレートは従来どおりローカル素材。別PCで再生成する場合は記録されたハッシュの素材が必要。

## 反映と確認

`tools/gen-plate/mask-recipes/published-20260919.json` が差し替え前後のURLと配信SHA256を記録する。
`/state` の rev 1236 → 1237で3cueの素材・マスクURLを更新。rev 1238でパイプ保護を撤去したマスクへ更新。
対象は `cue_hands_B`、`stain_A_in`、`cue_stain_A` の素材・マスクURLだけ。
旧素材と旧マスクを残す。戻すときも最新の `/state` を取得して対象URLだけ戻す。古いshow全体を復元しない。

- カーテン: マスク作成時は動画96コマと実際の末尾PNG1枚を走査。合成比較は96コマずつ192枚。
- 先頭・中間・末尾は `screen.py` の表示後処理も通した。外周とパイプ周辺は元の解像度から拡大して比較した。
- 対象を保持する指定範囲のalphaは100%維持。ただしこれは指定した範囲の検査。シミ全体を自動発見した証拠ではない。
- 両マスクをそれぞれ2回生成してSHA256一致。最終カーテン `e422a7f220caa90c4a01b09cc715567e705b2a2f125c5702451f4396395328b2`。壁 `86470fcc52de10404848c6082843dbf2fd608be49cc5bbcc4de4218b74947d73`。
- ビルダーの10テストが通った。意図的に狭い探索範囲を拒否する例と、通す例の両方を含む。
- 配信4ファイルのHTTP 200とハッシュを照合。GETと保存済みshowの対象cueが一致した。
- Quest上の映像と実際の体験者の通過は未確認。机上の静止プレートとの比較を実機の合格として扱わない。

## 今後の検査

直線的な切れを二度指摘された。マスクの外周だけでなく、内部の穴の要否も調べる。
生成素材に含まれるパイプや支柱を勝手に抜かない。実写を残す明示的な要件があるときだけ保護範囲を検討する。
比較には元の旧版だけでなく直前の候補も含める。全景を縮小して見ただけで合格にしない。
差分方式は背景が合う場合に使う。合わない素材で閾値を下げ続けるより、範囲を指定して目視する。

工房UIの再生時間で広がるマスクは今回変更していない。決定論的な生成はこのオフラインのレシピ方式で実装した。
