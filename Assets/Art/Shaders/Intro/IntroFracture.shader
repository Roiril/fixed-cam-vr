// 段 4 の破片。Passthrough Windows の alpha だけで、現実の全面が割れてスクリーンへ再構成される。
// UV1 は微細片の中心と面積、UV2 は大面の中心・波の開始差・面番号を持つ。
Shader "FixedCamVr/IntroFracture"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _ScreenFade("Screen crossfade", Range(0, 1)) = 0
        _VeilSize("Veil size m (xy) / distance (z)", Vector) = (2, 2, 0.3, 0)
        _ScreenCenter("Screen center", Vector) = (0, 0, 2, 0)
        _ScreenRight("Screen right", Vector) = (1, 0, 0, 0)
        _ScreenUp("Screen up", Vector) = (0, 1, 0, 0)
        _ScreenHalf("Screen half size", Vector) = (1, 0.56, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+900" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroFracture"
            Blend Zero SrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "IntroShatter.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 small : TEXCOORD1;
                float4 macro : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _Shatter;
            float _ScreenFade;
            float4 _VeilSize;
            float4 _ScreenCenter;
            float4 _ScreenRight;
            float4 _ScreenUp;
            float4 _ScreenHalf;
            float2 ScreenAngle(float2 local)
            {
                return atan(local * (2.0 / 0.30)) / atan(2.0);
            }

            float3 ScreenPoint(float2 local)
            {
                float2 angle = ScreenAngle(local);
                return _ScreenCenter.xyz
                     + normalize(_ScreenRight.xyz) * (_ScreenHalf.x * angle.x)
                     + normalize(_ScreenUp.xyz) * (_ScreenHalf.y * angle.y);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float p = saturate(_Shatter);
                float2 macroCenter = v.macro.xy;
                float2 smallCenter = v.small.xy;
                float2 macroNoise = IntroShardHash2(float2(v.macro.w, 17.0), 3.7);
                float2 pieceNoise = IntroShardHash2(smallCenter, v.macro.w + 11.0);

                // 左上寄りの起点から UV2.z の順で大面の亀裂が走る。最後の面も p=.24 で開く。
                float macroStart = 0.02 + saturate(v.macro.z / 0.14) * 0.14;
                float macroOpen = smoothstep(macroStart, 0.24, p);
                float macroAngle = radians((macroNoise.x * 2.0 - 1.0) * 0.30) * macroOpen;
                float2 macroDrift = (macroNoise * 2.0 - 1.0) * 0.0007 * macroOpen;

                float2 pos = macroCenter
                           + IntroShardSpin2(v.positionOS.xy - macroCenter, macroAngle) * (1.0 - 0.008 * macroOpen)
                           + macroDrift;
                float2 movingCenter = macroCenter
                                    + IntroShardSpin2(smallCenter - macroCenter, macroAngle) * (1.0 - 0.008 * macroOpen)
                                    + macroDrift;

                // 大面の内部は後からほどく。開始差は残すが、移動は全片で同じ時計を使う。
                float pieceDelay = (v.macro.z / 0.14 - 0.5) * 0.04 + pieceNoise.x * 0.02;
                float loosen = smoothstep(0.20 + pieceDelay, 0.40 + pieceDelay, p);
                float travel = smoothstep(0.30, 0.82, p);
                float seal = smoothstep(0.66, 0.82, p);

                // 元の角度空間をスクリーン実平面へ戻す。同じ元頂点は必ず同じ終点になるため、
                // p=.82 で全共有辺が隙間なく閉じ、大面変形も残らない。
                float3 sourceCenter = float3(movingCenter * _VeilSize.xy, _VeilSize.z);
                float3 sourceVertex = float3(pos * _VeilSize.xy, _VeilSize.z);
                float3 targetCenter = ScreenPoint(smallCenter);
                float3 targetVertex = ScreenPoint(v.positionOS.xy);
                float3 delta = targetCenter - sourceCenter;
                float travelDistance = length(delta);

                float3 screenNormal = normalize(cross(normalize(_ScreenRight.xyz), normalize(_ScreenUp.xyz)));
                float3 sideways = cross(screenNormal, delta);
                float sidewaysLength = length(sideways);
                sideways = sidewaysLength > 1e-5 ? sideways / sidewaysLength : normalize(_ScreenRight.xyz);
                float curveSign = pieceNoise.x < 0.5 ? -1.0 : 1.0;
                float curveVariation = 0.88 + saturate(pieceDelay * 8.0 + 0.5) * 0.24;
                float curve = sin(travel * PI) * travelDistance * 0.025 * curveSign * curveVariation;
                float3 centerHead = lerp(sourceCenter, targetCenter, travel) + sideways * curve;

                float3 sourceRelative = sourceVertex - sourceCenter;
                float3 targetRelative = targetVertex - targetCenter;
                float3 relativeHead = lerp(sourceRelative, targetRelative, travel);
                float spin = radians((pieceNoise.y * 2.0 - 1.0) * 3.0) * sin(travel * PI);
                relativeHead = IntroShardSpin3(relativeHead, screenNormal, spin);

                // 隙間は内部分離で開き、移動中も18%以内に留め、再構成の終端でゼロへ戻す。
                float gap = loosen * (0.06 + 0.12 * sin(travel * PI)) * (1.0 - seal);
                float3 head = centerHead + relativeHead * (1.0 - gap);
                float3 objectPosition = float3(
                    head.xy / max(_VeilSize.xy, float2(1e-4, 1e-4)),
                    head.z - _VeilSize.z);
                o.positionCS = TransformObjectToHClip(objectPosition);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // 全片を同じ量で現実から映像へ溶かす。中央を除外しない。
                return float4(0.0, 0.0, 0.0, saturate(_ScreenFade));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
