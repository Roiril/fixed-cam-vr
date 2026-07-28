---
name: show-json-is-live-config
description: show.json は git 管理外の現場設定。卓を検証で触ると現地の設定を直接書き換え、復元できない
metadata: 
  node_type: memory
  type: project
  originSessionId: 6841e293-f127-41c1-9c9f-172f7a816f5f
  modified: 2026-07-27T23:21:03.486Z
---

`tools/web-compositor/show.json` は **gitignore されていて git 管理外**（現場の DHCP IP や
その日の設定が入るため）。したがって：

- **`git checkout` では戻せない。** 検証で書き換えたら、値を覚えておいて手で戻すしかない
- **検証サーバ（8299）も本番の卓（8099）も同じ `show.json` を読み書きする**。
  ポートを分けても隔離にならない（同一ディレクトリ）
- `show.json.bak` は postState のたびに上書きされる 1 世代のみ。検証中に何度も command を送ると、
  bak も検証後の状態に化けて復元の役に立たない

**Why:** 2026-07-28 のラッチ列挙の検証で、`bindSlot` と `setDiscoveryEnabled false` を
curl で送って UI の反応を確かめた。`discoveryEnabled` は既定値 True へ手で戻したが、
「元が本当に True だったか」を確認する手段が無かった（git 差分が取れないため）。
そのとき現場では実カメラ 3 台が稼働中で、ユーザーが卓を開いていた可能性もあった。

**How to apply:**
- 卓の UI 検証で `POST /command` / `POST /state` を使う前に、**その値が現場設定かを見る**。
  `control.discoveryEnabled` / `autoFollow` / `cameras[].host` / `cameras[].pinned` は現場設定
- 触るなら **触る前に現在値を控える**（`curl /state | python -c ...` で該当キーだけ出す）
- できれば **`show.json` を別ディレクトリへコピーしてそこでサーバを起動**して検証する
  （`capture-server.py` は起動時のカレントを基準にする）
- `runEpoch` / `activeCue` / `cameraOverride` / `slots` は「ラン状態」なので、
  検証で立てたら必ず ⛑ か ▶ ラン開始 で畳んでから終わる

関連: [[web_compositor]]
