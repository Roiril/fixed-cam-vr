# 博士の部屋（試作）

博士とテレビを 1 つの部屋の絵に置き、カメラがその中を動く形の試作（LEDGER 0335・`canon/OPEN.md`「博士とテレビを 1 つの部屋に置き、1 台のカメラで繋ぐ」）。
**タブレットのアプリにはまだ入っていない。** ブラウザで見るだけの頁と、通しの動画を書き出す道具。

## 見る

`.claude/launch.json` の `room-proto`（`node tools/visitor-ui/room-proto/serve.cjs --port 8142`）→ `http://localhost:8142/`。
空白で再生と停止。左右の矢印で前後の文。声・字幕・絵はタブレットの正本（`tablet/app/src/main/assets/web/asset/`）をそのまま読む。

- `?eyes=0` 目元の闇を外す
- `?w=1280` 描く幅を固定する（書き出し用）

## ファイル

| | |
|---|---|
| `gen/seed.png` | Codex へ渡した土台。黒い画布に博士の元の写真（`../doctor-centered-v1.png`）だけを貼ったもの |
| `gen/plate-a1.png` | Codex が描き足した部屋（テレビは消えている）。1672×941 |
| `gen/plate-a1-lit.png` | 同じ部屋で、テレビだけを点けさせた版。使うのは光の差だけ |
| `build-layers.py` | 上の 2 枚を層へ分ける → `layers/`（博士を抜いた部屋・博士・光 2 枚・ガラスの形・`room.json`） |
| `room-stage.js` | 舞台。1 枚の canvas に 部屋 → テレビの中身 → 部屋への光 → 博士 → 博士への光 → 埃 → 粒 の順で描く |
| `index.html` | 声の時刻からカメラとピントとテレビの中身を決める台本と、再生の操作 |
| `serve.cjs` | 配る。書き出しのコマも受け取る（`POST /_frame`） |
| `make-preview.py` | 書き出したコマに声と字幕を合わせて mp4 にする |

## 通しの動画を作る

1. `http://localhost:8142/?w=1280` を開き、コンソールで `await RoomProto.export('full', 30)`（3466 コマ・2 分前後）
2. 文の時刻を送る: `fetch('/_frame?dir=meta&n=0', { method: 'POST', body: JSON.stringify({ cues: RoomProto.cues.map(c => ({ s: Math.round(c.start), e: Math.round(c.end), t: c.text, scene: c.scene, i: c.i })), total: RoomProto.TOTAL, tvOn: RoomProto.TV_ON, end: RoomProto.END_AT }) })`
3. `py -3.11 tools/visitor-ui/room-proto/make-preview.py <ローカルの出力.mp4>` → できたものを Drive `renders/doctor-room/` へ移す
4. `frames/` は消す（git 管理外）

## 分かったこと

- 元の博士の写真は、部屋の絵に 幅 926・左上 (96,126) で重なる（顔の窓の差が最小の所を探した値。`build-layers.py` の `FACE_RECT`）。Codex は「1 画素も変えない」と頼んでも顔を描き直すので、顔は必ず元の写真を上から重ねる
- 光を 1 枚で足すと、ぼかした光が博士の輪郭の外へはみ出して青い縁取りに見えた。部屋の分と博士の分に分け、博士の分は博士の形で抜く
- テレビの中身の平均の色を 60ms ごとに読み、光の色と強さにしている。電源の入りの白い光も、砂嵐の揺れも、何も足さずに博士へ乗る
- `Image.decode()` と `canvas.toBlob()` は、内蔵ブラウザの枠が見えていないと戻らない・遅い（1 コマ 1 秒）。`onload` と `toDataURL` を使う
- ⚠ Redmi の WebView で、動画を毎コマ canvas へ描いて足りる速さが出るかは未確認（博士はまだ静止画）
