Shader "FixedCamVr/OutroReportMedia"
{
    Properties
    {
        _MainTex("Texture", 2D) = "black" {}
        _Mode("0=Black 1=Logo 2=Photo", Float) = 0
        _Alpha("Alpha", Range(0, 1)) = 0
        _SourceAspect("Source aspect", Float) = 1.7777778
        _TargetAspect("Target aspect", Float) = 1.7777778
        _LogoInk("Logo ink", Color) = (0.88, 0.82, 0.71, 1)
        _LogoAccent("Logo accent", Color) = (0.62, 0.03, 0.035, 1)
    }

    SubShader
    {
        Tags { "Queue"="Overlay-2" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _Mode;
            float _Alpha;
            float _SourceAspect;
            float _TargetAspect;
            float4 _LogoInk;
            float4 _LogoAccent;

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

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float alpha = saturate(_Alpha);
                if (_Mode < 0.5) return half4(0, 0, 0, alpha);

                if (_Mode < 1.5)
                {
                    float4 packed = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                    float mainInk = saturate(max(packed.r, packed.a));
                    float accentInk = saturate(packed.g);
                    float logoAlpha = saturate(max(mainInk, accentInk)) * alpha;
                    float3 rgb = lerp(_LogoInk.rgb, _LogoAccent.rgb,
                        accentInk / max(mainInk + accentInk, 0.001));
                    return half4(rgb, logoAlpha);
                }

                float sourceAspect = max(_SourceAspect, 0.001);
                float targetAspect = max(_TargetAspect, 0.001);
                float2 uv = i.uv;
                float inside = 1;
                if (targetAspect > sourceAspect)
                {
                    float width = sourceAspect / targetAspect;
                    inside = step((1 - width) * 0.5, uv.x) * step(uv.x, (1 + width) * 0.5);
                    uv.x = (uv.x - 0.5) / width + 0.5;
                }
                else
                {
                    float height = targetAspect / sourceAspect;
                    inside = step((1 - height) * 0.5, uv.y) * step(uv.y, (1 + height) * 0.5);
                    uv.y = (uv.y - 0.5) / height + 0.5;
                }
                float4 photo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, saturate(uv));
                return half4(photo.rgb, photo.a * alpha * inside);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
