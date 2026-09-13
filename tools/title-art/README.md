# 廻リ視の題字

`mawarimi-title-master-v2.png` が採用した字形の正本。
短い「廻」の払いと、その下を流れる薄い一筆を持つ。

`keyvisual-clean-v2.png` は元のキービジュアルから旧題字だけを除いた背景版。
題字と背景は画像生成で作成した。Quest と Web UI は題字正本から用途別の版を生成する。

キービジュアルの完成原稿は `keyvisual-layout-v3.png`。
題字を右上に保ち、補助文字の配置を画像生成で仕上げた。
コピーは左上に横書き2行。ジャンル表記は題字の下。制作クレジットは右下にまとめる。
コピー全文は「廻るたびに狂う世界、あなたは生きて戻れるか。」。
ジャンルは「実世界 × 固定視点ホラー」。クレジットは「企画・監督・制作リーダー」「白石大晴」。
完成原稿には題字も含まれる。題字を変更する場合はこの原稿も更新する。
旧背景は制作履歴として保持する。通常の再生成でも完成原稿から書き出す。

```powershell
py -3.11 tools/make-title-art.py --preview
py -3.11 tools/make-title-art.py --keyvisual-only
```

生成先:

- Quest: `Assets/Resources/Title/MawarimiTitle.png`
- Web UI: `Assets/Resources/Visitor/title-logo-v2.png.bytes`
- キービジュアル: `Assets/Art/KeyVisual/MawarimiKeyVisual-v2.png`
- 確認画像: `Assets/Screenshots/title/art-preview.png`
