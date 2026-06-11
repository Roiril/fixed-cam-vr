---
name: streaming-offline-test
description: スマホ実機が手元に無い状態で fixed-cam-vr の MJPEG ストリーミングパイプラインを Unity Editor 上で end-to-end 検証する。Python の fake MJPEG server を立てて Phone01/02 を localhost に向け、Play して結果スクショを取る一連の作業フロー。streamer 側 /info の真偽確認に JPEG SOF パースも併載。
---

# Streaming Offline Test Workflow

スマホ無しで Unity 側のストリーミング実装（MjpegStreamReceiver / CameraStream / MjpegScreen / アスペクト処理）を検証するための完全自動フロー。

## 全体の流れ

1. fake MJPEG server を起動（[fake_streamer.py](fake_streamer.py)）
2. `Phone01.asset` / `Phone02.asset` の `host` を一時的に `127.0.0.1` に変更（**必ず backup を取る**）
3. Unity MCP で `FlatStreaming.unity` をロード → `manage_editor play`
4. 数秒待つ → `manage_camera screenshot include_image=true` で確認
5. **Play stop → host 復元 → fake server kill → Screenshots ディレクトリ削除 → git status クリーン確認**

## 1. Fake server 起動

`fake_streamer.py` を bg 実行。`/video` `/info` `/health` を fixed-cam-streamer 互換で出す（1080×1080 / quality=40 / rotationDeg=90 / isPortrait=true）。

```bash
python "<skill-dir>/fake_streamer.py"  # run_in_background=true
sleep 1
curl -s -m 1 http://127.0.0.1:8080/health  # 動作確認
```

## 2. Phone01/02 を localhost に

```bash
cp Assets/Settings/Cameras/Phone01.asset $TMP/Phone01.asset.bak
cp Assets/Settings/Cameras/Phone02.asset $TMP/Phone02.asset.bak
```

その後 Edit ツールで `host: 192.168.x.x` → `host: 127.0.0.1` に書き換え（両 asset とも）。**Phone02 を放置すると延々と reconnect ログでノイズが増える**ので必ず両方やる。

## 3. Unity Play

```
mcp__UnityMCP__manage_scene action=load path=Assets/Scenes/Debug/FlatStreaming.unity
mcp__UnityMCP__refresh_unity scope=assets compile=none  # asset 変更を Editor に反映
mcp__UnityMCP__manage_editor action=play
```

## 4. 動作確認

`sleep 6` 程度待ってからスクリーンショット：

```
mcp__UnityMCP__manage_camera action=screenshot include_image=true max_resolution=1024 super_size=2 screenshot_file_name=flatstreaming-fake-test.png
```

期待結果（ScreenComposite 固定枠モデル、2026-06-11 移行後）：
- **Quad の枠は 16:9 のまま不変**。square ソース (1080×1080) は高さ基準で contain-fit され、左右は黒 pillarbox（背景と同色なので見た目は「中央に正方形」）
- 中央に黄丸 + 青枠 + 赤×線（fake コンテンツ）が**正方形のまま**表示（歪んでいたら _LiveScale 計算のバグ）
- Transform 回転はもう適用されない（回転は uvRotSteps による UV 回転のみ）

`mcp__UnityMCP__read_console types=["error","warning"]` で `[MjpegScreen] No registry...` 等が無いか確認。

## 5. クリーンアップ（必須）

```
mcp__UnityMCP__manage_editor action=stop
TaskStop の task_id で fake server 停止
cp $TMP/Phone01.asset.bak Assets/Settings/Cameras/Phone01.asset
cp $TMP/Phone02.asset.bak Assets/Settings/Cameras/Phone02.asset
rm -rf Assets/Screenshots Assets/Screenshots.meta
```

最後に `git status -s` がクリーン（または既存 dirty のみ）になることを確認する。

## アスペクト/向きの真偽確認に JPEG SOF パース

`/info` の `widthPx/heightPx/rotationDeg` が実画像と乖離している疑いがある時、JPEG ヘッダの SOF マーカから真値を取る：

```python
import re, struct
data = open('frame.bin','rb').read()
i = data.find(b'\xff\xd8')
jpeg = data[i:]
j = 0
while j < len(jpeg)-9:
    if jpeg[j] == 0xFF and jpeg[j+1] in (0xC0, 0xC2):
        h = struct.unpack('>H', jpeg[j+5:j+7])[0]
        w = struct.unpack('>H', jpeg[j+7:j+9])[0]
        print(f'JPEG SOF: {w}x{h}'); break
    j += 1
```

このセッションでは「`/info` は 1080×1080 と返すが実画像も実際 square」と判明し、Unity 側 `useTextureAspect = true` で **Texture2D.width/height を直接 aspect として採用**する設計に至った。

## 設計原則メモ

- **Texture2D.width/height は streamer 側 /info より信頼できる aspect ソース**。MJPEG 経路では JPEG ヘッダが真実
- /info はメタ情報（rotationDeg 等）の取得用に残す
- Phone01/02 の asset 編集は **必ず backup → 検証 → 復元** をワンセットで。検証中に commit しないこと

## 落とし穴

- Phone02 を localhost にしないと、`192.168.11.3` 等への接続失敗で `[MJPEG] disconnected` がコンソールを埋め、active stream の問題と区別できなくなる
- `manage_camera screenshot` は `Assets/Screenshots/` を作る。**git に commit しないよう必ず削除**
- fake server は SIGINT で死なないことがある → `TaskStop` でちゃんと止める（残ると次回ポート競合）
