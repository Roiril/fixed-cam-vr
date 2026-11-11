# 演出ライブラリー

体験者の目で見た演出を、起きる順に並べた 1 冊。演出ごとに「山場の 1 枚」と「そこへ至る動画・コマ送り」を持つ。
演出を直すときの、いまの姿の見本と、直す前後を並べる台にする。

出力は Drive の `reports/fx-library/<時刻>/index.html`（`latest.json` が最新）。版ごとにフォルダが残るので、
直す前の版と並べて見られる。

## 組む

```powershell
py -3.11 tools/fx-library/build.py            # 目録の走行を探して組む（数分）
py -3.11 tools/fx-library/build.py --sheet    # 山場の秒を選ぶための 0.2 秒刻みのシートも出す（look/）
py -3.11 tools/fx-library/build.py --only takeover   # 1 本だけ切り直す
```

## 走行を作る

目録の 2 つの走行は `tools/twin-verify.py` で作る。人形がツインの画に合うよう `--calib twin` で走らせる。

```powershell
py -3.11 tools/twin-verify.py --calib twin --marks 1A,1B,2A,3B --tag fxlib-main   # 帰還 End まで。周の中でも報告する
py -3.11 tools/twin-verify.py --calib twin --nomark --tag fxlib-doll              # 人形 End まで
```

2 本は `tools/twin-tree.py run -- <同じ引数>` で同時に回せる。

`--marks` は、既定の走行では 1 度も画に出ない「報告で起きる演出」（手形が消える・幕がたなびく・返事の文）を撮るためのもの。
挙げた区間に入って 2.5 秒後に左 X を 1 回長押しする（`XpTwinDriver.TickExtraMarks`）。

## 目録 `catalog.json`

1 本の演出 = 1 行。どの走行の、どの出来事を起点に、何秒から何秒を切るか。

| 項目 | 意味 |
|---|---|
| `at` | 起点。`{"ev":"title","has":"stage=In"}` は replay_xp.log の `ev=title` で `stage=In` を含む最初の行。`{"video":0}` は動画の頭 |
| `from` / `to` | 起点からの秒。動画とコマ送りの区間 |
| `hero` | 起点からの秒。山場の 1 枚 |
| `crop` | `screen`（テレビの周り）・`full`（画面の外へ出る演出）・`panel`。1920×1080 の画素 |
| `say` / `now` / `ledger` | 説明。正は `canon/NOW.md`。そこから短く写す |

- 演出を足したら、ここに 1 行足して組み直す。体験者に見えることが変わったら `say` も直す
- 山場の秒は `--sheet` の `look/<id>.jpg` で選ぶ。判定は `fx/<id>/hero.jpg`（フル解像度）で見る

## 罠

- 動画がカクつくなら、撮影が内蔵 GPU で描いている。走行のログの `[Replay] 撮影を始めた ... gpu=` を見る（`描いた N・繰り返し M` の M が N の 1 割を超えたら撮り直す）。
  `tools/unity.ps1` は独立 GPU を `-force-device-index` で指す（`.claude/memory/fx_library.md`）。撮り直しは `--keep-rec` で残した記録から `.\tools\unity.ps1 replay -Set rec=<記録>,view=eye,fov=52,out=<出力>`。
  `replay_xp.log` は出力の隣に書かれる。撮り直したら動画と一緒に走行フォルダへ置き換える（時刻が揃うのは同じ撮影のログだけ）
- 時刻は再生のログ（`replay_xp.log` の `tau=`）で決める。走行のログ（`run_xp.log`・`events.json`）は同じ場面が数秒ずれる
- ツインは部屋が黒い（パススルーが無い）。導入の暗転より前の現実と、視野の端の床の矢印はほとんど映らない
- 年表（`poster.html`）は `~/.agents/skills/timeline/tools/timeline.mjs` で検査する。`data-date` は読む順の番号（人形 End の枝は別の走行で、時刻が本線と前後するため）。画に出す時刻は本物
- 動画は faststart で書き直すので、ローカルの `Logs/fx-library/` で組んでから Drive へ写す（G: へ直接書くと Drive が拒む）
