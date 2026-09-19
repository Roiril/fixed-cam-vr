# レシピ

このレシピは素材の座標で指定する。`frame_size` だけが出力枠の寸法。
画質の閾値は素材ごとに画像を見て決める。以下の数値を万能な既定として扱わない。

```json
{
  "version": 1,
  "mode": "difference",
  "reference": "empty.jpg",
  "sources": ["generated.mp4", "last.png"],
  "difference": {
    "kind": "darken",
    "low": 8,
    "high": 24,
    "blur": 0.7,
    "min_area": 8,
    "close": 1
  },
  "margin": 3,
  "feather": 8,
  "frame_size": [640, 360],
  "allow_domain_contact": false
}
```

- `sources` は静止画像と動画。動画は全コマを走査する。素材の最終フレームと切替先の静止画の両方を入れる。
- `transform` の `crop: [x0,y0,x1,y1]` と `size: [w,h]` は素材だけへ適用する。無人画像は最終座標のものを渡す。
- `domain` は探索を許す多角形の配列。`[[[x,y],...],...]`。完成alphaをこの図形で切らない。
- `seeds` は残す連結領域を指定する。同じ配列形式。
- `core_polygons` は必ず残す範囲を追加する。`surface` ではこれが壁の形そのものになる。
- `region` では `core_polygons` が必須。背景が描き直されているときに選ぶ。差分抽出を行わず、全コマの走査と寸法の検査は行う。
- `protect` は実写を残す明示的な要件がある場合だけ使う。同じ配列形式。生成素材に既にあるパイプや支柱を勝手に抜かない。使う場合も幅のある矩形で染みごと消さず、実物の形を見て指定する。
- `low` / `high` はRGBの差の0〜255階調。`darken` は暗くなる差。`absolute` は両方向の差。
- `margin` / `feather` は素材の画素。`difference` と `region` の中心は1を保ち、外側で遷移する。`surface` は多角形の内側で遷移する。
- `allow_domain_contact` は探索範囲で対象が切れたときの停止を解除する。既定はfalse。実物の境界であると確認した理由を記録できるときにだけ使う。

入力SHA256とレシピを保存しても、別バージョンの画像デコーダーの一致までは保証しない。
同じ実行環境での一致を試す。動画のフレーム数も記録する。
