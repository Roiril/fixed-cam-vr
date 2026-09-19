Shader "FixedCamVr/UnauthorizedAccessFx"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1, 1, 1, 1)
        _Mode("Mode", Float) = 0
        _Strength("Strength", Float) = 1
        _ReadMaskStrength("Read area attenuation", Float) = 0
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
            float4 _MainTex_TexelSize;
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Mode;
                float _Strength;
                float _ReadMaskStrength;
                float4x4 _ReadWorldToLocal;
                float4 _ReadRect0;
                float4 _ReadRect1;
                float4 _ReadRect2;
                float4 _ReadRect3;
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
                float3 positionWS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
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
            float ReadRectCoverage(float2 planePoint, float4 rect)
            {
                if (rect.z <= 0 || rect.w <= 0) return 0;
                float2 outside = abs(planePoint - rect.xy) - rect.zw;
                return 1 - smoothstep(0, .06, max(outside.x, outside.y));
            }
            float ReadMask(float3 positionWS)
            {
                if (_ReadMaskStrength < .001) return 1;
                // Project through the fragment onto the text plane from the current eye.
                // This preserves depth while protecting letters even after head movement.
                float3 eye = mul(_ReadWorldToLocal, float4(GetCameraPositionWS(), 1)).xyz;
                float3 fragmentLocal = mul(_ReadWorldToLocal, float4(positionWS, 1)).xyz;
                float3 ray = fragmentLocal - eye;
                if (abs(ray.z) < .0001) return 1;
                float travel = -eye.z / ray.z;
                if (travel < 0) return 1;
                float2 onPlane = eye.xy + ray.xy * travel;
                float coverage = max(ReadRectCoverage(onPlane, _ReadRect0), ReadRectCoverage(onPlane, _ReadRect1));
                coverage = max(coverage, max(ReadRectCoverage(onPlane, _ReadRect2), ReadRectCoverage(onPlane, _ReadRect3)));
                return 1 - coverage * saturate(_ReadMaskStrength);
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tint = input.color * _Color;
                tint.a *= ReadMask(input.positionWS);
                if (_Mode > 1.5)
                {
                    // A narrow dark edge keeps the real letterforms legible over bright footage.
                    half ink = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;
                    float2 delta = _MainTex_TexelSize.xy * 7;
                    half edge = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(delta.x, 0)).a;
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv - float2(delta.x, 0)).a);
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(0, delta.y)).a);
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv - float2(0, delta.y)).a);
                    float2 diagonal = delta * .7071;
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + diagonal).a);
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv - diagonal).a);
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(diagonal.x, -diagonal.y)).a);
                    edge = max(edge, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(-diagonal.x, diagonal.y)).a);
                    half opacity = max(ink, edge);
                    half3 light = lerp(half3(.008, .003, .005), tint.rgb, saturate(ink / max(opacity, .0001)));
                    return half4(light, saturate(opacity * tint.a * _Strength));
                }
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
