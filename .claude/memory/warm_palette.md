---
name: warm-palette
description: 廻リ視の色は 7 箇所に散っている。暖色へ寄せたときに直した場所と、直さなかった場所の理由
metadata: 
  node_type: memory
  type: project
  originSessionId: e1fa3157-1489-46d2-b4f5-a859929acf84
  modified: 2026-08-12T00:03:46.396Z
---

`canon/LEDGER.md` 0010（2026-08-12「全体的に暖色に寄せてほしい」「黒い箱の光も赤色に」）で
色を触ったときの棚卸し。**色の正本は 1 つではなく、層ごとに別のファイルにある。**

## 直した 7 箇所

| 層 | ファイル | 何を |
|---|---|---|
| 画面全体のグレーディング | `tools/web-compositor/show.json` の `post` | `temperature` −0.18 → **+0.48** / `tint` +0.16（緑）→ **0** / `saturation` 0.35 → **0.52** |
| 封印の箱 | `Assets/Art/Shaders/Intro/SealedBox.shader` | `_GlowColor` 青緑 → **朱** (0.62, 0.135, 0.060)。地と無点灯の線も暖色の暗色へ |
| タイトルの題字 | `Assets/Art/Shaders/Title/TitleGlyph.shader` | `_GlowColor` 青緑 → **灯り** (0.82, 0.52, 0.24)。`_InkColor` も少し暖色へ。**朱の `_AccentColor` は不変** |
| パススルーの輪郭 | `ShowControlClient.ShowIntroDef.edgeColor` ＋ 卓 `intro-model.js` ＋ `capture-server.py` | `#ffffff` → **`#ffcf9e`**（3 者一致を `intro-model.test.mjs` が固定） |
| 導入の構造線 | `IntroStructureWire.cs` の `lineColor` ＋ **`Main.unity` の焼き込み値** | 寒色 → (1.0, 0.66, 0.36, 0.8)。**既定 OFF は変えていない** |
| CG 人形の陰と縁 | `ShowActor.shader` ＋ `Assets/Art/Materials/Cg/ShowActor.mat` | 影 (0.10,0.10,0.12) / 縁 (0.85,0.85,0.90) の**青寄りを外す**。`ShowActor_Ichimatsu.mat` は既にこの値だったので既定側を揃えた |
| 実機へ届く写し | `Assets/StreamingAssets/show/show.json` | 焼き込みなので **`post` を手で同じ値へ**（卓の 📦 が動いていないとき） |

## 直さなかったもの（理由つき）

- **`layout.room.light.tempK`（5000K）** — CG 人形の主光源は**実際の部屋の照明に合わせる**もの。
  画面全体の暖色は post が担当し、post は合成の**後**に掛かるので人形も背景も同じ倍率を浴びる。
  ここを暖色へ振ると人形だけ二重に暖かくなって浮く
- **信号ロストの砂嵐 `_SignalLost`（灰色）** — 装置の故障表示。暖色にすると「演出」に見える
- **隔離殻 `ContainmentShell`（純黒）** — alpha しか書かない。色を持たない
- **StatusHud / ControllerGuidePanel の文字色** — スタッフしか見ない面（`rules/show-design.md` の
  「HMD に出す文言の規約」）。世界観の外なので触っていない

## 罠

- ⚠ **`saturation` を上げないと暖色にならない。** post は 色温度 → コントラスト → 黒浮き →
  **彩度** の順なので、彩度 0.35 は色温度で作った色ずれの 65% を捨てる。実測（`menu composite`）で
  r/b 比 1.02 → 1.19・彩度 6.2% → 15.3%。**輝度は 54.1 → 54.7 でほぼ不変**（暗さは変えていない）
- ⚠ **赤は「暗くなる」と思い込まない。** 同じ RGB の大きさなら赤の輝度は青緑の 1/3 以下だが、
  R を大きく取れば逆に明るくなる。実測で旧の青緑に対し新の朱は**輝度 1.8 倍**
- ⚠ **`Main.unity` に焼かれた色は C# の既定より強い。** `IntroStructureWire.lineColor` は
  SerializeField なので、C# だけ直しても実機の色は変わらない（`unity-prefab-fields` スキル）
- ⚠ **`show.json` を直す前に卓が動いていないか見る** → [[show_json_is_live_config]]。
  今回は 8099 に何も居なかったので直接書いた（`show.json.bak-20260812_warm` に旧版を退避）

関連: [[title_screen]] / [[cg_compositing]] / [[show_json_is_live_config]]
