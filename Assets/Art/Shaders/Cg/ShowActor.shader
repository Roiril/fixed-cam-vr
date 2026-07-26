// CG 人形（ShowCg レイヤ）専用のシェーディング。
//
// **シーンのライトを一切参照しない**のが要点。現場の照明・URP 設定・他アプリ（TableDuo）の都合で
// 人形の見えが変わると演出が壊れるため、方向・強さを固定したライティングをマテリアルに焼く。
// 逆に「CG レイヤ用のライトを置く」方式は採らない（URP は Light.cullingMask を尊重せず実シーンを汚す）。
//
// 合成は ScreenComposite の 3 層目（_CgTex）で、**ポスト FX の前**。露出・走査線・グレインは
// そちらで一括して浴びるので、ここでは素の色だけを出す。
Shader "FixedCamVr/ShowActor"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.72, 0.70, 0.67, 1)
        _ShadeColor("Shade Color", Color) = (0.10, 0.10, 0.12, 1)
        _RimColor("Rim Color", Color) = (0.85, 0.85, 0.90, 1)
        // ライト方向（ワールド）。既定は「やや上手前から」。
        _LightDir("Light Direction (world)", Vector) = (0.35, 0.85, -0.40, 0)
        _Wrap("Light Wrap", Range(0, 1)) = 0.45
        _Rim("Rim Strength", Range(0, 1)) = 0.25
        _RimPower("Rim Power", Range(1, 8)) = 3
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "ShowActor"
            Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShadeColor;
                float4 _RimColor;
                float4 _LightDir;
                float _Wrap;
                float _Rim;
                float _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionHCS = TransformWorldToHClip(posWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.viewWS = GetWorldSpaceViewDir(posWS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 n = normalize(input.normalWS);
                float3 l = normalize(_LightDir.xyz);
                float3 v = normalize(input.viewWS);

                // wrap lighting: 影側を完全に潰さない（監視カメラの粗い絵で形が読める程度に残す）
                float ndl = dot(n, l);
                float t = saturate((ndl + _Wrap) / (1.0 + _Wrap));
                half3 col = lerp(_ShadeColor.rgb, _BaseColor.rgb, t);

                // リム: 輪郭をわずかに立てる（映像に埋もれて「居るのに見えない」を防ぐ）
                float rim = pow(saturate(1.0 - saturate(dot(n, v))), _RimPower) * _Rim;
                col += _RimColor.rgb * rim;

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
