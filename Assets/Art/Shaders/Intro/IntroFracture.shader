// 段 4 の破片。撮影時の実景を厚み付きの破片へ貼り、現在のスクリーン実平面へ再構成する。
//
// 時計は映画の速度変化（canon/LEDGER.md 0221）:
//   予兆（亀裂が起点から光って走る）→ 一撃（最初の 0.2 秒で行程の 8 割を飛ぶ）→
//   引き延ばした時間（漂いと順回転は止めない）→ 集結（1 片ずつ直線に戻り、密度が上がる）。
// 見た目は光を通す結晶（同 0235）: 縁に幅のある光の帯、斜めの艶、回転で流れる鏡面の閃きに加えて、
// 片ごとに違う薄膜の色・面を横切るコースティクス・面の上で瞬くきらめき・代表の粒の反射を足す。
// 写真の明暗は残すが、乗算だけに縛らない（加算の自発光層 crystal と sparkLit が写真の暗さを越える）。
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
        _EdgeEmphasis("Edge emphasis", Range(0, 1)) = 1
        // 0 で旧描画（くすんだガラス）と厳密一致。計器の校正に使う（1 が通常値）。
        _Crystal("Crystal face", Range(0, 1)) = 1
        _SparkLit("Spark reflections", Range(0, 1)) = 1
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
            #include "IntroFractureTime.hlsl"
            #include "IntroSpark.hlsl"

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
                nointerpolation float landed : TEXCOORD9;
                // 代表の粒（IntroSpark.hlsl の HeroSpark）のうち、この片にいちばん近い 2 灯。
                // xyz = world の位置 / w = 明るさ 0..1（0 = まだ生まれていない or 消えた）。
                nointerpolation float4 spark0 : TEXCOORD10;
                nointerpolation float4 spark1 : TEXCOORD11;
                // x = 6 灯の柔らかい照りの総和 / y = 片ごとの種（薄膜の色の位相）
                nointerpolation float2 sparkSoft : TEXCOORD12;
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
            float _EdgeEmphasis;
            float _Crystal;
            float _SparkLit;
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
            // 集結の定数（ArriveFirst / ArriveSpan / PullPowLight / PullPowHeavy / CloserArrive）と
            // 片ごとの時刻・場所の式は IntroFractureTime.hlsl にある（光の粒 IntroSpark.shader と共有する）。
            static const float StretchMax = 0.55;   // 速い片を進行方向に伸ばす上限（モーションブラーの代わり）
            static const float StretchSpeed = 2.0;  // この速さ (m/s) で伸びが上限に届く

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

            float3 SafeNormalize(float3 value, float3 fallback)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 1e-8 ? value * rsqrt(lengthSquared) : fallback;
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
                float3 rawVertex = FractureShellPoint(v.positionOS.xy);
                float3 rawPieceCenter = FractureShellPoint(v.small.xy);
                float3 rawMacroCenter = FractureShellPoint(v.macro.xy);
                float3 macroRay = normalize(rawMacroCenter);
                float3 macroTangentX = normalize(float3(1.0, 0.0,
                    -macroRay.x / max(macroRay.z, 1e-4)));
                float3 macroTangentY = normalize(cross(macroRay, macroTangentX));

                float sizeRank = PieceSizeRank(v.small.z);
                float largePiece = smoothstep(0.016, 0.055, v.small.z);
                float edgeCloser = step(0.5, v.small.w);

                // 起点（tan 空間 (-0.16, 0.12)・IntroFractureMesh.Impact と同じ点）からの向きと距離。
                // 亀裂の光と破断の波はここから外へ走り、破片も粒もここから放射状に飛ぶ。
                float2 slope = rawPieceCenter.xy / max(rawPieceCenter.z, 0.01);
                float3 radialInfo = FractureRadial(rawPieceCenter);
                float2 radial = radialInfo.xy;
                float originDist = radialInfo.z;
                float2 tangent = float2(-radial.y, radial.x);

                // 破断の時刻。大区分の順（macro.z 0..0.14 ＝ 起点からの距離順）に 0.20 秒で全域へ。
                float breakAt = PieceBreakAt(v.macro.z, pieceNoise.x);
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
                float weight = PieceWeight(sizeRank, v.small.xy, pieceNoise2.y);
                float arrive = PieceArrive(weight, edgeCloser);
                float pullLen = arrive - PullStart;
                float pullPow = PiecePullPow(weight, edgeCloser);
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
                // 集結中も鏡面を生かす（0235。旧版は travel で真っ直ぐ消していた）。
                float glint = Ease(0.11, 0.20, p)
                            * (1.0 - lerp(1.0, 0.45, _Crystal) * travel) * step(breakAt, p);
                o.light = float4(glowFront, breakLight, landingLight, glint);
                o.landed = seal;
                // 遠い片は沈む。着地した面には掛けない（detail で消える）。
                float fog = saturate((eyeDistance - 1.7) / 2.2) * detail;
                // 着地から 0.12 秒で透明になり、その場所の映像が現れる（0225）。閃きは露出の途中で消えていく。
                float reveal = Ease(landAt, landAt + 0.024, p) * step(1e-4, _Reveal);
                o.detail = float4(detail, fog, max(shock, slam * 0.7), reveal);

                // 代表の粒（IntroSpark.hlsl）が面を照らし返す。閃きの出どころは必ず画の中に居る。
                // いちばん近い 2 灯だけを点光源として画素へ渡し、残りは柔らかい照りの総和にまとめる。
                float3 nearPos = 0.0;
                float nearIntensity = 0.0;
                float nearDistance = 1e9;
                float3 secondPos = 0.0;
                float secondIntensity = 0.0;
                float secondDistance = 1e9;
                float sparkSoft = 0.0;
                [unroll]
                for (int sparkIndex = 0; sparkIndex < HeroSparkCount; sparkIndex++)
                {
                    float3 sparkPos;
                    float sparkIntensity;
                    HeroSpark(sparkIndex, p, _CaptureHeadToWorld, _ScreenCenter.xyz,
                              sparkPos, sparkIntensity);
                    float3 toSpark = sparkPos - worldPosition;
                    float sparkDistance = dot(toSpark, toSpark);
                    sparkSoft += sparkIntensity / (1.0 + sparkDistance / 0.25);
                    if (sparkIntensity > 0.0)
                    {
                        if (sparkDistance < nearDistance)
                        {
                            secondDistance = nearDistance;
                            secondPos = nearPos;
                            secondIntensity = nearIntensity;
                            nearDistance = sparkDistance;
                            nearPos = sparkPos;
                            nearIntensity = sparkIntensity;
                        }
                        else if (sparkDistance < secondDistance)
                        {
                            secondDistance = sparkDistance;
                            secondPos = sparkPos;
                            secondIntensity = sparkIntensity;
                        }
                    }
                }
                o.spark0 = float4(nearPos, nearIntensity);
                o.spark1 = float4(secondPos, secondIntensity);
                o.sparkSoft = float2(sparkSoft, v.macro.w * 7.13 + v.small.z * 311.0);

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
                // 結晶は写真より明るくコントラストが立つ（0235）。_Crystal=0 で旧値へ戻る。
                float lumRaw = dot(photo, float3(0.299, 0.587, 0.114));
                float lift = lerp(1.0, 1.146, _Crystal);    // 0.96 → 1.10
                float liftC = lerp(1.0, 1.135, _Crystal);   // 1.04 → 1.18
                float gray = saturate((lumRaw - 0.5) * (_PhotoContrast * liftC) + 0.5);
                gray = saturate(gray * (_PhotoBrightness * lift)) * field;

                float detail = i.detail.x;
                float shock = i.detail.z;
                float3 viewDirection = normalize(_CurrentHeadPosition.xyz - i.positionWS);
                float3 normal = normalize(i.normalWS);
                float3 lightDirection = normalize(-normalize(cross(_ScreenRight.xyz, _ScreenUp.xyz))
                    + normalize(_ScreenUp.xyz) * 0.70 - normalize(_ScreenRight.xyz) * 0.45);
                float directionalShade = 0.68 + 0.32 * saturate(dot(normal, lightDirection));
                float shade = lerp(1.0, directionalShade, detail);

                // 薄膜干渉の色。片ごとに固有で、面の斜めと縁で強い（正面は生成り白のまま）。
                // 視線は _CurrentHeadPosition 由来なので両眼で一致する（眼ごとの位置を混ぜない）。
                float fres = pow(1.0 - saturate(abs(dot(normal, viewDirection))), 5.0);
                float pieceHash = frac(sin(i.sparkSoft.y * 12.9898) * 43758.5453);
                float spinPhase = 0.5 + 0.5 * dot(normal, normalize(_ScreenRight.xyz));
                float iridPhase = pieceHash * 0.5 + 0.5 + 0.35 * fres + 0.12 * spinPhase;
                float3 irid = SparkPalette(iridPhase);
                // 色相だけ移して輝度は保つ（彩度の上限 0.28）。
                float3 iridTint = irid / max(dot(irid, float3(0.299, 0.587, 0.114)), 1e-3);
                float3 faceTint = lerp(float3(1.0, 1.0, 1.0), iridTint, 0.28 * _Crystal * detail);

                // 一撃の閃きは線ではなく面を白ませる（線だけ光らせるとワイヤーフレームに見える）。
                float3 color = gray.xxx * (1.0 + 0.35 * i.light.y + 0.45 * shock);
                color *= faceTint;

                // 結晶: 斜めから見た面の艶と、回転で流れる鏡面の閃き（鋭い芯と柔らかい艶）。飛んでいる間だけ。
                float grazing = pow(1.0 - saturate(abs(dot(normal, viewDirection))), 3.0);
                float3 halfVector = normalize(lightDirection + viewDirection);
                float facing = saturate(dot(normal, halfVector));
                float specular = pow(facing, 36.0) + 0.5 * pow(facing, 12.0);
                float3 rimColor = float3(0.96, 0.90, 0.78);   // 生成り寄りの白（題字と同じ側の色）
                float edgeDistance = min(min(i.edgeDistances.x, i.edgeDistances.y),
                    min(i.edgeDistances.z, i.edgeDistances.w));
                float aa = max(fwidth(edgeDistance), 1e-6);
                // 稜線（1 画素）と、縁に沿う幅のある光の帯（約 1cm）。帯が「厚みのある結晶」に読ませる。
                float ridge = 1.0 - smoothstep(0.00012, 0.00012 + aa * 1.15, edgeDistance);
                float band = exp(-edgeDistance / 0.0011);
                float edgeGlow = ridge * (0.12 * i.light.x + 0.50 * i.light.y + 0.30 * shock + 0.30 * i.light.z
                                          + detail * (0.07 + 0.18 * grazing))
                               + band * (0.05 * i.light.x + 0.28 * i.light.y + 0.20 * shock + 0.16 * i.light.z
                                         + detail * (0.045 + 0.14 * grazing) * (0.6 + 0.8 * i.light.w));
                // 加算の自発光層。写真の明暗に掛からないので、暗い実景の上でも結晶が光る。
                float3 crystal = 0.0;
                // 薄膜の帯（斜め〜縁）。
                crystal += irid * fres * (0.22 * detail + 0.16 * i.light.w);
                // 面のきらめき場。位相は頭中心の視線なので両眼で揃う。
                // ⚠ 面の座標は写真の uv ではなく辺までの距離（i.edgeDistances）から取る。写真の uv は
                //    左右の撮影カメラの視差ぶん眼ごとにずれるので、格子を uv で切ると点の場所が両眼で食い違う。
                //    辺までの距離はメッシュのローカル値で、どちらの眼でも同じ（1 単位 ≈ 10.7m・0.0012 ≈ 1.3cm）。
                float2 cellUv = i.edgeDistances.xy * 820.0;
                float2 cell = floor(cellUv);
                float cellHash = frac(sin(dot(cell, float2(127.1, 311.7))) * 43758.5453);
                float2 cellLocal = frac(cellUv) - 0.5;
                float cellPoint = smoothstep(0.30, 0.06, length(cellLocal));
                float viewFacing = dot(viewDirection, normal);
                float twinkle = pow(saturate(
                    sin(viewFacing * 9.0 + cellHash * 6.283 + _Shatter * 26.0) * 0.5 + 0.5), 6.0);
                // 遠い片・小さい片ではちらつくので消す（1 格子が 2 画素を切ったら描かない）。
                float cellLod = saturate(1.0 - fwidth(cellUv.x) * 2.0);
                crystal += SparkCoreColor * cellPoint * twinkle * 0.55 * detail
                         * step(0.976, cellHash) * cellLod;
                // コースティクスの帯。片が回ると面を 1〜2 本の明るい帯が横切る（座標は同じ理由で辺までの距離）。
                float caustic = exp(-pow((frac(i.edgeDistances.x * 40.0 + i.edgeDistances.y * 21.0 + spinPhase)
                    - 0.5) * 7.2, 2.0));
                crystal += SparkCoreColor * caustic * 0.14 * detail * (0.4 + 0.6 * fres);

                // 代表の粒の反射（0235 の核心）。近い 2 灯を点光源として鏡面を返す。
                // HDR が無い（8bit LDR）ので必ずクランプする。
                float3 sparkLit = 0.0;
                [unroll]
                for (int litIndex = 0; litIndex < 2; litIndex++)
                {
                    float4 sparkSample = litIndex == 0 ? i.spark0 : i.spark1;
                    float3 toSpark = sparkSample.xyz - i.positionWS;
                    float sparkDistance = dot(toSpark, toSpark);
                    // 真後ろの粒で半ベクトルが 0 になっても NaN を出さない（_Crystal=0 の一致も守る）。
                    float3 sparkHalf = SafeNormalize(
                        SafeNormalize(toSpark, normal) + viewDirection, normal);
                    sparkLit += SparkCoreColor * pow(saturate(dot(normal, sparkHalf)), 48.0)
                              * sparkSample.w / (1.0 + sparkDistance / 0.09);
                }
                sparkLit += SparkCoreColor * i.sparkSoft.x * 0.10;
                sparkLit = min(sparkLit, 0.55) * detail * _SparkLit;

                // 実景の明暗は残す。飛んでいる間はわずかに暖かく、着地で素の写真へ戻る。
                color = color * shade * lerp(float3(1.0, 1.0, 1.0),
                            lerp(float3(0.94, 0.97, 1.03), float3(1.02, 0.99, 0.95), _Crystal), detail)
                      + rimColor * edgeGlow * field
                      + float3(0.90, 0.92, 0.96) * specular * (0.32 * i.light.w + 0.10 * detail) * field
                      + rimColor * grazing * 0.06 * detail * field;

                crystal *= _Crystal;
                float3 glow = crystal + sparkLit;
                // 明るい写真の上では抑える（白飛びさせない）。
                glow *= 1.0 - 0.5 * saturate(gray * 2.0 - 0.8);
                glow = min(glow, 1.6);
                color += glow * field;

                color *= 1.0 - lerp(0.30, 0.18, _Crystal) * i.detail.y;
                if (i.surface >= 0.5)
                {
                    // 裏は暗く、側面（結晶の厚み）は明るく光を返す。
                    float sideShade = 0.35 + lerp(1.6, 2.0, _Crystal) * saturate(dot(normal, lightDirection));
                    color = (i.surface < 1.5
                        ? float3(0.020, 0.022, 0.026) + rimColor * band * 0.10 * detail
                        : lerp(float3(0.16, 0.17, 0.18), float3(0.26, 0.24, 0.20), _Crystal) * sideShade
                            + rimColor * (0.30 * i.light.y + 0.25 * shock + 0.10 * detail
                                          + 0.08 * grazing * detail)
                            + sparkLit * 0.5) * field;
                }

                // 写真と縁を別々の straight-alpha 面として合成する。写真が消えた場所へ灰色の面を戻さない。
                float photoAlpha = saturate(1.0 - _ScreenFade) * (1.0 - i.detail.w);
                float oldAlpha = photoAlpha;

                float strongRidge = 1.0 - smoothstep(0.00020, 0.00020 + aa * 1.75, edgeDistance);
                float strongBand = exp(-edgeDistance / 0.0019);
                float flightEdge = saturate(
                    strongRidge * (0.32 + 0.38 * grazing + 0.28 * i.light.y + 0.22 * shock)
                    + strongBand * (0.16 + 0.24 * grazing + 0.18 * i.light.w)) * detail;
                // 戻った小片が中央へ密集しても映像を隠さないよう、着地後は明るさを保って幅を絞る。
                float landedFrontEdge = saturate(ridge * 0.72 + band * 0.16)
                    * i.landed * (1.0 - step(0.5, i.surface));

                // 全片着地後も p=.930 までは保つ。実ワールド平面の距離なので縦横比による楕円にならない。
                float2 screenMeters = float2(
                    dot(i.positionWS - _ScreenCenter.xyz, normalize(_ScreenRight.xyz)),
                    dot(i.positionWS - _ScreenCenter.xyz, normalize(_ScreenUp.xyz)));
                float screenRadius = length(screenMeters) / max(length(_ScreenHalf.xy), 1e-4);
                float radialFade = 1.0;
                if (_Shatter > 0.930 && _Shatter < 0.995)
                {
                    const float softness = 0.065;
                    float boundary = lerp(-softness, 1.0 + softness, Ease(0.930, 0.995, _Shatter));
                    radialFade = 1.0 - Ease(screenRadius - softness, screenRadius + softness, boundary);
                }
                else if (_Shatter >= 0.995)
                {
                    radialFade = 0.0;
                }

                float edgeAlpha = saturate(max(flightEdge, landedFrontEdge) * field) * radialFade;
                float unionAlpha = edgeAlpha + photoAlpha * (1.0 - edgeAlpha);
                float3 unionColor = (rimColor * edgeAlpha
                    + color * photoAlpha * (1.0 - edgeAlpha)) / max(unionAlpha, 1e-5);
                float4 oldResult = float4(color, oldAlpha);
                float4 emphasizedResult = float4(unionColor, unionAlpha);
                return lerp(oldResult, emphasizedResult, saturate(_EdgeEmphasis));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
