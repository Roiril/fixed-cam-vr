Shader "FixedCamVr/IntroPeripheralFracture"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _Mode("Window 0 / color 1", Range(0, 1)) = 0
        _EdgeEmphasis("Edge emphasis", Range(0, 1)) = 1
        // 0 で旧描画（くすんだガラス）と厳密一致。中央の IntroFracture と同じ既定（1）。
        _Crystal("Crystal face", Range(0, 1)) = 1
        _SparkLit("Spark reflections", Range(0, 1)) = 1
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
        Tags { "Queue" = "Overlay+904" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroPeripheralFracture"
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            BlendOp Add
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            ColorMask [_ColorMask]
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
                float3 normalOS : NORMAL;
                float2 patchUv : TEXCOORD0;
                float4 piece : TEXCOORD1;
                float4 timing : TEXCOORD2;
                float4 edges : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 leftQ : TEXCOORD0;
                float4 rightQ : TEXCOORD1;
                float4 screenQ : TEXCOORD2;
                float4 edges : TEXCOORD3;
                // x = 破断の受け渡し / y = 吸引の進み / z = 退場 / w = 頭中心の視線と法線の内積（きらめきの位相・両眼一致）
                nointerpolation float4 phase : TEXCOORD4;
                float4 surface : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_FrozenLeftTex);
            SAMPLER(sampler_FrozenLeftTex);
            TEXTURE2D(_FrozenRightTex);
            SAMPLER(sampler_FrozenRightTex);

            float _Shatter;
            float _Mode;
            float _EdgeEmphasis;
            float _Crystal;
            float _SparkLit;
            float4x4 _CaptureHeadToWorld;
            float4x4 _LeftWorldToUv;
            float4x4 _RightWorldToUv;
            float4 _CurrentHeadPosition;
            float4 _ScreenCenter;
            float4 _ScreenRight;
            float4 _ScreenUp;
            float4 _ScreenHalf;

            float Hash(float value)
            {
                return frac(sin(value * 91.731 + 17.13) * 43758.5453);
            }

            float3 SafeNormalize(float3 value, float3 fallback)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 1e-8 ? value * rsqrt(lengthSquared) : fallback;
            }

            float3 Rotate(float3 value, float3 axis, float angle)
            {
                float s;
                float c;
                sincos(angle, s, c);
                return value * c + cross(axis, value) * s + axis * dot(axis, value) * (1.0 - c);
            }

            float3 SphericalPath(float3 origin, float3 start, float3 target, float travel)
            {
                float3 startVector = start - origin;
                float3 targetVector = target - origin;
                float startRadius = max(length(startVector), 0.8);
                float targetRadius = max(length(targetVector), 0.8);
                float3 startDirection = SafeNormalize(startVector, float3(0.0, 0.0, 1.0));
                float3 targetDirection = SafeNormalize(targetVector, startDirection);
                float cosine = clamp(dot(startDirection, targetDirection), -0.999, 0.999);
                float angle = acos(cosine);
                float3 direction = angle > 1e-4
                    ? normalize(sin((1.0 - travel) * angle) * startDirection
                              + sin(travel * angle) * targetDirection)
                    : normalize(lerp(startDirection, targetDirection, travel));
                return origin + direction * lerp(startRadius, targetRadius, travel);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float p = saturate(_Shatter);
                float breakAt = CrackEnd + v.timing.x * BreakSpan;
                float handoff = Ease(breakAt, breakAt + lerp(0.016, 0.024, Hash(v.piece.w + 2.0)), p);
                float arrive = lerp(0.72, 0.84, Hash(v.piece.w + 7.0));
                float exponent = lerp(1.6, 3.5, Hash(v.piece.w + 13.0));
                float pullT = saturate((p - PullStart) / max(arrive - PullStart, 0.01));
                float travel = pow(pullT, exponent);
                float exit = 1.0 - Ease(arrive - 0.035, arrive, p);

                float3 captureOrigin = mul(_CaptureHeadToWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;
                float3 captureVertex = mul(_CaptureHeadToWorld, float4(v.positionOS.xyz, 1.0)).xyz;
                float3 captureCenter = mul(_CaptureHeadToWorld, float4(v.piece.xyz, 1.0)).xyz;
                float3 sourceDirection = normalize(captureCenter - captureOrigin);
                float3 tangent = SafeNormalize(cross(
                    abs(sourceDirection.y) < 0.85 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0),
                    sourceDirection), float3(1.0, 0.0, 0.0));
                float3 bitangent = normalize(cross(sourceDirection, tangent));
                float elapsed = max(p - breakAt, 0.0);
                float u = WarpedTime(elapsed, max(p - PullBegin, 0.0));
                float uSpin = SpinTime(elapsed);
                float3 burst = tangent * (Hash(v.piece.w + 19.0) - 0.5) * 0.34
                             + bitangent * (Hash(v.piece.w + 23.0) - 0.5) * 0.30
                             + sourceDirection * lerp(0.08, 0.15, Hash(v.piece.w + 27.0));
                float3 drift = bitangent * sin(u * 4.0 + v.piece.w) * 0.025;
                float3 startCenter = captureCenter + burst * u + drift;

                float side = floor(Hash(v.piece.w + 29.0) * 4.0);
                float edgePosition = lerp(-1.0, 1.0, Hash(v.piece.w + 31.0));
                float2 perimeter = side < 1.0 ? float2(-1.1, edgePosition)
                    : side < 2.0 ? float2(1.1, edgePosition)
                    : side < 3.0 ? float2(edgePosition, -1.1) : float2(edgePosition, 1.1);
                float3 screenRight = normalize(_ScreenRight.xyz);
                float3 screenUp = normalize(_ScreenUp.xyz);
                float3 target = _ScreenCenter.xyz + screenRight * (_ScreenHalf.x * perimeter.x)
                              + screenUp * (_ScreenHalf.y * perimeter.y);
                float3 center = SphericalPath(captureOrigin, startCenter, target, travel);
                float3 relative = captureVertex - captureCenter;
                relative = Rotate(relative, tangent,
                    radians(lerp(-18.0, 18.0, Hash(v.piece.w + 37.0))) * uSpin);
                relative = Rotate(relative, sourceDirection,
                    radians(lerp(-28.0, 28.0, Hash(v.piece.w + 41.0))) * uSpin);
                float shrink = max(exit, 0.001) * (1.0 - 0.11 * handoff);
                float3 worldPosition = _Mode < 0.5 ? captureVertex : center + relative * shrink;
                float3 fromEye = worldPosition - _CurrentHeadPosition.xyz;
                float eyeDistance = length(fromEye);
                if (_Mode > 0.5 && eyeDistance < 0.55)
                    worldPosition = _CurrentHeadPosition.xyz
                                  + SafeNormalize(fromEye, sourceDirection) * 0.55;

                o.positionCS = TransformWorldToHClip(worldPosition);
                float3 normal = normalize(mul((float3x3)_CaptureHeadToWorld, v.normalOS));
                normal = Rotate(normal, tangent,
                    radians(lerp(-18.0, 18.0, Hash(v.piece.w + 37.0))) * uSpin);
                normal = Rotate(normal, sourceDirection,
                    radians(lerp(-28.0, 28.0, Hash(v.piece.w + 41.0))) * uSpin);

                // 投影は頂点で同次座標まで求める。除算と撮影範囲の判定は補間後に片眼ごとに行う。
                o.leftQ = mul(_LeftWorldToUv, float4(captureVertex, 1.0));
                o.rightQ = mul(_RightWorldToUv, float4(captureVertex, 1.0));
                float3 screenNormal = normalize(cross(screenRight, screenUp));
                float3 screenRay = worldPosition - _CurrentHeadPosition.xyz;
                float denominator = dot(screenRay, screenNormal);
                float screenDistance = dot(_ScreenCenter.xyz - _CurrentHeadPosition.xyz, screenNormal);
                float2 screenNumerator = float2(
                    dot(_CurrentHeadPosition.xyz - _ScreenCenter.xyz, screenRight) * denominator
                        + dot(screenRay, screenRight) * screenDistance,
                    dot(_CurrentHeadPosition.xyz - _ScreenCenter.xyz, screenUp) * denominator
                        + dot(screenRay, screenUp) * screenDistance);
                o.screenQ = float4(screenNumerator / max(_ScreenHalf.xy, 1e-4),
                    denominator, screenDistance * denominator);

                // 周辺片は粗い明暗を優先する。8 tap と鏡面計算を頂点へ寄せ、面内では補間する。
                float2 patch = v.timing.yz + (v.patchUv - 0.5) * 0.085;
                float2 tap = float2(0.014, 0.011);
                float3 leftColor = (
                    SAMPLE_TEXTURE2D_LOD(_FrozenLeftTex, sampler_FrozenLeftTex, patch + tap, 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenLeftTex, sampler_FrozenLeftTex, patch - tap, 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenLeftTex, sampler_FrozenLeftTex,
                        patch + float2(-tap.x, tap.y), 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenLeftTex, sampler_FrozenLeftTex,
                        patch + float2(tap.x, -tap.y), 0).rgb) * 0.25;
                float3 rightColor = (
                    SAMPLE_TEXTURE2D_LOD(_FrozenRightTex, sampler_FrozenRightTex, patch + tap, 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenRightTex, sampler_FrozenRightTex, patch - tap, 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenRightTex, sampler_FrozenRightTex,
                        patch + float2(-tap.x, tap.y), 0).rgb
                    + SAMPLE_TEXTURE2D_LOD(_FrozenRightTex, sampler_FrozenRightTex,
                        patch + float2(tap.x, -tap.y), 0).rgb) * 0.25;
                float luma = dot(lerp(leftColor, rightColor, 0.5), float3(0.2126, 0.7152, 0.0722));
                float3 viewDirection = SafeNormalize(_CurrentHeadPosition.xyz - worldPosition, -sourceDirection);
                float grazing = pow(1.0 - saturate(abs(dot(normal, viewDirection))), 2.0);
                float3 lightDirection = normalize(-screenNormal + screenUp * 0.70 - screenRight * 0.45);
                float facing = saturate(abs(dot(normal, normalize(lightDirection + viewDirection))));
                float specular = pow(facing, 36.0) + 0.5 * pow(facing, 12.0);
                float shock = Ease(CrackEnd - 0.004, CrackEnd + 0.004, p)
                    * (1.0 - Ease(CrackEnd + 0.004, CrackEnd + 0.045, p));
                float3 rimColor = float3(0.96, 0.90, 0.78);
                // 地の色は寒色から暖色へ（0235 / LEDGER 0010）。_Crystal=0 で旧値へ戻る。
                float3 baseColor = luma * lerp(float3(0.78, 0.84, 0.88), float3(0.90, 0.86, 0.78), _Crystal)
                        * (1.0 + 0.45 * shock)
                    + rimColor * 0.10 * specular * (1.0 - travel);
                // 片ごとの薄膜の色。色相だけ移して明るさは保つ。
                float3 irid = SparkPalette(Hash(v.piece.w + 43.0) * 0.5 + 0.5 + 0.3 * grazing);
                float3 iridTint = irid / max(dot(irid, float3(0.299, 0.587, 0.114)), 1e-3);
                baseColor *= lerp(float3(1.0, 1.0, 1.0), iridTint, 0.20 * _Crystal);
                // 代表の粒の柔らかい照り。周辺は頂点で足す（画素で点光源は置かない）。
                float sparkSoft = 0.0;
                [unroll]
                for (int sparkIndex = 0; sparkIndex < HeroSparkCount; sparkIndex++)
                {
                    float3 sparkPos;
                    float sparkIntensity;
                    HeroSpark(sparkIndex, p, _CaptureHeadToWorld, _ScreenCenter.xyz,
                              sparkPos, sparkIntensity);
                    float3 toSpark = sparkPos - worldPosition;
                    sparkSoft += sparkIntensity / (1.0 + dot(toSpark, toSpark) / 0.25);
                }
                baseColor += SparkCoreColor * min(sparkSoft * 0.12, 0.30) * (1.0 - travel) * _SparkLit;
                float rim = 0.42 + 0.38 * grazing;
                o.surface = float4(baseColor, rim);
                o.edges = v.edges;
                o.phase = float4(handoff, travel, exit, dot(normal, viewDirection));
                return o;
            }

            float PhotoSupport(float2 uv, float valid)
            {
                float edge = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
                return step(1e-5, valid) * smoothstep(0.005, 0.055, edge);
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 photoQ = unity_StereoEyeIndex == 0 ? i.leftQ : i.rightQ;
                float safePhotoW = abs(photoQ.w) > 1e-5
                    ? photoQ.w : (photoQ.w < 0.0 ? -1e-5 : 1e-5);
                float2 photoUv = photoQ.xy / safePhotoW;
                // 中央片と同じ眼の撮影範囲を補う。片眼ごとの同次座標を面内補間してから判定する。
                float support = PhotoSupport(photoUv, min(photoQ.z, photoQ.w));
                float outsidePhoto = 1.0 - support;
                clip(outsidePhoto - 0.001);

                float edgeDistance = min(min(i.edges.x, i.edges.y), min(i.edges.z, i.edges.w));
                float crack = 1.0 - smoothstep(0.002, 0.010, edgeDistance);
                if (_Mode < 0.5)
                {
                    float crackLight = crack * Ease(0.0, CrackEnd, _Shatter);
                    float alpha = lerp(1.0, max(i.phase.x, crackLight * (1.0 - i.phase.x)), outsidePhoto);
                    return float4(alpha, alpha, alpha, alpha);
                }

                float safeScreenZ = abs(i.screenQ.z) > 1e-5
                    ? i.screenQ.z : (i.screenQ.z < 0.0 ? -1e-5 : 1e-5);
                float2 screenClip = i.screenQ.xy / safeScreenZ;
                float rectDistance = max(abs(screenClip.x), abs(screenClip.y));
                float insideScreen = (1.0 - smoothstep(0.96, 1.12, rectDistance))
                    * step(0.0, i.screenQ.w);
                float screenClear = 1.0 - insideScreen * Ease(PullStart, 0.70, _Shatter);
                clip(i.phase.x * i.phase.z * screenClear - 0.001);
                float3 rimColor = float3(0.96, 0.90, 0.78);
                float strongCrack = 1.0 - smoothstep(0.003, 0.016, edgeDistance);
                float edgeLight = lerp(crack * i.surface.a, strongCrack * (0.68 + 0.54 * i.surface.a),
                    saturate(_EdgeEmphasis));
                float3 color = i.surface.rgb + rimColor * edgeLight;
                // 0235: 面のきらめきと光の帯（中央の IntroFracture と同じ式）。座標は辺までの距離（m・両眼で同じ）。
                // 周辺片は 1 枚が大きいので、格子 1.25cm・密度 4.5% のまま面いっぱいに散る。飛んでいる間だけ。
                float flying = i.phase.x * (1.0 - i.phase.y) * i.phase.z;
                float2 cellUv = i.edges.xy * 80.0;
                float2 cell = floor(cellUv);
                float cellHash = frac(sin(dot(cell, float2(127.1, 311.7))) * 43758.5453);
                float2 cellLocal = frac(cellUv) - 0.5;
                float cellPoint = smoothstep(0.30, 0.06, length(cellLocal));
                float twinkle = smoothstep(0.55, 0.95,
                    sin(i.phase.w * 9.0 + cellHash * 6.283 + _Shatter * 26.0) * 0.5 + 0.5);
                float cellLod = saturate(1.0 - fwidth(cellUv.x) * 2.0);
                float3 crystal = SparkCoreColor * cellPoint * twinkle * 1.2 * step(0.955, cellHash) * cellLod;
                float caustic = exp(-pow((frac(i.edges.x * 3.0 + i.edges.y * 1.7 + i.phase.w) - 0.5) * 11.0, 2.0));
                crystal += SparkCoreColor * caustic * 0.08;
                color += crystal * flying * _Crystal;
                float alpha = i.phase.x * i.phase.z * outsidePhoto * screenClear;
                return float4(color, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
