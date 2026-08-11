---
name: title-screen
description: タイトル画面「廻リ視」を触る前に — 描画順で壁を隠している / 距離場は alpha / A は文脈分岐 / 素通しへ倒す
metadata: 
  node_type: memory
  type: project
  originSessionId: af5a556b-6b30-4a77-84ea-1e6cea94b69a
  modified: 2026-08-11T18:02:14.767Z
---

# タイトル画面「廻リ視」を触る前に

設計と経緯の正本は [plans/2026-08-12_title-screen.md](../plans/2026-08-12_title-screen.md)。
ここは**次に触る人が踏む罠**だけ。

## 1. 壁を隠しているのは「重み」ではなく「描画順」

依頼の絶対条件は **「一瞬でも壁が見えてはいけない」**。満たしているのは判定ロジックではなく順序:

```
IntroVeil 4900 → ContainmentShellMask 4905 → ContainmentShell 4910
  → SealedBox 4920 → TitleVeil 4950 → TitleGlyph 4960
```

段 0 では `IntroDirector` が毎フレーム封印の箱を描いていて、タイトルの黒は**その上から
alpha を 1 へ戻しているだけ**。alpha を 0 へ戻せば箱がそのまま現れる ＝ **開ける途中に
壁が覗くフレームが構造的に無い**。

⚠ **queue を動かすときはこの列ごと考える。** タイトルを 4920 より前に置くと封印の箱が
タイトルの上に描かれ、4900 より前に置くと覆いの `Blend Zero SrcAlpha` に丸ごと潰される。
**5000 を超えると URP の透明パスに入らず 1 画素も出ない**（2026-07-31 実害）。

`TitleLogic.ConcealWaitMaxSec`（0.5 秒）の待ちは**補強であって担保ではない**。
上限を外してラッチにしないこと（隠すものが壊れている現場でタイトルが永久に閉じなくなる）。

## 2. 距離場は alpha に入っている（RGB に移さない）

Unity は **RGB にだけ** sRGB 変換を掛ける。距離場を RGB に入れると `sRGBTexture` の設定 1 つで
値が歪み、**縁の太さが実機でだけ変わる**。alpha は常に線形なのでこの事故が起きない。

- 焼く: `py -3.11 tools/make-title-sdf.py`（`--preview` で目視用の PNG も出る）
- 読む: `TitleGlyph.shader` の `.a`
- 取り込み設定は `TitleSdfImporter`（AssetPostprocessor）が機械で固定する。
  **既定のまま取り込むと Android で ASTC 圧縮になり、字の輪郭がブロック状にギザつく**

⚠ **上下を反さない。** PNG の 1 行目は Unity では v=1（上）に入るので、
「左上原点の px」→「v = 1 - y/H」で読めば一致する（`TitleScreen.AddQuad` がそう読む）。
生成器で `FLIP_TOP_BOTTOM` すると二重になり、**字が上下逆さまに出る**（2026-08-12 に実測して修正）。

⚠ **帯（テクスチャ内の置き場所）は 2 箇所が同じ値を持つ**: `make-title-sdf.py` の
`TITLE_BAND` / `SUB_BAND` と `TitleScreen.cs` の `TitleX0..SubY1`。片方だけ直すと字が伸びる・切れる。

## 3. A ボタンは文脈で分岐する（奪っていない）

- タイトルが立っている（Normal）→ **タイトルを閉じて体験を始める**
- それ以外（Normal）→ 従来どおりカメラ手動送り Next
- Registration → 従来どおり点サンプル

入力面は右手 4 入力のまま（2026-07-23 の凍結は破っていない）→ [[controller-input-final]]。

`TitleScreen.RequestDismiss()` は**タイトルが立っていないと false を返す**ので、
呼び出し側（`OvrControllerBridge`）はそのまま従来の割り当てへ落ちる。

## 4. 失敗したら必ず素通しへ倒す

`TitleScreen.IsBlocking` は **実体を組めたときしか true にならない**（シェーダ剥がれ・
距離場欠落なら false）。この値が `UserPresentProvider` を通じて**導入の開始門**に効くので、
ここをラッチにすると**タイトルが出せない現場で体験が二度と始まらない**
（2026-07-31 のシェーダ剥がれと同型）。

出口は 2 つある。**両方消さないこと**:
- 右 A（`RequestDismiss`）
- 導入が段 0 を出た（卓の ⏭ 等）→ `ForceClose`。**コントローラが死んでいる現場での唯一の出口**

`TitleShaderInclusionTests` が「2 本のシェーダが `m_AlwaysIncludedShaders` に入っていること」と
「距離場が Resources から読めること」を機械で固定している。

## 5. 見た目を変えたら必ず絵を出す

```powershell
.\tools\unity.ps1 menu title
```

静止画 9 枚 → `Assets/Screenshots/title/` / 閉じる演出 56 枚 → `logs/title/`。Play も HMD も不要。
**背景には本物の封印の箱を置いてある** — 単色の背景で撮ると「黒が開いたとき何が見えるか」を
判定できない。

判定の作法は [[../rules/visual-verification]] のとおり: **フル解像度で、対象だけを画面いっぱいに、
実物と並べて、「これは何に見えるか」を言語化する**。2026-08-12 に、縁の色を封印の箱と同じ暗さで
焼いて「絵の中で字が判別できない」を 1 度出している — **箱は環境だがタイトルは読めなければ意味が無い**。

## 6. まだ入っていないもの

- **テレメトリ `title=` / `titleBuilt=`**。`ShowTelemetryHost` と `analyze-xp-log.py` を
  **対で**直す必要がある（片方だけだと沈黙して食い違う）。2026-08-12 は別セッションが
  両ファイルに未コミット変更を持っていたので見送った
- `rules/show-design.md` への節（同じ理由。plans が代わりを務めている）
- **実機未検証**。立体感・大きさ・怖さは被らないと分からない
