---
name: show-design
description: 廻リ視の体験の仕様の置き場の案内。体験者に見えることは canon/NOW.md、コードの契約は話題別の rule。シーンを触る前に読む
paths:
  - "Assets/Scenes/**"
---

# 廻リ視の体験の仕様 — どこにあるか

2026-10-22 まで、このファイルは 188KB の日付つき差分ログだった。いまは次に分かれている。

| 知りたいこと | 正本 |
|---|---|
| 体験者にいま何が見えるか・それを誰が決めたか | `canon/NOW.md`（`py -3.11 tools/canon-now.py --check` でずれを検出） |
| ユーザーが言ったこと（逐語） | `canon/LEDGER.md` |
| コードの契約（破ると壊れること） | 話題別の rule（索引は [streaming.md](streaming.md)） |
| 尺・秒数・区間の割り当て | `show.json`（現場の値）と実装。文書に秒数を写さない |
| 経緯の全文 | `reference/archive/show-design-2026-10-22.md`（凍結） |

シーン（`Main.unity`）は `.\tools\unity.ps1 menu scene`（Setup Main Demo Scene）が組む。手で編集した値は
次の Setup で消えるので、変えるなら Setup のコード側を直す。
