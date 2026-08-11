// 封印の箱。**体験エリアの外に立っている人から見た、隔離の外側**。
//
// 出どころは .claude/canon/LEDGER.md 0003 / 0004。中から見た同じ境界が ContainmentShell で、
// **箱の外面と内面は同じ 1 つの体積**（footprint は ContainmentShellLogic が持つ）。
//
// 模様は手続きで描く（テクスチャを持たない）。理由は 3 つ:
//   - どの距離でも縁がなまらない（体験者は 3m から 0.5m まで近づく）
//   - 六角の大きさ・線の太さ・光の速さを数値で振れる（絵を焼き直さなくてよい）
//   - 面をまたいで模様が連続する（world 座標で引くので、箱が「削り出した塊」に見える）
//
// **線は細く、幾何は正確に、動くのは光だけ**（LEDGER 0004）。塗りつぶしも斑も置かない —
// 面を汚すと「不気味」ではなく「傷んだ板」になる。非一様さは光の走りが作る。
//
// ⚠ 描画順は覆い（IntroVeil・4900）の**後**。覆いは `Blend Zero SrcAlpha` を全画面へ掛けるので、
// 先に描くと箱ごと 0 に潰れる。**5000 を超えてもいけない**（URP の透明パスは [2501, 5000] だけ）。
//
// ⚠ Cull Back。外から見たときだけ面が出る。中へ入ると背面は描かれないので、
// **箱を消し忘れても中の体験は汚れない**（消す判断は IntroLogic の重みが持つ）。
Shader "FixedCamVr/SealedBox"
{
    Properties
    {
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0
        // 地。ほぼ黒。わずかに緑を残すと「黒い板」ではなく「暗い面」に見える。
        _BaseColor("Base color", Color) = (0.018, 0.021, 0.017, 1)
        // 光が来ていないときの線。**ほとんど見えない**くらいでよい（形は光が見せる）。
        _LineColor("Hex line color (unlit)", Color) = (0.045, 0.054, 0.042, 1)
        // 光。線の上にだけ加算する。
        _GlowColor("Glow color", Color) = (0.140, 0.260, 0.240, 1)
        _HexSizeM("Hex size (m)", Float) = 0.45
        _LineWidth("Hex line width (0..0.5)", Range(0.003, 0.08)) = 0.012
        _GlowGain("Glow gain", Range(0, 3)) = 1.0
        // 光の波。上へ昇る波と、横へ流れる波を重ねる（同じ明滅が面全体で揃わない）。
        _WaveLenM("Wave length (m)", Float) = 1.2
        _WaveUpSec("Rising wave period (s)", Float) = 5.0
        _WaveSideSec("Side wave period (s)", Float) = 7.5
        _WaveSharp("Wave crest sharpness", Range(1, 8)) = 4.0
        // Editor プレビューで時間を進めるための位相 (s)。実行時は 0。
        _PhaseSec("Preview phase (s)", Float) = 0
    }

    SubShader
    {
        // Overlay+920 = 4920。隔離の黒 (4910) の後、構造の線と文字 (5000) の前。
        Tags { "Queue" = "Overlay+920" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SealedBox"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define TAU 6.2831853

            float _Opacity;
            float4 _BaseColor;
            float4 _LineColor;
            float4 _GlowColor;
            float _HexSizeM;
            float _LineWidth;
            float _GlowGain;
            float _WaveLenM;
            float _WaveUpSec;
            float _WaveSideSec;
            float _WaveSharp;
            float _PhaseSec;

            // 覆い（IntroVeil）が配る開口。**箱は覆いより後に描かれる**ので、ここで切らないと
            // 枠の外へはみ出して「枠が閉じる」が画に出ない（2026-08-11）。
            float4x4 _IntroFrameW2L;
            float4 _IntroFramePlane0;
            float4 _IntroFramePlane1;
            float4 _IntroFramePlane2;
            float4 _IntroFramePlane3;
            float _IntroFrameFeather;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            // 六角の中心からの「距離」。0 が中心、0.5 が辺。
            float HexDist(float2 p)
            {
                p = abs(p);
                return max(dot(p, normalize(float2(1.0, 1.7320508))), p.x);
            }

            // 六角格子の 1 セルを解く。gv = セル中心からの相対座標 / id = セルの識別子。
            void HexCell(float2 p, out float2 gv, out float2 id)
            {
                // ⚠ HLSL の fmod は負の入力で符号が残るので使えない（座標が原点の南西で崩れる）。
                const float2 s = float2(1.0, 1.7320508);
                float2 a = (p - s * floor(p / s)) - s * 0.5;
                float2 q = p + s * 0.5;
                float2 b = (q - s * floor(q / s)) - s * 0.5;
                bool useA = dot(a, a) < dot(b, b);
                gv = useA ? a : b;
                id = p - gv;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float a = saturate(_Opacity);
                if (a <= 0.002) return half4(0, 0, 0, 0);

                // 覆いの開口で切る。平面は覆いのローカル空間なので、世界の点を移してから見る。
                // ⚠ 内側へ **_ApertureBias** ぶん寄せる。平面は中央眼で解かれているので、
                //    眼ごとに 1〜2cm ずれる。寄せておけば覆いの黒が必ず箱の縁を覆う。
                float3 dl = normalize(mul(_IntroFrameW2L, float4(i.positionWS, 1.0)).xyz);
                float m = max(max(dot(dl, _IntroFramePlane0.xyz), dot(dl, _IntroFramePlane1.xyz)),
                              max(dot(dl, _IntroFramePlane2.xyz), dot(dl, _IntroFramePlane3.xyz)));
                const float _ApertureBias = 0.03;
                a *= 1.0 - smoothstep(-_IntroFrameFeather - _ApertureBias, -_ApertureBias, m);
                if (a <= 0.002) return half4(0, 0, 0, 0);

                // 面ごとに world 座標の 2 軸を選ぶ（箱の 3 方向で模様が連続する）。
                float3 n = abs(i.normalWS);
                bool topFace = n.y > max(n.x, n.z);
                float2 pm = topFace ? i.positionWS.xz
                          : ((n.x > n.z) ? i.positionWS.zy : i.positionWS.xy);

                float2 gv, id;
                HexCell(pm / max(_HexSizeM, 0.02), gv, id);
                float d = HexDist(gv);

                // 辺の線。画素あたりの変化量でぼかすので、遠くでも近くでも同じ太さに見える。
                // ⚠ 変数名に `line` を使わない。HLSL の予約語（ジオメトリシェーダの入力型）で、
                //    `syntax error: unexpected token 'line'` になる。C# のコンパイルでは出ないので、
                //    シェーダの誤りは**絵を出すまで気づけない**（2026-08-10 実測）。
                // ⚠ fwidth は面が視線と平行に近いところで発散する。上限を切らないと箱の下端に
                //    横縞が出る（同日実測）。
                float aa = clamp(fwidth(d), 1e-4, 0.2);
                // 細い線は遠くで消える。**1 画素は残す** —「幾何学的にきれい」は線が繋がっていること。
                float wdt = clamp(max(_LineWidth, aa * 0.9), _LineWidth, 0.06);
                float edge = smoothstep(0.5 - wdt - aa, 0.5 - wdt + aa, d);

                // ---- 光の走り。**動くのは光だけ**で、幾何は 1 ミリも動かない ----
                float t = _Time.y + _PhaseSec;
                float up = topFace ? pm.y : i.positionWS.y;   // 天面は world Y が一定なので横で代用
                float len = max(_WaveLenM, 0.05);

                // 主役は**面の中心から広がる輪**。中に何かが居て、そこから伝わってくる形にする
                // （平面波だと「横一列が順に点く看板」に見えた）。
                float rad = length(pm - float2(0.0, topFace ? 0.0 : 1.2));
                float w1 = 0.5 + 0.5 * sin(TAU * (rad / len - t / max(_WaveUpSec, 0.2)));
                // 副役はゆっくり昇る帯。輪だけだと同心円が整いすぎる。
                float w2 = 0.5 + 0.5 * sin(TAU * (up / (len * 2.2) - t / max(_WaveSideSec, 0.2)));
                float glow = pow(w1, _WaveSharp) * 0.9 + pow(w2, _WaveSharp) * 0.45;

                // セルごとに位相をずらす。揃うと「面全体が点滅する看板」に見える。
                float r = Hash21(id + float2(31.7, 17.3));
                glow *= 0.72 + 0.28 * (0.5 + 0.5 * sin(TAU * (t / 6.3 + r)));
                glow = saturate(glow) * _GlowGain;

                float3 rgb = lerp(_BaseColor.rgb, _LineColor.rgb, edge);
                rgb += _GlowColor.rgb * (edge * glow);

                // 少しだけ縦に沈める。上端まで一様だと「板」に見える。
                float h = saturate(i.positionWS.y * 0.35 + 0.15);
                rgb *= lerp(0.78, 1.0, h);

                // ⚠ rgb は straight（ブレンドの SrcAlpha が掛ける）。ここで a を掛けると二重になる。
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
