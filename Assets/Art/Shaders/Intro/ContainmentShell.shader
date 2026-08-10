// 隔離殻の黒。**「見てよいもの」以外を黒で潰す全画面の面**。
//
// 呪われた壁は隔離されている、という設定（.claude/canon/LEDGER.md 0002）を画にする層。
// 会場（人・机・天井・他の展示）が視界から消え、実物の壁と足元の床だけが黒の中に残る。
//
// ⚠ **パススルーには深度が無い**ので、「境界の向こうだけ隠す」は screen-space では書けない。
// 手前の実物（壁）と奥の会場は同じ画素に重なっていて、区別する情報がフレームバッファに無い。
// だから「見てよいもの」は<b>著作した幾何をそのままラスタライズして</b>ステンシルに立てる
// （ContainmentShellMask.shader・queue 4905）。ここはその**外側だけ**を塗る。
//
// ⚠ 描画順は覆い（IntroVeil・4900）の**後**。覆いは `Blend Zero SrcAlpha`（結果 = dst × srcAlpha）を
// 全画面へ掛けるので、先に描くと黒ごと 0 に潰されて 1 画素も残らない。
// **5000 を超えてはいけない**（URP の透明パスは [2501, 5000] しか描かない・2026-07-31 実害）。
//
// alpha を `One OneMinusSrcAlpha`（premultiplied over）で合成するのは、**0 から 1 へ戻す**ため。
// 覆いの乗算ブレンドは alpha を下げることしかできない。
Shader "FixedCamVr/ContainmentShell"
{
    Properties
    {
        // 殻の強さ。0 = 何も隠さない / 1 = 印の無い所は真っ黒。
        _Strength("Shell strength (0..1)", Range(0, 1)) = 0
    }

    SubShader
    {
        // Overlay+910 = 4910。印 (4905) の後、構造の線と文字 (5000) の前。
        Tags { "Queue" = "Overlay+910" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ContainmentShell"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            // 印（ContainmentShellMask）が立っていない所だけ塗る。
            Stencil
            {
                Ref 1
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _Strength;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // rgb は 0（over なので背景を暗くするだけ）。alpha を上げるとパススルーが出なくなる。
                return half4(0.0h, 0.0h, 0.0h, (half)saturate(_Strength));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
