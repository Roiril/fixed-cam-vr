// 固定枠スクリーン用の合成シェーダ。web-compositor (tools/web-compositor) のモデルを移植:
//   スクリーン枠 (Quad) は不変、ソースは contain-fit で letterbox、
//   ライブ映像 × 事前撮影オーバーレイをマスクで合成し、ポスト FX はスクリーン内容にのみかける。
// Transform を映像に合わせて変形させる旧 MjpegScreen.ApplyOrient 方式を置き換える。
Shader "FixedCamVr/ScreenComposite"
{
    Properties
    {
        _LiveTex("Live (MJPEG)", 2D) = "black" {}
        _OverlayTex("Overlay (Pre-recorded)", 2D) = "black" {}
        _MaskTex("Overlay Mask (R, screen space)", 2D) = "black" {}
        _CgTex("CG Layer (RGBA, screen space)", 2D) = "black" {}
        _LiveScale("Live Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _OverlayScale("Overlay Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _CgScale("CG Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        // 実レンズの歪みを CG にも掛けるための係数（ShowCgLayer が較正から供給）。
        //   _CgLens   = (k1, 主点 cx/W, 主点 cy/H, 未使用)
        //   _CgFocalN = (fx/W, fy/H, 0, 0)
        // k1=0 のときは何もしない（較正が無い＝概算姿勢のとき）。
        _CgLens("CG Lens (k1, cxN, cyN, unused)", Vector) = (0, 0.5, 0.5, 0)
        _CgFocalN("CG Focal normalized (fx/W, fy/H)", Vector) = (1, 1, 0, 0)
        // CG を実映像の粗さへ寄せる弱いぼかし（テクセル単位のオフセット。0 = 無効）。
        _CgSoften("CG Soften (texels)", Range(0, 2)) = 0.7
        // 実写だけが受けている「色の粗さ」を CG にも掛ける（JPEG 4:2:0 + 暗所のカラーノイズ抑制）。
        // ShowCgLayer が const から書く（_CgSoften と同じ流儀。現場調整の対象ではない）。
        _CgChromaBlur("CG Chroma Blur (texels)", Range(0, 8)) = 2.2
        _CgChromaGain("CG Chroma Gain", Range(0, 2)) = 0.55
        _UvRotSteps("Live UV Rotation (90deg steps, 0-3)", Float) = 0
        _OverlayStrength("Overlay Strength", Range(0, 1)) = 0
        _CgStrength("CG Layer Strength", Range(0, 1)) = 0
        // 差し替え素材を実写へ寄せる色統計マッチング（Reinhard per-channel を gain/offset へ落としたもの）。
        // 卓が cue 保存時に算出して焼く。既定は恒等（gain=1 / offset=0）。
        _OverlayGain("Overlay Color Match Gain (rgb)", Vector) = (1, 1, 1, 0)
        _OverlayOffset("Overlay Color Match Offset (rgb)", Vector) = (0, 0, 0, 0)
        // スクリーン枠の縦横比 (w/h)。低解像度化のブロックを正方に保つために要る（MjpegScreen が供給）。
        _FrameAspect("Screen Frame Aspect (w over h)", Float) = 1.7778
        [Header(Post FX inside screen)]
        _Exposure("Exposure (EV)", Range(-2, 2)) = 0
        _Contrast("Contrast", Range(0.5, 2)) = 1
        _Saturation("Saturation", Range(0, 2)) = 1
        _Temperature("Temperature", Range(-1, 1)) = 0
        _Vignette("Vignette", Range(0, 1)) = 0
        _Grain("Grain", Range(0, 0.3)) = 0
        _Scanline("Scanline", Range(0, 1)) = 0
        _ScanlineCount("Scanline Count", Float) = 240
        _Lift("Lift (raised black)", Range(0, 0.3)) = 0
        _Tint("Tint (green-magenta)", Range(-1, 1)) = 0
        // 以下 2 つは post 9 項目と同じ「画作り」の仲間だが、色ではなく**サンプル位置**を動かすので
        // 合成の前段（テクスチャを引くとき）に効く。卓の FS_POST も同じ位置・同じ式で実装する。
        _Aberration("Chromatic Aberration", Range(0, 1)) = 0
        _Pixelate("Pixelate (low-res)", Range(0, 1)) = 0
        [Header(Camera feel and echo (out of FS_POST parity))]
        // 「装置らしさ」の系統。post 12 項目とは**別の writer**（CameraFeelFx）が持ち、時間で動く。
        // post を時間の関数にすると shader / FS_POST / common.js / pipeline.js の 4 箇所 × 時間の同期に
        // なり、沈黙した食い違いが必ず出る（機械テストが無い）。だから別系統にしてある。
        //
        // 現行の加工はすべて全域一様で、すべてに物理的な言い訳（機材のせい）が付く。だから安全で、
        // だから怖くない。ここに足すのは「言い訳が破れる」ための道具立て。
        _NoiseDark("Read Noise (sensor floor)", Range(0, 0.5)) = 0
        _NoiseFixed("Fixed Pattern Noise", Range(0, 0.3)) = 0
        // 粒が動く時刻。**VR の 90Hz ではなく映像が更新されたときだけ変わる**（CameraFeelFx が
        // 受信フレーム番号を書く）。_Time で動かすと、カメラが 15fps のときでも粒だけ 90Hz でざわつき、
        // 「映像の上に別の層が乗っている」と読まれる（＝加工者の指紋）。
        _SrcFrame("Source Frame Id (noise clock)", Float) = 0
        // レンズの内面反射（ベイリンググレア）。明るい所の光が画全体へ薄く回り、
        // 暗い所のコントラストを奪う。**安いレンズほど強い**ので、監視カメラの画では
        // 光源のまわりが必ず滲む。mip があるので広いぼかしが 1 タップで得られる。
        _Glare("Veiling Glare (lens flare bloom)", Range(0, 1)) = 0
        // 周を重ねるごとに色が抜けて夜間モードへ落ちる。**解像度の劣化と同じ進み**（0 → 1）で、
        // 3 周目の A で 1 ＝ 完全な無彩（`canon/LEDGER.md` 0019）。
        // 色を抜くだけでなく、赤外の夜間モードらしさ（照らされた範囲だけが残る・増感で粒が増える・
        // 中間調が減って白と黒になる）を同じ 1 本から派生させる。別 uniform に分けると、
        // 片方だけ動いた絵（色は残っているのに粒だけ多い等）が作れてしまう。
        _Mono("Night Mode (0=color, 1=IR monochrome)", Range(0, 1)) = 0
        [Header(CRT tube shape)]
        // ブラウン管の見た目。**形（曲面）はメッシュが持つ**（CrtScreenMesh）ので、ここは
        // 面の中で完結するものだけ — 角の丸みと、管の縁が落ちる暗さ。
        _CrtRound("CRT Corner Round", Range(0, 0.5)) = 0
        _CrtEdge("CRT Edge Darkness", Range(0, 1)) = 0
        _CrtEdgeWidth("CRT Edge Width", Range(0.01, 0.5)) = 0.16
        // 管が点く（導入）。**0 = 消えている / 1 = 点いている**。
        // 途中は「縁が光る → 中央から水平に開く → 面が満ちる → 行き過ぎて落ち着く」。
        // ⚠ 既定 **1**（点いている）。0 を既定にすると、この uniform を書かない場面
        //   （本編・終幕・卓のプレビュー・Editor の合成プレビュー）で画がまるごと消える。
        _CrtIgnite("CRT Ignition (0=off, 1=on)", Range(0, 1)) = 1
        // 映像そのものの出方（導入の段 4）。**管が点くこととは別**。
        // ⚠ 分けていないと「管が点き切った瞬間に映像も出る」ことになり、重み `live` が
        //   0 のままなのに画には映像が出る ＝ 重みと画が食い違う（2026-08-13 に絵で見つけた）。
        // ⚠ 既定 **1**。0 を既定にすると、この uniform を書かない場面で画がまるごと消える。
        _IntroLive("Intro Live (0=dark tube, 1=image)", Range(0, 1)) = 1
        // 終幕。**装置に届いている電力**（canon/LEDGER.md 0048「電池が切れかけみたいな感じで
        //   だんだんとちかちかしながら消えていき」）。画の**いちばん最後**に掛ける ＝
        //   映像も砂嵐も管の縁も一緒に落ちる（電池が切れるのは画の一部ではなく装置そのもの）。
        // ⚠ 既定 **1**（点いている）。0 を既定にすると、この uniform を書かない場面
        //   （本編・導入・卓のプレビュー・Editor の合成プレビュー）で画がまるごと消える。
        _ScreenPower("Screen Power (1=on, 0=dead)", Range(0, 1)) = 1
        // 終幕: ブラウン管の電源断の進み（0 = ふつうの画 / 1 = 点が消え切った）。
        // 形（潰れる → 縮む → 消える の割合・線の太さ・明るさの上限）は **このシェーダが持つ**。
        // `OutroLogic.ScreenCollapse` が出すのは進みだけ（数字を 2 か所に書かない）。
        // ⚠ 既定 **0**（恒等）。`_ScreenPower` とは逆向きなので取り違えないこと。
        _ScreenCollapse("Screen Collapse (0=normal, 1=gone)", Range(0, 1)) = 0
        // 暗部の色を殺す量。安い ISP はノイズリダクションで**暗い所の色差から捨てる**ので、
        // 一様な脱色ではなく「明るい所に色が残り、暗がりが無彩へ落ちる」形になる。
        _ChromaKill("Dark Chroma Kill (ISP noise reduction)", Range(0, 1)) = 0
        // 周を重ねるごとに落ちていく解像度（canon/LEDGER.md 0012）。**枠を横切るブロック数**をそのまま受ける。
        //   0 = 量子化しない（今までと 1 ビットも変わらない画）
        // 0..1 → ブロック数の対応表は **C# の ScreenDecayLogic にしかない**。ここにも式を置くと、
        // テレメトリが読む値と画が黙って食い違う（この codebase が何度も踏んだ型）。
        _CoarseBlocks("Coarse (blocks across frame, 0=off)", Float) = 0
        _ExposureBias("Exposure Bias (EV, AGC lag)", Range(-2, 2)) = 0
        // 人形に付き従う劣化。(中心 u, 中心 v, 半径, 強さ)。ShowCgLayer が人形の投影から供給する。
        // 対象に紐づく非一様な乱れ＝機材のせいにできない＝原因が世界の側にあることになる。
        _ActorFocus("Actor Focus (cx, cy, radius, amount)", Vector) = (0.5, 0.5, 0.2, 0)
        // 焼き付き / ホールド。指定した瞬間の画を 1 枚だけ保持して混ぜる（フレーム履歴は持たない
        // ＝決定的で、同じ show.json は同じ絵になる）。1.0 で完全に止まって見える。
        // ソースはライブ映像と同じなので contain-fit も _LiveScale を共用する。
        _EchoTex("Echo (frozen frame)", 2D) = "black" {}
        _Echo("Echo Mix", Range(0, 1)) = 0
        [Header(OSD (the device clock))]
        // スクリーン左上の日付と時刻（`canon/LEDGER.md` 0108）。**唯一の writer は ScreenOsd**。
        // 虚構上は**カメラではなく観測装置（画面の側）が打っている時計**なので、
        // 映像に起きること（切替・差し込み・録画・乱れ・劣化・信号断）では 1 ビットも変わらず、
        // 装置に起きること（切替の黒・管の縁・終幕の電力）は一緒に受ける。
        //   _OsdTex     = いまの時刻を敷き直した 1 枚（straight alpha・生成りの字＋暗い縁）
        //   _OsdRect    = 枠 UV の矩形 (x, y, w, h)。**w が 0 なら 1 画素も触らない**
        //                 ＝ この uniform を書かない場面（卓・各プレビュー・旧シーン）は従来と同じ絵
        //   _OsdOpacity = 不透明度
        _OsdTex("OSD Text", 2D) = "black" {}
        _OsdRect("OSD Rect (x, y, w, h in frame uv)", Vector) = (0, 0, 0, 0)
        _OsdOpacity("OSD Opacity", Range(0, 1)) = 1
        [Header(Switch and Signal FX (out of FS_POST parity))]
        // ↓ これらは web-compositor の FS_POST 一致規約の対象外（別系統 uniform）。
        //   dip-to-black（切替演出）と信号ロスト（配信断のフェイルソフト＝砂嵐）を post FX の後段にかける。
        _SwitchDim("Switch Dip Dim", Range(0, 1)) = 0
        _SignalLost("Signal Lost (static)", Range(0, 1)) = 0
        // 砂の下に**画が 1 枚も無いか**（1 = 無い）。SignalLostFx が唯一の writer。
        // 砂は掛け算で乗るので、下に画が無いと真っ黒に掛かって何も見えなくなる。
        // カメラが 1 台も繋がっていない現場はこの砂だけで体験が流れる（canon/LEDGER.md 0025）ので、
        // そのときだけ地を持ち上げる。⚠ **既定は 1**（書き忘れたら明るい側へ倒す）。
        _SignalFloor("Signal Lost: nothing under the sand", Range(0, 1)) = 1
        // 演出としての「映像の乱れ」。障害表示（_SignalLost）とは所有者も意味も別。
        //   _Glitch     = 強さ (0-1)。GlitchFx が唯一の writer。
        //   _GlitchSeed = 時間シード (秒)。_Time に依らないので Editor プレビューで再現できる。
        _Glitch("Glitch (staged tearing)", Range(0, 1)) = 0
        _GlitchSeed("Glitch Seed (seconds)", Float) = 0
        [Header(Split screen (mirror and dual overlay))]
        // 画面を縦に割って、左右で別のものを出す（canon/LEDGER.md 0050）。
        // 3 周目 A の「左＝左右反転したライブ / 右＝ライブ」と、
        // 4 周目 A の「左＝大量の人形 / 右＝体験者人形＋環境」の両方がこれに乗る。
        //   _SplitX          分割位置（0 = 分割なし）。境目は環境の縦線（カーテンの合わせ目）へ置く
        //   _SplitFlipLeft   左側だけ左右反転して読む。**環境が左右対称なカメラでしか成立しない**
        //   _SplitFreezeLeft 左側だけ _EchoTex へ寄せる（そこだけ画が止まる）
        _SplitX("Split X (0=off)", Range(0, 1)) = 0
        _SplitFlipLeft("Split: mirror left half", Range(0, 1)) = 0
        _SplitFreezeLeft("Split: freeze left half", Range(0, 1)) = 0
        // 第 2 の差し替え層。**左と右へ別の素材を同時に置くために要る**（1 枚では片方しか置けない）。
        _Overlay2Tex("Overlay 2", 2D) = "black" {}
        _Mask2Tex("Overlay 2 Mask (R, screen space)", 2D) = "black" {}
        _Overlay2Scale("Overlay 2 Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _Overlay2Strength("Overlay 2 Strength", Range(0, 1)) = 0

        [Header(Swap morph (visitor unravels into scanlines))]
        // 入れ替わりのほどけ（canon/LEDGER.md 0089 / 0090）。全画面ではなく
        // **映像の中の体験者だけ**が水平の細い線にほどけ、もつれ、人形へ吸い込まれて晴れる。
        // 線は描き足すのではなく**下の映像を行ごとに水平へ引き伸ばして**作る。
        //   _SwapRect    (cx, cy, halfHeight, active) — 人型の投影中心と見かけの半高（枠 UV 空間）
        //   _SwapCover   ほどけの前線 0..1
        //   _SwapKnot    もつれが塊になっている度合い 0..1（1 で下を完全に隠す）
        //   _SwapThread  体の外へ流れ出た糸の量 0..1
        //   _SwapReal    CG が実体として見えている度合い 0..1
        //   _SwapFromTop 1 = 頭からほどける / 0 = 足元から
        // 唯一の writer は SwapMorphFx（_Glitch を GlitchFx が独占するのと同じ流儀）。
        _SwapRect("Swap Rect (cx, cy, halfH, active)", Vector) = (0.5, 0.5, 0.2, 0)
        // ⚠⚠ **ほどけ始めた瞬間の矩形**。映像の中の人は縮まないので、マスクは**この枠で引く**
        //   （`_SwapRect` は縮む段で小さくなる。そちらで引くと人型が縮むたびにマスクが外れる）。
        _SwapRect0("Swap Rect At Begin (cx, cy, halfH)", Vector) = (0.5, 0.5, 0.2, 0)
        // 映像の中の人を拾う無人プレート（そのカメラで撮った、誰も居ない画）。
        _SwapMaskTex("Swap Mask Plate", 2D) = "black" {}
        // x = 使うか / y,z = 差の下限・上限 / w = 差を取る mip の段（粗くするほどノイズが消える）
        _SwapMask("Swap Mask (on, lo, hi, lod)", Vector) = (0, 0.060, 0.20, 0.6)
        _SwapCover("Swap Cover", Range(0, 1)) = 0
        _SwapKnot("Swap Knot", Range(0, 1)) = 0
        _SwapThread("Swap Thread", Range(0, 1)) = 0
        _SwapReal("Swap Real", Range(0, 1)) = 0
        _SwapFromTop("Swap Unravels From Top", Range(0, 1)) = 1
        // ⚠ **線の色を作るプロパティは置かない**（2026-08-19 に `_SwapInk` を削除）。
        //   参考画像の線の明暗はその画素が元々どこの色だったかで決まっていて、乱数ではない。
        //   濃淡を振るのは「元の映像を捨てて縞を描き足す」のと同じことだった。
        // 帯の刻み。x = **枠の縦**に対する行数（＝ 1 帯が何画素か。48 行 ＝ 実機で 15〜20 画素）。
        // ⚠⚠ **細くしない**（2026-08-20・ユーザーが参考画像を送ってきた）。320 行（3 画素）は
        //   走査線であって、参考画像の**太い筆で引いた横棒**にはならない。
        //   ⚠ 行数を変えたら `SwapWaveExt` の周波数も対で直す（帯あたりの周期が変わる）。
        // **y / z / w はもう読んでいない**（2026-08-21 に棒を「体の実範囲 ＋ 端の波」へ
        // 作り直し、中心を泳がせる方式を廃止した — 泳いだ分だけ体が覆いから外れていた）。
        // ⚠ 行数は**映像の粗さと独立**でよい（走査線は装置が走査した跡で、映像の内容ではない）。
        _SwapSmear("Swap Smear (rows, unused, unused, unused)", Vector) = (48, 0.0, 0.26, 0)
        // ⚠ **もう読んでいない**（2026-08-20 に線の矩形波を、2026-08-21 にぼかし読みも廃止した。
        //   マスクのぼかしは `_SwapMask.w` が持つ）。既存マテリアルの値を壊さないため宣言だけ残す。
        _SwapLine("Swap Line (unused)", Vector) = (0.22, 0.12, 0.26, 0.8)
        // 糸のうねりの時計（秒）。**_Time を使わない** — Editor のプレビューは 1 エディタフレームの中で
        // 何コマも描くので、_Time だと連番 PNG の糸が全コマ同じになる（_GlitchSeed と同じ理由）。
        _SwapSeed("Swap Seed (seconds)", Float) = 0
        // 黒い波（`reports/2026-08-21_swap-wave-design.html`）。**唯一の writer は SwapMorphFx**、
        // 値の正本は `SwapWaveLogic` / `SwapEnergyLogic` の const（数値を 2 か所に置かない）。
        //   x = 走る山の中心（figure y。-1 足元 / +1 頭）
        //   y = 山の振幅（figure 単位）／ z = 針の強さ ／ w = 稀に跳ぶ帯の強さ
        // ⚠ すべて 0 で**従来の絵に戻る**（層を切って焼く `-Set layers=` はこれで効く）。
        _SwapWave("Swap Wave (crestY, crestAmp, needle, spike)", Vector) = (0, 0, 0, 0)
        //   x = 入れ替わりが始まってからの秒 ／ y = キメの一拍 1..0 ／ z = 晴れる段の進み 0..1
        //   w = 基本の波へ掛ける倍率（全身のエネルギー。**既定 1**）
        _SwapWave2("Swap Wave 2 (elapsed, beat, clear, ampMul)", Vector) = (0, 0, 0, 1)
        // 手のホットスポット（設計 F）。位置は**実寸の人の figure 空間**（`_SwapRect0` 側 = `pM`）。
        //   xy = 中心 ／ z = 半径 ／ w = 結合済みの強さ（0 = 使わない）
        _SwapHotA("Swap Hot A (px, py, r, amount)", Vector) = (0, 0, 0, 0)
        _SwapHotB("Swap Hot B (px, py, r, amount)", Vector) = (0, 0, 0, 0)
        // 持続の覆い（`canon/LEDGER.md` 0102）。**唯一の writer は SwapMorphFx**。
        //   _SwapMinX     覆いを効かせる左端（枠 UV・0 = 制限しない）。左右分割の右側だけに効かせる
        //   _SwapDiffHold 1 = 覆い切っていても差分を読み続ける（映像にまだ当人が写っている）
        _SwapMinX("Swap Min X (0=off)", Range(0, 1)) = 0
        _SwapDiffHold("Swap Diff Hold", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "ScreenComposite"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LiveTex);    SAMPLER(sampler_LiveTex);
            TEXTURE2D(_SwapMaskTex); SAMPLER(sampler_SwapMaskTex);
            TEXTURE2D(_OverlayTex); SAMPLER(sampler_OverlayTex);
            TEXTURE2D(_MaskTex);    SAMPLER(sampler_MaskTex);
            // CG レイヤ（実カメラの双子の仮想カメラが描く人形）。スクリーン空間・アルファ = 被覆率。
            TEXTURE2D(_CgTex);      SAMPLER(sampler_CgTex);
            // 焼き付き / ホールド用に凍らせた 1 枚（CameraFeelFx が Graphics.CopyTexture で作る）。
            TEXTURE2D(_EchoTex);    SAMPLER(sampler_EchoTex);
            // 第 2 の差し替え層（左右分割で片側だけ別の素材を出す）。
            TEXTURE2D(_Overlay2Tex); SAMPLER(sampler_Overlay2Tex);
            TEXTURE2D(_Mask2Tex);    SAMPLER(sampler_Mask2Tex);
            // 装置が打っている時計（ScreenOsd が秒ごとに敷き直す 1 枚）。
            TEXTURE2D(_OsdTex);      SAMPLER(sampler_OsdTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _LiveScale;
                float4 _OverlayScale;
                float4 _CgScale;
                float4 _CgLens;
                float4 _CgFocalN;
                float4 _CgTex_TexelSize;   // Unity が自動で埋める (1/w, 1/h, w, h)
                float4 _LiveTex_TexelSize; // 同上。**ソースの実寸**が要る（粒をソース画素で刻むため）
                float4 _OverlayGain;
                float4 _OverlayOffset;
                float4 _Overlay2Scale;
                float _Overlay2Strength;
                float4 _SwapRect;
                float4 _SwapRect0;
                float4 _SwapMask;
                float4 _SwapSmear;
                float4 _SwapLine;
                float4 _SwapWave;
                float4 _SwapWave2;
                float4 _SwapHotA;
                float4 _SwapHotB;
                float _SwapMinX;
                float _SwapDiffHold;
                float _SwapCover;
                float _SwapKnot;
                float _SwapThread;
                float _SwapReal;
                float _SwapFromTop;
                float _SwapSeed;
                float _SplitX;
                float _SplitFlipLeft;
                float _SplitFreezeLeft;
                float _FrameAspect;
                float _CgSoften;
                float _CgChromaBlur;
                float _CgChromaGain;
                float _UvRotSteps;
                float _OverlayStrength;
                float _CgStrength;
                float _Exposure;
                float _Contrast;
                float _Saturation;
                float _Temperature;
                float _Vignette;
                float _Grain;
                float _Scanline;
                float _ScanlineCount;
                float _Lift;
                float _Tint;
                float _Aberration;
                float _Pixelate;
                // FS_POST 一致規約の対象外（別系統）。CameraSwitchDirector / SignalLostFx / GlitchFx が駆動。
                float4 _OsdRect;
                float _OsdOpacity;
                float _SwitchDim;
                float _SignalLost;
                float _SignalFloor;
                float _Glitch;
                float _GlitchSeed;
                // 同じく別系統。CameraFeelFx（撮像の質と残像）と ShowCgLayer（_ActorFocus）が駆動。
                float4 _ActorFocus;
                float _NoiseDark;
                float _NoiseFixed;
                float _SrcFrame;
                float _ChromaKill;
                float _Glare;
                float _Mono;
                float _CrtRound;
                float _CrtEdge;
                float _CrtEdgeWidth;
                float _CrtIgnite;
                float _IntroLive;
                float _ScreenPower;
                float _ScreenCollapse;
                float _ExposureBias;
                float _Echo;
                float _CoarseBlocks;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            // 90 度単位の UV 回転（中心まわり）。streamer が正立フレームを送る前提なので
            // 通常 0。緊急時の手動補正用。
            float2 RotateUvSteps(float2 uv, float steps)
            {
                float2 p = uv - 0.5;
                int s = (int)round(steps) & 3;
                if (s == 1) p = float2(-p.y, p.x);
                else if (s == 2) p = -p;
                else if (s == 3) p = float2(p.y, -p.x);
                return p + 0.5;
            }

            // contain-fit: scale<1 の軸を縮めてソース全体を枠内に収める。
            // 枠外 (uv が [0,1] を出る) は inside=0 → 黒 letterbox。
            float2 ContainUv(float2 uv, float2 scale, out float inside)
            {
                float2 s = max(scale, 1e-4);
                float2 p = (uv - 0.5) / s + 0.5;
                float2 ok = step(0.0, p) * step(p, 1.0);
                inside = ok.x * ok.y;
                return saturate(p);
            }

            // CG レイヤを実映像の粗さへ寄せる 9-tap tent ぼかし。
            //
            // CG はピンホールで完璧に鮮鋭だが、実映像は JPEG q40 で高周波が落ちている。RT を映像実寸まで
            // 下げたうえで、さらに少しだけ鈍らせないと**輪郭のクッキリ度が食い違って「貼り付けた絵」に見える**。
            // 合成物が偽物に見える最大の要因のひとつが「camera / lens settings の不整合」で、その一番安い対処。
            //
            // ⚠ premultiplied なので **rgb と a を同じ重みでぼかす**。別々にぼかすと rgb > a の画素ができて
            //    人形の縁に明るい滲みが出る（over 合成の前提が壊れる）。
            half4 SampleCgTent(float2 uv, float texels)
            {
                half4 c = SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv);
                if (texels <= 0.001) return c;
                float2 t = _CgTex_TexelSize.xy * texels;
                half4 s = c * 4.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x, 0.0)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x, 0.0)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(0.0,  t.y)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(0.0, -t.y)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x,  t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x, -t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x, -t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x,  t.y));
                return s * (1.0 / 16.0);
            }

            /// 人形にも**映像と同じ伝送の痩せ**を掛ける。装置を通して見えている以上、同じだけ落ちる。
            ///
            /// ⚠⚠ **人形の RT は mip を使えない**（MSAA 4x で描いており、手動 Render の経路では
            ///   自動生成が走らず、`GenerateMips()` も「自動生成に任せろ」と拒否される）。
            ///   だから映像側の LOD をぼかし幅へ翻訳する — LOD n は 2^n テクセルの箱平均に相当し、
            ///   その実効半径は 2^n / 2。9-tap tent の幅をそこへ合わせれば同じだけ鈍る。
            ///   **タップ数は増えない**（オフセットが広がるだけ）。
            /// ⚠ 渡し忘れると**人形だけ鮮明**になり、そこだけ別の層に見える（2026-08-12 の実害）。
            float CgSoftenTexels(float lod)
            {
                return _CgSoften + max(exp2(lod) - 1.0, 0.0) * 0.5;
            }

            half4 SampleCgSoft(float2 uv, float lod) { return SampleCgTent(uv, CgSoftenTexels(lod)); }

            /// 枠 UV → CG レイヤの UV。**contain-fit ＋ 実レンズの歪み**を通す。
            ///
            /// 実レンズの歪みを CG にも掛ける。CG はピンホール、実映像は樽型に歪んでいるので、
            /// 掛けないと画面の端で必ずずれる（広角ほど大きい）。
            /// **除算モデル**（Fitzgibbon）: r_u = r_d / (1 + k1 r_d^2)。多項式モデルと違って
            /// 順・逆の両方に解析解があるので、卓の順投影（較正・ワイヤー重畳）とこの逆変換が
            /// **厳密に一致する**。互いに近似の逆だと画面端で食い違い、較正の検証が成立しない。
            ///
            /// ⚠ 入れ替わりのスメアも**この関数で人型マスクを引く**。写経した変換を別に持つと、
            ///   歪みや contain-fit を直したときに片方だけ取り残されて黙って食い違う。
            float2 CgUvOf(float2 uv, out float inside)
            {
                float2 uvC = ContainUv(uv, _CgScale.xy, inside);
                if (abs(_CgLens.x) > 1e-5)
                {
                    float2 n = (uvC - _CgLens.yz) / max(_CgFocalN.xy, 1e-4);
                    uvC = _CgLens.yz + n / (1.0 + _CgLens.x * dot(n, n)) * _CgFocalN.xy;
                }
                return uvC;
            }

            // 実写だけが受けている「色の粗さ」を CG にも掛ける。
            //
            // 実写は JPEG 4:2:0 で**色差が半解像度**、そのうえ q40 の粗い量子化と暗所のカラーノイズ抑制で
            // 色がにじみ、彩度そのものも落ちている。CG はどれも受けていないので、同じ絵の中で
            // 人形だけ色が鮮鋭で濃い。実測（2026-08-07・無人プレート 3 台）で人形の胴の彩度は
            // 周囲の **3〜4 倍**あった。post の彩度は乗算なので比が保存され、**原理的に埋まらない**。
            //
            // 輝度は鮮鋭なまま、色差だけ広く均して倍率を掛ける。premultiplied のまま平均して a で割ると
            // 「a で重み付けした平均色」になるので、透明な周囲の色に引っ張られない。
            half3 CgChromaMatched(half4 cg, float2 uv, float lod)
            {
                if (cg.a <= 0.002) return cg.rgb;
                half3 sharp = cg.rgb / max(cg.a, 1e-4);
                half yS = dot(sharp, half3(0.299, 0.587, 0.114));
                half3 chroma = sharp - yS;
                if (_CgChromaBlur > 0.001)
                {
                    half4 wide = SampleCgTent(uv, _CgChromaBlur + CgSoftenTexels(lod));
                    half3 blur = wide.rgb / max(wide.a, 1e-4);
                    chroma = blur - dot(blur, half3(0.299, 0.587, 0.114));
                }
                return max(yS + chroma * _CgChromaGain, 0.0) * cg.a;
            }

            /// ブラウン管の面。**角丸矩形の符号付き距離**（負 = 内側 / 0 = 縁）。
            /// 枠のアスペクトを掛けて、角の丸みが縦横で同じ半径になるようにする
            /// （掛けないと 16:9 では横に伸びた楕円の角になる）。
            float CrtSdf(float2 uv)
            {
                float2 p = (uv - 0.5) * 2.0;                 // 中心 0 / 縁 ±1
                p.x *= max(_FrameAspect, 1e-3);
                float2 half = float2(max(_FrameAspect, 1e-3), 1.0);
                float r = _CrtRound * min(half.x, half.y);
                float2 q = abs(p) - (half - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            // ================= 終幕: ブラウン管の電源断 =================
            //
            // 逐語は `canon/LEDGER.md` 0111、設計は `reports/2026-08-23_outro-redesign.html`。
            // 画が縦に潰れて横一本の線になり、線が中央へ縮んで点になり、残光が消える。
            //
            // ⚠⚠ **形の寸法はここが正**（C# は進み `_ScreenCollapse` だけを出す）。
            //   2 か所に数字を書くと、片方だけ直したときに黙って食い違う。
            // ⚠⚠ **一方向。往復も反復もしない。** 反復する明滅は原理的に終端に読めないうえ、
            //   6〜24Hz は光過敏の危険帯（0111 で廃止した旧 `Flicker` がそれだった）。
            // ⚠ **線が育つ向きの動きを 1 フレームも入れない** — 逆向きは「起動しかけ」に反転する。

            // 潰れの内訳（進みに対する割合）。合計 1.0。
            #define COLLAPSE_SQUEEZE_END 0.389   // ここまでで縦が潰れて線になる（0.9s なら 0.35s）
            #define COLLAPSE_SHRINK_END  0.722   // ここまでで線が横に縮んで点になる（同 0.30s）
                                                 // 残り（同 0.25s）は残光が消える
            // 線の太さ・点の幅（枠に対する割合）
            #define COLLAPSE_LINE_H  0.006
            #define COLLAPSE_POINT_W 0.004

            /// 潰れたぶんの明るさの上限。
            ///
            /// ⚠⚠ **エネルギー保存にしない。** 1/0.006 ＝ 167 倍になり、暗所の VR で
            /// 視界の中心に閃光を置くことになる（ユーザーの「目に悪い」と同じ軸の問題を作る）。
            ///
            /// ⚠⚠ **1.25 は「白飛びさせない」から決めた値**（`menu outro` の実測・2026-08-23）。
            /// 素の画の尖頭は 0.797 なので、1.25 を超えたぶんは 1.0 に飽和する ＝
            /// **装置が元々持っていない光を演出が作る**ことになる。2.5 で焼いたときは
            /// 白飛びが 0 px → 537 px（画面の 0.10%）へ増えた。
            ///
            /// ⚠ **効いているのは post であってプレートではない。** 同日にカメラを据え直した
            /// 別構図のプレートで測り直しても尖頭は **0.797 のまま**だった（post の露出・
            /// コントラストが天井を決めているため）。⇒ **プレートが変わっても測り直しは要らないが、
            /// `show.json` の global post を変えたら測り直す。**
            /// ⚠ 暗所の実機で眩しくないかは、被って 1 度見るまで確定しない。
            #define COLLAPSE_GAIN_MAX 1.25

            /// 電源断の逆写像とマスク。**c=0 のとき厳密に恒等**（本編・導入は 1 ビットも変わらない）。
            ///
            /// ⚠ 分岐は uniform 同士の比較なので画面全体で一様 ＝ 暗黙の微分は壊れない。
            /// ⚠ マスクは **画面座標（rawUv）で測る** — 潰れた座標で `fwidth` を取ると
            ///   1/squeeze で発散して、線そのものが黙って消える。
            void CollapseScreen(float c, float2 rawUv, out float2 uv, out float mask, out float gain)
            {
                uv = rawUv;
                mask = 1.0;
                gain = 1.0;
                if (c <= 0.0001) return;

                // ① 縦が潰れる → ② 横が縮む → ③ 残光が消える
                float sq = 1.0 - smoothstep(0.0, COLLAPSE_SQUEEZE_END, c) * (1.0 - COLLAPSE_LINE_H);
                float sh = 1.0 - smoothstep(COLLAPSE_SQUEEZE_END, COLLAPSE_SHRINK_END, c)
                                 * (1.0 - COLLAPSE_POINT_W);
                float fade = 1.0 - smoothstep(COLLAPSE_SHRINK_END, 1.0, c);

                // 画面 → 元の画。潰れた帯の外は範囲外へ出る（そこはマスクが切る）。
                uv = 0.5 + (rawUv - 0.5) / float2(max(sh, 1e-4), max(sq, 1e-4));

                float2 d = abs(rawUv - 0.5);
                float aa = fwidth(rawUv.y) + 1e-5;
                mask = (1.0 - smoothstep(0.5 * sq - aa, 0.5 * sq + aa, d.y))
                     * (1.0 - smoothstep(0.5 * sh - aa, 0.5 * sh + aa, d.x));

                // 潰れたぶん明るい。**上限つき、しかも縮む段では落とす**
                // （尖頭を保持すると暗所で残像が残る。実物の電源断も線は一瞬で暗くなる）。
                float squeezeGain = min(1.0 / max(sq, 1e-4), COLLAPSE_GAIN_MAX);
                gain = lerp(squeezeGain, 1.0,
                            smoothstep(COLLAPSE_SQUEEZE_END, COLLAPSE_SHRINK_END, c)) * fade;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            /// なだらかな雑音（格子の間を補間する）。**燐光の焼けムラ**に使う。
            /// ⚠ `Hash21` を直接使うと格子のブロックが見える（それは「わざと加工した感」の側）。
            float ValueNoise21(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            /// 管が点いていく途中の「電子が当たっている量」。
            ///
            /// ⚠ **チャンネルごとに少しずらして呼ぶ**と、実物の CRT と同じ収束のずれ
            /// （縁で色が割れる）になる。単色の板に見えないための仕掛けのひとつ
            /// （`canon/LEDGER.md` 0030「単色なのもなんか怖くないというかリアルじゃない」）。
            float CrtIgniteEnergy(float2 uv, float t)
            {
                float d = CrtSdf(uv);
                float2 p = (uv - 0.5) * 2.0;

                // 1) 縁に高電圧が乗る（0.00〜0.30 で立ち、0.65 までに引く）
                float rim = saturate(t / 0.30) * (1.0 - saturate((t - 0.30) / 0.35));
                float e = exp(-abs(d) * 22.0) * rim;

                // 2) 走査が中央から水平に開く（0.25〜0.78）。**ゆっくり開く** —
                //    速いと「光った」になり、管が立ち上がる過程が読めない
                float open = saturate((t - 0.25) / 0.53);
                float band = 1.0 - smoothstep(0.0, max(open * open, 1e-3), abs(p.y));

                // 3) 面が満ちるにつれて走査の帯は消える（映像が来る前の間を持たせる）
                float fill = saturate((t - 0.55) / 0.35);
                return e + band * open * (1.0 - fill) * 0.42;
            }

            /// 燐光の色。**強さで色が変わる。** 電子が届き切っていない所は深い赤、
            /// 強く当たっている所だけ白へ寄る（実物の燐光と同じ）。
            /// ⚠ ここを 1 色で書くと「明るい単色の板」になる。
            half3 PhosphorColor(float e)
            {
                return lerp(half3(0.52, 0.115, 0.038), half3(1.0, 0.84, 0.62), saturate(e));
            }

            /// 信号ロストの砂（`canon/LEDGER.md` 0131）。
            ///
            /// ⚠⚠ **置き換えではなく掛け算**なので、`SAND_LO` と対になる上限は
            /// `2 - SAND_LO` として式の中で導く。**平均を 1.0 に保つのがこの演出の芯**で、
            /// 片方だけ手で書くと平均がずれ、直したはずの「映像との段差」がそのまま戻る。
            ///   0.18 ⇒ 粒ごとの振れは 0.18〜1.82 倍（約 10 倍）。砂には見えるが平均は動かない
            #define SAND_LO 0.18

            /// 砂の地（`PhosphorColor(0.55)` に掛ける）。**足す。`max` で切らない。**
            /// `max` で切ると、下限より暗い所が全部ひとつの値へ潰れて**映像の暗部が丸ごと消える**
            /// （実測: 暗い部屋のプレートで面がまるごと平らな茶色になった。2026-09-03 に絵で見つけた）。
            ///
            /// 2 つあるのは、**砂の下に画があるかどうかで要る明るさが違う**から。
            ///   LIVE = 画がある（最後のフレームが残っている）。ほとんど足さない ＝ 段差が出ない
            ///   DEAD = 画が 1 枚も無い（0025 の現場）。この砂だけで体験が流れるので消えない明るさ
            /// linear で約 0.008 と 0.084（sRGB で約 21 と 82）。切り替えるのは `_SignalFloor`。
            #define SAND_FLOOR_LIVE 0.015
            #define SAND_FLOOR_DEAD 0.15

            /// 受け側で乗る粒（平均 0 の**足し算**）。掛け算だけだと、暗い所は
            /// 「暗いものに 10 倍の幅を掛けても暗いまま」なので**映像がそのまま読める**
            /// （2026-09-03 に絵で見つけた。掛け算だけの版は砂ではなく「粒の乗った映像」だった）。
            /// 実物の受信機の雑音も信号の大きさに依らない加算なので、**暗い所ほど強く出る**のが正しい。
            /// ⚠ 平均 0 なので、足しても画の明るさは動かない（黒で切られるぶんだけ僅かに上がる）。
            #define SAND_ADD 0.030

            /// 砂の色。**明るさ 1.0 の暖色**（`PhosphorColor(0.62)` を輝度で割ったもの）。
            /// 掛けても輝度が動かないので、上の「平均 0」を壊さずに色だけ付く。
            #define SAND_TINT half3(1.346, 0.928, 0.690)

            /// 粒の格子と刻み。**格子で切らないと 1 画素ごとに散り、頭が動くたびに沸く。**
            /// 横をわずかに長く取る（走査は水平に帯域が延びるので、実物の砂も縦より横が広い）。
            #define SAND_CELL_X 210.0
            #define SAND_CELL_Y 150.0
            #define SAND_HZ     30.0

            /// 粗さの出どころは 2 つあり、**粗い方だけ**を掛ける（2 つの格子が干渉すると縞が出る）。
            ///   _Pixelate     著作した値（post 12 項目。卓の FS_POST と同式・硬い格子のまま）
            ///   _CoarseBlocks 周を重ねるごとに痩せる伝送（別系統。C# の ScreenDecayLogic が解いたブロック数）
            /// 返すのは「_CoarseBlocks 側を使うか」。使うならブロック数を <paramref name="blocks"/> へ。
            bool PickCoarse(out float blocks)
            {
                blocks = 0.0;
                float pix = _Pixelate > 0.001 ? max(6.0, floor(lerp(400.0, 18.0, saturate(_Pixelate)))) : 0.0;
                float cb  = _CoarseBlocks > 0.5 ? max(6.0, floor(_CoarseBlocks)) : 0.0;
                if (cb <= 0.0) return false;
                if (pix > 0.0 && pix <= cb) return false;   // 著作した方が粗いならそちらに譲る
                blocks = cb;
                return true;
            }

            // 著作した低解像度化。**サンプル位置の量子化**（硬い格子）。卓の FS_POST と同じ絵にする側。
            float2 PixelateUv(float2 uv)
            {
                if (_Pixelate <= 0.001) return uv;
                float bx = max(6.0, floor(lerp(400.0, 18.0, saturate(_Pixelate))));
                float2 b = float2(bx, max(4.0, floor(bx / max(_FrameAspect, 1e-3))));
                return (floor(uv * b) + 0.5) / b;
            }

            /// 周回で痩せる伝送を **mip で作る**。
            ///
            /// ⚠⚠ 旧実装はサンプル位置を量子化していた（点サンプル）。それは低域通過でも符号化でもなく
            ///    **周期的な停止と局所拡大を持つ座標変形**で、ブロック内は 1 テクセルを引き伸ばし、
            ///    境目だけが元画像を数倍速で走査する。実測（2026-08-12）で格子の境目に元の **2.9 倍**の
            ///    段差が乗っていた（実際の JPEG 劣化も解像度低下もすべて 1.0 前後）。
            ///    これが「現実でこんな粗くなり方はしない」の正体（`canon/LEDGER.md` 0018）。
            ///
            /// 実物の順序は **帯域を落としてから間引く**。mip はまさにそれ（面積平均のピラミッド）で、
            /// trilinear が中間の LOD を補間するので「低い解像度で送られてきた画」そのものになる。
            /// **1 タップのまま**なので Quest の予算も動かない。
            /// ⚠ ソーステクスチャが mipChain を持っていないと**何も起きない**（LOD が無視される）。
            ///   CameraStream / MjpegScreen / RecordedFramePlayer の Texture2D は mipChain:true で作る。
            float CoarseLod()
            {
                float blocks;
                if (!PickCoarse(blocks)) return 0.0;
                // 枠を横切るブロック数 → 映像を横切るブロック数 → 1 ブロックのソース画素数
                float acrossImage = max(blocks * max(_LiveScale.x, 1e-3), 1.0);
                float srcW = max(_LiveTex_TexelSize.z, 2.0);
                return max(log2(srcW / acrossImage), 0.0);
            }

            /// 色差は輝度より先に捨てられる（4:2:0 とクロマの粗い量子化）。輝度の LOD へ足す分。
            float ChromaLodBias()
            {
                float blocks;
                return PickCoarse(blocks) ? 1.0 : 0.0;
            }

            // ---- 入れ替わりのほどけ（canon/LEDGER.md 0089 / 0090）-------------------------
            //
            // 全画面の砂嵐は「機材が壊れた」で説明が付くので、その最中に何が入れ替わっても
            // **入れ替わったことにならない**。だから乱れは**人型に紐づいて**起こす。
            //
            // ⚠⚠ **描き足した線ではなく、その人の画素を引き伸ばす**
            //   （2026-08-19・ユーザーが参考画像を 3 枚渡して 2 度目の作り直し）。
            //   参考画像は人型が**水平の細い線**に分解され、線ごとに横へずれて伸びている絵で、
            //   **線の中身はその人自身の画素**。だから新しい絵を上に描くのではなく、
            //   **下の映像を行ごとに水平へ引き伸ばして置き換える**。
            //
            //   1 度目（等高線の渦）を捨てた理由は焼いた絵に出ている:
            //     ① 人型が読めなくなる（画面の広い範囲へ木目が広がり、どこが人だったか分からない）
            //     ② 中身が体験者の画素でないので「本人がほどけた」に 1 ビットも見えない
            //
            // ⚠ **マスク（どこをほどくか）は当面 CG の人型シルエット。** 中身は既に本人の画素なので、
            //   人体検知（MediaPipe 等）へ差し替えるときに直すのは `silSmear` / `sil` の 2 行で済む。

            /// 人型を原点、身長の半分を 1 とする座標。線の密度と広がりが figure に付いてくるので、
            /// 縮んでいく間も「同じ糸が集まっている」に見える（画素で刻むと縮小＝別物になる）。
            float2 SwapFigureSpace(float2 uv)
            {
                float h = max(_SwapRect.z, 1e-3);
                return float2((uv.x - _SwapRect.x) * _FrameAspect, uv.y - _SwapRect.y) / h;
            }

            /// figure 空間 → 枠 UV（<see cref="SwapFigureSpace"/> の逆）。
            float2 SwapFrameUv(float2 p)
            {
                float h = max(_SwapRect.z, 1e-3);
                return float2(_SwapRect.x + p.x * h / max(_FrameAspect, 1e-3),
                              _SwapRect.y + p.y * h);
            }

            /// 帯の棒の端の伸び（figure 空間・体の実範囲へ足す分）。
            ///
            /// ⚠⚠ **覆いは「人型の芯 ∪ 帯の棒」の和**（2026-08-21・ユーザー報告「黒いマスクに
            ///   体験者が隠れ切れていない」を受けて作り直した）。棒の幾何だけで人を隠そうと
            ///   しない — 隠すのは芯（その画素の人型）の仕事で、**棒は端の波を読ませる仕事**。
            ///   だからここの波がどれだけ泳いでも、人が覆いから外れることは構造的に無い。
            ///
            /// 波の作り（0098「①シルエットを縦に割る ②sin/cos のように横へずらす
            /// ③ずれが時間的に連続に変化する」＋ 続き「もっと細かくて波と分かるような波」）:
            ///   - **shift（横ずれ）と breath（伸縮）の 2 成分**。shift は左右の端へ逆符号で
            ///     効くので「帯が横へずれた」に読め、breath は同符号で「帯が伸びた」に読める
            ///   - 主の周期は **10 帯前後で 1 周**（8〜20 帯の範囲）。隣の帯が近い値を取るので
            ///     端が続いた波になる。細かい成分は**振幅を小さく**（質感であって構造にしない）
            ///   - 時間は連続に流れる（`_SwapSeed`）。階段や乱数の組み替えはしない
            void SwapWaveExt(float rowN, float t, float spread, out float extL, out float extR)
            {
                // 主 24 rad ＝ rows48 で約 12.5 帯 / 周期（8〜20 帯の範囲）。副はさらに低く。
                // ⚠ 振幅 × 周波数（＝隣の行との差）を帯 1 本の高さ以下に保つ。超えると
                //   波ではなく角ブロックの階段に見える（2026-08-21 に絵で確かめた）。
                float shift = 0.17 * sin(rowN * 24.0 - t * 2.4)
                            + 0.05 * sin(rowN * 11.0 + t * 1.0);
                float breath = 0.10 + 0.10 * sin(rowN * 17.0 + 1.7 - t * 1.7)
                             + 0.03 * sin(rowN * 47.0 + t * 3.1);   // 細かい成分は質感だけ
                // 晴れる段は棒が芯へ寄る（広がりは _SwapThread に付いてくる）。
                float k = 0.40 + 0.60 * saturate(spread);
                extL = max((0.05 + breath - shift) * k, 0.02);
                extR = max((0.05 + breath + shift) * k, 0.02);
            }

            // ---- 黒い波（`reports/2026-08-21_swap-wave-design.html` の設計 A〜F）--------------
            //
            // 上の `SwapWaveExt` は**その場で伸縮する対称な波**（＝ 振り子）で、連続で読めるが穏やかで
            // 「ゆらゆらした切り絵」に寄っていた。足りなかったのは 4 つ — **緩急・方向・対比・キメ**。
            //
            // ⚠⚠ **縦は機械、横は生き物。** 帯の格子（48 行の等間隔）は装置の走査線なので
            //   **縦は絶対に揺らさない**。有機的な動きは全部横（端の波・ほつれ・走り）に置く。
            //   機械の規則の上で黒だけが生きて動く、という対比が「アートチック」の正体。
            // ⚠⚠ **黒だけで組む**（`canon/LEDGER.md` 0091）。明るい線・色・白を出さない。
            //   かっこよさは**形と時間（緩急）だけ**で作る。
            // ⚠ 時計と段の値は C#（`SwapWaveLogic` / `SwapEnergyLogic`）、
            //   **形の寸法はここ**（この 4 関数の const）。数値を 2 か所へ書かない。

            /// 走る波（設計 A）。**sin ではなく非対称パルス** — 前縁は体高の 5% で立ち上がり、
            /// 後ろへ exp で減衰して尾を引く（立ち上がりが速く減衰が遅い ＝ 緩急）。
            /// 向きは `_SwapFromTop`（人 → 人形は下へ / 人形 → 人は上へ）。1 本の swap で変わらない。
            float SwapCrest(float pRowY)
            {
                if (_SwapWave.y <= 0.0001) return 0.0;
                float dir = _SwapFromTop > 0.5 ? -1.0 : 1.0;
                float s = (pRowY - _SwapWave.x) * dir;      // >0 = まだ来ていない / <0 = 通り過ぎた
                const float RISE = 0.10;                    // 前縁（体高の 5% ＝ figure 0.10）
                float tail = 0.55 * (1.0 + _SwapWave2.y);   // キメの一拍では尾が伸びる
                return s > 0.0 ? 1.0 - smoothstep(0.0, RISE, s) : exp(s / tail);
            }

            /// 手が速く動いた所だけ毛羽立つ（設計 F）。
            /// ⚠⚠ 位置は**実寸の人の figure 空間**（`pM`）。縮む人型の空間で渡すと、
            ///   人型が縮むほどホットスポットが体から離れていく（映像の中の腕はそこに在り続ける）。
            /// ⚠ 結合は弱く、平滑の遅れ（0.3 秒）は消さない — **遅れて反応することが
            ///   「気づかれた」と読ませる演出そのもの**（強さは `SwapEnergyLogic.HotGain`）。
            float SwapHot(float2 pM)
            {
                float h = 0.0;
                if (_SwapHotA.w > 0.0001)
                {
                    float2 d = (pM - _SwapHotA.xy) / max(_SwapHotA.z, 1e-3);
                    h += _SwapHotA.w * exp(-dot(d, d));
                }
                if (_SwapHotB.w > 0.0001)
                {
                    float2 d = (pM - _SwapHotB.xy) / max(_SwapHotB.z, 1e-3);
                    h += _SwapHotB.w * exp(-dot(d, d));
                }
                return h;
            }

            /// 稀に跳ぶ帯（設計 C）。hash(行) 上位 10% の行だけが、段あたり 1〜2 回、
            /// 一瞬だけ大きく突き出して戻る（立ち上がり 0.15 秒・減衰 0.30 秒）。
            /// 規則的な波の上に**事故のような棘**が乗ると、信号の乱れらしくなる。
            ///
            /// ⚠⚠ **頻度を上げない。** 上げると全体がノイズへ戻り、0089（全画面の乱れの否定）に逆行する。
            ///   検査は「1 画面に同時に跳んでいる帯 ≤ 2」（`tools/swap-motion-audit.py`）。
            ///   48 行 × 上位 10% ≒ 5 行、うち跳んでいるのは 0.45 / 1.5 秒 ＝ 同時 1.4 本。
            float SwapSpike(float rowN)
            {
                if (_SwapWave.w <= 0.0001) return 0.0;
                if (Hash21(float2(rowN * 131.0, 7.0)) < 0.90) return 0.0;
                const float PERIOD = 1.5, ATK = 0.15, DEC = 0.30;
                float t = frac(_SwapWave2.x / PERIOD + Hash21(float2(rowN * 57.0, 19.0))) * PERIOD;
                float env = t < ATK ? smoothstep(0.0, ATK, t) : saturate(1.0 - (t - ATK) / DEC);
                return env * _SwapWave.w;
            }

            /// 針（設計 B）。帯の中の**縦中央だけ**（15 画素の行のうち 5 画素）が、棒の先から伸びる。
            /// 長さは**裾の重い分布** — 短いのが大半、ごく稀にすごく長い。参考画像の実測
            /// （中央値 13px / 最大 257px ＝ 人型の高さの 1.5% / 29%）をそのまま設計値にしてある
            /// （`pow(u, 4.3)` の中央値 0.05 がその比）。行ごとの長さは hash(行) で固定。
            ///
            /// ⚠⚠ **時間で長さを振らない**（パチパチする。0098 が階段を廃したのと同じ理由）。
            ///   代わりに**山に引きずり出される** — 山が通っている行だけ伸び、通り過ぎると縮む。
            ///   時間は連続のまま「波が体から糸を引き抜いていく」因果が画に出る。
            float SwapNeedleFig(float rowN, float rowFrac, float pRowY, float crest, float hot)
            {
                if (_SwapWave.z <= 0.0001) return 0.0;
                const float MAXFIG = 0.58;   // 参考画像の最大 257px ＝ 人型の高さの 29%
                // 縦中央 1/3 だけ。縁はなだらかに（硬く切ると 1 画素の点滅になる）。
                float mid = 1.0 - smoothstep(0.13, 0.30, abs(rowFrac - 0.5));
                if (mid <= 0.0) return 0.0;
                float len = MAXFIG * pow(Hash21(float2(rowN * 79.0, 31.0)), 4.3);
                // 山に引きずり出される。0.25 は地の質感（全部を山に預けると体の大半が無地になる）。
                float pull = 0.25 + 0.75 * crest;
                // 縮む / 育つ段だけ**行き先へ向かって流れる** ＝ モーションライン。
                // ⚠ 段の判定は knot（縮む / 育つ段だけ 1）。C# から段を渡さないで済む。
                float aim = step(0.999, _SwapCover) * step(0.999, _SwapKnot)
                          * (_SwapFromTop > 0.5 ? -1.0 : 1.0);
                float bias = clamp(1.0 + 0.5 * aim * pRowY, 0.35, 1.5);
                return len * mid * pull * bias * (1.0 + hot) * _SwapWave.z;
            }

            /// 晴れ方（設計 A の晴れる段 ＋ H の余韻）。**最後の山が走り抜け、その後ろから帯が消える** —
            /// 「消える」ではなく「離れていく」。人 → 人形は人形の輪郭に針が数本残ってから引き、
            /// 人形 → 人は最後の帯が肩から上へ剥がれて終わる（どちらも山の進む向きの帰結）。
            ///
            /// ⚠⚠ **効かせるのは緩み（1 - knot）の分だけ。** 晴れる段の 1 フレーム目（knot = 1）は
            ///   人型を人形へ差し替える縁なので、そこで 1 行でも抜けると差し替えが見える。
            float SwapClearFade(float rowN, float pRowY)
            {
                float clear01 = _SwapWave2.z;
                if (clear01 <= 0.0001) return 1.0;
                float dir = _SwapFromTop > 0.5 ? -1.0 : 1.0;
                // 山の行き先に近い行が最後に残る（人 → 人形は足元 ＝ 人形が現れる所）。
                float order = saturate(0.5 + 0.5 * pRowY * dir);
                float jitter = Hash21(float2(rowN * 313.0, 5.0));
                float death = min(order * 0.78 + jitter * 0.14, 0.92);
                death = max(death, step(0.94, jitter));   // 余韻: 数本だけ最後まで残る
                float fade = 1.0 - smoothstep(death - 0.20, death, clear01);
                return lerp(1.0, fade, 1.0 - saturate(_SwapKnot));
            }

            /// その行がどれだけ糸で塞がれているか 0..1。
            ///
            /// ⚠ <paramref name="knot"/> が 1 のときは**全部の行**が塞がる。差し替えの 1 フレームが
            ///   そこなので、隙間が残ると下の映像が入れ替わる瞬間が見えてしまう。
            ///   緩むほど行が抜けて、隙間から下が覗く（参考画像も線の間に背景が見えている）。
            float SwapRowInk(float rowR, float front, float knot)
            {
                float gate = smoothstep(0.72, 0.16, rowR);   // 半分ほどの行が濃く残る
                float k = saturate(knot);
                // ⚠⚠⚠ **締まるときは薄めない**（2026-08-20・ユーザー指定「人は完全に黒色で
                //   塗りつぶされ、人は 1 ミリも映像内に映らないようにしてほしい」）。
                //   `knot` は**ほどける段で 0 → 1**・晴れる段で 1 → 0 と両方向に動くので、
                //   値だけで薄めると**ほどけている最中がずっと半透明**になり、体が透けて見える。
                //   ほどけ切ったか（`_SwapCover`）で向きを分ける。
                // ⚠⚠ **緩むときは行を抜くだけでなく全体も薄める**（2026-08-19 の 9 巡目）。
                //   抜くだけだと、残った行と抜けた行が 1 行おきに並んで**ブラインドの羽根**になる。
                float loose = step(0.999, _SwapCover) * (1.0 - k);
                return saturate(front * lerp(1.0, gate * 0.35, loose));
            }

            /// ほどけの前線。0 = まだ / 1 = ほどけた。
            /// **行き先の方向へほどける** — 人 → 人形は頭から（人形は足元に現れる）、
            /// 人形 → 人は足元から（人は上へ育つ）。
            float SwapFront(float2 uv)
            {
                float h = max(_SwapRect.z, 1e-3);
                float v = saturate((uv.y - (_SwapRect.y - h)) / (h * 2.0));   // 足元 0 → 頭 1
                float x = _SwapFromTop > 0.5 ? 1.0 - v : v;
                // ⚠ **広げない**（2026-08-19 の 10 巡目に 0.50 → 0.30）。覆いは前線の後ろでしか
                //   濃くならないので、ぼけ幅が体の半分もあると**ほどけ切る直前まで体が透けて見える**。
                const float band = 0.30;  // 前線のぼけ幅（体の高さに対する割合）
                return saturate((_SwapCover * (1.0 + band) - x) / band);
            }

            /// 走査線の色。**素材は引き伸ばした下の映像そのもの**（新しい色を作らない）。
            /// 行ごとに濃さを振って、参考画像の「黒く潰れた線」と「背景と同じ明るさの線」を混ぜる。
            ///
            /// ⚠ **単色で塗らない。** 塗ると黒い穴になって線に見えないし、
            ///   その画素が「誰だったか」の手掛かりが 1 ビットも残らない。
            // ⚠⚠ **線の色を作る関数は置かない**（2026-08-19・ユーザー指摘
            //   「今、ノイズはランダムに白黒じゃん。けど、元画像はそうじゃない」）。
            //   参考画像の線の明暗は**その画素が元々どこの色だったか**で決まっている —
            //   明るい壁から来た画素は白い線、暗い体から来た画素は黒い線。乱数ではない。
            //   一度は行ごとの乱数で濃い / 淡いへ振っていたが、それは
            //   **元の映像を捨てて縞を描き足している**のと同じで、
            //   「線の中身は本人の画素」（`canon/LEDGER.md` 0090）と正面から衝突する。
            //
            //   ⇒ 引き伸ばした画素を**そのまま**出す。線に見えるのは
            //     「隣り合う行が別の場所を読んでいる」ことだけで足りる。
            //     線と地の差は**ずれの大きさ**で作る（`lineAmt` が色ではなく距離に掛かる）。

                        // 演出としての「映像の乱れ」の位置ずれ成分。帯（走査線ブロック）の一部だけを水平に飛ばし、
            // 強いときは垂直の同期ずれも足す。**全層のサンプル前**に掛けるので、ライブ・差し替え素材・
            // マスク・CG が一緒にずれる = 差し替えの継ぎ目も一緒に乱れて隠れる（企画書 2.3）。
            // 枠外へ出た分は ContainUv が黒に落とすので、伝送のブロック落ちに見える。
            float2 GlitchUv(float2 uv)
            {
                if (_Glitch <= 0.001) return uv;
                float g = saturate(_Glitch);
                float t = floor(_GlitchSeed * 18.0);       // 約 18Hz で組み替わる
                float band = floor(uv.y * 24.0);
                float pick = Hash21(float2(band, t));
                float amt = Hash21(float2(band + 37.0, t + 11.0));
                float active = step(1.0 - 0.6 * g, pick);  // 強いほど多くの帯が飛ぶ
                uv.x += (amt - 0.5) * 0.22 * g * active;
                uv.y += (Hash21(float2(t, 3.7)) - 0.5) * 0.06 * g * g;
                return uv;
            }

            // ライブ × 差し替え素材の合成だけを 1 点で評価する。色収差はこれを RGB 別の uv で 3 回引く。
            // CG（人形）を含めないのは、3 回引くと 9-tap のぼかしが 27 サンプルに膨らむため。
            // 人形は色収差の後に中心 uv で 1 回だけ重ねる。
            /// 輝度は <paramref name="lod"/>、色差は <c>lod + chromaBias</c> で引く。
            /// 実物の符号化は**色差を先に捨てる**（4:2:0 とクロマの粗い量子化）ので、輝度と色差が
            /// 同じ大きさの格子で落ちるのは起こりえない。bias=0 のときは 1 タップのまま。
            half3 SplitChroma(half3 sharp, half3 wide)
            {
                half y = dot(sharp, half3(0.299, 0.587, 0.114));
                return max(y + (wide - dot(wide, half3(0.299, 0.587, 0.114))), 0.0);
            }

            /// レンズの内面反射（ベイリンググレア）。明るい所の光が画全体へ薄く回り、
            /// **暗い所のコントラストを奪う**。安いレンズほど強く、監視カメラの絵では必ず出る。
            /// 閾値を高く取るので、光源が無い場面では何も起きない（無い所に滲みを作ると即座に嘘になる）。
            /// ⚠ **ぼけた値が元より明るい所にだけ足す**。物理的にもそれが正しい（暗い画素の隣に
            ///   光源があるときだけ光が回り込む）が、実装上の安全網でもある —
            ///   ソースが mipChain を持たないと LOD 指定は無視されて <c>wide == sharp</c> が返り、
            ///   その場合この式は**恒等に 0** になる。閾値方式だと明るい画素が二重加算されて飛ぶ。
            half3 Glare(half3 sharp, half3 wide)
            {
                return max(wide - max(sharp, 0.45), 0.0) * (_Glare * 2.2);
            }

            half3 SampleBase(float2 uv, float lod, float chromaBias)
            {
                // 左右分割（canon/LEDGER.md 0050）。分割位置より左は**左右反転して**ライブを読む。
                // ⚠ 反転が効くのは live と凍結だけ。差し替え素材（録画・生成画像）は反転しない —
                //   反転したまま録画を流すと映像の中の自分が逆走し、画面中央の境目へ入って消える。
                // ⚠ 環境が左右対称なカメラでしか成立しない（このプロジェクトではカメラ A だけ）。
                float onLeft = (_SplitX > 0.0001 && uv.x < _SplitX) ? 1.0 : 0.0;
                float2 uvSrc = uv;
                if (onLeft > 0.5 && _SplitFlipLeft > 0.5) uvSrc.x = 1.0 - uvSrc.x;

                float liveIn;
                float2 uvL = ContainUv(RotateUvSteps(uvSrc, _UvRotSteps), _LiveScale.xy, liveIn);
                half3 live = SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex, uvL, lod).rgb;
                if (chromaBias > 0.001)
                    live = SplitChroma(live, SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex,
                                                                  uvL, lod + chromaBias).rgb);
                if (_Glare > 0.001)
                    live += Glare(live, SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex, uvL, lod + 4.5).rgb);
                live *= liveIn;

                // 凍らせた 1 枚を混ぜる。1.0 = 完全に止まって見える（ホールド）、
                // 小さい値 = 少し前の姿がそこに薄く残る（焼き付き）。
                // 動いていない画素は同じ値なので何も起きず、**動いたものの跡だけが残る**。
                // 全画面の焼き付き（_Echo）と、左半分だけの凍結（_SplitFreezeLeft）の**大きい方**。
                // 左半分の凍結は 3 周目 A で「凍った自分」を残すためのもので、
                // 凍らせた 1 枚は反転した uv で読むので**反転したまま止まる**（そこが狙い）。
                float echoMix = max(_Echo, onLeft * _SplitFreezeLeft);
                if (echoMix > 0.001)
                {
                    half3 echo = SAMPLE_TEXTURE2D_LOD(_EchoTex, sampler_EchoTex, uvL, lod).rgb * liveIn;
                    live = lerp(live, echo, saturate(echoMix));
                }

                float ovIn;
                float2 uvO = ContainUv(uv, _OverlayScale.xy, ovIn);
                // 差し替え素材（録画・プレート）にも**同じだけ**掛ける。片方だけ鮮明だと、
                // 3 周目に録画へ切り替わった瞬間に画の素性が変わって切替がばれる。
                half3 overlay = SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex, uvO, lod).rgb;
                if (chromaBias > 0.001)
                    overlay = SplitChroma(overlay, SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex,
                                                                        uvO, lod + chromaBias).rgb);
                if (_Glare > 0.001)
                    overlay += Glare(overlay, SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex,
                                                                   uvO, lod + 4.5).rgb);
                // 色統計マッチング（卓が焼いた per-channel の gain/offset）。恒等なら何も起きない。
                overlay = saturate(overlay * _OverlayGain.rgb + _OverlayOffset.rgb) * ovIn;

                // マスクも同じだけ鈍らせる。継ぎ目だけが鮮明に残ると、粗い画の中でそこだけ浮く。
                half mask = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, uv, lod).r;
                half3 col = lerp(live, overlay, saturate(mask * _OverlayStrength));

                // 第 2 の差し替え層。**左と右へ別の素材を同時に置く**ときだけ効く
                // （3 周目 A: 左＝録画 / 右＝環境＋人形、4 周目 A: 左＝大量の人形 / 右＝体験者人形）。
                // ⚠ 色統計マッチング（_OverlayGain/_Offset）は 1 層目にしか掛からない。
                //   第 2 層の素材は生成のパイプラインが post を抜いた素の色で入る（tools/gen-tone.py）。
                if (_Overlay2Strength > 0.001)
                {
                    float ov2In;
                    float2 uvO2 = ContainUv(uv, _Overlay2Scale.xy, ov2In);
                    half3 ov2 = SAMPLE_TEXTURE2D_LOD(_Overlay2Tex, sampler_Overlay2Tex, uvO2, lod).rgb;
                    if (chromaBias > 0.001)
                        ov2 = SplitChroma(ov2, SAMPLE_TEXTURE2D_LOD(_Overlay2Tex, sampler_Overlay2Tex,
                                                                    uvO2, lod + chromaBias).rgb);
                    ov2 *= ov2In;
                    half mask2 = SAMPLE_TEXTURE2D_LOD(_Mask2Tex, sampler_Mask2Tex, uv, lod).r;
                    col = lerp(col, ov2, saturate(mask2 * _Overlay2Strength));
                }
                return col;
            }

            /// <b>映像の中に写っている人</b>を、無人プレートとの差で拾う。−1 = 使わない。
            ///
            /// ⚠⚠ **入れ替わりの対象は映像の中の人であって、CG の人形ではない**
            ///   （3 周目なら 1 周目の録画の中の自分、帰りの A ならライブの自分）。
            ///   CG のシルエットで覆うと、**いまの体験者の立ち位置**に黒が出る —
            ///   3 周目の映像は過去の自分なので、まったく別の場所を覆うことになる。
            /// ⚠ 固定視点カメラなので**背景差分で足りる**（人体検知の推論を実機で回さない）。
            ///   無人プレートは卓が既に撮っていて（`plate_<ID>_<時刻>.jpg`）、素材としても配っている。
            /// ⚠⚠ **マスクは「ほどけ始めた瞬間の枠」で引く**（`_SwapRect0`）。映像の中の人は
            ///   縮まないので、縮んだ `_SwapRect` で引くと縮む段でマスクが人から外れる。
            /// ⚠ **粗い mip で取る**（`_SwapMask.w`）。等倍だと粒と符号化のゆらぎを人として拾う。
            /// <paramref name="uv0"/> は**枠 UV そのもの**（写像済み）。人 → 人形の縮む段は
            /// <see cref="SwapDiffSil"/>（figure 写像）で、人形 → 人の覆い切った後は**写像なし**で呼ぶ —
            /// 映像の中の当人は実寸で静止しているので、縮む枠を通すと（人型がまだ小さいあいだ）
            /// 頭や肩が写像の外へ出て**差分が当人を拾えない**（2026-08-21 に絵で踏んだ。
            /// 人形 → 人の育つ段のあいだ、当人が半透明のまま丸見えだった）。
            float SwapDiffAt(float2 uv0, float lod)
            {
                if (_SwapMask.x < 0.5) return -1.0;
                if (uv0.x < 0.0 || uv0.x > 1.0 || uv0.y < 0.0 || uv0.y > 1.0) return 0.0;

                float plateIn;
                float2 uvP = ContainUv(RotateUvSteps(uv0, _UvRotSteps), _LiveScale.xy, plateIn);

                // ⚠⚠ **粗さの違う 2 段で取って大きい方を採る**（2026-08-20）。
                //   粗い段は**人の中の穴を埋める**（服と背景の明るさがたまたま近い所で
                //   マスクが抜け、そこだけ体が透ける）。細かい段は**縁を保つ**。
                //   片方だけだと「穴が空く」か「輪郭が膨らむ」のどちらかになる。
                float lodA = lod + _SwapMask.w;
                float lodB = lod + max(_SwapMask.w - 2.0, 0.0);
                half3 plA = SAMPLE_TEXTURE2D_LOD(_SwapMaskTex, sampler_SwapMaskTex, uvP, lodA).rgb;
                half3 plB = SAMPLE_TEXTURE2D_LOD(_SwapMaskTex, sampler_SwapMaskTex, uvP, lodB).rgb;
                half3 curA = SampleBase(uv0, lodA, 0.0);
                half3 curB = SampleBase(uv0, lodB, 0.0);
                float dA = max(abs(curA.r - plA.r), max(abs(curA.g - plA.g), abs(curA.b - plA.b)));
                float dB = max(abs(curB.r - plB.r), max(abs(curB.g - plB.g), abs(curB.b - plB.b)));
                float d = max(dA, dB);
                return plateIn * smoothstep(_SwapMask.y, _SwapMask.z, d);
            }

            /// 人 → 人形用: いまの人型枠 → ほどけ始めた瞬間の枠（映像の中の人はそこに写っている）。
            /// ⚠⚠ **マスクは「ほどけ始めた瞬間の枠」で引く**（`_SwapRect0`）。映像の中の人は
            ///   縮まないので、縮んだ `_SwapRect` で引くと縮む段でマスクが人から外れる。
            ///   この写像のおかげで、覆いは人型と一緒に縮みながら実寸の人を指し続ける（0095）。
            float SwapDiffSil(float2 uv, float lod)
            {
                if (_SwapMask.x < 0.5) return -1.0;
                float2 p = SwapFigureSpace(uv);
                float h0 = max(_SwapRect0.z, 1e-3);
                float2 uv0 = float2(_SwapRect0.x + p.x * h0 / max(_FrameAspect, 1e-3),
                                    _SwapRect0.y + p.y * h0);
                return SwapDiffAt(uv0, lod);
            }


            half4 frag(Varyings input) : SV_Target
            {
                // 枠の座標（post FX の空間。卓の FS_POST と一致させる側）と、
                // テクスチャを引く座標（低解像度化 → 乱れ の順に劣化させた側）を分ける。
                //
                // ⚠⚠ **終幕の電源断はここで掛ける。** 潰れるのは「画」なので、post も OSD も
                //   乱れも一緒に潰れるのが正しい（`col` に成り切った末尾では、もう潰す材料が無い）。
                //   ⚠ **管のガラス（`CrtSdf`）だけは `rawUv` を使う** — 潰れるのは中の画で、
                //     管の枠ではない。潰れた uv を渡すと `fwidth` が 1/squeeze で発散し、
                //     角を切る `saturate(-d/aa)` が **線そのものを黙って消す**。
                float2 rawUv = input.uv;
                float2 collapseUv; float collapseMask; float collapseGain;
                CollapseScreen(_ScreenCollapse, rawUv, collapseUv, collapseMask, collapseGain);

                float2 screenUv = collapseUv;
                float2 sampleUv = GlitchUv(PixelateUv(screenUv));

                // 伝送が痩せたぶん（mip）＋ **周辺の解像度低下**（像面湾曲。実レンズは角ほど像がゆるい）。
                // 角だけぼけるのは静的なので怖さには効かないが、これが無いと「中心も端も等しく鮮明」
                // というレンズの存在しない絵になる。
                float2 dir = screenUv - 0.5;
                float r2 = saturate(dot(dir, dir) * 4.0);
                float lod = CoarseLod() + saturate(r2 - 0.45) * 0.7;
                float chromaBias = ChromaLodBias();

                // 1-2) ライブ × 差し替え素材。色収差は放射方向に RGB をずらす（端ほど強い）。
                half3 col;
                if (_Aberration > 0.001)
                {
                    float2 c = sampleUv - 0.5;
                    float r = saturate(length(c) * 2.0);
                    float2 off = c * (r * r) * _Aberration * 0.02;
                    col.r = SampleBase(sampleUv + off, lod, chromaBias).r;
                    col.g = SampleBase(sampleUv, lod, chromaBias).g;
                    col.b = SampleBase(sampleUv - off, lod, chromaBias).b;
                }
                else col = SampleBase(sampleUv, lod, chromaBias);

                // 2.5) CG レイヤ（人形）。**ポスト FX の前**に重ねるのが要点 —
                //      映像と同じ露出・彩度・ヴィネット・走査線・グレインを浴びて初めて
                //      「映像の中に居る」ように見える（後段に足すと必ず浮く）。
                //
                //      **ライブ映像と同じ contain-fit 枠に収める**（_CgScale = MjpegScreen.ContainScale）。
                //      旧実装は生の screenUv で枠いっぱいに描いており、4:3 の映像が 16:9 の枠へ
                //      レターボックスされている分（_LiveScale.x=0.75）だけ人形が水平 1.33 倍外側へずれていた
                //      ＝ 姿勢を完璧に測っても絶対に合わない（2026-07-27 監査 CRITICAL 1）。
                //      letterbox 帯に人形が出ないのは正しい挙動（映像の外に人形は居ない）。
                // ⚠⚠⚠ **ほどけは CG が居なくても走る**（2026-08-20）。覆う相手は
                //   **映像の中に写っている人**なので、CG（人形）が出ているかとは無関係。
                //   `_CgStrength` の内側に置いていたころは、**人形を出さないカットでは
                //   入れ替わりが 1 画素も出なかった**（プレビューで踏んだ）。
                float cgIn = 0.0;
                half4 cg = half4(0.0, 0.0, 0.0, 0.0);
                half aCg = 0.0;
                half3 rgbCg = half3(0.0, 0.0, 0.0);
                if (_CgStrength > 0.001)
                {
                    // 乱れ・低解像度化は人形にも同じだけ掛ける（映像だけが壊れて人形が無傷だと必ず浮く）。
                    float2 uvC = CgUvOf(sampleUv, cgIn);
                    // 人形にも**映像と同じ伝送の痩せ**を掛ける（装置を通して見えている以上、同じだけ落ちる）。
                    cg = SampleCgSoft(uvC, lod);
                    cg.rgb = CgChromaMatched(cg, uvC, lod);
                    // ⚠ premultiplied を崩さない — アルファに掛けた分は rgb にも掛ける。
                    aCg = saturate(cg.a);
                    rgbCg = cg.rgb;
                }
                // ⚠⚠ **入れ替わりの絵は CG を重ねた後に混ぜる**（2026-08-19）。先に混ぜると
                //   人形（実機）や代役（Editor）が上書きして、線が乗っていない絵になる。
                half3 swapCol = 0.0;
                half swapMix = 0.0;
                if (_SwapRect.w > 0.5)
                {
                    float2 p0 = SwapFigureSpace(sampleUv);
                    float frontHere = SwapFront(sampleUv);        // この画素までほどけが来たか
                    // ⚠ CG の形は **_CgStrength に依らず**引く（覆いの形の供給元であって、
                    //   画に混ぜるかとは別の話）。Editor プレビューはほどけている間
                    //   cgVisible=false（_CgStrength=0）で回すので、aCg 経由だけだと
                    //   ほどけ切る手前の「CG の形へ渡す」区間で芯が消える。
                    float silCg = saturate(aCg);
                    if (_CgStrength <= 0.001)
                    {
                        float cgInSwap;
                        float2 uvCgSwap = CgUvOf(sampleUv, cgInSwap);
                        silCg = saturate(SAMPLE_TEXTURE2D_LOD(_CgTex, sampler_CgTex, uvCgSwap, 0).a
                                         * cgInSwap);
                    }

                    // 早い棄却（figure 空間の箱）。棒が届く最大より外は 1 画素も触らない。
                    // ⚠ 差分（重い）は箱の中でしか引かない — 旧実装は棄却の**前**に毎画素
                    //   引いていたので、入れ替わりの 2.6 秒は全画面が差分を払っていた。
                    // ⚠⚠ **箱は「いまの人型」と「マスクの枠（実寸の人）」の両方で取る**（2026-08-21）。
                    //   人形 → 人の育つ段は人型がまだ小さく、縮んだ枠の箱だけで切ると
                    //   **実寸の当人の上半身が箱の外に出て、芯が届かず丸見えになる**（絵で踏んだ）。
                    float2 pM = float2((sampleUv.x - _SwapRect0.x) * _FrameAspect,
                                       sampleUv.y - _SwapRect0.y) / max(_SwapRect0.z, 1e-3);
                    // ⚠⚠ **左右分割と併せるときは左端を切る**（0102）。芯は**合成後の画**と
                    //   無人プレートの差で引くので、制限しないと左半分の鏡映しの人物・録画の人物まで
                    //   拾って包む（同じ人が 2 か所に写っているのが 3 周目 A の作りそのもの）。
                    float sideOk = step(_SwapMinX, sampleUv.x);
                    if (sideOk > 0.5
                        && ((abs(p0.x) < 1.25 && abs(p0.y) < 1.35)
                            || (abs(pM.x) < 1.25 && abs(pM.y) < 1.35)))
                    {
                        // その画素の人型 ＝ **芯**。映像の中の人が居るなら差分が正、
                        // 無人プレートが配られていなければ CG の形へ落ちる（0095）。
                        //
                        // ⚠⚠⚠ **差分を引くのは人 → 人形のほどける段だけ**（2026-08-21）。
                        //   差分は「映像の中の当人」を拾うので、**映像に当人が居る間**しか出番が無い:
                        //   - 人 → 人形: ほどける段は当人がライブに居る → 差分。覆い切った縁で
                        //     画面が無人へ差し替わるので、そこから先は CG の形へ渡す（0095）
                        //   - 人形 → 人: **差し替えが育ち切るまで来ない**ので（`justSwapScreen`）、
                        //     覆っている間ずっと画は素材（無人プレート）＝ 映像に当人は居ない。
                        //     引くだけ無駄で、しかも生成画像を人と誤読する
                        //
                        // ⚠⚠ **人形 → 人で「実寸の当人の形」を足さない**（ユーザー赤入れ
                        //   「人形→人間で、人型の黒いのが見えてしまう」）。一度そうしていたのは
                        //   差し替えを覆い切った縁で行っていたからで、**当人がはみ出す方を
                        //   差し替えの側で解いた**いまは要らない。足すと、育っている最中に
                        //   実寸の黒い人型が立って「黒い波が育つ」が読めなくなる。
                        // ⚠⚠ 覆い切ったら CG の形へ渡す（`hand`）のは、**覆い切った縁で画面が
                        //   無人へ差し替わる**前提だから。持続の覆い（0102）はまだ差し替えていないので、
                        //   渡すと覆いが**いまの体験者の立ち位置**へ出て、映像の中の人からずれる。
                        float hand = smoothstep(0.86, 1.0, _SwapCover) * (1.0 - _SwapDiffHold);
                        float silDiff = (_SwapFromTop > 0.5 && hand < 0.999)
                            ? SwapDiffSil(sampleUv, lod) : -1.0;
                        float sil = silDiff < 0.0 ? silCg : lerp(silDiff, silCg, hand);

                        float rows = max(_SwapSmear.x, 4.0);
                        float rowN = (floor(sampleUv.y * rows) + 0.5) / rows;
                        float rowFrac = frac(sampleUv.y * rows);   // 帯の中の縦位置（針は中央だけ）

                        // 帯の棒 ＝ **行の中心線で引いた人型を、波の量ぶん横へ引き伸ばしたもの**。
                        // 自分の列から extR / extL だけ内側へ寄った点に体が在れば、この画素は棒の上。
                        // 端は「体の縁 ＋ 波」の連続関数になるので、うねりが縦に続いて読める
                        // （タップの min/max で範囲を推定していた版は、量子化の段が波より大きく
                        //   端が角ブロックにしか見えなかった — 2026-08-21 に絵で確かめて置き換え）。
                        //
                        // ⚠⚠ **中心 1 点にしない**（2026-08-20 の実害）。脚の段は左右に分かれて
                        //   中心が股の空白になるので、1 点だと棒が 1 本も出ず脚が素通しになる。
                        //   片側 3 点（ext の 100% / 62% / 30%）＋ 自分の列で引く。脚のような
                        //   細い部位では点の間が抜けて棒が途切れるが、それは筆のかすれとして
                        //   読める（参考画像の線も途切れている）。隠し切りは芯が保証している。
                        // ⚠ 在否を CG で引いてよい理由: 縦の占有（どの高さに体が在るか）は
                        //   CG と映像の人で一致する（同じ足元・同じ背丈）。横のずれは芯が拾う。
                        // ⚠ CG の RT は mip を持たない（手動 Render 経路）ので LOD は書かない。
                        float pRowY = (rowN - _SwapRect.y) / max(_SwapRect.z, 1e-3);
                        float extL, extR;
                        SwapWaveExt(rowN, _SwapSeed, _SwapThread, extL, extR);

                        // ---- 黒い波を端へ足す（設計 A / B / C / E / F）----------------------
                        // ⚠ **足すだけ。** どの層も棒の端を伸ばす方向にしか働かないので、
                        //   隠し切り（芯 ∪ 棒）は 1 ビットも弱くならない。
                        // ⚠ テクスチャの読みは 1 つも増えていない（全部 ALU）。7 タップのままなので、
                        //   隔離殻で踏んだ「全画面で毎画素の重い数式 ＝ 90fps → 39fps」には当たらない。
                        float hot = SwapHot(pM);
                        float crest = SwapCrest(pRowY);
                        // 全身のエネルギー（E）は**基本の波**に掛かる（山・針・跳びは C# 側で掛けてある）。
                        float amp = max(_SwapWave2.w, 0.0);
                        extL *= amp;
                        extR *= amp;
                        float crestExt = _SwapWave.y * crest * (1.0 + hot);
                        float needle = SwapNeedleFig(rowN, rowFrac, pRowY, crest, hot);
                        // 跳びは**片側だけ**（行ごとに向きを固定）。両側だと「膨らんだ」に見えて棘にならない。
                        float spike = SwapSpike(rowN) * 0.45 * (1.0 + hot);
                        float spikeR = Hash21(float2(rowN * 211.0, 3.0)) < 0.5 ? 0.0 : spike;
                        extL += crestExt + needle + (spike - spikeR);
                        extR += crestExt + needle + spikeR;
                        float stub = 0.0;
                        {
                            float aIn;
                            float2 uvS;
                            [unroll]
                            for (int side = 0; side < 7; side++)
                            {
                                // 0 = 自分の列 / 1..3 = 右の端用（左へ寄る）/ 4..6 = 左の端用（右へ寄る）
                                float off = side == 0 ? 0.0
                                          : side <= 3 ? -extR * (side == 1 ? 1.0 : side == 2 ? 0.62 : 0.30)
                                                      :  extL * (side == 4 ? 1.0 : side == 5 ? 0.62 : 0.30);
                                uvS = CgUvOf(SwapFrameUv(float2(p0.x + off, pRowY)), aIn);
                                stub = max(stub, SAMPLE_TEXTURE2D_LOD(_CgTex, sampler_CgTex, uvS, 0).a * aIn);
                            }
                        }
                        float mixBar = smoothstep(0.08, 0.35, stub);

                        // ⚠⚠ **覆い = 芯 ∪ 棒**（2026-08-21）。棒がどう泳いでも、人が居る画素は
                        //   芯が必ず黒くする — 「隠し切る」を棒の幾何に依存させない。
                        // ⚠⚠ **覆いは黒。中身を 1 画素も見せない**（0091 / 0096）。
                        //   自分だと分かるのは中身ではなく**動く形**（棒の帯が人型に付いてくる）。
                        float rowRand = 0.5 + 0.5 * sin(rowN * 89.0 + _SwapSeed * 1.31);
                        // 走る波は前線より 1.5 倍速いので、**まだほどけていない所を先に舐める**
                        // （設計 A の「先触れ」）。ここが無いと、山が前線の外に出た瞬間に
                        // 行の墨が 0 になって**山そのものが見えなくなる**。
                        // ⚠ 下の `body`（CG の実体をどこまで残すか）には混ぜない — 人形が斑に消える。
                        float frontInk = saturate(frontHere + crest * 0.35 * step(_SwapCover, 0.999));
                        float rowInk = SwapRowInk(rowRand, frontInk, _SwapKnot)
                                     * SwapClearFade(rowN, pRowY);
                        float w = max(mixBar, smoothstep(0.10, 0.45, sil));
                        swapCol = 0.0;
                        swapMix = saturate(rowInk * w);
                    }

                    // 棒は映像の側に描いたので、CG に残るのは実体だけ。
                    // ⚠⚠ **人 → 人形（fromTop=1）では、ほどけている最中の CG を画に混ぜない**
                    //   （2026-08-21）。当人は**映像の中**に居るので、CG（人の代役）まで描くと
                    //   同じ場所に二重の人が立つ。CG が実体として出るのは晴れる段（_SwapReal）だけ。
                    //   Editor プレビューはこの間 CG を切っている（cgOn=false）ので絵は変わらない
                    //   — 変わるのは実機だけ。
                    // ⚠⚠ **人形 → 人（fromTop=0）は、ほどけが来ていない所は実体のまま残す**
                    //   （2026-08-19 に絵で確かめて直した）。`_SwapReal` だけで消すと、入れ替わりが
                    //   始まった 1 フレーム目に人形が丸ごと消える（実体は CG 層に居て、棒はまだ
                    //   前線のぶんしか出ていないため）。前線の後ろは棒が引き受け、前は実体が立つ。
                    float body = _SwapFromTop > 0.5
                        ? saturate(_SwapReal)
                        : saturate(max(_SwapReal, 1.0 - frontHere));
                    // ⚠ ここは**CG の実体の形**（`silCg`）。覆いの形（差分）で切ると、
                    //   映像の中の人の形で人形が切り抜かれる。
                    aCg = silCg * body;
                    rgbCg = cg.rgb * body;
                }

                // premultiplied over（Porter-Duff 1984）。straight alpha の lerp から変えたのは、
                // **影が「乗算」だから** — 影を rgb=0 / a=濃さ の断片として同じ RT に描けば、
                // この式が自動的に背景を (1-a) 倍する。不透明な人形（a=1）に対しては lerp と同値。
                float s = saturate(_CgStrength) * cgIn;
                col = col * (1.0 - aCg * s) + rgbCg * s;
                // ⚠ **ほどけは最後。** 映像も人形（実機）も代役（Editor）も、まとめてこの下に入る。
                col = lerp(col, swapCol, swapMix);

                // ================= ここから撮像の順（レンズ → センサ → ISP）=================
                // ⚠⚠ 旧実装は **現像 → レンズ → センサ** の逆順だった。だから
                //    「潰れた黒の上に砂が浮く」「角に後から黒い楕円を乗せた」絵になっていた
                //    （`canon/LEDGER.md` 0018 の「わざと加工してる感」）。順序は見た目の飾りではない。
                //    卓の FS_POST（shaders.js / common.js / pipeline.js）も同じ順序に揃えてある。

                // 3) レンズ — 周辺光量の落ち。**現像より前**（届いていない光は後段でも戻らない）。
                //    cos^4 に近い形（r^4）。放射 2 次だと中心付近から効いて「楕円を乗せた」に見える。
                //    夜間モードでは赤外の照射範囲だけが残るので、進みに応じて周辺がさらに落ちる。
                float vig = saturate(_Vignette + saturate(_Mono) * 0.16);
                col *= 1.0 - vig * 0.58 * r2 * r2;

                // 4) センサ — 粒。**トーンカーブの前**。後に置くと、暗部を締めた黒の上に
                //    最大量の砂が浮く（旧実装がそうだった）。
                {
                    // 刻むのは**ソース画素**。枠空間で刻むと、粗くなった画より粒の方が細かくなる
                    // ＝ 符号化は粒を真っ先に捨てるので物理的に起こりえない絵になる。
                    float2 srcUv = (sampleUv - 0.5) / max(_LiveScale.xy, 1e-3) + 0.5;
                    float2 srcPx = srcUv * max(_LiveTex_TexelSize.zw, float2(2.0, 2.0));
                    float grainPx = max(exp2(lod), 1.0);
                    float2 np = floor(srcPx / grainPx);
                    // 時刻は**映像が更新されたときだけ**進む（_Time だと 15fps の映像の上で粒だけ 90Hz）。
                    float t = frac(_SrcFrame * 0.0173) * 97.0;

                    half y = max(dot(col, half3(0.299, 0.587, 0.114)), 0.0);
                    // 光ショットノイズ（√信号）＋ 読み出しノイズ（信号に依らない床 = _NoiseDark）。
                    // 粗い画ほど符号化が粒を捨てるので、ブロックが大きいほど弱める。
                    // 夜間モードは増感で成り立つので、進むほど粒が増える。
                    // ⚠ 粗い画では符号化が粒を捨てるが、**捨て切らない**（pow 0.6）。
                    //   完全に消すと「のっぺりした低解像度」になり、暗視カメラの手触りが無くなる。
                    float amp = (sqrt(y) * 0.030 + _NoiseDark * 0.30)
                              * (1.0 + saturate(_Mono) * 2.6) / max(pow(grainPx, 0.6), 1.0);
                    if (amp > 0.0005)
                    {
                        col += (Hash21(np + t) - 0.5) * amp;
                        // 色ノイズは低周波の塊（デモザイクと NR で広がる）。輝度成分を抜いて色差だけ動かす。
                        //
                        // ⚠⚠ **時計は輝度の粒と分ける**（`canon/LEDGER.md` 0121）。塊が輝度の粒より
                        //    大きいので、映像のフレームごとに引き直すと**暗い区間では領域まるごとの
                        //    色相が 30Hz で振れる**。実機の 1 周目 A（いちばん暗いカメラ）で
                        //    「ちかちかする」として見えていたのはこれ。輝度の粒が毎フレーム動くのは
                        //    正しい（実センサもそう）が、色差は違う — 安い ISP の NR は色差を
                        //    **時間方向にも均す**ので、実物の色ノイズはフレーム独立には振れない。
                        //    10 フレームに 1 度だけ引き直し、そのあいだは補間する（ゆっくり滲んで動く）。
                        float ct = _SrcFrame * (1.0 / 10.0);
                        float cf = floor(ct);
                        float cw = smoothstep(0.0, 1.0, ct - cf);
                        float ta = frac(cf * 0.0173) * 97.0;
                        float tb = frac((cf + 1.0) * 0.0173) * 97.0;
                        float2 cp = floor(np * 0.5);
                        float3 cnA = float3(Hash21(cp + ta * 1.7),
                                            Hash21(cp + ta * 2.9 + 17.0),
                                            Hash21(cp + ta * 4.1 + 41.0));
                        float3 cnB = float3(Hash21(cp + tb * 1.7),
                                            Hash21(cp + tb * 2.9 + 17.0),
                                            Hash21(cp + tb * 4.1 + 41.0));
                        float3 cn = lerp(cnA, cnB, cw) - 0.5;
                        // ⚠ 2 つを混ぜると分散が痩せる（真ん中で 1/√2）。戻さないと**粒の強さ自体が
                        //   5Hz で脈打つ** ＝ 直したつもりで別の明滅を作る。
                        cn *= rsqrt(cw * cw + (1.0 - cw) * (1.0 - cw));
                        cn -= (cn.r + cn.g + cn.b) * (1.0 / 3.0);
                        // ⚠ **色差は輝度より小さい**（旧実装は 1.6 倍 ＝ 逆だった）。安い ISP の NR が
                        //   いちばん強く潰すのが色差なので、実物では色ノイズの方が弱くて鈍い。
                        //   1.6 のままだと暗い区間で領域の色相が振れ、実機で「ちかちか」に見える。
                        col += cn * amp * 0.55;
                    }
                    // 固定パターン（画素ごとの感度ばらつき）は**乗算**。明るい所ほど出て、黒には浮かない。
                    if (_NoiseFixed > 0.001)
                    {
                        float f = (Hash21(floor(srcPx)) - 0.5)
                                + (Hash21(floor(srcPx * 0.04)) - 0.5) * 1.8;
                        col *= 1.0 + f * _NoiseFixed;
                    }
                }

                // 5) ISP — 露出 → ホワイトバランス → トーン → 彩度。
                //    _ExposureBias だけは別系統（装置の自動露出が遅れて追いつくぶん）。
                // ⚠ 夜間モードは**増感**で成り立つ（だから粒が増える）。色を抜いて周辺を落とすだけだと
                //    真っ暗になって「装置が壊れた」ではなく「何も映っていない」になる。
                col = max(col, 0.0) * exp2(_Exposure + _ExposureBias + saturate(_Mono) * 0.95);
                col.r *= 1.0 + 0.25 * _Temperature;
                col.b *= 1.0 - 0.25 * _Temperature;
                // 色かぶり（緑↔マゼンタ）: 色温度と直交する軸。安物 CMOS + 蛍光灯の緑寄りを作る。
                col.g *= 1.0 + 0.25 * _Tint;
                col.r *= 1.0 - 0.12 * _Tint;
                col.b *= 1.0 - 0.12 * _Tint;
                // 夜間モードは中間調が減って白と黒に寄る（照らされた所と、届かない闇）。
                col = (col - 0.5) * (_Contrast + saturate(_Mono) * 0.10) + 0.5;
                // 黒浮き: **コントラストの後**に黒の床を上げる（前だと潰されて意味が無い）。
                col = col * (1.0 - _Lift) + _Lift;
                // 夜間モードでは赤外カットフィルタが外れるので**赤いものが明るく写る**。
                // ただの脱色（Rec.601）だと赤い着物が灰色に沈むが、実物の暗視映像では白く浮く。
                half luma = lerp(dot(col, half3(0.299, 0.587, 0.114)),
                                 dot(col, half3(0.52, 0.34, 0.14)), saturate(_Mono));
                // 彩度。_ChromaKill が立っていると**暗い所ほど色が死ぬ** — 安い ISP のノイズリダクションは
                // 暗部の色差から捨てるので、一様な脱色（＝フィルタ）ではなくこの形になる。
                half sat = _Saturation + max(1.0 - _Saturation, 0.0)
                                       * saturate(luma * 3.33) * saturate(_ChromaKill);
                // 周回で夜間モードへ落ちる。**最後は色がまったく無い**（`canon/LEDGER.md` 0019）。
                sat *= 1.0 - saturate(_Mono);
                col = lerp(luma.xxx, col, sat);

                if (_Scanline > 0.001)
                {
                    float s = 0.5 + 0.5 * sin(screenUv.y * _ScanlineCount * 3.14159265);
                    col *= 1.0 - _Scanline * (1.0 - s) * 0.6;
                }

                // 人形に付き従う劣化。**この作品に唯一無かったのが「対象に紐づく非一様な乱れ」**で、
                // 全域一様な加工はすべて機材のせいにできてしまう（＝安全に見える＝怖くない）。
                // 人形が動くと荒れも動くので、原因が機材ではなく世界の側にあることになる。
                float aura = 0.0;
                if (_ActorFocus.w > 0.001)
                {
                    float2 ad = (screenUv - _ActorFocus.xy) * float2(_FrameAspect, 1.0);
                    float ar = max(_ActorFocus.z, 0.01);
                    aura = saturate(_ActorFocus.w) * (1.0 - smoothstep(ar * 0.6, ar * 2.6, length(ad)));
                }
                if (aura > 0.001)
                {
                    half al = dot(col, half3(0.299, 0.587, 0.114));
                    col = lerp(col, al.xxx, aura * 0.6);   // そこだけ色が抜ける
                    col *= 1.0 - aura * 0.18;              // そこだけ沈む
                }

                // 著作した粒（post 12 項目。卓の FS_POST と同じ位置・同じ式）と、人形に付き従う荒れ。
                // **センサの粒はここではない**（上のレンズ→センサ→ISP の段に移した）。
                // _Grain は「装置の素性」ではなく作者が足す粒なので、既定 0 で使わない。
                {
                    float w = _Grain + aura * 0.10;
                    if (w > 0.001)
                        col += (Hash21(screenUv * 480.0 + frac(_SrcFrame * 0.0173)) - 0.5) * w;
                }

                // --- 演出の乱れ（色の成分）。位置ずれは既に sampleUv へ掛かっている ---
                // 帯ごとに濃さを変える。一様に混ぜると「フィルタ」に見えて伝送障害に見えない。
                if (_Glitch > 0.001)
                {
                    float g = saturate(_Glitch);
                    float t = floor(_GlitchSeed * 42.0);
                    float st = Hash21(screenUv * 300.0 + t);
                    float band = floor(screenUv.y * 24.0);
                    float bandPick = Hash21(float2(band + 5.0, t + 2.0));
                    float amt = g * 0.5 * step(1.0 - 0.75 * g, bandPick);
                    col = lerp(col, half3(st, st, st), saturate(amt));
                    col *= 1.0 - 0.22 * g * Hash21(float2(t, 9.3)); // 同期崩れの明滅
                }

                // ブラウン管に電源が入る（導入の段 3）。**映像はまだ無い**ので、ここで作るのは光そのもの。
                //
                // ⚠ 「ホログラムのように」を字義どおり作らない。青い線・走査の束・粒は
                //   `canon/LEDGER.md` 0011 / 0016 / 0018 で**3 回続けて「安っぽい」と判定された語彙**。
                //   代わりに**管が点く**（このスクリーンは既にブラウン管なので、装置の側に理由がある）。
                //
                // 段取りは実物の CRT と同じ順: 高電圧が乗って縁が光る → 走査が中央から上下へ開く →
                // 面が満ちる → 少し行き過ぎて落ち着く。
                // 映像そのものの出方。**管が点くこととは別の段**（段 3 で管が点き、段 4 で映像が出る）。
                // 実物のブラウン管も、電源を入れてから絵が出るまで暖機の時間がある。
                col *= saturate(_IntroLive);

                // ⚠⚠ **2026-08-13 に作り直した**（`canon/LEDGER.md` 0030
                //   「枠のホログラムの色が明るく、単色なのもなんか怖くないというかリアルじゃない」）。
                //   直したのは 3 つで、どれも「1 色の明るい板」を崩すためのもの:
                //     ① **色が強さで変わる**（弱い所は深い赤、強い所だけ白へ寄る＝ 実物の燐光）
                //     ② **収束のずれ**（チャンネルごとに横へずらす＝ 縁で色が割れる。点き始めが最大）
                //     ③ **焼けムラと明滅**（一様に光る面は板に見える／高電圧が安定するまで揺れる）
                //   加えて全体を **0.34 倍**へ落とした（旧 0.55 / 0.22）。
                if (_CrtIgnite < 0.999)
                {
                    float t = saturate(_CrtIgnite);

                    // ② 収束のずれ。**点き始めがいちばん大きく、安定すると揃う**（実物と同じ）。
                    float conv = 0.0065 * (1.0 - t);
                    float3 e = float3(CrtIgniteEnergy(screenUv + float2(conv, 0.0), t),
                                      CrtIgniteEnergy(screenUv, t),
                                      CrtIgniteEnergy(screenUv - float2(conv, 0.0), t));

                    // ③ 燐光の焼けムラ（低い周波数のなだらかな雑音）と、高電圧が落ち着くまでの明滅。
                    float uneven = 0.58 + 0.80 * ValueNoise21(screenUv * float2(4.3, 3.1));
                    float flick = 1.0 - 0.30 * (1.0 - t)
                                * Hash21(float2(floor(_Time.y * 31.0), 7.3));
                    e *= uneven * flick * 0.34;

                    // ⚠ **映像はここで制御しない**（`_IntroLive` の担当）。ここが作るのは光だけ。
                    // ① 色は強さで決まる。**暖色**（`canon/LEDGER.md` 0010）は保つ。
                    col += PhosphorColor(e.g * 3.0) * e;
                }

                // 点いた管の面がぼんやり光っている（映像が来るまでの間）。
                // ⚠ **上のブロックの外に置く**。あちらは「点いていく過程」なので `_CrtIgnite = 1` で
                //   走らなくなり、点き切った瞬間に光が消えて画が真っ黒になる（2026-08-13 に絵で見つけた）。
                //   映像が出るぶんだけ引く — 映像そのものが光になるので、足したままだと白く濁る。
                // ⚠ ここも一様にしない（同じ焼けムラを掛ける）。0.07 → 0.052 へ落とした。
                {
                    float tubeLit = saturate(_CrtIgnite) * (1.0 - saturate(_IntroLive));
                    if (tubeLit > 0.001)
                    {
                        float uneven = 0.58 + 0.80 * ValueNoise21(screenUv * float2(4.3, 3.1));
                        col += PhosphorColor(0.30) * (tubeLit * 0.052 * uneven);
                    }
                }

                // --- 信号ロスト砂嵐（FS_POST 一致規約の対象外・別系統）---
                // 強=1.0（配信断）/ 弱（トラッキングロスト）は低い値。
                // 演出の乱れより後に置く: 実際に信号が切れたら、演出が何をしていても障害表示が勝つ。
                //
                // ⚠⚠ **砂は置き換えではなく掛け算**（`canon/LEDGER.md` 0131）。
                //   平均 1.0 の乗数として乗せるので、局所の明るさと色はそのまま残り、形だけが潰れる。
                //   置き換えていたころは無彩の linear 0.34（sRGB 147）へ跳んでいて、
                //   暗い部屋の映像（linear 0.049 / sRGB 52）から **6.9 倍明るく**なっていた。
                //   ユーザー: 「前に移っている映像との差が激しく目がちかちかするし、集中が途切れてしまう」。
                //
                // ⚠⚠ **管の形（下のブロック）より前に置く。** 砂嵐は管の中で生まれるものなので、
                //   後ろに置くと**角の丸みも縁の暗さも上書きした四角い砂の板**になる
                //   （2026-08-13 に `menu intro` の絵で見つけた）。
                // ⚠⚠ **導入で管がまだ映像を出していない間は砂嵐も出さない**（`_IntroLive` で切る）。
                //   切らないと `_CrtIgnite` の点灯過程をまるごと上書きする ＝ カメラが繋がっていない
                //   現場では**段 0〜3 のあいだずっとスクリーンが砂嵐**で、闇の中で管が点く導入の山が
                //   1 度も画に出ない。`_IntroLive` は演出の外では 1 なので、本編・終幕は 1 ビットも変わらない。
                //   段 4 では live と同じ進みで砂嵐が入ってくる ＝「映像が出るはずの所に砂嵐が出た」。
                float sl = saturate(_SignalLost) * saturate(_IntroLive);
                if (sl > 0.001)
                {
                    // 粒は**格子で切る**。連続の `Hash21` は 1 画素ごとに散るので、
                    // 頭が動くたびに面がちりちり沸く（HMD ではこれがいちばん目に障る）。
                    // 横をわずかに長く取る — 走査は水平に帯域が延びるので、実物の砂も縦より横が広い。
                    // 時間は 30Hz で刻む（表示 90Hz で 3 フレーム保つ。60Hz は表示と拍が合わず明滅した）。
                    // ⚠ 時間は必ず**巻き戻して**使う。`_Time.y` は起動から増え続けるので、
                    //   展示で 1 日開けっ放しにすると値が大きくなりすぎ、格子の整数（0〜210）が
                    //   float の刻みに飲まれて**粒が数倍に太る**。0〜1023 に畳めば見た目は変わらない。
                    float tq = fmod(floor(_Time.y * SAND_HZ), 1024.0);
                    float tr = fmod(_Time.y, 1024.0);
                    float2 cell = floor(screenUv * float2(SAND_CELL_X, SAND_CELL_Y));
                    float fine = Hash21(cell + tq * 1.7);
                    // 大きな斑がゆっくり流れる。一様に沸かせると「砂」ではなく「板」に見える。
                    float roll = ValueNoise21(screenUv * float2(6.0, 4.0)
                                              + float2(tr * 0.7, tr * 0.23));
                    float n = saturate(fine * 0.80 + roll * 0.20);
                    // 足し算の粒は別に引く。同じ抽選を使い回すと明暗が揃って粒の種類が減る。
                    float add = Hash21(cell + tq * 3.1 + 19.0);

                    // 砂の地。燐光の色から取る ＝ 砂も管が光って出しているもの。
                    // ⚠ **足す。`max` で切らない**（切ると映像の暗部が丸ごと平らになる）。
                    // ⚠ 量は「砂の下に画があるか」で変える — 画があるときはほとんど足さない。
                    float floorK = lerp(SAND_FLOOR_LIVE, SAND_FLOOR_DEAD, saturate(_SignalFloor));
                    half3 bed = col + PhosphorColor(0.55) * floorK;
                    // 乗数の平均は 1.0。粒ごとの振れ幅は約 10 倍あるので砂には見えるが、
                    // 局所の平均は動かないので、前の映像から段差なく砂へ移る。
                    // ⚠ **掛け算だけでは足りない**（暗い所に映像がそのまま残る）ので、
                    //   平均 0 の足し算を重ねる。効くのは暗い所ほど強い。
                    half3 sand = bed * (SAND_LO + n * (2.0 - SAND_LO * 2.0))
                               + SAND_TINT * ((add - 0.5) * (2.0 * SAND_ADD));

                    col = lerp(col, sand, sl);
                    col *= 1.0 - 0.10 * sl; // わずかに沈む（「映像が来ていない」を残す）
                }

                // --- OSD: 装置が打っている時計（`canon/LEDGER.md` 0108）---
                //
                // ユーザーの狙い（逐語）: 「カメラが切り替わって演出が入っても、この表示が
                // 変わらずあり続けることで、スクリーン＝現実であり合成でない感じを強めたい」。
                //
                // ⚠⚠ **砂嵐の後・管の形の前**という位置が意味そのもの。
                //   前へ動かすと砂嵐や乱れが時計を飲む ＝ 時計が feed の一部になり、
                //   「装置が別に打っている」が崩れる。後ろへ動かすと管の縁の暗さも角の外の黒も
                //   受けなくなり、**管の外に字が浮く**（装置の外に貼った層に見える）。
                //   この後に来る 切替の黒（_SwitchDim）と 終幕の電力（_ScreenPower）は
                //   どちらも装置側の出来事なので、時計が一緒に沈むのが正しい。
                //
                // ⚠ **映像を出していない管には出さない**（_IntroLive）。導入の段 0〜3 は
                //   「装置は点いているが、まだ何も映していない」。**演出の外では 1** なので
                //   本編・終幕は 1 ビットも変わらない（砂嵐と同じ切り方）。
                // ⚠ 3 周目に流れるのは 1 周目の**録画**だが、録画は配信の生 JPEG で OSD を
                //   焼き込んでいないので、ここで乗る時計は「いま」のまま進む。
                //   これが「合成でない感じ」を構造的に成立させている（焼き込む方式にすると
                //   3 周目に過去の時刻が出て種明かしになる）。
                if (_OsdRect.z > 0.0001 && _IntroLive > 0.001)
                {
                    float2 o = (screenUv - _OsdRect.xy) / max(_OsdRect.zw, 1e-4);
                    // ⚠⚠ **矩形の内外を分岐にしない。** 分岐にすると非一様分岐の中で
                    //   テクスチャを引くことになり、暗黙の微分（＝ミップの段の選択）が壊れる。
                    //   外側の if は uniform 同士の比較なので画面全体で一様＝ここには当たらない。
                    float inside = step(0.0, o.x) * step(o.x, 1.0)
                                 * step(0.0, o.y) * step(o.y, 1.0);
                    // ⚠⚠ **v は反転しない。** Unity はテクスチャを左下原点で持つので、
                    //   PNG の 1 行目（＝字の上）は v=1 側に来る。矩形の上端も o.y=1 なので、
                    //   そのまま渡すと向きが揃う。`1.0 - o.y` にすると**字が上下逆さまに出る**
                    //   （2026-08-22 に 1 度そう書いて、`menu osd` の絵で見つけた）。
                    half4 g = SAMPLE_TEXTURE2D(_OsdTex, sampler_OsdTex, o);
                    float a = saturate(g.a) * inside
                            * saturate(_OsdOpacity) * saturate(_IntroLive);
                    col = col * (1.0 - a) + g.rgb * a;
                }

                // 終幕の電源断。**画が線へ潰れて点になる**（`CollapseScreen` が形を持つ）。
                // ⚠ 管のガラス（下）より前に掛ける — 潰れた線も管の中にあるものなので、
                //   管の縁の暗さと角の切り落としはその後から掛かるのが正しい順序。
                // ⚠ 演出の外では mask=1 / gain=1 ＝ 恒等。
                col *= collapseMask * collapseGain;

                // ブラウン管の面。**縁へ向かって落ち、角の外は黒**。
                // ヴィネット（レンズ）とは別のもの — あちらは光が届かない話で、こちらは管の形。
                // だから post の最後（切替の暗転より前）に、枠の座標で掛ける。
                // ⚠⚠ **`screenUv` ではなく `rawUv`** — 潰れるのは中の画で、管の枠ではない（上）。
                if (_CrtEdge > 0.001 || _CrtRound > 0.001)
                {
                    float d = CrtSdf(rawUv);
                    // 縁の内側 _CrtEdgeWidth のあいだで暗くなる（管のガラスが厚くなる所）
                    float inner = saturate(-d / max(_CrtEdgeWidth, 1e-3));
                    col *= 1.0 - saturate(_CrtEdge) * (1.0 - inner) * (1.0 - inner);
                    // 角の外は黒。1 画素で切ると階段が出るので、画素幅で渡す
                    float aa = fwidth(d) * 1.2 + 1e-4;
                    col *= saturate(-d / aa);
                }

                // dip-to-black: 切替の一瞬だけ黒へ（CCTV の瞬断）。
                col *= 1.0 - saturate(_SwitchDim);

                // 終幕: 装置に届いている電力。**いちばん最後に掛ける** — 電池が切れるのは
                // 画の一部ではなく装置そのものなので、映像も砂嵐も管の縁も一緒に落ちる。
                // ⚠ 演出の外では 1 なので、本編・導入は 1 ビットも変わらない。
                col *= saturate(_ScreenPower);

                // ⚠⚠ **alpha は必ず 1**。パススルーの合成は `アプリの rgb + 現実 × (1 - alpha)` なので、
                //   ここを下げると**枠の中に現実が透ける**（canon/LEDGER.md 0005 が禁じたもの）。
                //   2026-08-13 に `_CrtIgnite` を書いてみたが、段 3（管が点く途中）で
                //   スクリーンの面に部屋が透けて出た。**管が点いていない間スクリーンが黒い矩形として
                //   立っている**問題（箱の面から約 2.0m 以上下がると箱の外へはみ出す）は実在するが、
                //   直すならここではない — 面の存在ごと消す側（Renderer）で解くこと。
                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
