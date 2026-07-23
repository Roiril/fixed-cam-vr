// 描画パッド RT へのオフスクリーン・スタンプ描画専用（CommandBuffer 直描画・カメラ非経由）。
// Pass 0 = 描く（柔らか円 × _Color を alpha ブレンド）/ Pass 1 = 消す（dst を (1-円alpha) 倍 = 色も alpha も抜く）。
Shader "TableDuoVr/PadStamp"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        // Pass 0: 描く
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.uv = IN.uv;
                return o;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float d = length(IN.uv - 0.5);
                float a = 1.0 - smoothstep(0.75, 1.0, d * 2.0);
                return float4(_Color.rgb, _Color.a * a);
            }
            ENDHLSL
        }

        // Pass 1: 消す（Blend Zero OneMinusSrcAlpha → dst *= (1 - srcAlpha)）
        Pass
        {
            Blend Zero OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.uv = IN.uv;
                return o;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float d = length(IN.uv - 0.5);
                float a = 1.0 - smoothstep(0.75, 1.0, d * 2.0);
                return float4(0.0, 0.0, 0.0, a);
            }
            ENDHLSL
        }
    }
}
