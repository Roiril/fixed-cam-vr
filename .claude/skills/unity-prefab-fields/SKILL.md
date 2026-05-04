---
name: unity-prefab-fields
description: Unity の prefab YAML に新規 SerializeField を確実に反映させるための作法。MonoBehaviour に [SerializeField] を追加した際、prefab YAML に当該フィールドが書かれていないと C# 初期化子ではなく型 default で読まれる現象（bool=false / float=0 等）への対処。.cs を編集して prefab を使うと「なんで OFF / 0 になってるの？」となるパターンの再発防止。
---

# Unity Prefab — Missing-Field 落とし穴

## 現象

MonoBehaviour に新しい `[SerializeField]` を追加して **C# 側で初期値を書いた**のに、prefab を貼った GameObject では **その値が C# default になる**。

具体例（このリポジトリで 2 回ハマった）:

```csharp
[SerializeField] private bool autoOrient = true;       // → prefab だと false で起動
[SerializeField] private bool useTextureAspect = true;  // → 同様
[SerializeField] private float landscapeAspectWPerH = 19.5f / 9f;  // → 0.0 になり aspect=NaN/0
```

## 原因

Unity の serialization は：
- **prefab YAML にそのフィールド名がある** → YAML の値を使う
- **prefab YAML に無い (= "missing field")** → C# の **型の default** (bool=false, int=0, float=0, リファレンス=null) を使う。**C# クラス側の `= true` 等の初期化子は無視される**

つまり「C# で初期値を書いてあれば動く」と思って prefab を更新せずに放置すると、**bool は false、float は 0** になって挙動が壊れる。

## 対処（必須手順）

`[SerializeField]` を追加 / 既存フィールドに初期値を変更したら、対応する prefab YAML に **明示的にフィールド行を書き足す**。

### 手順

1. `Assets/Prefabs/<...>.prefab` を開く
2. 対象 MonoBehaviour ブロック（`m_Script: { guid: <該当スクリプト> }` の直後）を探す
3. 既存のフィールド行の末尾に新フィールド行を追加：

```yaml
--- !u!114 &<id>
MonoBehaviour:
  ...
  m_Script: {fileID: 11500000, guid: <スクリプトのguid>, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  registry: {fileID: 0}
  source: {fileID: 0}
  overrideUrl:
  autoOrient: 1                  # ← bool true は 1
  manualRotationDegOverride: -1   # ← int 負値は OK
  landscapeRotationOffsetDeg: 90
  portraitAspectWPerH: 0.4615385  # ← float
  useTextureAspect: 1             # ← 新フィールドはここに
```

### YAML 値の書き方

| C# 型 | YAML 値 |
|---|---|
| `bool true` | `1` |
| `bool false` | `0` |
| `int` / `enum` | 数値そのまま |
| `float` | `0.4615385` のように普通に |
| `string` | `""` 不要、コロンの後にそのまま、空なら省略可 |
| `Object 参照 null` | `{fileID: 0}` |
| `Vector2` | `{x: 12, y: 12}` |

### 検証

1. `mcp__UnityMCP__refresh_unity scope=all` を呼ぶ
2. Editor で対象 prefab の Inspector を確認 or `mcpforunity://scene/gameobject/<id>/components` で実際の値を読む
3. Play して期待動作になるか確認

## 予防

- **新フィールドを追加するたび**、prefab YAML にも書き加えるのを 1 セットの作業にする
- `[SerializeField] private bool xxxEnabled = true;` のような **「true がデフォルト想定」のフラグ**は特に注意（false で起動するとバグに見える）
- 検証: 新規 SerializeField がある PR をマージ前に、`grep -r "yyy:" Assets/Prefabs/` で全 prefab に行があるか確認

## 同じ落とし穴の派生

- **ScriptableObject** も同じ挙動（asset YAML に missing → 型 default）
- **Scene 内インスタンス**は prefab override 経由で変わる場合がある。シーン側 .unity ファイルにも目を通す
