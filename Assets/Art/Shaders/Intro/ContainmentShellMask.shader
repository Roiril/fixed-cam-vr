// 隔離殻の「見てよいもの」の印。**色も深度も書かず、ステンシルに 1 を置くだけ**の面。
//
// 実物の壁（layout.room）と足元の床（layout.floor）をこのマテリアルで描くと、
// その**投影された形**にステンシル 1 が立ち、次に来る ContainmentShell.shader が
// そこを避けて黒を塗る。結果として「壁と床だけが現実のまま残る」。
//
// なぜ幾何で塗るか（2026-08-10 実測で置き換え）:
// 最初は覆いと同じ全画面 1 パスで視線 × 箱の交差を解いていたが、**除算が画素あたり数十回**入り、
// 実機の導入が **90fps → 39fps** に落ちた（`logs/capture/20260810_161524`）。
// 箱をそのままラスタライズすれば同じ形が**ほぼ無料**で出るうえ、
//   - 眼ごとの投影が自動で正しい（視差の作り込みが要らない）
//   - 縁が MSAA でなまる（全画面の 2 値判定は MSAA が効かない）
// という利点まで付く。
//
// ⚠ Cull Off。眼が箱の中に入ったとき（壁に頭を寄せた等）に**前面が near clip で消えても
// 背面が残る**ようにする。消えると視界が丸ごと黒に落ちる側へ倒れて危ない。
// ⚠ ZTest Always / ZWrite Off。深度は本編のスクリーンが握っているので触らない。
Shader "FixedCamVr/ContainmentShellMask"
{
    SubShader
    {
        // Overlay+905 = 4905。覆い (4900) の後、隔離の黒 (4910) の前。
        Tags { "Queue" = "Overlay+905" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ContainmentShellMask"
            ColorMask 0
            ZWrite Off
            ZTest Always
            Cull Off

            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                return 0;   // ColorMask 0 なので書かれない。要るのはステンシルだけ。
            }
            ENDHLSL
        }
    }
    Fallback Off
}
