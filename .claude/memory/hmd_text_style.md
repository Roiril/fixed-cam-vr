# HMD 内テキストの正と、2 回踏んだ罠

制定 2026-08-16（`canon/LEDGER.md` 0052）。**契約は `rules/show-design.md`「HMD に出す文言の規約」が正本。**
ここは実装の経緯と、次のシュビーが踏みそうな罠だけ。

## 大きさ・色・書式の正は 1 か所

[`Assets/Scripts/Diagnostics/HmdTextStyle.cs`](../../Assets/Scripts/Diagnostics/HmdTextStyle.cs)。
面は**距離だけ**を持ち、fontSize はそこから逆算する。段は 3 つ（補助 1.5° / **本文 1.8°** / 注目 2.2°）で、
既定は本文の 1 つ。色は 2 つ（地 / 警告）だけ。

## ⚠⚠ 単位系が 2 つある — この 0.1 が事故の正体

| 系 | 1 文字の世界サイズ |
|---|---|
| Canvas（`TextMeshProUGUI` ＋ WorldSpace Canvas） | `fontSize × canvasScale` |
| **3D（`TextMeshPro`）** | `fontSize × 0.1 × localScale` |

3D の TMP は**透視カメラのとき内部で 0.1 を掛ける**（`TMP_Text.m_fontScale`）。
これを知らずに数字を決めて、**2 回続けて実機で読めない大きさになった**。

- 2026-08-14: 体験前の注意書きが「1 文字 7cm のつもり」で **StatusHud の 1/8.5**
  （ユーザー報告「すごく奥に小さい白い文字」）。実機の画で測って 8.5 倍した
- 2026-08-15: 上司からの連絡が同じ間違いを**10 倍の規模**で。コードのコメントには
  「0.67 なら 1 文字 1.8°」と書いてあったが、実際は **0.18°** ＝ 点

⇒ **面が倍率を手で持っている限り再発する。** いまは `HmdTextStyle.MeshScale` / `CanvasFontSize` が
距離から解くので、面に数字が無い。⚠ **`sizeScale` の SerializeField は消した** —
シーンに焼かれた値が SerializeField の初期値より優先されるので、フィールドを残すと直せなくなる。

## ⚠ 折り返し幅も同じ scale で割る

3D の面は「小さい fontSize ＋ 大きい localScale」で組む規約なので、`RectTransform.sizeDelta` も
**世界の幅 ÷ scale** で書く。固定値にすると、字を直したとき枠だけ取り残されて面からはみ出す。

## ⚠⚠ 1 字ずつ出すと、TMP は縦中央を取り直す

`maxVisibleCharacters` は**レイアウトを組み直さない**ので毎フレーム触ってよい（文字列の作り直しも
GC も起きない）。ところが**縦中央揃え（`TextAlignmentOptions.Left` 等）だけは見えている行数で
中央を取り直す** — 2 行目の 1 文字目が出た瞬間に、**打ち終わった 1 行目がひょいと上へ跳ねる**
（2026-08-16 に上司からの連絡で実測 38px）。

⇒ **縦は上寄せ（`TopLeft`）にして、全文が出ている状態の重心を 1 度だけ中心へ運ぶ**
（`CommsPanel.Build` の `tmp.textBounds.center.y`）。
⚠ `preferredHeight` で枠を詰める手は駄目 — あれは字の上下に余白を含むので、そのぶん本文が
上へ寄る（実測 33px）。**組み上がったメッシュの実寸から測る。**

出方そのものの正は [`CommsPanelLogic`](../../Assets/Scripts/Streaming/CommsPanelLogic.cs)
（枠が左から開く → 1 字ずつ打つ → 読ませる → 文字が消えてから枠が畳まれる）。
⚠ **打つところに smoothstep を掛けない**（打鍵の間隔が伸び縮みして機械に見えなくなる）。
見るのは `.\tools\unity.ps1 menu comms-preview` → `tools/make-preview-video.py`。

## 見る道具

```powershell
.\tools\unity.ps1 menu text-audit
```

[`HmdTextAudit`](../../Assets/Scripts/Streaming/Editor/HmdTextAudit.cs)。Play 不要・シーンは保存しない。

- 見かけ角は **Unity が組んだメッシュから測る**（全角 2 文字の字送りの差）。式で出さない
- 行の幅は **`lineInfo[i].lineExtents`**。⚠ `textBounds` も `lineInfo[i].width` も
  **枠いっぱいに置いた TMP では枠の幅がそのまま返る** ＝ はみ出しを検出できない門になる
  （実際 1 度そう書いて、わざと長い行を入れても緑のままだった）
- 撮る前に面を**起こす**（不透明度 0・非活性・Canvas 切りで待っている面がある）。
  寝たままだと TMP がメッシュを組まず、測っても撮っても嘘が出る
- 撮り終えた面は台から**降ろす**。置いたままだと次の 1 枚に写り込む（2 枚が同じ絵になった）
- 絵は `Assets/Screenshots/hud-text/` に**同じ画角で** 8 枚。並べれば大きさがそのまま比べられる

## 語の規約は 2 つの asmdef にまたがる

異常の 2 行は `Diagnostics/RecoveryGuidance`、位置合わせは `Tracking/RegistrationGuidance`。
**同じ 1 枚の面（StatusHud）に出るのに、2026-08-16 まで片方だけが規約を守っていた**
（あちらは「ずれ」、こちらは「残差」「再登録」「基準点」）。両方にテストを置いてある。

⚠ **Tracking は Diagnostics を参照できない**（依存の向きが逆）。だから色の 16 進は
`RegistrationGuidance` にも複製してあり、`HmdTextStyleTests.TrackingHex_MatchesPalette` が
食い違いを落とす。**色そのもの（`Color`）は Tracking に持たせない** —
`CourseRegistrationController` は「対応が要る行か」（`GuidanceIsAlert`）だけを返す。

## 文言を変えたら

`.\tools\unity.ps1 menu hud-font`（静的ベイクなので、収集元 .cs に無い字は実機で豆腐）→
`menu scene`（面の寸法はシーンに焼かれる）→ `menu text-audit`。
収集元の一覧は `JapaneseHudFontSetup.CollectHudCharset()` と `tools/unity.ps1` の `Src` が 1:1。
⚠ **全角空白 `　` と全角スラッシュ `／` も字送りのグリフが要る**ので記号保険に入れてある。
