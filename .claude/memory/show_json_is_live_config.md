---
name: show-json-is-live-config
description: show.json は git 管理外の現場設定。卓を検証で触ると現地の設定を直接書き換え、復元できない
metadata: 
  node_type: memory
  type: project
  originSessionId: 6841e293-f127-41c1-9c9f-172f7a816f5f
  modified: 2026-07-30T03:56:45.207Z
---

`tools/web-compositor/show.json` は **gitignore されていて git 管理外**（現場の DHCP IP や
その日の設定が入るため）。したがって：

- **`git checkout` では戻せない。** 検証で書き換えたら、値を覚えておいて手で戻すしかない
- **検証サーバ（8299）も本番の卓（8099）も同じ `show.json` を読み書きする**。
  ポートを分けても隔離にならない（同一ディレクトリ）
- **さらに悪い: 2 つ立てると互いを上書きし合う**（2026-07-29 実測）。`capture-server.py` は起動時に
  ファイルを読んで**メモリ上の `_show` を正**にし、書き込みのたびにそれを丸ごと書き戻す。
  片方が rev 683、もう片方が rev 674 という乖離が実際に起きていた。しかも `autoFollow` の
  discovery スレッドが beacon 受信で勝手に `_mutate_show` を呼ぶので、**誰も操作していなくても**
  古い方が新しい方を巻き戻す。→ **卓は 1 プロセスだけにする**。検証で立てたら必ず止めてから本番卓を使う
- `show.json.bak` は postState のたびに上書きされる 1 世代のみ。検証中に何度も command を送ると、
  bak も検証後の状態に化けて復元の役に立たない

**Why:** 2026-07-28 のラッチ列挙の検証で、`bindSlot` と `setDiscoveryEnabled false` を
curl で送って UI の反応を確かめた。`discoveryEnabled` は既定値 True へ手で戻したが、
「元が本当に True だったか」を確認する手段が無かった（git 差分が取れないため）。
そのとき現場では実カメラ 3 台が稼働中で、ユーザーが卓を開いていた可能性もあった。

## サーバが動いている間、show.json を Write / Edit で直接書いてはいけない（2026-07-30 実害）

`capture-server.py` は起動時にファイルを読んで**メモリ上の `_show` を正**にし、`_mutate_show` の
たびに全文を書き戻す。だから**外から書いたファイル編集はサーバから見えず、次の mutation で黙って消える**。
discovery スレッドが beacon 受信で勝手に mutation を呼ぶので、**誰も操作していなくても消える**。

2026-07-30 に実際に踏んだ: `layout.startSpot` / `layout.room` / `run.intro` / 2 周目 B の演出を
Edit ツールで書き込み → EditMode テストを回している数分のあいだに rev 695→696 で全部巻き戻った
（`git diff` も取れないので、消えたことに気づけるのは値を読み直した時だけ）。

**How to apply（正しい手順）:**
1. `netstat -ano | grep :8099` でサーバが生きているか見る
2. 生きていたら **`GET /state` で現在値を取り、その上に変更を載せて `POST /state` で返す**。
   `_STATE_KEYS`（cameras / cues / post / control / layout / schedule / timeline / bgmTracks /
   bgm / actors / record / run / lenses）は**トップレベル shallow 置換**なので、
   部分だけ送るのではなく**そのキーの完全なオブジェクトを送る**
3. 直接ファイルを書きたいなら**先にサーバを止める**（止めれば起動時に読み直す）

## How to apply（従来分）
- 卓の UI 検証で `POST /command` / `POST /state` を使う前に、**その値が現場設定かを見る**。
  `control.discoveryEnabled` / `autoFollow` / `cameras[].host` / `cameras[].pinned` は現場設定
- 触るなら **触る前に現在値を控える**（`curl /state | python -c ...` で該当キーだけ出す）
- できれば **`show.json` を別ディレクトリへコピーしてそこでサーバを起動**して検証する
  （`capture-server.py` は起動時のカレントを基準にする）
- `runEpoch` / `activeCue` / `cameraOverride` / `slots` は「ラン状態」なので、
  検証で立てたら必ず ⛑ か ▶ ラン開始 で畳んでから終わる

関連: [[web_compositor]]
