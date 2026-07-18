# アルゴ (algo) — 数字カード 24 枚（Unity 用 GLB）

ボードゲーム「アルゴ」の数字カードをテクスチャ付き GLB に再構築したもの。
海底探検（`../DeepSeaAdventure/`）と同じ資産ライン（実寸メートル・glTFast 取込・テクスチャ埋め込み・原点中心水平 Y-up）。

## 構成（glb/ 内・全 24 枚）

| 種別 | 地色 | 数字色 | 値 | ファイル |
|---|---|---|---|---|
| 白カード | オフホワイト | 黒 | 0–11 | `white_0`〜`white_11` |
| 黒カード | オフブラック | 白 | 0–11 | `black_0`〜`black_11` |

- カード寸法: **42 × 66 mm**（公式寸法・角丸 3.5mm）、厚み 2.0mm（表示用の暫定値）
- 表面のみテクスチャ。側面・裏は地色ソリッド（**裏面の数字なし** — 裏向き配置で値が隠せる）
- 数字フォントは Jost（Futura 系近似）

## 出所・再生成

生成元: `model-lab`（`C:\Users\kouga\Projects\Web\model-lab\models\algo\`）。取得日 2026-07-18（exports 生成 2026-07-18 15:38）。

```bash
# model-lab ルートで
python models/algo/gen_textures.py
./run.sh models/algo/build_glb.py        # bpy (Blender) で GLB 生成
# → exports/algo/glb/*.glb をこのディレクトリへコピー
```
