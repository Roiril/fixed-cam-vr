// 部屋のプロキシ（壁・箱）を「色を書かず深度だけ書く」で描き、CG 人形を隠す。
//
// 実映像の深度は要らない — **部屋のプロキシ幾何の深度**があれば足りる、というのがこの層の要点。
// 人形と同じ仮想カメラで先に描いておけば、壁の裏に立った人形は普通に深度テストで消える。
// 参照実装は Meta MRUK の `Meta/MRUK/MixedReality/InvisibleOccluder`（同じ Blend Zero One 方式）。
//
// ⚠ RT は透明背景（アルファに被覆率が出る）なので、**アルファも書き換えてはいけない**。
//    書くと壁の形に「不透明な黒」が合成されて実映像が消える。ColorMask 0 と Blend の二重で塞いである。
Shader "FixedCamVr/ShowOccluder"
{
    SubShader
    {
        // Geometry-200 = 影（-100）・人形（Geometry）より前。深度を先に置くのがこのパスの唯一の仕事。
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-200" }
        LOD 100

        Pass
        {
            Name "ShowOccluder"
            ColorMask 0                    // RGB もアルファも書かない（これが本命の保証）
            Blend Zero One, Zero One       // MRUK と同じ「dst をそのまま残す」指定（保険）
            ZWrite On
            ZTest LEqual
            Cull Off                       // 薄い壁の内側にカメラが入っても深度が抜けない

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
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(0, 0, 0, 0);   // ColorMask 0 なので実際には書かれない
            }
            ENDHLSL
        }
    }
    FallBack Off
}
