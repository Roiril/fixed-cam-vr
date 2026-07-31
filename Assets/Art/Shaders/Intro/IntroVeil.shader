// 導入演出の「覆い」。**現実を枠の中へ閉じ込める面**。
//
// 方式は Passthrough Windows（Meta 公式）: フレームバッファの alpha を書き、
// **alpha 0 の領域だけパススルーが透ける**。`Blend Zero SrcAlpha` なので
//   出力 = dst * srcAlpha
// になり、srcAlpha=1 の領域は VR の絵がそのまま（＝パススルーは見えない）、
// srcAlpha=0 の領域は dst ごと 0 になってパススルーが出る。
//
// ⚠ この方式では **暗くする方向にしか描けない**（RGB を足せない）。粒・走査線・乱れは
// 「alpha を落とす」＝ 明滅として出す。明るい粒が要るなら別の加算面が必要になるが、
// パススルーは元々明るいので暗くする側だけで質感の格下げは伝わる。
//
// ⚠ 手前に置くが深度で弾かれてはいけない（ZTest Always / ZWrite Off）。
// queue はスクリーンより後（Overlay+1000 = 5000）— 枠の中で見せる映像は既に描かれている必要がある。
//
// 設計の正本: .claude/plans/2026-07-30_intro-passthrough-to-screen.md §6
Shader "FixedCamVr/IntroVeil"
{
    Properties
    {
        // 枠の閉じ具合。0 = 全画面が「中」（何も覆わない）/ 1 = 開口だけが「中」
        _Frame("Frame close (0..1)", Range(0, 1)) = 0
        // 枠の中のパススルーの見え。1 = 現実が見える / 0 = 枠の中も VR の絵（＝映像）になる
        _Passthrough("Passthrough inside frame (0..1)", Range(0, 1)) = 1
        // 閉じ切ったときの開口の半径（uv の中心からの比。x=横 y=縦）。
        // 本編のスクリーンの見かけの大きさに合わせて IntroVeil が供給する。
        _Aperture("Aperture radius (uv)", Vector) = (0.42, 0.24, 0, 0)
        _Feather("Edge feather", Range(0.002, 0.4)) = 0.08
        // 粒と走査線（質感の格下げ）。alpha を揺らすので「暗い粒」になる。
        _Grain("Grain and scanline", Range(0, 1)) = 0
        _ScanlineCount("Scanline count", Float) = 240
        // 乱れ。帯ごとに alpha を落として伝送の劣化を装う（企画書 2.3）。
        _Glitch("Glitch", Range(0, 1)) = 0
        _GlitchSeed("Glitch seed", Float) = 0
    }

    SubShader
    {
        // ⚠ **Queue は 4900 まで。5000 を超えてはいけない**（2026-07-31 実害）。
        // URP の透明パスは `RenderQueueRange.transparent` = **[2501, 5000]** しか描かない
        // （`UniversalRenderer.cs` / Unity 公式 API リファレンス「render queue value should be
        // in [0..5000] range to work properly」）。この覆いより**後**に描く必要があるもの
        // （構造の線 = IntroStructureWire / HMD 内の指示 = IntroPrompt）が 5000 に入るので、
        // 覆いはその手前の 4900 に置く。以前は覆い 5000 / 後続 5100 にしていて、
        // **後続が範囲外で 1 つも描画されていなかった**（段 3 の線も導入の指示も画に出ていなかった）。
        Tags { "Queue" = "Overlay+900" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroVeil"
            Blend Zero SrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float _Frame;
            float _Passthrough;
            float4 _Aperture;
            float _Feather;
            float _Grain;
            float _ScanlineCount;
            float _Glitch;
            float _GlitchSeed;

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float hash12(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float4 frag(Varyings i) : SV_Target
            {
                // 中心からの距離（0..1）。枠は矩形なので軸ごとに正規化して max を取る。
                float2 d = abs(i.uv - 0.5) * 2.0;

                // 開口の半径。枠が開いているときは画面の外（1.4）まで広げて「覆いが無い」状態にする。
                float2 r = lerp(float2(1.4, 1.4), max(_Aperture.xy, 0.02), saturate(_Frame));
                float m = max(d.x / r.x, d.y / r.y);
                // inside = 1 が枠の中。縁は feather でぼかす（硬い矩形は「UI の窓」に見える）。
                float inside = 1.0 - smoothstep(1.0 - _Feather, 1.0, m);

                // 枠の中は「パススルーを透かす」= alpha を落とす。外は 1（＝ VR の絵 = 黒）。
                float alpha = lerp(1.0, 1.0 - saturate(_Passthrough), inside);

                // ---- 質感の格下げ（枠の中だけに掛ける）----------------------------
                // 走査線: 横縞で alpha を落とす。パススルーが縞に暗くなる。
                float scan = 0.5 + 0.5 * sin(i.uv.y * _ScanlineCount * 3.14159265);
                // 粒: 時間で流れる白色ノイズ。fract(_GlitchSeed) で毎フレーム位相を変える。
                float grain = hash12(i.uv * 480.0 + frac(_GlitchSeed) * 97.0);
                float degrade = saturate(_Grain) * inside * (0.18 * scan + 0.22 * grain);

                // 乱れ: 帯ごとに大きく落とす。継ぎ目を隠すためのもので、常時は出さない。
                float band = floor(i.uv.y * 24.0 + frac(_GlitchSeed) * 13.0);
                float bandNoise = hash12(float2(band, floor(frac(_GlitchSeed) * 60.0)));
                float glitch = saturate(_Glitch) * inside * step(0.72, bandNoise) * (0.35 + 0.4 * bandNoise);

                // 掛けるのは「枠の中のパススルーが見えている分」だけ。
                // 映像へ移った後（_Passthrough=0）に縞を残すと二重に掛かる（映像側の post FX が持つ）。
                float visible = inside * saturate(_Passthrough);
                alpha = saturate(alpha + (degrade + glitch) * visible);

                // RGB は使われない（dst * srcAlpha なので）。0 を返すのが Passthrough Windows の作法。
                return float4(0.0, 0.0, 0.0, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
