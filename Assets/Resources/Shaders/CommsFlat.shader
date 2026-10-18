Shader "FixedCamVr/CommsFlat"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _Recovery("Recovery amount and time", Vector) = (0,0,0,0)
        _RecoveryRect("Recovery panel rect", Vector) = (0,0,1,0.3)
        [HideInInspector] _Surface("Surface", Float) = 1
        [HideInInspector] _Blend("Blend", Float) = 0
        [HideInInspector] _SrcBlend("Source blend", Float) = 5
        [HideInInspector] _DstBlend("Destination blend", Float) = 10
        [HideInInspector] _SrcBlendAlpha("Source alpha blend", Float) = 1
        [HideInInspector] _DstBlendAlpha("Destination alpha blend", Float) = 10
        [HideInInspector] _ZWrite("Depth write", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Art/Shaders/Streaming/CommsRecovery.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _Recovery;
                float4 _RecoveryRect;
                float4x4 _RecoveryWorldToPanel;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 panelPos : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.panelPos = mul(_RecoveryWorldToPanel, float4(TransformObjectToWorld(input.positionOS.xyz), 1.0)).xy;
                return output;
            }
            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 color = _Color;
                if (_Recovery.x > 0.001)
                    color.rgb = lerp(color.rgb, float3(0.92,0.035,0.025), CommsRecoveryMask(input.panelPos, _Recovery, _RecoveryRect));
                return color;
            }
            ENDHLSL
        }
    }
}
