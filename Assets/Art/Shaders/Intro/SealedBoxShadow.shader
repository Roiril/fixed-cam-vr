// 封印の箱が床に落とす影。**現実の床を暗くするだけの、水平な板 1 枚**。
//
// 出どころは `canon/LEDGER.md` 0024（「床に影を斜め方向くらいで落とすようにしてほしい。
// リアル感がなさすぎる」）。箱は 2.4m の塊なのに床との接点が 1 つも無く、
// パススルーの上に貼った黒い板に見えていた。
//
// ⚠⚠ **URP のシャドウマップは使えない。** 影の受け手（床）は**現実**なのでシーンに幾何が無い。
// CG 人形が平面投影シャドウを使っているのと同じ理由（rules/streaming.md「影・接地・オクルージョン」）。
//
// ⚠ **パススルーを暗くする仕掛け**: 最終画は `アプリの rgb + 現実 × (1 - アプリの alpha)` なので、
// **rgb = 0 / alpha = 濃さ** を書けばそこの現実だけが暗くなる（ContainmentShell と同じ。
// あちらは alpha を 1 まで上げて会場を消す）。
//
// ⚠ 描画順は 4915。覆い (4900) → 隔離の黒 (4910) → **影 (4915)** → 箱 (4920)。
//   - 覆いより後: 覆いは `Blend Zero SrcAlpha` を全画面へ掛けるので、先に描くと潰れる
//   - 箱より前: 箱と重なる所は箱が勝つ（影が箱の面に乗らない）
//   - **5000 を超えてはいけない**（URP の透明パスは [2501, 5000] だけ・2026-07-31 実害）
//
// ⚠ **形の式は `SealedBoxShadowLogic.Coverage` と同じ**。片方だけ直すと沈黙して食い違う。
Shader "FixedCamVr/SealedBoxShadow"
{
    Properties
    {
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0
        // 影の最大の濃さ。1 にすると現実が完全に消えて「床に空いた穴」になる。
        _Density("Shadow density (0..1)", Range(0, 1)) = 0.55
        // 箱の footprint の半寸 (m)。xy = (X, Z)。
        _HalfXZ("Box footprint half (m)", Vector) = (0.9, 0.9, 0, 0)
        // 影が伸びる向きと長さ (m)。xy = (X, Z)。天面が床へ落ちる先。
        _Sweep("Shadow sweep (m)", Vector) = (0, 0, 0, 0)
        // 板の大きさ (m) と、板の中心（箱の中心を原点とする箱ローカル XZ）。
        _QuadSizeM("Quad size (m)", Vector) = (1, 1, 0, 0)
        _QuadCenterM("Quad center (m)", Vector) = (0, 0, 0, 0)
        // 縁のぼかしの基準幅 (m) と、接地帯の幅 (m) / 足す濃さ。
        _FeatherM("Penumbra base width (m)", Float) = 0.11
        _FeatherNear("Penumbra at contact (x)", Float) = 0.35
        _FeatherFar("Penumbra at tip (x)", Float) = 2.0
        _FarDensity("Density at tip (x)", Float) = 0.42
        _ContactM("Contact band width (m)", Float) = 0.22
        _ContactGain("Contact darkening", Float) = 0.40
    }

    SubShader
    {
        // Overlay+915 = 4915。隔離の黒 (4910) の後、封印の箱 (4920) の前。
        Tags { "Queue" = "Overlay+915" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SealedBoxShadow"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            // 床の板なので、体験者が影の上に立てば裏から見ることになる。
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _Opacity;
            float _Density;
            float4 _HalfXZ;
            float4 _Sweep;
            float4 _QuadSizeM;
            float4 _QuadCenterM;
            float _FeatherM;
            float _FeatherNear;
            float _FeatherFar;
            float _FarDensity;
            float _ContactM;
            float _ContactGain;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // 箱の中心を原点とする箱ローカルの床座標 (m)。
                // ⚠ 板は Euler(90, courseYaw, 0) で寝かせてあるので、**ローカル XY がそのまま箱ローカルの XZ**。
                //    行列を渡さずに済むのはこの姿勢を守っているから（SealedBox.PlaceShadow と対）。
                float2 floorXZ : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.floorXZ = v.positionOS.xy * _QuadSizeM.xy + _QuadCenterM.xy;
                return o;
            }

            // 矩形の符号つき距離（負 = 内側）。`SealedBoxShadowLogic.SdBox` と同じ。
            float SdBox(float2 p, float2 half2)
            {
                float2 d = abs(p) - half2;
                return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
            }

            // ⚠ **`SealedBoxShadowLogic.Coverage` と同じ式**。対で直すこと。
            float Coverage(float2 p)
            {
                float2 s = _Sweep.xy;
                float ss = dot(s, s);
                // 矩形を線分方向へ掃いた形（凸六角）への距離。線分への射影を clamp して解く
                // ＝ 軸に沿う向きでは厳密、斜めでもぼかし幅の内側に収まる近似。
                float t = ss > 1e-6 ? saturate(dot(p, s) / ss) : 0.0;
                float d = SdBox(p - s * t, _HalfXZ.xy);

                // 半影は遮蔽物から離れるほど広がる。接地点で硬く、先端で柔らかい。
                float f = max(_FeatherM * lerp(_FeatherNear, _FeatherFar, t), 1e-4);
                // 縁は幾何の縁をまたぐ（内外に半分ずつ）。d = 0 でちょうど半分の濃さ。
                float cov = 1.0 - smoothstep(-f * 0.5, f * 0.5, d);

                float dens = lerp(1.0, _FarDensity, t);
                // 接地帯。**「浮いている」に一番効くのはここ**（CG 人形の接地影と同じ判断）。
                float contact = 1.0 - smoothstep(0.0, max(_ContactM, 1e-4), SdBox(p, _HalfXZ.xy));
                return saturate(cov * saturate(dens + contact * _ContactGain));
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float a = saturate(_Opacity) * saturate(_Density) * Coverage(i.floorXZ);
                if (a <= 0.002) return half4(0, 0, 0, 0);
                // rgb は 0。over なので現実を (1 - a) 倍に暗くするだけ。
                return half4(0.0h, 0.0h, 0.0h, (half)a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
