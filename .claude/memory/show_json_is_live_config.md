---
name: show-json-is-live-config
description: show.json は git 管理外の現場設定。卓を検証で触ると現地の設定を直接書き換え、復元できない
metadata: 
  node_type: memory
  type: project
  originSessionId: 6841e293-f127-41c1-9c9f-172f7a816f5f
  modified: 2026-09-03T11:47:17.395Z
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

### 消えなくても、実機には届かない（2026-08-02 実害・実機テストを 1 回無駄にした）

上は「書いた値が次の mutation で消える」話。**消えなくても実機には届かない**という別の症状がある。

サーバは起動時に読んだメモリを配る。だから mutation が起きなければファイルは新しいまま残るが、
**long-poll で実機へ流れるのは古いメモリの方**。実測: `record.laps` を `[1]`→`[1,2]` にし、
帰りの A の区間を足して APK を焼き直し、端末キャッシュも消したのに、実機は **rev 718（古い設定）で走った**
（ファイルは rev 719 のまま無傷）。2 周目が録れず、帰りの A の区間そのものが存在しなかった。

**紛らわしいのは、実機のテレメトリが `src=live` と報告すること。** 「卓が配った」は正しいが、
配られた中身が古い。`ev=config` の `rev=` を見れば分かるが、その行が logcat から落ちていると
何も分からないまま「著作した演出が出ない」と読む。

→ **実機テストの前に必ず `curl /state` でサーバのメモリを確認する**（ファイルではなく）。
食い違っていたらサーバを止めて起動し直す（`capture-server.py` に終了時の保存は無いので kill して安全）。

```bash
curl.exe -m 3 -s http://127.0.0.1:8099/state | py -3.11 -c "import sys,json;d=json.load(sys.stdin);print('rev',d['rev'],d['record'])"
```

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
- 触るなら **触る前に現在値を控える**（`curl /state | py -3.11 -c ...` で該当キーだけ出す）
- できれば **`show.json` を別ディレクトリへコピーしてそこでサーバを起動**して検証する
  （`capture-server.py` は起動時のカレントを基準にする）
- `runEpoch` / `activeCue` / `cameraOverride` / `slots` は「ラン状態」なので、
  検証で立てたら必ず ⛑ か ▶ ラン開始 で畳んでから終わる

関連: [[web_compositor]]

## ⚠⚠ 卓サーバは**メモリ上の状態**を配る — show.json を直接書いても届かない（2026-08-15 実害）

`capture-server.py` は起動時に show.json を読んで**自分の中に持ち**、long-poll ではその値を配る。
だから **`show.json` をエディタやスクリプトで直接書き換えても、走っているサーバは知らない**。

実害: 実機検証で `layout.lines` を 3 回書き換えて 3 回走らせたが、実機はずっと最初の版で走っていた。
実機ログの `ev=config rev=791` が動かないことに気づくまで、走行 3 本（約 12 分）を無駄にした。
**画も演出も「正しく」動いていたので、差分だけ見ていると永久に分からない。**

⇒ **直接書いたら卓サーバを入れ直す。** または卓の UI / API 経由で書く。
⇒ **走行のたびに `ev=config rev=` を見る。** 上げたはずの rev が実機に出ていなければ届いていない。
   解析レポートの「## 実機が使った設定」に出る。

## APK の焼き込みは、いちばん下にあるので古くても気づけない（2026-09-03）

`Assets/StreamingAssets/show/show.json` は**卓の 📦 ボタンでしか更新されなかった**。
手で押す 1 手なので押し忘れる。実際に `timeline.rev` が 33（8/17）のまま止まっていて、
卓は 44 まで進んでいた。当時の差は「3 周目 A が 1 周目 A の録画で、左右分割も無い」。

**気づけないのは、優先順位が 焼き込み < 端末キャッシュ < ライブ だから。** 卓に一度でも繋いだ機は
キャッシュの側で走るので、焼き込みが何周遅れていても画は正しい。
⚠ **ただし APK を焼き直すとキャッシュは捨てられる**（`CachedConfig.buildGuid` の照合・2026-08-14 から）。
**焼き直した機を卓なしで起動した時だけ**、古い著作がそのまま体験になる。当日いちばん踏みやすい形。

⇒ **`unity.ps1 build fixedcam` が毎回焼き込むようにした**（[`tools/export-show-build.py`](../../tools/export-show-build.py)）。
焼き込みの中身は卓と共通（[`export_build.py`](../../tools/web-compositor/export_build.py)・
テストは `test_export_build.py`）。飛ばすのは `-NoExport`。
いま古いかを見るのは `py -3.11 tools/export-show-build.py --check`（`unity.ps1 doctor` も出す）。

⚠ **この CLI は卓のディスク上の `show.json` を読む**（サーバのメモリではない）。上の節のとおり
サーバはメモリを正とするので、**卓で著作したまま保存が走っていない状態では 1 手ぶん古いものを焼く**
可能性がある。`_mutate_show` は変更のたびに書き出すので通常は一致するが、
`ev=config rev=` と焼き込みの rev が食い違ったらここを疑う。
