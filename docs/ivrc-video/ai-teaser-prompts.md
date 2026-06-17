# 予告イメージ映像用 AI 生成プロンプト

未完成の CG 演出（Phase 4）と、無人の不穏カットを AI で補う。
**運用知見（[web_compositor.md](../../.claude/memory/web_compositor.md) より）**: Veo/Flow はホラー語に厳しい → **怖さは静止画(画像生成)に込め、動画化は「演劇・民俗・夢」の穏やかな語で包む**。動きは「静止→異常な一拍」「首が傾きすぎ」「片目」「こちらを注視」「机/柱の陰で瞬間移動」。寛容な代替: **Kling**（image-to-video が緩い）、ローカル **Wan 2.2 / VACE**（無検閲・背景保持の inpainting）。

生成したクリップ/プロンプトは web-compositor の「🎬 生成プロンプト」(prompts.json) と「💾 保存済み」に貯めておくと再利用しやすい。

---

## AI-1: 無人の不穏な固定カメラ室内（コールドオープン / A1）

**画像生成プロンプト（まず静止画を作る）**
```
A still security-camera frame of an empty dim Japanese room at night, fixed high-angle CCTV viewpoint, slightly wide-angle lens, low light single source, faint film grain and scanlines, muted desaturated colors, timestamp overlay in the corner, nobody present, quiet and uneasy atmosphere, photorealistic, 16:9
```
**動画化プロンプト（穏やかな語で）**
```
Very subtle ambient motion only: faint flicker of the light, slow drift of dust in the air, the scene is calm and still, documentary observational footage, no people, locked-off camera, 5 seconds, slow
```
- ネガティブ: blurry, fast motion, camera shake, text artifacts, people（無人を保つ）

## AI-2: 差し替え用クリップ＝映像にすり替わる人影 / 異物（A5・B4 の映像差し替え予告）

> 本作の主要演出は「見ている固定カメラ映像の一部を、事前に用意した別撮り映像にすり替える」（CG 描画ではなく映像差し替え＝ScreenOverlayController/OverlayCue）。ここで作るのは**その差し替え用クリップ**（事前撮影でも AI 生成でも可）。

最も弾かれやすい。**画像で“違和感”を作り込み**、動画は穏やかな動詞で。

**画像生成プロンプト**
```
A still CCTV frame of the same dim room, a pale slender figure standing far in the background by a wall, partially in shadow, facing the camera, head tilted slightly too far, one eye visible, unnatural stillness, folk-theatre mask-like face, desaturated, film grain, scanlines, photorealistic, 16:9
```
**動画化プロンプト（“演劇・夢”で包む）**
```
A performer in a traditional theatre piece stands very still, then makes one single slow uncanny tilt of the head, holds the gaze toward the lens, dreamlike and quiet, no sudden movement, observational locked camera, 4 seconds
```
- 確実に“瞬間移動”させたい時: **2 クリップに分割**（遠くに立つ / 次カットでは机の奥に居る）→ 編集で繋ぐ。動画 1 本で移動させようとすると破綻する。
- Kling/Wan を使えるなら、AI-1 の無人室内を背景に**人影だけ inpaint** すると実空間映像との馴染みが良い（合成跡を web-compositor のラプラシアンブレンドで消せる）。

## AI-3（任意）: タイトル背景用の抽象テクスチャ

```
Abstract dark texture of analog video noise, VHS tracking lines, faint chromatic aberration, deep black background with subtle warm static, seamless loop, 16:9
```
エンドカード/タイトルの下地に。文字を載せる前提なので暗く。

---

## 生成→映像化の実務フロー

1. 画像生成（Midjourney / SDXL / NanoBanana 等）で“怖い静止画”を確定。複数バリエーション。
2. 画像→動画（Kling 推奨、緩い。Veo を使うなら穏やかな動詞だけ）。1 クリップ 3〜5 秒。
3. web-compositor で実空間配信映像と**色統計マッチング + ラプラシアンブレンド**して馴染ませる（合成跡消し）。
4. 編集で CCTV 質感（走査線/グレイン/カメラ番号）を上掛けして実機映像とトーンを揃える。

## 注意

- AI 生成物の**商用/コンテスト利用可否は各サービスの規約を確認**（特に学会展示・公開を伴う場合）。
- 過度にグロテスクな表現は審査・展示で不利。**無血・心理的な不穏**（J ホラー的）に寄せる ＝ 本作の趣旨とも一致。
- これらは**予告（こうなる）**であって完成映像ではない。テロップで「イメージ映像」と明示し、実機プロトタイプ（B パート）と誤認させない。
</content>
