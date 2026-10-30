---
name: crt-reference-rule-layout
description: screen-postの容量上限と参照CRT材質の詳細の導線。
type: project
---

`rules/screen-post.md`はpaths付きルールの12,000bytes上限へ近づいている。
CRT材質の追加で12,838bytesとなり、詳細を移した後も12,057bytesで同じ検査に再度失敗した。

現行の色空間・処理順・診断の詳細は`reference/crt-reference-material.md`へ全文を移した。
`rules/screen-post.md`の「参照材質を触る前に読む」から到達する。
詳細をルールへ貼り戻さず、この参照資料を更新する。ルールを編集した場合はUTF-8の実バイト数を測ってから`tools/harness-check.py`を実行する。
