Shader "FixedCamVr/UnauthorizedAccessFx"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1, 1, 1, 1)
        _Mode("Mode", Float) = 0
        _Strength("Strength", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent+450" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "UnauthorizedAccessFx"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Mode;
                float _Strength;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 glyph : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 glyph : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.glyph = input.glyph;
                output.color = input.color;
                return output;
            }
            float RoundedBox(float2 pos, float2 extent, float radius)
            {
                float2 q = abs(pos) - extent + radius;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
            }
            float Stroke(float2 pos, float2 a, float2 b, float radius)
            {
                float2 delta = b - a;
                float h = saturate(dot(pos-a, delta) / dot(delta, delta));
                return length(pos - a - delta*h) - radius;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tint = input.color * _Color;
                if (_Mode < .5)
                {
                    half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                    return half4(texel.rgb * tint.rgb, saturate(texel.a * tint.a * _Strength));
                }
                float2 pos = (input.uv - .5) * 2;
                // Narrow rectangular phosphor zero and the hooked upright one from the reference.
                float zero = abs(RoundedBox(pos, float2(.40, .63), .065)) - .041;
                float one = min(Stroke(pos, float2(.11,-.65), float2(.11,.65), .047),
                    min(Stroke(pos, float2(-.18,.45), float2(.11,.65), .047),
                        Stroke(pos, float2(-.20,-.65), float2(.37,-.65), .047)));
                float distanceToInk = lerp(zero, one, step(.5, input.glyph.x));
                float blur = saturate(input.glyph.y);
                float softness = lerp(.003, .15, blur) + max(fwidth(distanceToInk), .002);
                float core = saturate(.5 - distanceToInk / softness);
                float halo = exp(-max(distanceToInk, 0) * lerp(23, 8, blur)) * .30;
                float opacity = saturate(core * (1 - blur*.45) + halo);
                float2 rim = min(input.uv, 1-input.uv);
                opacity *= smoothstep(0, .08, min(rim.x, rim.y));
                half3 light = lerp(tint.rgb, half3(1,.38,.25), core * .32 * (1-blur));
                return half4(light, opacity * tint.a * _Strength);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
