// 段 4 の破片。撮影時の実景を厚み付きの破片へ貼り、現在のスクリーン実平面へ再構成する。
//
// 時計は映画の速度変化（canon/LEDGER.md 0221）:
//   予兆（亀裂が起点から光って走る）→ 一撃（最初の 0.2 秒で行程の 8 割を飛ぶ）→
//   引き延ばした時間（漂いと順回転は止めない）→ 集結（1 片ずつ直線に戻り、密度が上がる）。
// 見た目はガラス（同 0222）: 縁に幅のある光の帯、斜めの艶、回転で流れる鏡面の閃き。写真の明暗は残す。
Shader "FixedCamVr/IntroFracture"
{
    Properties
    {
        _Shatter("Fracture progress", Range(0, 1)) = 0
        _ScreenFade("Screen crossfade", Range(0, 1)) = 0
        _Reveal("Landed pieces reveal the video", Range(0, 1)) = 0
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
        // 深度パスだけがステンシルに「破片のある所」を刻む（0225）。色パスは触らない。
        [HideInInspector] _StencilRef("Stencil ref", Float) = 0
        [HideInInspector] _StencilComp("Stencil comp", Float) = 8
        [HideInInspector] _StencilPass("Stencil pass", Float) = 0
        [HideInInspector] _StencilWriteMask("Stencil write mask", Float) = 255
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
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
                WriteMask [_StencilWriteMask]
            }

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
                float4 edgeDistances : TEXCOORD4;
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
                // x = 飛んでいる度合い（縁・厚み・艶はこれで出す）/ y = 遠さ（奥の片を沈める）/ z = 一撃の閃き（面を白ませる）
                // w = 着地してからの露出 0..1（0225: 透明になって、その下に描かれているその場所の映像が現れる）
                float4 detail : TEXCOORD6;
                float4 edgeDistances : TEXCOORD7;
                // x = 予兆の亀裂の光 / y = 破断・一撃の閃き / z = 着地の閃き / w = スロー中の閃き
                nointerpolation float4 light : TEXCOORD8;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_FrozenLeftTex);
            SAMPLER(sampler_FrozenLeftTex);
            TEXTURE2D(_FrozenRightTex);
            SAMPLER(sampler_FrozenRightTex);

            float _Shatter;
            float _ScreenFade;
            float _Reveal;
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

            // ---- 時計（段の進み p = 0..1。数値は 5.0 秒の段で決めた ＝ IntroTiming.Default.frameSec）----
            //   予兆   .000-.060 (0.30s) 亀裂が起点から光って走る。位置は保つ
            //   一撃   .060-.100 (0.20s) 破断の波が起点から全域へ。行程の 8 割を最初の 0.2 秒で飛ぶ
            //   スロー .100-.520         引き延ばした時間。漂いと順回転は止めない
            //   集結   .520-.900 (1.90s) 中心の磁石（0223）。磁力が立ち上がる .52-.64 で漂いが止まり、
            //                            軽い片・中央寄りの片から捕まり、遅く始まった片ほど速く引かれる。
            //                            着地は .70 から .90 へ増えていく。枠を閉じる 3 片が最後
            //   実景の面 .900-.940 / 混合 .940-.990 は IntroLogic の frame / live が持つ
            // ⚠ 音（ingest-sounds.py の SWARM_*）はこの表を秒に直したもの。片方だけ動かさない。
            static const float CrackEnd = 0.060;
            static const float BreakSpan = 0.040;
            static const float PullBegin = 0.520;   // 磁力が立ち上がる（漂いが止まり始める）
            static const float PullArrest = 0.120;  // 漂いが止まるまで（0.6 秒）。回転は止めない
            static const float PullStart = 0.560;   // 全片が同時に引かれ始める（2.8 秒・0224）
            static const float ArriveFirst = 0.700; // いちばん軽い片の着地
            static const float ArriveSpan = 0.170;  // 着地 = ArriveFirst + ArriveSpan × w^0.6（重い片ほど遅く、密度は終わりへ増える）
            static const float PullPowLight = 1.6;  // 進み = t^k。軽い片は早くから動き
            static const float PullPowHeavy = 3.5;  // 重い片は遅れて一気に加速する（減速せずに嵌まる）
            static const float CloserArrive = 0.900; // 枠を閉じる 3 片は最後（直前の 0.15 秒は静まる）
            static const float StretchMax = 0.55;   // 速い片を進行方向に伸ばす上限（モーションブラーの代わり）
            static const float StretchSpeed = 2.0;  // この速さ (m/s) で伸びが上限に届く
            static const float BurstTau = 0.024;   // 一撃の時定数（120ms）
            static const float DriftRate = 0.60;   // スローの漂い（行程 / p）

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

            // 引き延ばした時間。破断からの経過 x（p 単位）を、破片が生きる物理の時間 u へ写す。
            // 一撃で 0.80 を時定数 BurstTau で飛び、あとは DriftRate の遅い漂いが続く（止めない）。
            // xs = 磁力が立ち上がってからの経過。漂いは PullArrest かけて滑らかに 0 になる（磁石がまず漂いを止める）。
            float WarpedTime(float x, float xs)
            {
                float arrested = xs * Ease(0.0, PullArrest, xs);
                return 0.80 * (1.0 - exp(-x / BurstTau)) + DriftRate * (x - arrested);
            }

            // 回転の時計。漂いは磁石が止めるが、回転は慣性で続く（止めると 0216「止まって見える」に戻る）。
            float SpinTime(float x)
            {
                return 0.80 * (1.0 - exp(-x / BurstTau)) + DriftRate * x;
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
                float2 pieceNoise2 = IntroShardHash2(v.small.xy + 0.37, v.macro.w + 29.0);
                float3 rawVertex = ShellPoint(v.positionOS.xy);
                float3 rawPieceCenter = ShellPoint(v.small.xy);
                float3 rawMacroCenter = ShellPoint(v.macro.xy);
                float3 macroRay = normalize(rawMacroCenter);
                float3 macroTangentX = normalize(float3(1.0, 0.0,
                    -macroRay.x / max(macroRay.z, 1e-4)));
                float3 macroTangentY = normalize(cross(macroRay, macroTangentX));

                float sizeRank = saturate((v.small.z - 0.016) / (0.055 - 0.016));
                float largePiece = smoothstep(0.016, 0.055, v.small.z);
                float edgeCloser = step(0.5, v.small.w);

                // 起点（tan 空間 (-0.16, 0.12)・IntroFractureMesh.Impact と同じ点）からの向きと距離。
                // 亀裂の光と破断の波はここから外へ走り、破片もここから放射状に飛ぶ。
                float2 slope = rawPieceCenter.xy / max(rawPieceCenter.z, 0.01);
                float2 fromOrigin = slope - float2(-0.16, 0.12);
                float originDist = saturate(length(fromOrigin) / 2.2);
                float2 radial = fromOrigin / max(length(fromOrigin), 1e-4);
                float2 tangent = float2(-radial.y, radial.x);

                // 破断の時刻。大区分の順（macro.z 0..0.14 ＝ 起点からの距離順）に 0.20 秒で全域へ。
                float breakAt = CrackEnd + v.macro.z * (BreakSpan / 0.14) + pieceNoise.x * 0.004;
                float crack = Ease(breakAt, breakAt + 0.006, p);
                float loosen = Ease(breakAt, breakAt + 0.020, p);
                float u = WarpedTime(max(p - breakAt, 0.0), max(p - PullBegin, 0.0));
                float uSpin = SpinTime(max(p - breakAt, 0.0));

                // 破断の瞬間、大区分ごとにわずかに傾いて剥がれる。
                float macroAngle = radians(lerp(1.5, 3.5, macroNoise.y))
                                 * (macroNoise.x < 0.5 ? -1.0 : 1.0) * crack;
                float3 macroAxis = SafeNormalize(
                    lerp(macroTangentX, macroTangentY, macroNoise.x), macroTangentX);
                float3 peelOffset = macroRay * 0.018 * crack;
                float3 sourceVertex = rawMacroCenter
                    + IntroShardSpin3(rawVertex - rawMacroCenter, macroAxis, macroAngle) + peelOffset;
                float3 sourcePieceCenter = rawMacroCenter
                    + IntroShardSpin3(rawPieceCenter - rawMacroCenter, macroAxis, macroAngle) + peelOffset;
                float3 relativeSource = sourceVertex - sourcePieceCenter;

                // 一撃の行程（撮影時の頭の空間・m）。小片は速く遠く、周縁の片は奥へ、大片の一部だけ手前へ。
                float edgeDistance = smoothstep(0.35, 1.25, length(slope));
                float lateral = (lerp(0.34, 0.58, pieceNoise.x) + v.small.z * 0.7)
                              * lerp(1.15, 0.85, sizeRank);
                float depth = lerp(0.30, -0.14, largePiece) + edgeDistance * 0.26
                            + (pieceNoise.y - 0.5) * 0.14;
                float3 burst = float3(radial * lateral + tangent * (pieceNoise2.x - 0.5) * 0.14, depth);
                float3 flightCenter = sourcePieceCenter + burst * u;

                // 順回転。引き延ばした時間 u で回るので、一撃で速く、スローで遅く、止まらず、戻らない。
                // 0217「勢いよく回りすぎ」を越えない振れ幅（全行程で小片 30〜70° / 大片 10〜25°）。
                float tiltRate = radians(lerp(52.0, 18.0, sizeRank) * (0.55 + 0.9 * pieceNoise2.y))
                               * (pieceNoise.x < 0.5 ? -1.0 : 1.0);
                float rollRate = radians(lerp(38.0, 13.0, sizeRank) * (0.55 + 0.9 * pieceNoise2.x))
                               * (pieceNoise.y < 0.5 ? -1.0 : 1.0);

                // 集結は中心の磁石（0223 / 0224）。重さ = 大きさ・中心からの距離・乱数。全片が同時に引かれ始め、
                // 軽い片は早くから動いて先に着き、重い片は遅れて一気に加速して最後に来る（進み = t^k、k は重さで 1.6 → 3.5）。
                // 減速せずに嵌まる（跳ね返りは付けない）。着地は .70 から .87 へ増え、枠を閉じる 3 片が .90 で閉じる。
                float centerDist = saturate(length(v.small.xy) / 0.15);
                float weight = saturate(0.55 * sizeRank + 0.25 * centerDist + 0.20 * pieceNoise2.y);
                float arrive = lerp(ArriveFirst + ArriveSpan * pow(weight, 0.6), CloserArrive, edgeCloser);
                float pullLen = arrive - PullStart;
                float pullPow = lerp(PullPowLight, PullPowHeavy, max(weight, edgeCloser));
                float pullT = saturate((p - PullStart) / pullLen);
                float travel = pow(pullT, pullPow);
                // 速さ（m/s）。進みの微分 × 行程の長さ ÷ 段の秒数。伸びと着地の閃きに使う。
                float pullRate = pullPow * pow(max(pullT, 1e-4), pullPow - 1.0) / pullLen;
                float pullStart = PullStart;
                float alignment = travel;
                float seal = travel;
                float detail = crack * (1.0 - seal);

                float3 startCenter = mul(_CaptureHeadToWorld, float4(flightCenter, 1.0)).xyz;
                float3 sourceRelative = mul((float3x3)_CaptureHeadToWorld, relativeSource);
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

                // 漂っている位置から対応点へ直線で戻る（弧も跳ね返りも付けない）。
                float3 centerWorld = lerp(startCenter, targetCenter, travel);
                if (travel >= 1.0) centerWorld = targetCenter;
                float3 pullPath = targetCenter - startCenter;
                float pullDistance = length(pullPath);
                float3 pullDir = SafeNormalize(pullPath, screenNormal);
                float speed = pullRate * pullDistance / 5.0 * step(1e-4, 1.0 - travel);
                // 速い片は進行方向に伸びる（後処理の無い VR での速さの表現）。嵌まる直前の 8% で元へ戻す。
                float stretch = StretchMax * saturate(speed / StretchSpeed)
                              * (1.0 - Ease(0.92, 1.0, travel)) * step(PullStart, p);
                float3 fromEye = centerWorld - _CurrentHeadPosition.xyz;
                float eyeDistance = length(fromEye);
                if (eyeDistance < 0.55)
                {
                    float3 safeFromEye = eyeDistance > 1e-4 ? fromEye / eyeDistance : screenNormal;
                    centerWorld = _CurrentHeadPosition.xyz + safeFromEye * 0.55;
                    eyeDistance = 0.55;
                }

                float3 relativeWorld = lerp(sourceRelative, targetVertex - targetCenter, alignment);
                // 回転量は引き延ばした時間に比例し、帰還と同時に自然な向きへ揃う。
                float spinAmount = uSpin * (1.0 - travel);
                float3 travelAxis = SafeNormalize(lerp(
                    mul((float3x3)_CaptureHeadToWorld, macroTangentX), screenRight, alignment), screenRight);
                float3 faceAxis = SafeNormalize(lerp(
                    mul((float3x3)_CaptureHeadToWorld, macroRay), screenNormal, alignment), screenNormal);
                float tilt = tiltRate * spinAmount;
                float roll = rollRate * spinAmount;
                relativeWorld = IntroShardSpin3(relativeWorld, travelAxis, tilt);
                relativeWorld = IntroShardSpin3(relativeWorld, faceAxis, roll);
                relativeWorld += pullDir * (dot(relativeWorld, pullDir) * stretch);

                // 片を小さく消すのではなく、位置と向きで間隔を空ける。
                float gap = lerp(crack * 0.004, 0.006 + pieceNoise.x * 0.010, loosen) * (1.0 - seal);
                float thickness = lerp(0.006, 0.015, largePiece) * detail;
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
                o.edgeDistances = v.edgeDistances;

                // 光。予兆は起点から外へ走る亀裂の光 — 先端が明るく、通り過ぎた所は 4 割まで落ちる
                // （網を一斉に点灯させると「亀裂が走る」ではなく「網目」に見える）。
                // 一撃は全域が同時に閃き、破断した片はその瞬間にも光る。
                float glowOn = 0.008 + originDist * 0.040;
                float glowFront = Ease(glowOn, glowOn + 0.010, p)
                                * (0.40 + 0.60 * (1.0 - Ease(glowOn + 0.010, glowOn + 0.030, p)))
                                * (1.0 - Ease(CrackEnd - 0.002, CrackEnd + 0.012, p));
                float shock = Ease(CrackEnd - 0.004, CrackEnd + 0.004, p)
                            * (1.0 - Ease(CrackEnd + 0.004, CrackEnd + 0.045, p));
                float breakLight = Ease(breakAt, breakAt + 0.005, p)
                                 * (1.0 - Ease(breakAt + 0.005, breakAt + 0.040, p));
                float landAt = pullStart + pullLen;
                // 着地の閃き。速く嵌まる片ほど強く光る（当たりの強さ）。枠を閉じる瞬間は全片が白む（ドン）。
                float landingLight = Ease(landAt - 0.004, landAt, p)
                                   * (1.0 - Ease(landAt, landAt + 0.010, p))
                                   * lerp(0.6, 1.4, saturate(speed / StretchSpeed));
                float slam = Ease(CloserArrive - 0.004, CloserArrive, p)
                           * (1.0 - Ease(CloserArrive, CloserArrive + 0.030, p));
                float glint = Ease(0.11, 0.20, p) * (1.0 - travel) * step(breakAt, p);
                o.light = float4(glowFront, breakLight, landingLight, glint);
                // 遠い片は沈む。着地した面には掛けない（detail で消える）。
                float fog = saturate((eyeDistance - 1.7) / 2.2) * detail;
                // 着地から 0.12 秒で透明になり、その場所の映像が現れる（0225）。閃きは露出の途中で消えていく。
                float reveal = Ease(landAt, landAt + 0.024, p) * step(1e-4, _Reveal);
                o.detail = float4(detail, fog, max(shock, slam * 0.7), reveal);
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

                float detail = i.detail.x;
                float shock = i.detail.z;
                float3 viewDirection = normalize(_CurrentHeadPosition.xyz - i.positionWS);
                float3 normal = normalize(i.normalWS);
                float3 lightDirection = normalize(-normalize(cross(_ScreenRight.xyz, _ScreenUp.xyz))
                    + normalize(_ScreenUp.xyz) * 0.70 - normalize(_ScreenRight.xyz) * 0.45);
                float directionalShade = 0.68 + 0.32 * saturate(dot(normal, lightDirection));
                float shade = lerp(1.0, directionalShade, detail);
                // 一撃の閃きは線ではなく面を白ませる（線だけ光らせるとワイヤーフレームに見える）。
                float3 color = gray.xxx * (1.0 + 0.35 * i.light.y + 0.45 * shock);

                // ガラス: 斜めから見た面の艶と、回転で流れる鏡面の閃き（鋭い芯と柔らかい艶）。飛んでいる間だけ。
                float grazing = pow(1.0 - saturate(abs(dot(normal, viewDirection))), 3.0);
                float3 halfVector = normalize(lightDirection + viewDirection);
                float facing = saturate(dot(normal, halfVector));
                float specular = pow(facing, 36.0) + 0.5 * pow(facing, 12.0);
                float3 rimColor = float3(0.96, 0.90, 0.78);   // 生成り寄りの白（題字と同じ側の色）
                float edgeDistance = min(min(i.edgeDistances.x, i.edgeDistances.y),
                    min(i.edgeDistances.z, i.edgeDistances.w));
                float aa = max(fwidth(edgeDistance), 1e-6);
                // 稜線（1 画素）と、縁に沿う幅のある光の帯（約 1cm）。帯が「厚みのあるガラス」に読ませる。
                float ridge = 1.0 - smoothstep(0.00012, 0.00012 + aa * 1.15, edgeDistance);
                float band = exp(-edgeDistance / 0.0011);
                float edgeGlow = ridge * (0.12 * i.light.x + 0.50 * i.light.y + 0.30 * shock + 0.30 * i.light.z
                                          + detail * (0.07 + 0.18 * grazing))
                               + band * (0.05 * i.light.x + 0.28 * i.light.y + 0.20 * shock + 0.16 * i.light.z
                                         + detail * (0.045 + 0.14 * grazing) * (0.6 + 0.8 * i.light.w));
                // 実景の明暗は残す。飛んでいる間はわずかに冷たく、着地で素の写真へ戻る。
                color = color * shade * lerp(float3(1.0, 1.0, 1.0), float3(0.94, 0.97, 1.03), detail)
                      + rimColor * edgeGlow * field
                      + float3(0.90, 0.92, 0.96) * specular * (0.32 * i.light.w + 0.10 * detail) * field
                      + rimColor * grazing * 0.06 * detail * field;
                color *= 1.0 - 0.30 * i.detail.y;
                if (i.surface >= 0.5)
                {
                    // 裏は暗く、側面（ガラスの厚み）は明るく光を返す。
                    float sideShade = 0.35 + 1.6 * saturate(dot(normal, lightDirection));
                    color = (i.surface < 1.5
                        ? float3(0.020, 0.022, 0.026) + rimColor * band * 0.10 * detail
                        : float3(0.16, 0.17, 0.18) * sideShade
                            + rimColor * (0.30 * i.light.y + 0.25 * shock + 0.10 * detail
                                          + 0.08 * grazing * detail)) * field;
                }
                // 着地した片は透明になり、その下に描かれているその場所の映像が現れる（0225）。
                return float4(color, saturate(1.0 - _ScreenFade) * (1.0 - i.detail.w));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
