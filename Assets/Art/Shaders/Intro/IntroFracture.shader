// 段 4 の破片。撮影時の実景を厚み付きの破片へ貼り、現在のスクリーン実平面へ再構成する。
Shader "FixedCamVr/IntroFracture"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _ScreenFade("Screen crossfade", Range(0, 1)) = 0
        _HasFrozenFrame("Has frozen reality", Range(0, 1)) = 0
        _PhotoBrightness("Frozen frame brightness", Range(0.5, 1.5)) = 0.96
        _PhotoContrast("Frozen frame contrast", Range(0.5, 1.5)) = 1.04
        _VeilSize("Veil size m (xy) / distance (z)", Vector) = (2, 2, 0.3, 0)
        _ScreenCenter("Screen center world", Vector) = (0, 0, 2, 0)
        _ScreenRight("Screen right world", Vector) = (1, 0, 0, 0)
        _ScreenUp("Screen up world", Vector) = (0, 1, 0, 0)
        _ScreenHalf("Screen half size", Vector) = (1, 0.56, 0, 0)
        [HideInInspector] _SrcBlend("Source blend", Float) = 0
        [HideInInspector] _DstBlend("Destination blend", Float) = 5
        [HideInInspector] _SrcBlendAlpha("Source alpha blend", Float) = 0
        [HideInInspector] _DstBlendAlpha("Destination alpha blend", Float) = 5
        [HideInInspector] _ZWrite("Z write", Float) = 0
        [HideInInspector] _ZTest("Z test", Float) = 8
        [HideInInspector] _ColorMask("Color mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+901" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroFracture"
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            BlendOp Add
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            ColorMask [_ColorMask]
            Cull Back

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
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 small : TEXCOORD1;
                float4 macro : TEXCOORD2;
                float4 surface : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 leftUv : TEXCOORD2;
                float3 rightUv : TEXCOORD3;
                float2 projectionValidity : TEXCOORD4;
                nointerpolation float surface : TEXCOORD5;
                float detail : TEXCOORD6;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_FrozenLeftTex);
            SAMPLER(sampler_FrozenLeftTex);
            TEXTURE2D(_FrozenRightTex);
            SAMPLER(sampler_FrozenRightTex);

            float _Shatter;
            float _ScreenFade;
            float _HasFrozenFrame;
            float _PhotoBrightness;
            float _PhotoContrast;
            float4 _VeilSize;
            float4 _ScreenCenter;
            float4 _ScreenRight;
            float4 _ScreenUp;
            float4 _ScreenHalf;
            float4 _CurrentHeadPosition;
            float4x4 _CaptureHeadToWorld;
            float4x4 _LeftWorldToUv;
            float4x4 _RightWorldToUv;

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

            float3 ShellPoint(float2 local)
            {
                return normalize(float3(local / 0.15, 1.0)) * 1.6;
            }

            float3 Bezier(float3 a, float3 b, float3 c, float3 d, float t)
            {
                float u = 1.0 - t;
                return u * u * u * a + 3.0 * u * u * t * b
                     + 3.0 * u * t * t * c + t * t * t * d;
            }

            float3 SafeNormalize(float3 value, float3 fallback)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 1e-8 ? value * rsqrt(lengthSquared) : fallback;
            }

            float Ease(float from, float to, float value)
            {
                float t = saturate((value - from) / (to - from));
                return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
            }

            float EaseIntegral(float t)
            {
                return t * t * t * t * (2.5 + t * (t - 3.0));
            }

            float OrbitProgress(float p)
            {
                // 角速度の山を積分する。最大開口で最速、四角形になる前に回転を終える。
                const float begin = 0.12;
                const float peak = 0.42;
                const float end = 0.68;
                float up = peak - begin;
                float down = end - peak;
                float a = saturate((p - begin) / up);
                float b = saturate((p - peak) / down);
                return saturate((up * EaseIntegral(a) + down * (b - EaseIntegral(b)))
                    / (0.5 * (up + down)));
            }

            float2 ProjectFrozenUv(float4x4 worldToUv, float3 captureWorld, out float valid)
            {
                float4 q = mul(worldToUv, float4(captureWorld, 1.0));
                float safeW = abs(q.w) > 1e-5 ? q.w : (q.w < 0.0 ? -1e-5 : 1e-5);
                valid = min(q.z, q.w);
                return q.xy / safeW;
            }

            float3 FrozenScreenPoint(float3 captureWorld)
            {
                float leftValid;
                float rightValid;
                float2 leftUv = ProjectFrozenUv(_LeftWorldToUv, captureWorld, leftValid);
                float2 rightUv = ProjectFrozenUv(_RightWorldToUv, captureWorld, rightValid);
                float2 uv = (leftUv + rightUv) * 0.5;
                return _ScreenCenter.xyz
                     + normalize(_ScreenRight.xyz) * (_ScreenHalf.x * (uv.x * 2.0 - 1.0))
                     + normalize(_ScreenUp.xyz) * (_ScreenHalf.y * (uv.y * 2.0 - 1.0));
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float p = saturate(_Shatter);
                float2 macroNoise = IntroShardHash2(float2(v.macro.w, 17.0), 3.7);
                float2 pieceNoise = IntroShardHash2(v.small.xy, v.macro.w + 11.0);
                float3 rawVertex = ShellPoint(v.positionOS.xy);
                float3 rawPieceCenter = ShellPoint(v.small.xy);
                float3 rawMacroCenter = ShellPoint(v.macro.xy);
                float3 macroRay = normalize(rawMacroCenter);
                float3 macroTangentX = normalize(float3(1.0, 0.0,
                    -macroRay.x / max(macroRay.z, 1e-4)));
                float3 macroTangentY = normalize(cross(macroRay, macroTangentX));

                // 起点から裂け、片が外へ離れた後に同じ片がスクリーンへ戻る。
                float macroStart = 0.02 + v.macro.z * (0.16 / 0.14);
                float crack = Ease(macroStart, macroStart + 0.055, p);
                float loosen = Ease(0.10 + v.macro.z * 0.30,
                    0.28 + pieceNoise.x * 0.03, p);
                float launch = Ease(0.12 + v.macro.z * 0.35, 0.42, p);
                float largePiece = smoothstep(0.016, 0.055, v.small.z);
                float macroAngle = radians(lerp(2.0, 5.0, macroNoise.y))
                                 * (macroNoise.x < 0.5 ? -1.0 : 1.0) * crack;
                float3 macroAxis = SafeNormalize(
                    lerp(macroTangentX, macroTangentY, macroNoise.x), macroTangentX);
                float3 peelOffset = macroRay * 0.018 * crack;
                float3 sourceVertex = rawMacroCenter
                    + IntroShardSpin3(rawVertex - rawMacroCenter, macroAxis, macroAngle) + peelOffset;
                float3 sourcePieceCenter = rawMacroCenter
                    + IntroShardSpin3(rawPieceCenter - rawMacroCenter, macroAxis, macroAngle) + peelOffset;

                float2 slope = rawPieceCenter.xy / max(rawPieceCenter.z, 0.01);
                float2 radial = slope - float2(-0.16, 0.12);
                radial /= max(length(radial), 1e-4);
                float2 tangent = float2(-radial.y, radial.x);
                float edgeDistance = smoothstep(0.35, 1.25, length(slope));
                float lateral = lerp(0.80, 1.08, pieceNoise.x) + v.small.z * 2.0;
                // 周縁の片は奥へ逃がす。大きな片の一部だけが少し手前を通る。
                float depth = lerp(0.26, -0.26, largePiece) + edgeDistance * 0.95
                            + (pieceNoise.y - 0.5) * 0.18;
                float3 burst = float3(radial * lateral + tangent * 0.08, depth);
                sourcePieceCenter += burst * launch;

                float travelStart = 0.42 + v.macro.z * 0.08 + pieceNoise.x * 0.012;
                float travelEnd = lerp(0.79, 0.82, largePiece);
                float travel = Ease(travelStart, travelEnd, p);
                float alignment = Ease(0.40, 0.68, p);
                float seal = Ease(0.60, 0.82, p);
                float detail = crack * (1.0 - seal);
                float3 startCenter = mul(_CaptureHeadToWorld, float4(sourcePieceCenter, 1.0)).xyz;
                float3 sourceRelative = mul((float3x3)_CaptureHeadToWorld,
                    sourceVertex - sourcePieceCenter + burst * launch);
                float3 captureWorld = mul(_CaptureHeadToWorld, float4(rawVertex, 1.0)).xyz;
                float3 captureCenterWorld = mul(_CaptureHeadToWorld, float4(rawPieceCenter, 1.0)).xyz;
                float3 targetCenter = _HasFrozenFrame > 0.5
                    ? FrozenScreenPoint(captureCenterWorld) : ScreenPoint(v.small.xy);
                float3 targetVertex = _HasFrozenFrame > 0.5
                    ? FrozenScreenPoint(captureWorld) : ScreenPoint(v.positionOS.xy);
                float3 screenRight = normalize(_ScreenRight.xyz);
                float3 screenUp = normalize(_ScreenUp.xyz);
                float3 screenNormal = normalize(cross(screenRight, screenUp));
                if (dot(screenNormal, targetCenter - _CurrentHeadPosition.xyz) < 0.0)
                    screenNormal = -screenNormal;

                float3 worldBurst = mul((float3x3)_CaptureHeadToWorld, burst);
                float3 drift = (screenRight * tangent.x + screenUp * tangent.y) * 0.10;
                float3 control1 = startCenter + worldBurst * 0.14 + drift;
                float3 control2 = targetCenter - screenNormal * lerp(0.24, 0.42, pieceNoise.y) + drift * 0.35;
                float3 centerWorld = Bezier(startCenter, control1, control2, targetCenter, travel);

                // 中央を時計回りに囲み、元の写真の位置まで半径を単調に縮める。
                // 回転した直線経路では一度集まりすぎて再拡大するため、角度と半径を分ける。
                float3 fromScreen = startCenter - _ScreenCenter.xyz;
                float3 toScreen = targetCenter - _ScreenCenter.xyz;
                float2 orbitStart = float2(dot(fromScreen, screenRight), dot(fromScreen, screenUp));
                float2 orbitTarget = float2(dot(toScreen, screenRight), dot(toScreen, screenUp));
                float angleDelta = atan2(orbitStart.x * orbitTarget.y - orbitStart.y * orbitTarget.x,
                    dot(orbitStart, orbitTarget)) - TWO_PI;
                float orbitAngle = atan2(orbitStart.y, orbitStart.x) + angleDelta * OrbitProgress(p);
                float orbitRadius = lerp(length(orbitStart), length(orbitTarget), travel);
                float orbitSin, orbitCos;
                sincos(orbitAngle, orbitSin, orbitCos);
                float orbitDepth = dot(centerWorld - _ScreenCenter.xyz, screenNormal);
                centerWorld = _ScreenCenter.xyz + screenRight * (orbitRadius * orbitCos)
                            + screenUp * (orbitRadius * orbitSin) + screenNormal * orbitDepth;
                // 終端を厳密に揃える。進みを戻す回転や、貼り付け直す別面は使わない。
                if (p <= 0.12) centerWorld = startCenter;
                if (travel >= 1.0) centerWorld = targetCenter;
                float3 fromEye = centerWorld - _CurrentHeadPosition.xyz;
                float eyeDistance = length(fromEye);
                if (eyeDistance < 0.55)
                {
                    float3 safeFromEye = eyeDistance > 1e-4 ? fromEye / eyeDistance : screenNormal;
                    centerWorld = _CurrentHeadPosition.xyz + safeFromEye * 0.55;
                }

                float3 relativeWorld = lerp(sourceRelative, targetVertex - targetCenter, alignment);
                float middle = loosen * (1.0 - Ease(0.38, 0.68, p));
                float3 travelAxis = SafeNormalize(lerp(
                    mul((float3x3)_CaptureHeadToWorld, macroTangentX), screenRight, alignment), screenRight);
                float3 faceAxis = SafeNormalize(lerp(
                    mul((float3x3)_CaptureHeadToWorld, macroRay), screenNormal, alignment), screenNormal);
                float tilt = radians(lerp(26.0, 48.0, pieceNoise.y) * (1.0 - 0.35 * largePiece))
                           * (pieceNoise.x < 0.5 ? -1.0 : 1.0) * middle;
                float roll = radians((pieceNoise.y * 2.0 - 1.0) * (34.0 - 17.0 * largePiece)) * middle;
                relativeWorld = IntroShardSpin3(relativeWorld, travelAxis, tilt);
                relativeWorld = IntroShardSpin3(relativeWorld, faceAxis, roll);

                // 片を小さく消すのではなく、位置と向きで間隔を空ける。
                float gap = lerp(crack * 0.014, 0.025 + pieceNoise.x * 0.035, loosen) * (1.0 - seal);
                float thickness = lerp(0.002, 0.005, pieceNoise.y) * detail;
                float3 faceNormal = faceAxis;
                faceNormal = IntroShardSpin3(faceNormal, travelAxis, tilt);
                faceNormal = IntroShardSpin3(faceNormal, faceAxis, roll);
                float3 worldPosition = centerWorld + relativeWorld * (1.0 - gap)
                                     + normalize(faceNormal) * (v.positionOS.z * thickness);

                float3 pieceRay = normalize(rawPieceCenter);
                float3 sourceTangentX = normalize(float3(1.0, 0.0,
                    -pieceRay.x / max(pieceRay.z, 1e-4)));
                float3 sourceTangentY = normalize(cross(pieceRay, sourceTangentX));
                float3 sourceNormal = normalize(sourceTangentX * v.normalOS.x
                    + sourceTangentY * v.normalOS.y + pieceRay * v.normalOS.z);
                sourceNormal = mul((float3x3)_CaptureHeadToWorld, sourceNormal);
                float3 targetNormal = normalize(screenRight * v.normalOS.x
                    + screenUp * v.normalOS.y + screenNormal * v.normalOS.z);
                float3 normalWorld = normalize(lerp(sourceNormal, targetNormal, alignment));
                normalWorld = IntroShardSpin3(normalWorld, travelAxis, tilt);
                normalWorld = IntroShardSpin3(normalWorld, faceAxis, roll);

                // UV は変形前の撮影ワールド点から一度だけ求める。移動中には再投影しない。
                float leftValid;
                float rightValid;
                float2 leftUv = ProjectFrozenUv(_LeftWorldToUv, captureWorld, leftValid);
                float2 rightUv = ProjectFrozenUv(_RightWorldToUv, captureWorld, rightValid);
                float leftQ = lerp(mul(_LeftWorldToUv, float4(captureWorld, 1.0)).w, 1.0, alignment);
                float rightQ = lerp(mul(_RightWorldToUv, float4(captureWorld, 1.0)).w, 1.0, alignment);
                o.leftUv = float3(leftUv * leftQ, leftQ);
                o.rightUv = float3(rightUv * rightQ, rightQ);
                o.projectionValidity = float2(leftValid, rightValid);
                o.positionWS = worldPosition;
                o.normalWS = normalize(normalWorld);
                o.surface = v.surface.x;
                o.detail = detail;
                o.positionCS = TransformWorldToHClip(worldPosition);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                if (_HasFrozenFrame < 0.5)
                    return float4(0.0, 0.0, 0.0, saturate(_ScreenFade));

                float3 projected = unity_StereoEyeIndex == 0 ? i.leftUv : i.rightUv;
                float2 uv = projected.xy / max(projected.z, 1e-5);
                float valid = unity_StereoEyeIndex == 0
                    ? i.projectionValidity.x : i.projectionValidity.y;
                float edge = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
                float field = smoothstep(0.0, 0.018, edge) * step(1e-5, valid);
                clip(field - 1e-4);
                float2 sampleUv = saturate(uv);
                float3 photo = unity_StereoEyeIndex == 0
                    ? SAMPLE_TEXTURE2D(_FrozenLeftTex, sampler_FrozenLeftTex, sampleUv).rgb
                    : SAMPLE_TEXTURE2D(_FrozenRightTex, sampler_FrozenRightTex, sampleUv).rgb;
                float gray = dot(photo, float3(0.299, 0.587, 0.114));
                gray = saturate((gray - 0.5) * _PhotoContrast + 0.5);
                gray = saturate(gray * _PhotoBrightness) * field;

                float3 viewDirection = normalize(_CurrentHeadPosition.xyz - i.positionWS);
                float directionalShade = 0.82
                    + 0.18 * saturate(dot(normalize(i.normalWS), viewDirection));
                float shade = lerp(1.0, directionalShade, i.detail);
                float3 color = gray.xxx;
                if (i.surface >= 0.5)
                    color = (i.surface < 1.5
                        ? float3(0.035, 0.035, 0.035)
                        : float3(0.055, 0.055, 0.055)) * field;
                return float4(color * shade, saturate(1.0 - _ScreenFade));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
