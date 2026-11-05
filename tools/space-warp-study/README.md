# 空間の歪みの試作と計測

LEDGER 0313 の方式を決めるために作った。本編の場は `Assets/Scripts/Streaming/RoomWarpField.cs`。経緯は `.claude/memory/room_warp.md`。

- `bake_depth.py`: 写実のツインから深度を焼く（Blender）
- `study.py`: 版を描いて並べる
- `depth_modes.py`: 奥行きの持ち方を比べる
- `measure.py`: 動画の変位と一体感を測る
- `export_table.py`: 本編へ写す表と照合値を書き出す

カメラの据え付けを変えたら `bake_depth.py` → `export_table.py` の順に流し、`RoomWarpField` と `RoomWarpTests` の値を差し替える。
