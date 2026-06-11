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

## 未初期化 Texture2D が Quest GPU で白ノイズになる

`new Texture2D(w, h, fmt, false)` は **ピクセル内容未定義**。PC エディタでは黒に見えがちでも、Quest 3（Mali）で大画面に貼るとフレームごとに違う未定義メモリが出て **白チカチカ** する。

**Why**: 2026-05-04 セッションで MJPEG 未接続中の `CameraStream._texture` がチカチカし、ユーザーから「目に悪い」と指摘された。

**How to apply**: 動的にテクスチャを生成して画面に貼る系は、コンストラクタ直後に `SetPixels(black) + Apply()` で **必ず初期化**する。LoadImage でサイズが変わる用途でも、初回 LoadImage が走るまでの一瞬を埋めるために必要。`MjpegStreamReceiver` のような「接続が遅延する受信器」と組み合わさると顕著に出る。

## UnityMCP `execute_code` は Windows でコード長すぎ NG

`mcp__UnityMCP__execute_code` は内部で Mono を CLI 起動するため、**Windows のコマンドライン長制限 (~8192 chars) に当たる**と「ファイル名または拡張子が長すぎます」で即死。一見短いコード（500 文字程度）でも落ちることがある。

**How to apply**: 1〜2 行の極小スニペットしか通らないと割り切る。複数のコンポーネント状態を確認したい時は、`mcpforunity://scene/gameobject/{id}/components` リソースを `ReadMcpResourceTool` 経由で読む方が確実（serialized field の値が JSON で返る）。runtime のプロパティ（`IsConnected` 等）はリソース経由では取れないため、その用途は **HUD / Diagnostics オーバーレイ系の MonoBehaviour で吐く方針**にする。

## UnityMCP `manage_components set_property` の Object 参照は Component instance ID

`registry: CameraStreamRegistry` のような **Component 参照** フィールドに値をセットする時、`value` には GameObject の instance ID ではなく **Component の instance ID** を渡す必要がある。GameObject ID を渡すと "Failed to convert value for serialized field 'X' to type 'Y'" で失敗する。

**How to apply**:
1. まず `mcpforunity://scene/gameobject/{go-id}/components` を `ReadMcpResourceTool` で読む
2. 返却 JSON の中で対象 Component の `instanceID` を取得
3. その値を `set_property value=<componentInstanceID>` に渡す

## Texture2D.width/height が aspect の真値

MJPEG 経由の映像でアスペクト比を決める時、配信側の `/info` (widthPx/heightPx) より **`Texture2D.width/height`（LoadImage 後の実寸）の方が信頼できる**。`/info` は streamer 側で固定値を返すケースがあるが、JPEG ヘッダ (SOF) は嘘を付けない。

**How to apply**: `MjpegScreen.useTextureAspect = true`（デフォルト）で Texture から aspect を採用。Texture が未デコード（2x2 placeholder）の間だけ /info にフォールバックする。streamer が square (1080×1080) を返す現状でも、将来 19.5:9 を返すようになれば自動追従。詳細は [streaming-offline-test skill](../skills/streaming-offline-test/SKILL.md) 参照。

## DroidCam の単一クライアント制約

DroidCam は同時に 1 クライアントしか配信を受け付けない。ブラウザで `/video` を見ながら Unity で受信しようとすると "他のクライアントに接続されています" になる。

**How to apply**: Unity Play 前にブラウザタブ・PC 版 DroidCam を閉じる。CI 等で複数同時受信が必要なら IP Webcam に切り替える（こちらは複数 OK）。

## シーン .unity をディスク直編集したら「再ロード」必須（2026-06-11）

Editor で開いているシーンの .unity ファイルを外部編集（Edit ツール等）した場合、
`refresh_unity` では**メモリ上のシーンは更新されない**。そのまま Play すると編集前の
シーンで走る（prefab override の追加が効かない等、静かに古い挙動になる）。

→ 対策: 外部編集後は `manage_scene action=load path=...` で**明示的に再ロード**してから Play。

## execute_code が「ファイル名または拡張子が長すぎます」で全滅する状態（2026-06-11）

mono.exe 起動時の引数長の環境問題で、コード長に関係なく（1 行でも）失敗する。
既知の「コード長制限」とは別物。Editor 再起動で直る可能性が高い。
代替: `mcpforunity://scene/gameobject/{id}/component/{name}` リソース読み +
manage_* ツールで大半は代替できる（このセッションで実証）。
