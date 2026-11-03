// 裸電球の光の筋（舞う埃に光が当たって見える霞）。箱の中を視線に沿って積分する。
// 光の量と影は URP の追加ライトそのもの（GetAdditionalLight）なので、人やカーテンの影が筋に抜ける。
Shader "FixedCamVr/Twin/Volume"
{
    Properties
    {
        _Density ("Density", Float) = 0.08
        _Anisotropy ("Forward scattering", Range(0, 0.9)) = 0.45
        _NoiseScale ("Haze scale", Float) = 1.6
        _Tint ("Tint", Color) = (1,1,1,1)
        _BoxMin ("Box min (world)", Vector) = (-2,0,-2,0)
        _BoxMax ("Box max (world)", Vector) = (2,3,2,0)
        _HazeTex ("Haze (tiling 3D noise)", 3D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+50" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Volume"
            Tags { "LightMode"="UniversalForward" }
            Blend One One
            ZWrite Off ZTest Always Cull Front
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "TwinCommon.hlsl"

            TEXTURE3D(_HazeTex); SAMPLER(sampler_HazeTex);
            CBUFFER_START(UnityPerMaterial)
                float _Density, _Anisotropy, _NoiseScale;
                float4 _Tint, _BoxMin, _BoxMax;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float HenyeyGreenstein(float g, float cosTheta)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * PI * pow(abs(1.0 + g2 - 2.0 * g * cosTheta), 1.5));
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.positionWS - ro);
                float3 inv = 1.0 / rd;
                float3 t0 = (_BoxMin.xyz - ro) * inv;
                float3 t1 = (_BoxMax.xyz - ro) * inv;
                float3 tmin = min(t0, t1), tmax = max(t0, t1);
                float tNear = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                float tFar = min(min(tmax.x, tmax.y), tmax.z);

                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                float raw = SampleSceneDepth(suv);
                float eye = LinearEyeDepth(raw, _ZBufferParams);
                float3 fwd = -UNITY_MATRIX_V[2].xyz;
                float sceneT = eye / max(dot(rd, fwd), 1e-3);
                tFar = min(tFar, sceneT);
                if (tFar <= tNear) return 0;

                const int STEPS = 36;
                float stepLen = (tFar - tNear) / STEPS;
                float jitter = InterleavedGradientNoise(i.positionCS.xy, (uint)(_TwinTime * 30.0) % 64u);
                float3 acc = 0;
                // 霞は主光（電球の真下向きのスポット）だけで積分する。光源が増えるたびに全部の影を毎段引くと
                // GPU が時間切れで落ちる（2026-11-04 に 2 回）。主光は色×強さ（_TwinKeyColor）で見分ける
                uint count = GetAdditionalLightsCount();
                int keyIndex = -1;
                for (uint k = 0u; k < count; k++)
                {
                    int li = GetPerObjectLightIndex(k);
                    Light probe = GetAdditionalPerObjectLight(li, ro);
                    if (distance(probe.color, _TwinKeyColor.rgb) < 0.02 * max(length(_TwinKeyColor.rgb), 1e-3)) keyIndex = li;
                }
                if (keyIndex < 0) return 0;
                for (int s = 0; s < STEPS; s++)
                {
                    float t = tNear + (s + jitter) * stepLen;
                    float3 p = ro + rd * t;
                    // 霞のむら（ゆっくり上へ流れる）。床近くは少し濃く、天井付近は薄く
                    float3 q = p * _NoiseScale + float3(0.0, -_TwinTime * 0.05, _TwinTime * 0.02);
                    float haze = 0.15 + 1.4 * SAMPLE_TEXTURE3D_LOD(_HazeTex, sampler_HazeTex, q * 0.25, 0).r;
                    float height = lerp(1.15, 0.7, saturate((p.y - _BoxMin.y) / max(_BoxMax.y - _BoxMin.y, 0.01)));
                    float density = _Density * haze * height * (0.06 + 0.94 * TwinKeyCone(p));
                    Light l = GetAdditionalPerObjectLight(keyIndex, p);
                    float shadow = AdditionalLightRealtimeShadow(keyIndex, p, l.direction);
                    float phase = HenyeyGreenstein(_Anisotropy, dot(l.direction, rd)) * 4.0 * PI;
                    // 光源の数十 cm 以内は 1/d² が発散して塊になるので上限で切る。上限は電球際だけに効く値にし、
                    // 床際の霞は電球際の 2 割前後まで落ちる（上限を低くすると円錐全体が同じ明るさの板になる）
                    acc += l.color * (min(l.distanceAttenuation, 4.0) * shadow * phase * density);
                }
                float3 color = acc * stepLen * _Tint.rgb;
                // 霧の向こうの筋は霧に沈む（面と同じ式）
                float mid = 0.5 * (tNear + tFar);
                color *= exp(-_TwinFogDensity * mid);
                return float4(color, 0);
            }
            ENDHLSL
        }
    }
}
