// タイトルの黒。**体験が始まる前に、現実を 1 画素も見せない面**。
//
// パススルーは「フレームバッファの alpha が 0 の画素」にだけ OS が合成する。段 0 の覆い
// （IntroVeil・4900）はそこへ alpha 0 を書いて現実を出しているので、タイトルの間はここで
// **alpha を 1 へ戻す**（＝現実が消える）。A を押すと alpha が 1 → 0 へ落ち、下で既に描かれている
// 封印の箱つきのパススルーがそのまま現れる。**箱は最初から描かれている**ので、
// 開ける途中に壁が覗く瞬間が構造的に存在しない。
//
// ⚠ 描画順は封印の箱（4920）の**後**、構造の線と文字（5000）の前 ＝ Overlay+950 = 4950。
//    5000 を超えると URP の透明パスに入らず 1 画素も出ない（2026-07-31 実害）。
//
// ⚠ alpha は別ブレンドで書く（`Blend ..., One OneMinusSrcAlpha`）。RGB と同じ式にすると
//    dstA = a² + dstA(1-a) になり、a=0 でも 1 に張り付いて**パススルーが二度と出ない**。
Shader "FixedCamVr/TitleVeil"
{
    Properties
    {
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0
        // 完全な黒にしない。ごく薄い中心の持ち上げがあると「消灯した画面」ではなく
        // 「奥行きのある闇」に見える。値は肉眼でぎりぎり分かる程度。
        //
        // ⚠ **わずかに赤へ寄せる**（2026-08-12）。純黒の地に生成りの墨を置くと印刷物ではなく
        // 発光する看板に見える。暗い漆のような暖かい地だと、墨が紙に載っているように見える。
        _CoreColor("Core color", Color) = (0.0400, 0.0215, 0.0185, 1)
        _EdgeDarken("Vignette", Range(0, 1)) = 0.80
        // ⚠ 大きくしない。地がほぼ黒なので、0.01 でも sRGB では砂嵐に見える（2026-08-12 実測）。
        _Grain("Grain", Range(0, 0.05)) = 0.004
        _PhaseSec("Preview phase (s)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+950" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "TitleVeil"
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

            float _Opacity;
            float4 _CoreColor;
            float _EdgeDarken;
            float _Grain;
            float _PhaseSec;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
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

                float a = saturate(_Opacity);
                if (a <= 0.002) return half4(0, 0, 0, 0);

                float2 c = i.uv * 2.0 - 1.0;
                float vig = 1.0 - saturate(length(c) * 0.55) * _EdgeDarken;
                float t = _Time.y + _PhaseSec;
                float g = (Hash21(i.uv * 733.0 + frac(t) * 91.0) - 0.5) * _Grain;

                float3 rgb = max(_CoreColor.rgb * vig + g, 0.0);
                // ⚠ rgb は straight（ブレンドの SrcAlpha が掛ける）。ここで a を掛けると二重になる。
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
