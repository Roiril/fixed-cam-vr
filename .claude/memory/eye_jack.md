# 目の視界ジャック（当日写真）を触る前に

`canon/LEDGER.md` 0099 の実装。契約の正本は `rules/streaming.md`「目の視界ジャック」節。
ここは**次に触る人が踏みそうな罠**だけ。

## 1. ⚠⚠ 「全開で発火」は多数派に届かない

最初この設計で書きかけて、設計批評が `tools/web-compositor/dwell_stats.json` から止めた。

- 3 周目 C の**実測滞在は 7.04 秒**（n=98）
- `EyesCueLogic.FinishAt` = 0.5 ＝ **空間の半分**なので、およそ **3.5 秒**で `Finishing` が立つ
- 目が全開になるのは **4.92 秒**
- `Wanted = !Finishing || !openDone` なので、**`Hold` は 1 フレームしか存在しない**

⇒ 発火は **「全開 ＋ 1 拍」と「区間の半分」の早い方**。片方だけにすると
**歩き続ける体験者（多数派）に一度も出ない**。

⭐ 副産物: 同じ理由で **0084 の「待機中のぎょろぎょろ」（`GazeRiseSec` = 0.9 秒）も
実機では走っていない**。目の待機中の芝居を足すなら、まずこの構造を見ること。

## 2. ⚠⚠ 覆いの裏で目を閉じさせない

歩く体験者は**区間の半分**で「終了演出に入る」のと「ジャックが出る」が**同じ縁**なので、
素直に書くと **1.6 秒の閉じが写真の裏で終わり、視界が戻ったとき何も残らない**。
0084 / 0085 でユーザーが赤入れした「笑ってから閉じる」が 1 フレームも見えない。

⇒ `AnomalyEyes.LateUpdate` は `_logic.Tick(dt, _cue.Wanted || _jack.Active, …)`。
`EyeJackLogicTests.WhileJackCovers_EyesAreHeldOpen_AndCloseOnlyAfterItReturns` が固定する。

⚠ これは**開く側を塞がない**（`Hold` に留めるだけ）。掛けっぱなしにもならない —
ジャックは `armed` が落ちた次のフレームで必ず消える。

## 3. 正規化は PC で終わらせる（端末でやらない）

`capture-server.py` の `_sync_eyejack` が **長辺 1280 / EXIF の向きを画素へ焼き込み /
減光 0.72 / JPEG q85**。端末は decode するだけ。

- **12MP をそのまま配ると** `Texture2D.LoadImage` で 1 枚 50MB 級の一時確保が走る
- **Unity は EXIF を読まない** ＝ 縦写真がそのまま横を向く
- どちらも**実機でしか出ない失敗**で、当日は直せない

⚠ `EYEJACK_BRIGHTNESS`（0.72）は**机上の値**。暗順応した視界へ室内写真を出すので落としてあるが、
実機で被って決め直す。触ったら `menu eyejack` の絵と実機の走行を対で見る。

## 4. 見る手段

```powershell
.\tools\unity.ps1 menu eyejack          # stationary / walker の 2 通りを通しで焼く
```
```bash
py -3.11 tools/make-preview-video.py Assets/Screenshots/eyejack-preview/walker eyejack_walker
```

⚠ **`make-preview-video.py` は `f0000.png` の連番しか拾わない**ので、
2 通りは**別フォルダ**へ焼いてある（接頭辞で 1 フォルダに混ぜると mp4 にできない）。

⚠ `frames.tsv` の `jackActive` / `photoIndex` / `stage` を並べると、
**「写真が終わる → 視界が戻る → 目が閉じる」の順**が数値で読める（絵より速い）。

## 5. 卓のサーバを止めずに検証する

`tools/web-compositor/show.json` は**現場設定で git 管理外**、しかも
**動いているサーバが `_show` をメモリに持っている**。ディスクを直接書くと、
そのサーバの次の書き込みで**黙って巻き戻る**。

- 台本（`steps[].eyeJack`）を変えるなら **`POST /state` 経由**（`_mutate_show` を通る）
- サーバの新しいエンドポイントを試すなら、**`importlib` で読み込んで
  `SHOW_FILE` / `EYEJACK` を一時フォルダへ差し替える**（このセッションはそれで
  取り込み・差し替え・空にする・焼き込みの 4 経路を確かめた）
- 別ポートで 2 つ目を立てるのは **GET だけ**なら安全（`/eyejack/list` は
  `eyejack/norm/` にしか書かない）

## 6. 届いたかは heartbeat にしか出ない

`eyeJackListed`（show.json が言う枚数）と `eyeJackReady`（**その機がデコードまで済ませた枚数**）。
**2 台のうち片方だけ欠けるのは画にも音にも出ない。** 卓のライブ状態の「目の写真」行が唯一の手掛かり。

関連: [[hmd_text_style]] / [[onsite_experience_test]] / [[show_json_is_live_config]]
