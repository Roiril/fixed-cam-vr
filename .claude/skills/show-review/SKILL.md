---
name: show-review
description: 廻リ視を頭から終わりまで通して見て、体験全体の食い違い・判定の空白・場面どうしの不調和を洗い出す。発動例：「通しで見て」「全体を見直して」「シナリオを監査して」「作品全体として弱い所は」「演出監査」、および大きな演出を 3 つ以上入れた後。結果は canon/NOW.md と canon/OPEN.md に落とす（報告書だけで終わらせない）。
---

# 通し監査

場面ごとの作りこみ（`work-round`）では、**場面どうしの食い違い**が見えない。
2026-10-22 のシナリオ監査は、4-A の締めの判定が LEDGER と矛盾しているのを臨時の 3 体監査でやっと見つけた。
演出監査は「赤が 3 か所で別の意味を持つ」を見つけた。どちらも通しで並べないと見えない種類の指摘だった。

## 1. 通しの画を作る（安い順）

```powershell
.\tools\unity.ps1 menu virtualwalk -Set laps=4,maxsec=300,tag=review
```

`laps` は経路を回る周の数（整数。文字を渡すと既定の 1 に落ちる）。最後の A まで含めて 4。
`-Set` は引用しない（`memory/virtual_walk.md`）。終幕まで届いたかは `sheet_outro.png` が埋まっているかで見る。
```bash
py -3.11 tools/virtualwalk-video.py          # walk.mp4・sheet_cuts.png・sheet_outro.png・report.md
```

実機の画が要るなら `py -3.11 tools/quest-record.py --sec 200 --walk --serial <α>`。
`sheet_cuts.png` は索引。気になるコマはフル解像度で開く。

## 2. 検査を流す

```bash
py -3.11 tools/canon-now.py --check     # NOW.md と show.json・LEDGER のずれ
py -3.11 tools/canon-now.py --orphans   # どの場面にも結び付いていない判定
```

最終確認より新しい LEDGER 項目が出たら、その場面の NOW の行を読み直す。

## 3. 視点を分けて見る（サブエージェント 3 体まで・並列）

| 視点 | 見ること |
|---|---|
| 正規の流れ | NOW の各行が、実装と LEDGER の両方に合っているか。判定済みの行が実装で崩れていないか |
| 逸脱 | 引き返す・止まる・押さない・押し続ける・切断・休止。入力の累計と出力の累計を並べる |
| 場面どうし | 色・書体・音・動きの語彙が場面ごとに別の意味を持っていないか。`canon/TASTE.md` の問いを場面をまたいで当てる |

各体には `canon/NOW.md` と該当する話題別 rule（索引は `rules/streaming.md`）を読ませ、
指摘は「場面・NOW の行・LEDGER 番号・コードの記号」の 4 つを付けて返させる。

## 4. 落とす

- 食い違い → `canon/NOW.md` 末尾の「LEDGER と実装の食い違い」の表へ 1 行。直したら消す
- 判定の空白（未判定のまま体験の芯に効いている行）→ 次の `direction-round` の「訊きたい 1 点」の候補
- シュビーの案（語彙の割り当て表など）→ `canon/OPEN.md` の「案」。**LEDGER には入れない**
- バグ → 直す。直さないなら `memory/` に未修正として 1 本（`MEMORY.md` の「いま開いている指摘」へ）
- 報告書（HTML）を作るなら `reports/YYYY-MM-DD_<slug>.html`。**報告書だけで終えない**（10-22 の 17 件は報告書にしか無かった）

## やってはいけないこと

- ❌ 通しの画を見ずに、コードとログだけで監査する
- ❌ 「良い・悪い」を点数にする（`reference/why.md`「品質を採点しない」）
- ❌ 指摘を LEDGER に書く。LEDGER はユーザーの発話だけ
