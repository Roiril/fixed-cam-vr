// タイトルのロゴタイプ「廻リ視」。
//
// 字形と質感は**焼いた版**（`Assets/Resources/Title/MawarimiTitle.png`）が持つ。焼くのは
// `tools/make-title-art.py`。チャンネルの意味は生成器と対で、片方だけ直すと沈黙して食い違う:
//
//     R = 主の白墨（廻・リ）  G = 朱の墨（視）  B = 溶ける順  A = 添えの白墨（払い・ルビ・罫）
//
// ⚠ **これはマスクであって絵ではない。** sRGB 変換が掛かると墨の量が変わる。
// 取り込み設定は `TitleSdfImporter` が機械で固定している（`sRGBTexture = false`）。
//
// 版は 1 枚だが、**3 つの層を別の Z に置いて描く**（添えを奥・主を中・朱を手前）。
// どの層かは頂点色で選ぶ: (1,0,0)=主 / (0,1,0)=朱 / (0,0,1)=添え。
// 押し出さずに層を離すのは、**押し出すと明朝の細い横画が潰れる**から。VR の立体感は
// 両眼視差が主役なので、平らな版のままでも層が分かれれば奥行きは出る。
//
// ⚠ Queue は Overlay+960 = 4960。タイトルの黒（4950）の後、5000 を超えない。
Shader "FixedCamVr/TitleGlyph"
{
    Properties
    {
        _Art("Artwork (R=main G=accent B=order A=deco)", 2D) = "black" {}
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0

        // 白は**わずかに暖かい生成り**。純白だと紙ではなく発光板に見える。
        _InkColor("Ink color", Color) = (0.86, 0.83, 0.77, 1)
        // 朱。暗い地の上で 1 文字だけが持つ色なので、彩度は高く明度は抑える。
        _AccentColor("Accent ink color", Color) = (0.62, 0.030, 0.035, 1)
        // 走る光・閃光の色。墨の上に加算する。
        _GlowColor("Glow color", Color) = (0.55, 0.72, 0.70, 1)

        _Reveal("Reveal (0..1)", Range(0, 1)) = 1
        _Dissolve("Dissolve (0..1)", Range(0, 1)) = 0

        _SweepGain("Running light gain", Range(0, 2)) = 0.35
        _SweepSec("Running light period (s)", Float) = 7.5
        _SweepSharp("Wave crest sharpness", Range(1, 10)) = 6.0

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

            TEXTURE2D(_Art);
            SAMPLER(sampler_Art);

            float _Opacity;
            float4 _InkColor;
            float4 _AccentColor;
            float4 _GlowColor;
            float _Reveal;
            float _Dissolve;
            float _SweepGain;
            float _SweepSec;
            float _SweepSharp;
            float _FlashPos;
            float _FlashAmt;
            float _PhaseSec;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;      // 版の中の座標
                float2 local : TEXCOORD1;   // 版の中の 0..1（光の走り・出現のワイプ用）
                float4 color : COLOR;       // 層の選択 (主, 朱, 添え)
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

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float op = saturate(_Opacity);
                if (op <= 0.002) return half4(0, 0, 0, 0);

                float4 t = SAMPLE_TEXTURE2D(_Art, sampler_Art, i.uv);
                // この層が受け持つ墨だけを取り出す。
                float ink = t.r * i.color.r + t.g * i.color.g + t.a * i.color.b;
                if (ink <= 0.003) return half4(0, 0, 0, 0);

                // ---- 出現。下から墨が満ちていく ----
                float wipe = smoothstep(i.local.y - 0.32, i.local.y + 0.12, _Reveal * 1.42);

                // ---- 溶ける。版に焼いた順（B）で食う。削れ際が光る ----
                float d = saturate(_Dissolve);
                float order = t.b;
                float left = smoothstep(d - 0.07, d + 0.02, order);   // 1 = まだ残っている
                float front = saturate(1.0 - abs(order - d) * 12.0) * step(0.002, d);

                // ---- 走る光。**弱く**。版は印刷物なので、光り出すと紙に見えなくなる ----
                float tm = _Time.y + _PhaseSec;
                float w = 0.5 + 0.5 * sin(TAU * (i.local.y * 1.1 + i.local.x * 0.35
                                                 - tm / max(_SweepSec, 0.2)));
                float sheen = pow(w, _SweepSharp) * _SweepGain;

                // ---- A を押した瞬間に走り抜ける光 ----
                float bx = (i.local.x - (_FlashPos * 1.7 - 0.35)) / 0.13;
                float flash = exp(-bx * bx) * saturate(_FlashAmt);

                float3 rgb = lerp(_InkColor.rgb, _AccentColor.rgb, saturate(i.color.g));
                rgb *= 1.0 + sheen;
                rgb += _GlowColor.rgb * (flash * 1.6 + front * 1.3);

                float a = ink * wipe * left * op;
                // ⚠ rgb は straight（ブレンドの SrcAlpha が掛ける）。ここで a を掛けると二重になる。
                return half4(rgb, saturate(a));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
