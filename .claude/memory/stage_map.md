---
name: stage_map
description: 体験者が画のどこに映るかをツインの動画から測り、台本の素材（マスク）と体の重なりを数える tools/twin/stage_map.py。測った値・罠・版を当てる道具
metadata:
  node_type: memory
  type: project
  originSessionId: c9fbd65f-b41e-4acc-9900-3c2606ee3815
  modified: 2026-11-12T13:49:15.968Z
---

2026-11-12 ユーザー依頼「体験者の位置とボタン駆動の演出設計にして、堅牢な体験になるようにリデザインしてほしい」「体験者が画面内のどこにいるときに出す演出なのかでデザインすること」。
設計の案は `reports/2026-11-12_position-driven-redesign.html`（未判定。`canon/OPEN.md` の同じ日付）。

**回し方**

```
py -3.11 tools/twin/stage_map.py --out "G:/マイドライブ/shubie/reports/stage-map/<名前>"      # 測定と、本番の台本の重なり
py -3.11 tools/twin/stage_map.py --out <フォルダ> --show <別の show.json> --assets <素材> --check
py -3.11 tools/twin/stage_cards.py --map <上の出力> --out <フォルダ>                          # 床の図と配置の画
py -3.11 tools/twin/variant_posdriven.py --root ../fcv-twin-tree [--chase]                    # 位置で動く版を検証用の木へ当てる
```

**測った値（順走 1 周・大人 1 人・手すりから 0.16m）**

- A: 体は右端（x 0.83〜1.0）から入り、角（進み 40〜50%）で x 0.39〜0.64 の全身になり、左（x 0〜0.24）へ抜ける。前 1/3 は画の左 3/8、後 1/3 は右 3/8 が空く
- B: 10% 以降は x 0.47〜0.76 → 0.48〜0.62 の中央の列。左 3/8 と右 2/8 は終始空く
- C: 進み 75% あたりで体が画から出る。右 3/8 は終始空く
- 本番の 2 周目 A のシミ（左半分の矩形マスク）は進み 32% から体と重なり、体の最大 93% が隠れる。版（締めたマスク＋角の線で引く）は 0

**罠**

- マスクは枠空間（16:9・640×360）、映像は 4:3。重なりを数えるときは枠の中央の 4:3 を切り出す（`mask_in_source`）
- C は黒い服が暗い幕の前で差分に出ない（頭しか取れない）。C だけ床の位置から投影した矩形を足す。A と B に矩形を足すと偽の重なりが出る
- `cv2.imread` / `imwrite` は Drive の日本語パスを開けない。`np.fromfile` → `imdecode` で読む
- 秒で終わる演出の「出ている帯」は目安。束の歩く速さは本番の走行の約 2 倍
- 焼いた show.json を書き換えたら `manifest.json` の `configSha256` を合わせる（合わないと読み込みで弾かれる）。Editor は `sa://assets/<名前>` を `FIXEDCAM_ASSET_OVERRIDE_DIR` から先に読むので、版の素材は StreamingAssets に無くてよい
- 版は検証用の木にだけ当てる（`variant_posdriven.py` はメインの木を断る）。`twin-tree.py sync` で消える

**版を走らせて分かったこと（`20261112_223717_posdrv-after`）**

- `untilLine` をつないだ進行は、横断の猶予 0.6 秒のおかげで止まらなかった。ただし線の間が 0.2m だとカットのあいだが 0.1 秒になる
- C のライブに立たせた人形（cg fixed）は、画の暗い側に入って読めない
- 追いつきを C の出口近くに置くと、乗っ取りが 3 周目 A へはみ出す
- エージェント画面の侵食の段 0.25 は L2C2#0（入りの短いカット）に結ばれている。外すと段が抜ける

関連: [[twin_verify]] / [[twin_cycles]] / [[composite_mask_quality]]
