// タイトルの立体文字「廻リ視」。
//
// 字形は距離場（SDF）で持つ。**距離場は alpha に入っている** — Unity は RGB にだけ sRGB 変換を
// 掛けるので、RGB に入れると `sRGBTexture` の設定 1 つで縁の太さが実機でだけ変わる。
// 焼くのは tools/make-title-sdf.py（射程 _SpreadPx は生成器と同じ値を持つ。片方だけ直すと沈黙して食い違う）。
//
// 厚みは**同じ字を Z 方向へ 16 枚重ねた 1 メッシュ**で出す（C# 側 TitleScreen が組む）。
// 面ごとに法線を偽装する陰影（ベベル）ではなく実際に奥行きを持たせるのは、
// **VR では両眼視差が立体感の主役**だから。ベベルは片眼ずつ見ると平らなままで、
// 「目の前に置かれた立体文字」にならない。
//
// 見た目は封印の箱（SealedBox）と揃える — 面はほぼ黒、縁だけ細く光り、光が模様を走る
// （canon/LEDGER.md 0004「幾何学的にきれい / 細めの線 / 光が明滅 / かっこいい不気味」）。
// タイトルが箱と同じ素材でできて見えると、体験の入口と体験が地続きになる。
//
// ⚠ Queue は Overlay+960 = 4960。タイトルの黒（4950）の後、5000 を超えない。
Shader "FixedCamVr/TitleGlyph"
{
    Properties
    {
        _Sdf("Signed distance (in alpha)", 2D) = "black" {}
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0

        // 面はほぼ黒（封印の箱と同じ地）。ただし**縁と光は箱よりはっきり明るくする** —
        // 箱は「気づかせない環境」だが、タイトルは**読めなければ意味が無い**。
        // 2026-08-12 に箱と同じ暗さで焼いたら、絵の中で字がほとんど判別できなかった。
        _FaceColor("Face color", Color) = (0.028, 0.034, 0.032, 1)
        _LineColor("Edge line color", Color) = (0.380, 0.520, 0.500, 1)
        _GlowColor("Glow color", Color) = (0.260, 0.620, 0.580, 1)

        _SpreadPx("SDF spread (px) — 生成器と同値", Float) = 24
        _LinePx("Edge line width (px)", Float) = 5.5
        _Flare("Back-layer flare (sdf)", Range(0, 0.3)) = 0.085
        _BackDim("Back layer darkening", Range(0, 1)) = 0.16
        _BackLine("Back layer line gain", Range(0, 1)) = 0.30
        _SubDim("Subtitle dimming", Range(0, 1)) = 0.55

        _Reveal("Reveal (0..1)", Range(0, 1)) = 1
        _Dissolve("Dissolve (0..1)", Range(0, 1)) = 0
        _ErodeSdf("Dissolve erosion (sdf)", Range(0, 1)) = 0.52

        _SweepGain("Running light gain", Range(0, 3)) = 1.0
        _SweepUpSec("Rising wave period (s)", Float) = 6.5
        _SweepSideSec("Side wave period (s)", Float) = 9.0
        _SweepSharp("Wave crest sharpness", Range(1, 8)) = 4.5

        _FlashPos("Flash position (0..1)", Range(-0.5, 1.5)) = 0
        _FlashAmt("Flash amount (0..1)", Range(0, 1)) = 0

        _PhaseSec("Preview phase (s)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+960" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "TitleGlyph"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define TAU 6.2831853

            TEXTURE2D(_Sdf);
            SAMPLER(sampler_Sdf);

            float _Opacity;
            float4 _FaceColor;
            float4 _LineColor;
            float4 _GlowColor;
            float _SpreadPx;
            float _LinePx;
            float _Flare;
            float _BackDim;
            float _BackLine;
            float _SubDim;
            float _Reveal;
            float _Dissolve;
            float _ErodeSdf;
            float _SweepGain;
            float _SweepUpSec;
            float _SweepSideSec;
            float _SweepSharp;
            float _FlashPos;
            float _FlashAmt;
            float _PhaseSec;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;      // 距離場アトラス内の座標
                float2 local : TEXCOORD1;   // その字塊の中での 0..1（光の走り・出現のワイプ用）
                float4 color : COLOR;       // r = 奥行き 0(手前)..1(奥) / g = 副題なら 1
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.local = v.local;
                o.color = v.color;
                return o;
            }

            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float op = saturate(_Opacity);
                if (op <= 0.002) return half4(0, 0, 0, 0);

                float s = SAMPLE_TEXTURE2D(_Sdf, sampler_Sdf, i.uv).a;
                // ⚠ 上限を切る。字塊を大きく寄って見ると fwidth が発散し、縁が滲んで消える。
                float aa = clamp(fwidth(s), 1e-5, 0.25);

                float layerT = i.color.r;
                float isSub = i.color.g;

                // 奥の層ほどわずかに張り出させる。真正面から見ると本来は前面しか見えないので、
                // これが無いと「厚みがある」ことが頭を動かすまで分からない。
                float thr = 0.5 - layerT * _Flare;

                // 溶ける。内側へ削る + ざらつきで一様な縮小に見せない。
                float grit = (Hash21(i.uv * 420.0) - 0.5) * 0.18;
                float diss = saturate(_Dissolve) * (1.0 + grit);
                thr += diss * _ErodeSdf;

                float fill = smoothstep(thr - aa, thr + aa, s);
                if (fill <= 0.0005) return half4(0, 0, 0, 0);

                // 縁の細い線。**画素幅で下から押さえる** — 細いままだと遠くで消え、
                // 字の形が線で読めなくなる（SealedBox の格子と同じ理由）。
                float lw = max(_LinePx / max(_SpreadPx * 2.0, 1.0), aa * 1.15);
                float inner = smoothstep(thr + lw - aa, thr + lw + aa, s);
                float ring = saturate(fill - inner);

                // ---- 出現。線が下から点いていき、遅れて面が満ちる ----
                float wipe = smoothstep(i.local.y - 0.35, i.local.y + 0.10, _Reveal * 1.45);
                float lineA = saturate(_Reveal * 2.2) * wipe;
                float faceA = smoothstep(0.30, 1.0, _Reveal) * wipe;

                // ---- 走る光。動くのは光だけで、字は 1 ミリも動かない ----
                float t = _Time.y + _PhaseSec;
                float w1 = 0.5 + 0.5 * sin(TAU * (i.local.y * 1.35 - t / max(_SweepUpSec, 0.2)));
                float w2 = 0.5 + 0.5 * sin(TAU * (i.local.x * 0.90 - t / max(_SweepSideSec, 0.2)));
                float glow = pow(w1, _SweepSharp) * 0.9 + pow(w2, _SweepSharp) * 0.5;
                glow *= _SweepGain;

                // ---- A を押した瞬間に走り抜ける光 ----
                float bx = (i.local.x - (_FlashPos * 1.7 - 0.35)) / 0.14;
                float flash = exp(-bx * bx) * saturate(_FlashAmt);
                // 削れ際も光る（溶けているのが「崩れ」ではなく「光に還る」に見える）。
                float front = ring * saturate(_Dissolve) * 1.6;

                float dim = lerp(1.0, _SubDim, isSub);
                float3 face = _FaceColor.rgb * lerp(1.0, _BackDim, layerT);
                float lineGain = lerp(1.0, _BackLine, layerT) * dim;

                float3 rgb = face;
                rgb = lerp(rgb, _LineColor.rgb, saturate(ring * lineGain * lineA));
                rgb += _GlowColor.rgb * ((ring * lineGain) * (glow + flash * 2.4) + front
                                         + fill * flash * 0.20 * dim);

                float a = max(fill * faceA, ring * lineA) * op;
                // ⚠ rgb は straight（ブレンドの SrcAlpha が掛ける）。ここで a を掛けると二重になる。
                return half4(rgb, saturate(a));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
