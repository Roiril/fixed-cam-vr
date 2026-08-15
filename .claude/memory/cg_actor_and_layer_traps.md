---
name: cg-actor-and-layer-traps
description: CG 人形（ShowCg レイヤ）を触る前に知るべき 4 つの罠 — レイヤ外部編集の消失 / Edit Mode スキニング固着 / Remy は Generic / Setup メニューのモーダル
metadata: 
  node_type: memory
  type: project
  originSessionId: ad44ab49-c74f-4be4-9d34-124bab912968
  modified: 2026-08-15T11:36:11.404Z
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

## 5. 人形は 2 系統ある（2026-08-15 追記）

- `Ichimatsu` — 正面写真のシルエットを前後へ押し出したもの（`tools/doll-model/`・全高 0.40m）
- **`Ichimatsu2` — 生成した 3D（Tripo の GLB）にリグを付けたもの**（`tools/doll-rig/`・全高 0.98m）。
  腕は**上下にしか振らない**・可動域 90°・袖は腕の軸からの距離で付く。罠は
  [tools/doll-rig/README.md](../../tools/doll-rig/README.md)（ボーンのローカル Y はねじり /
  袖の下端も x 0.37 まで張り出す / 指数減衰だと袖口が裂ける）

⚠ **`show.json` の `actors[].prefab` を書き換えるまで、体験に出るのは旧 `Ichimatsu`**
（show.json は git 管理外の現場設定）。

関連: [[unity_pitfalls]] / [[hud_font_and_preview]]（Play せずに見た目を確かめる系の作法）
