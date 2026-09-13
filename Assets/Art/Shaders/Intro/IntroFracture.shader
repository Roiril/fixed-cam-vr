// 段 4 の破片。Passthrough Windows の alpha だけで、現実の面が割れてスクリーンへ収束する。
// UV1 は微細片の中心と面積、UV2 は大面の中心・波の開始差・面番号を持つ。
Shader "FixedCamVr/IntroFracture"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _VeilSize("Veil size m (xy) / distance (z)", Vector) = (2, 2, 0.3, 0)
        _ScreenCenter("Screen center", Vector) = (0, 0, 2, 0)
        _ScreenRight("Screen right", Vector) = (1, 0, 0, 0)
        _ScreenUp("Screen up", Vector) = (0, 1, 0, 0)
        _ScreenHalf("Screen half size", Vector) = (1, 0.56, 0, 0)
        _ScreenPlane0("Screen edge plane 0", Vector) = (0, 0, -1, 0)
        _ScreenPlane1("Screen edge plane 1", Vector) = (0, 0, -1, 0)
        _ScreenPlane2("Screen edge plane 2", Vector) = (0, 0, -1, 0)
        _ScreenPlane3("Screen edge plane 3", Vector) = (0, 0, -1, 0)
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
                float2 local : TEXCOORD0;
                float closed : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _Shatter;
            float4 _VeilSize;
            float4 _ScreenCenter;
            float4 _ScreenRight;
            float4 _ScreenUp;
            float4 _ScreenHalf;
            float4 _ScreenPlane0;
            float4 _ScreenPlane1;
            float4 _ScreenPlane2;
            float4 _ScreenPlane3;

            float EaseOutCubic(float t)
            {
                t = saturate(t);
                float r = 1.0 - t;
                return 1.0 - r * r * r;
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

                // 大面の内部は後からほどく。各三角形は中心を共有せず、自分の中心へ連続して細くなる。
                float pieceDelay = (v.macro.z / 0.14 - 0.5) * 0.04 + pieceNoise.x * 0.02;
                float loosen = smoothstep(0.22 + pieceDelay, 0.48 + pieceDelay, p);
                float2 relative = pos - movingCenter;
                relative *= lerp(1.0, 0.82, loosen);

                // 微細片の中心から、見かけ上もっとも近いスクリーン辺を求める。
                float3 source = float3(movingCenter * _VeilSize.xy, _VeilSize.z);
                float2 targetOnPlane;
                float3 targetOnScreen;
                IntroShardTarget(source, _ScreenCenter.xyz, normalize(_ScreenRight.xyz),
                    normalize(_ScreenUp.xyz), _ScreenHalf.xy, _VeilSize.z,
                    targetOnPlane, targetOnScreen);
                float2 target = targetOnPlane / max(_VeilSize.xy, float2(1e-4, 1e-4));
                float2 delta = target - movingCenter;
                float travelDistance = length(delta);
                float2 direction = travelDistance > 1e-5 ? delta / travelDistance : float2(1.0, 0.0);
                float2 sideways = float2(-direction.y, direction.x);

                float travel = smoothstep(0.32 + pieceDelay, 0.82, p);
                float curveSign = pieceNoise.x < 0.5 ? -1.0 : 1.0;
                float curve = sin(travel * PI) * travelDistance * 0.06 * curveSign;
                movingCenter += delta * travel + sideways * curve;

                // 収束中の面積を減らし、重なった幕にならないよう短い片へ変える。
                float spin = radians((pieceNoise.y * 2.0 - 1.0) * 4.0) * travel;
                relative = IntroShardSpin2(relative, spin);
                float along = dot(relative, direction);
                float across = dot(relative, sideways);
                float remaining = max(1.0 - travel, 0.001);
                relative = direction * along * pow(remaining, 1.10)
                         + sideways * across * pow(remaining, 1.45);

                // 辺に着いた破片は細い継ぎ目へ畳まれ、p=.84 で完全に閉じる。
                float closed = smoothstep(0.76, 0.84, p);
                relative = direction * dot(relative, direction) * lerp(1.0, 0.28, closed)
                         + sideways * dot(relative, sideways) * lerp(1.0, 0.03, closed);
                movingCenter = lerp(movingCenter, target, closed);
                pos = movingCenter + relative;

                o.local = pos;
                o.closed = closed;
                o.positionCS = TransformObjectToHClip(float3(pos, 0.0));
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 dir = normalize(float3(i.local * _VeilSize.xy, _VeilSize.z));
                float screenDistance = max(
                    max(dot(dir, _ScreenPlane0.xyz), dot(dir, _ScreenPlane1.xyz)),
                    max(dot(dir, _ScreenPlane2.xyz), dot(dir, _ScreenPlane3.xyz)));

                // スクリーン矩形は base quad が正確に透かす。跨いだ破片も fragment 単位で塞ぐ。
                float onScreen = 1.0 - step(0.0, screenDistance);
                float alpha = max(saturate(i.closed), onScreen);
                return float4(0.0, 0.0, 0.0, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
