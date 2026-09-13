# 廻リ視の題字

`mawarimi-title-master-v2.png` が採用した字形の正本。
短い「廻」の払いと、その下を流れる薄い一筆を持つ。

`keyvisual-clean-v2.png` は元のキービジュアルから旧題字だけを除いた背景版。
どちらも画像生成で作成し、以後は描き直さず同じ画像から用途別の版を生成する。

キービジュアルの題字は右上の暗幕に配置する。人形の顔と左の縦書きコピーを避ける。
幅は背景の43%以内。薄い払いまで含めて右端に余白を残す。

```powershell
py -3.11 tools/make-title-art.py --preview
```

生成先:

- Quest: `Assets/Resources/Title/MawarimiTitle.png`
- Web UI: `Assets/Resources/Visitor/title-logo-v2.png.bytes`
- キービジュアル: `Assets/Art/KeyVisual/MawarimiKeyVisual-v2.png`
- 確認画像: `Assets/Screenshots/title/art-preview.png`
