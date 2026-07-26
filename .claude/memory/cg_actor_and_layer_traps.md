---
name: cg-actor-and-layer-traps
description: CG 人形（ShowCg レイヤ）を触る前に知るべき 4 つの罠 — レイヤ外部編集の消失 / Edit Mode スキニング固着 / Remy は Generic / Setup メニューのモーダル
metadata: 
  node_type: memory
  type: project
  originSessionId: ad44ab49-c74f-4be4-9d34-124bab912968
  modified: 2026-07-26T16:09:50.787Z
---

廻リ視の CG 人形（`Assets/Scripts/Streaming/Cg/`）を触るときに踏んだ実害。2026-07-27。

## 1. ProjectSettings のレイヤは**外部編集すると消える**

`ProjectSettings/TagManager.asset` を Unity 起動中にファイル直編集で足したレイヤ（`ShowCg`）が、
別の保存契機で Unity のメモリ上の内容に**上書きされて消えていた**（`LayerMask.NameToLayer` が -1 を返して
「レイヤ未定義」で CG が出ない状態になる）。

→ レイヤ・タグの追加は **MCP の `manage_editor action=add_layer`**（Unity のメモリ側を更新する経路）で行う。
現在 `ShowCg` は **slot 9**。TableDuo の `TableProps`(slot 8) と同居しているので、slot を動かさない。

## 2. Edit Mode では骨を動かしてもスキニングが更新されない

`ShowActorRig.Drive()` でボーンを動かして `Camera.Render()` しても、**全ポーズが同じ絵**になる
（実測: idle と raise がバイト単位で同一の PNG）。Edit Mode ではスキン結果がキャッシュされるため。

→ プレビュー側で `SkinnedMeshRenderer.forceMatrixRecalculationPerRender = true`。
実行時は `updateWhenOffscreen = true`（IK で bind ポーズの bounds を外れて**人形が丸ごとカリングされる**のを防ぐ。
プレハブ生成時に設定済み）。

## 3. `Assets/ThirdParty/Mixamo/Remy.fbx` は **Generic**（Humanoid ではない）

TableDuo のフルボディアバターと同じ実体なので、**取り込み設定を Humanoid に変えてはいけない**（向こうが壊れる）。
そのため `ShowActorRig` は Humanoid（`Animator.isHuman`）と Generic の両対応で、Generic では
**手のボーン名（`mixamorig:LeftHand` 等）を見つけて親を 2 つ遡り**肘・肩を取る。実寸は 3.72m あるので
`heightM`（既定 1.6m）に合わせて自動縮尺される（縮尺 0.43）。

## 4. `Setup Main Demo Scene` は別シーンを開いたまま実行すると**モーダルで Editor ごと止まる**

MCP 経由の自動実行だと人がクリックするまで全 MCP 応答が timeout する（＝ブリッジが死んだように見える）。
2026-07-27 に「未保存でなければ自分で Main.unity を開く」よう直したので通常は起きないが、
**未保存変更があるときは今も止まる**。自動実行の前に `execute_code` で
`EditorSceneManager.GetActiveScene().path / isDirty` を確認する癖をつける。

関連: [[unity_pitfalls]] / [[hud_font_and_preview]]（Play せずに見た目を確かめる系の作法）
