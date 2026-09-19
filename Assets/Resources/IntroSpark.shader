// 割れた破片から飛ぶ光の粒（canon/LEDGER.md 0235）。CPU のパーティクルは持たず、
// 時計 p（_Shatter）から頂点シェーダが決定的に動かす。1 粒 = 四角 1 枚のビルボード。
//
// 頂点属性（Assets/Scripts/Streaming/IntroSparkMesh.cs と同じ並び。片方だけ直すと黙って食い違う）:
//
// | 属性 | 中身 |
// |---|---|
// | POSITION | 放出点（覆いのローカル ±0.30）の xy・z = 0 |
// | TEXCOORD0 | 四隅の符号 (±1, ±1) |
// | TEXCOORD1 | (seedA, seedB, kind 0 = 流れ / 1 = 代表, heroIndex) |
// | TEXCOORD2 | (macroOrder, macroIndex, pieceNoise 0..1, size) |
// | TEXCOORD3 | (star 上位 20% = 1, sizeMul 0.6..1.8, deathP .62..80, paletteT 0..1) |
//
// ⚠ ビルボードも明滅の位相も**頭中心**で解く（眼の位置・UNITY_MATRIX_V・_WorldSpaceCameraPos・
//    unity_StereoEyeIndex を使わない）。眼ごとに別の粒が光ると立体視で焦点が合わない。
// ⚠ alpha は 0 を返し、Blend の alpha は Zero One。compositor の alpha を二度と開けない。
Shader "FixedCamVr/IntroSpark"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _HasFrozenFrame("Has frozen reality", Range(0, 1)) = 0
        _Spark("Spark level", Range(0, 1)) = 1
        _ScreenCenter("Screen center world", Vector) = (0, 0, 2, 0)
        _ScreenRight("Screen right world", Vector) = (1, 0, 0, 0)
        _ScreenUp("Screen up world", Vector) = (0, 1, 0, 0)
        _ScreenHalf("Screen half size", Vector) = (1, 0.56, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+907" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroSpark"
            Blend One One, Zero One
            BlendOp Add
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Art/Shaders/Intro/IntroFractureTime.hlsl"
            #include "Assets/Art/Shaders/Intro/IntroSpark.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 corner : TEXCOORD0;
                float4 seed : TEXCOORD1;
                float4 piece : TEXCOORD2;
                float4 look : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : TEXCOORD0;   // rgb = 粒の色 × 明るさ / a = 明るさ
                float2 corner : TEXCOORD1;
                nointerpolation float star : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _Shatter;
            float _HasFrozenFrame;
            float _Spark;
            float4x4 _CaptureHeadToWorld;
            float4 _CurrentHeadPosition;
            float4 _ScreenCenter;
            float4 _ScreenRight;
            float4 _ScreenUp;
            float4 _ScreenHalf;

            float SparkHash(float value)
            {
                return frac(sin(value * 78.233 + 12.9898) * 43758.5453);
            }

            float3 SparkSafeNormalize(float3 value, float3 fallback)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 1e-8 ? value * rsqrt(lengthSquared) : fallback;
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float p = saturate(_Shatter);
                float seedA = v.seed.x;
                float seedB = v.seed.y;
                float star = v.look.x;
                float death = v.look.z;

                // ---- 流れる粒。片の放出点から放射状に飛び、集結でスクリーンへ引かれて消える ----
                float3 shell = FractureShellPoint(v.positionOS.xy);
                float3 radial = FractureRadial(shell);
                float breakAt = PieceBreakAt(v.piece.x, v.piece.z);
                float elapsed = max(p - breakAt, 0.0);
                float u = WarpedTime(elapsed, max(p - PullBegin, 0.0));

                // 片の放射に 22 度の円錐の乱れを載せ、眼側へわずかに寄せる。
                float3 axis = SparkSafeNormalize(float3(radial.xy, 0.0), float3(0.0, 0.0, 1.0));
                float3 side = SparkSafeNormalize(float3(-axis.y, axis.x, 0.0), float3(1.0, 0.0, 0.0));
                float3 normal = float3(0.0, 0.0, 1.0);
                float coneAngle = radians(22.0) * sqrt(SparkHash(seedA * 3.71 + seedB * 7.13));
                float coneRoll = 6.2831853 * seedA;
                float3 direction = axis * cos(coneAngle)
                                 + (side * cos(coneRoll) + normal * sin(coneRoll)) * sin(coneAngle);
                direction.z -= 0.05 + 0.20 * seedB;
                direction = SparkSafeNormalize(direction, axis);

                // 片より 1.9〜3 倍速い。破片が漂っているあいだに視界を横切って先へ抜ける。
                float reach = lerp(0.60, 1.10, seedA);
                float3 flight = shell + direction * reach * u
                              + float3(sin(u * 4.0 + seedA * 6.28), cos(u * 3.0 + seedB * 6.28), 0.0) * 0.015;
                float flightRadius = length(flight);
                if (flightRadius > HeroMaxRadius)
                    flight *= HeroMaxRadius / max(flightRadius, 1e-4);

                float pullT = saturate((p - PullStart) / max(death - PullStart, 0.01));
                float travel = pow(pullT, 1.8);
                float3 screenRight = normalize(_ScreenRight.xyz);
                float3 screenUp = normalize(_ScreenUp.xyz);
                // 1 点に集めない（矩形の中へばらけて吸い込まれる）。
                float3 target = _ScreenCenter.xyz
                              + screenRight * (_ScreenHalf.x * 0.35 * (seedA * 2.0 - 1.0))
                              + screenUp * (_ScreenHalf.y * 0.35 * (seedB * 2.0 - 1.0));
                float3 flightWS = mul(_CaptureHeadToWorld, float4(flight, 1.0)).xyz;
                float3 posWS = lerp(flightWS, target, travel);

                float born = Ease(breakAt, breakAt + 0.010, p);
                float fade = 1.0 - Ease(death - 0.05, death, p);
                float flash = 0.8 * Ease(death - 0.03, death - 0.01, p)
                            * (1.0 - Ease(death - 0.01, death, p));
                float intensity = born * (lerp(0.42, 0.55, star) * fade + flash) * step(p, death);
                float sizeM = 0.006 * v.look.y;
                // 閃きの最後は小さく強く（点になって消える）。
                sizeM *= lerp(1.0, 0.4, Ease(death - 0.03, death - 0.01, p));

                // ---- 代表の粒。位置も明るさも IntroSpark.hlsl が決める（破片の反射と同じ出どころ）----
                if (v.seed.z > 0.5)
                {
                    float3 heroPos;
                    float heroIntensity;
                    HeroSpark((int)v.seed.w, p, _CaptureHeadToWorld, _ScreenCenter.xyz,
                              heroPos, heroIntensity);
                    posWS = heroPos;
                    intensity = heroIntensity * 0.7;
                    sizeM = 0.010;
                    star = 1.0;
                }

                // 眼に近すぎる粒は押し出す（IntroFracture.shader の破片と同じ扱い）。
                float3 fromEye = posWS - _CurrentHeadPosition.xyz;
                float eyeDistance = length(fromEye);
                if (eyeDistance < 0.55)
                {
                    float3 safeFromEye = eyeDistance > 1e-4
                        ? fromEye / eyeDistance : float3(0.0, 0.0, 1.0);
                    posWS = _CurrentHeadPosition.xyz + safeFromEye * 0.55;
                    fromEye = posWS - _CurrentHeadPosition.xyz;
                    eyeDistance = 0.55;
                }

                // 瞬き。位相に頭から見た向きを混ぜて、同じ時刻でも粒ごとに違う明滅にする。
                float3 toSpark = SparkSafeNormalize(fromEye, float3(0.0, 0.0, 1.0));
                // 向きは量子化してから hash に入れる（連続値のまま入れると毎コマ白色雑音になり、
                // 明滅ではなくストロボになる）。45 段/p ＝ 約 9 回/秒の瞬き。
                float gaze = dot(toSpark, float3(0.3, 0.9, 0.3));
                float twinkle = lerp(0.55, 1.0,
                    SparkHash(seedA * 13.0 + floor(gaze * 6.0) + floor(p * 45.0)));
                intensity *= twinkle;

                // 画素の大きさで切る（遠くて消える・近くて面になる を両方防ぐ）。
                float pixelsPerMeter = _ScreenParams.y * abs(unity_CameraProjection._m11) * 0.5
                                     / max(eyeDistance, 1e-3);
                if (pixelsPerMeter > 1e-3)
                    sizeM = clamp(sizeM, 3.0 / pixelsPerMeter, 14.0 / pixelsPerMeter);

                // ビルボードの基底は頭中心（眼ごとに向きを変えない）。
                float3 forward = toSpark;
                float3 right = cross(forward, float3(0.0, 1.0, 0.0));
                right = SparkSafeNormalize(right, float3(1.0, 0.0, 0.0));
                float3 up = cross(right, forward);
                float3 world = posWS + (right * v.corner.x + up * v.corner.y) * sizeM;

                float3 tint = lerp(SparkCoreColor, SparkPalette(v.look.w), 0.35);
                o.positionCS = TransformWorldToHClip(world);
                o.color = float4(tint * intensity, intensity);
                o.corner = v.corner;
                o.star = star;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d = length(i.corner);
                float core = pow(saturate(1.0 - d * d), 2.0);
                // 十字の光芒は上位 20% の粒だけ。
                float spikes = i.star * 0.35 * (
                      exp(-abs(i.corner.x) * 9.0) * exp(-i.corner.y * i.corner.y * 1.5)
                    + exp(-abs(i.corner.y) * 9.0) * exp(-i.corner.x * i.corner.x * 1.5));
                float shape = saturate(core + spikes);
                // 芯は生成り寄りの白、縁へ向かうほどパレットの色。
                float3 color = lerp(SparkCoreColor * i.color.a, i.color.rgb, saturate(d));
                float3 rgb = min(color * shape * _Spark, 0.6);
                return float4(rgb * step(0.5, _HasFrozenFrame), 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
