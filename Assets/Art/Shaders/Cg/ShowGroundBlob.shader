// 足元の**接地影**（濃い楕円）。「人形が浮いている」に一番効くのはこれで、投影シャドウより先に入れる価値がある
// （光が斜めだと投影シャドウは足元から離れた所に落ちるので、接地点そのものは暗くならない）。
//
// SSAO では代用できない — CG レイヤの深度しか知らないので、実写の床とは決して接地しない。
//
// 出力は投影シャドウと同じ「rgb=0 / a=濃さ」の premultiplied 断片。
// ⚠ **わざとステンシルを使わない**。投影シャドウと同じ Ref 1 で弾くと、影の輪郭で blob が切り取られて
//    はっきりした段差が出る。重なった所が少し余分に暗くなる方は、接地点が濃くなるだけなので望ましい。
Shader "FixedCamVr/ShowGroundBlob"
{
    Properties
    {
        _BlobDensity("Blob Density", Range(0, 1)) = 0.5
        // 縁のぼけ幅（半径に対する比）。小さいほど中心が濃く縁が急になる。
        _BlobFeather("Blob Feather", Range(0.01, 1)) = 0.55
    }

    SubShader
    {
        // Geometry-99 = 投影シャドウ（-100）の直後・人形本体（Geometry）の前。
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-99" }
        LOD 100

        Pass
        {
            Name "ShowGroundBlob"
            Blend One OneMinusSrcAlpha
            ZWrite Off
            // ⚠ Always にすると、人形が ShowRoomProxy の壁・箱の裏へ回ったときに
            //   本体と投影影は隠れるのに**足元の楕円だけが壁の上に浮く**（投影シャドウ側と同じ理由で
            //   LEqual に揃える。床は色を書かないので z-fight しない）。
            ZTest LEqual
            Cull Off        // 床に寝かせた Quad の裏表を気にしなくて済む（向きの取り違えで消えない）

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _BlobDensity;
                float _BlobFeather;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // テクスチャは持たない（1 枚のためにアセットと帯域を増やす価値が無い）。
                float d = length(input.uv - 0.5) * 2.0;                 // 中心 0・縁 1
                float f = saturate((1.0 - d) / max(_BlobFeather, 0.01));
                float a = saturate(_BlobDensity) * f * f * (3.0 - 2.0 * f);   // smoothstep
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
