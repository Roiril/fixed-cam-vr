---
name: unity_pitfalls
description: 本プロジェクトで踏んだ Unity / Meta XR の地雷集（再発防止用）
type: project
---

# Unity / Meta XR 地雷集

## Quad の正面方向

Unity 標準 Quad の **可視面は -Z 側**（mesh の法線が +Z だが、デフォルトのカメラから見ると -Z が前面に見える）。

- カメラが `+Z` を向く OVRCameraRig 配置で、Quad を `(0, y, +z)` に置く場合は **回転 0 のままで OK**
- `Y=180` 回転を加えると裏返って見えなくなる（バックフェイスカリングで透明）

**Why**: 2026-04-29 PoC で `Y=180` を入れて見えず、デバッグに時間を要した。

**How to apply**: Quad で映像を表示するときは rotation を Identity から始める。「カメラに正対させなきゃ」と直感で 180 を入れない。

## OVRCameraRig 利用時の Game ビュー

OpenXR Plugin / XR Plugin Management が未導入だと、Editor の Game ビューでカメラ出力が出ないことがある（Scene ビューでは見える）。

- 当面の検証は Scene ビュー or 実機ビルドで行う
- きちんとしたいなら OpenXR Plugin を入れる（`com.unity.xr.openxr`）

## Unity Scene YAML 直編集（MCP 不在時のフォールバック）

MCP for Unity が落ちている時、`.unity` ファイルを直接編集して MonoBehaviour を貼ることは可能。手順：

1. 対象スクリプトの `<file>.cs.meta` から `guid:` を取得
2. 既存 GameObject の `m_Component:` リストに `- component: {fileID: <新ID>}` を追加（fileID は既存と被らない値、+1 で十分）
3. シーン末尾近くに新規 `--- !u!114 &<新ID>` ブロックを書く（`m_Script: {fileID: 11500000, guid: <取得したguid>, type: 3}` を含める）
4. `[SerializeField]` フィールドを順に書き出す。`KeyCode` は int（Space=32）、Object 参照は `{fileID: 0}` で null

**How to apply**: MCP 復旧待ちで詰まったらこの手順で進める。ただし PrefabInstance を弄ると壊れるので Prefab には適用しない。

## DroidCam の単一クライアント制約

DroidCam は同時に 1 クライアントしか配信を受け付けない。ブラウザで `/video` を見ながら Unity で受信しようとすると "他のクライアントに接続されています" になる。

**How to apply**: Unity Play 前にブラウザタブ・PC 版 DroidCam を閉じる。CI 等で複数同時受信が必要なら IP Webcam に切り替える（こちらは複数 OK）。
