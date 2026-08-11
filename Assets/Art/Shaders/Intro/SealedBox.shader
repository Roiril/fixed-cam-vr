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
        // 箱の実寸 (m)。模様を「1 枚のシートで巻く」ために要る（C# が毎フレーム書く）。
        _BoxSize("Box size (m)", Vector) = (3, 2.4, 3, 0)
        // 噛み合わせられない継ぎ目（側面と天面）で模様を消す幅 (m)。
        _SeamFadeM("Seam fade (m)", Float) = 0.18
        _LineWidth("Hex line width (0..0.5)", Range(0.003, 0.08)) = 0.012
        _GlowGain("Glow gain", Range(0, 3)) = 1.0
        // 光の波。上へ昇る波と、横へ流れる波を重ねる（同じ明滅が面全体で揃わない）。
        _WaveLenM("Wave length (m)", Float) = 1.2
        _WaveUpSec("Rising wave period (s)", Float) = 5.0
        _WaveSideSec("Side wave period (s)", Float) = 7.5
        _WaveSharp("Wave crest sharpness", Range(1, 8)) = 4.0
        // Editor プレビューで時間を進めるための位相 (s)。実行時は 0。
        _PhaseSec("Preview phase (s)", Float) = 0

        // ---- 破砕（段 4）。封印そのものが割れてスクリーンへ入る --------------------
        // 数値は C# の `IntroShatterCurve` が正。ここの既定値は Editor で見るためだけのもの。
        _Shatter("Shatter progress (0..1)", Range(0, 1)) = 0
        _Stagger("Peripheral-first spread (0..1)", Float) = 0.38
        _Jitter("Per-cell start jitter (0..1)", Float) = 0.14
        _Gap("Crack width (fraction of cell)", Float) = 0.14
        _Spin("Max spin (rad)", Float) = 0.61
        _Drift("Break-loose drift (m)", Float) = 0.03
        _PullAt("Travel starts at (0..1)", Float) = 0.18
        _TravelMax("Travel amount (0..1)", Float) = 0.85
        _ShrinkAt("Shrink starts at (0..1)", Float) = 0.72
        _CloseAt("Close starts at (0..1)", Float) = 0.60
        // スクリーン矩形の周りこれだけは割らずに残す（覆いのセルとの刻みの差を塞ぐ）。
        _KeepFar("Keep intact around the screen (0..1)", Float) = 0.06
        _CellSeed("Cell noise seed", Float) = 17.13
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
            // 破砕の時計と形は覆い（IntroVeil）と共有する（1 本しか無い）。
            #include "IntroShatter.hlsl"

            #define TAU 6.2831853

            float _Opacity;
            float4 _BaseColor;
            float4 _LineColor;
            float4 _GlowColor;
            float _HexSizeM;
            float4 _BoxSize;
            float _SeamFadeM;
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
            float4x4 _IntroFrameL2W;
            float4 _IntroFramePlane0;
            float4 _IntroFramePlane1;
            float4 _IntroFramePlane2;
            float4 _IntroFramePlane3;
            float _IntroFrameFeather;

            // 破片の行き先（スクリーン矩形）。**覆いが global で配る**ので、覆いのセルと
            // 同じ格子・同じ順番・同じ行き先になる。
            // _IntroScreenHalf = (halfW, halfH, 覆いの面までの距離, 「遠い周縁」とみなす m)
            float4 _IntroScreenC;
            float4 _IntroScreenR;
            float4 _IntroScreenU;
            float4 _IntroScreenHalf;

            float _Shatter;
            float _Stagger;
            float _Jitter;
            float _Gap;
            float _Spin;
            float _Drift;
            float _PullAt;
            float _TravelMax;
            float _ShrinkAt;
            float _CloseAt;
            float _KeepFar;
            float _CellSeed;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                // 破砕用。この頂点が属する破片の中心（OS）。1 枚板の Cube には無いので 0 が読まれ、
                // その場合は破砕の枝へ入っても中心が原点になるだけ（_Shatter が 0 なので入らない）。
                float3 cell : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                // 模様は**箱のローカル座標**で引く。world だと箱の向きで模様が回り、
                // 面をまたいだ継ぎ目も合わない。
                // ⚠ 破砕中もここは**ホームの座標**のまま渡す。だから破片は
                //    **自分の模様を持ったまま飛ぶ**（覆いのセルとの決定的な違い）。
                float3 positionOS : TEXCOORD2;
                float3 normalOS : TEXCOORD3;
                // 破片が生きている量（1 = まだ現実を返していない）。
                float alive : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posOS = v.positionOS.xyz;
                float3 pw;
                float alive = 1.0;

                // ⚠ 覆いが行き先を配っていなければ割らない（`_IntroScreenHalf.w` が 0）。
                //    Editor プレビューのように覆いが居ない場所で割ると、行き先が原点になって
                //    破片が全部 1 点へ吸い込まれる。
                UNITY_BRANCH
                if (_Shatter > 0.0001 && _IntroScreenHalf.w > 1e-3)
                {
                    float3 c = v.cell;
                    float3 cw = TransformObjectToWorld(c);
                    float3 cl = mul(_IntroFrameW2L, float4(cw, 1.0)).xyz;   // 覆いのローカル（頭固定）

                    // 「遠さ」は 3D の距離ではなく**見かけの隔たり**で測る。スクリーンは箱の奥行きの
                    // 途中に浮いているので、3D 距離だと手前の面と奥の面がほぼ同じ値になり、
                    // 「周縁から中心へ」という前線が立たない。覆いのセルと同じ関数を通すので、
                    // 視界の中では**箱もパススルーも同じ順番で割れる**。
                    float2 qPlane;
                    float3 ql;
                    float d = IntroShardTarget(cl, _IntroScreenC.xyz, _IntroScreenR.xyz, _IntroScreenU.xyz,
                                               _IntroScreenHalf.xy, max(_IntroScreenHalf.z, 0.01),
                                               qPlane, ql);
                    float far = saturate(d / _IntroScreenHalf.w);

                    // ⚠ **スクリーン矩形の中を向いている破片は割らない**（覆いのセルと同じ規約）。
                    //    ここを割ると、箱が退いた向こうのパススルー ＝ **体験エリアの中**が
                    //    枠の中に出る（canon/LEDGER.md 0005 が禁じたもの）。段 4 の終わりの画は
                    //    従来どおり「黒 ＋ 枠の中に封印の面」。
                    if (far > max(_KeepFar, 1e-4))
                    {
                        float2 rnd = IntroShardHash2(c.xz * 71.3 + c.y * 13.7, _CellSeed);
                        IntroShard s = IntroShardEval(far, rnd.x, rnd.y, _Shatter,
                                                      _Stagger, _Jitter, _Gap, _Spin, _Drift,
                                                      _PullAt, _TravelMax, _ShrinkAt, _CloseAt);

                        // 回り・縮みは箱のローカルで（面の法線を軸にすれば破片は平らなまま回る）。
                        float3 off = (posOS - c) * (1.0 - s.gap) * s.shrink;
                        off = IntroShardSpin3(off, normalize(v.normalOS), s.spin);
                        posOS = c + off;

                        float3 qw = mul(_IntroFrameL2W, float4(ql, 1.0)).xyz;
                        pw = TransformObjectToWorld(posOS) + (qw - cw) * s.travel
                             + float3(s.drift.x, 0.0, s.drift.y);
                        alive = 1.0 - s.closed;
                    }
                    else
                    {
                        pw = TransformObjectToWorld(posOS);
                    }
                }
                else
                {
                    pw = TransformObjectToWorld(posOS);
                }

                o.positionWS = pw;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionOS = v.positionOS.xyz;
                o.normalOS = v.normalOS;
                o.alive = alive;
                o.positionCS = TransformWorldToHClip(pw);
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

                // 破片が閉じ切ったら消える（現実を返し終えた）。
                float a = saturate(_Opacity) * saturate(i.alive);
                if (a <= 0.002) return half4(0, 0, 0, 0);

                // 覆いの開口で切る。平面は覆いのローカル空間なので、世界の点を移してから見る。
                // ⚠ 内側へ **_ApertureBias** ぶん寄せる。平面は中央眼で解かれているので、
                //    眼ごとに 1〜2cm ずれる。寄せておけば覆いの黒が必ず箱の縁を覆う。
                // ⚠ **誰も配っていないなら切らない。** 平面は単位ベクトルで配られるので、
                //    長さ 0 は「未設定」を意味する。Editor のプレビューのように覆いが居ない場所で
                //    切ってしまうと、箱が丸ごと透明になる（2026-08-11 にプレビューが全部空になった）。
                if (dot(_IntroFramePlane0.xyz, _IntroFramePlane0.xyz) > 0.5)
                {
                    float3 dl = normalize(mul(_IntroFrameW2L, float4(i.positionWS, 1.0)).xyz);
                    float m = max(max(dot(dl, _IntroFramePlane0.xyz), dot(dl, _IntroFramePlane1.xyz)),
                                  max(dot(dl, _IntroFramePlane2.xyz), dot(dl, _IntroFramePlane3.xyz)));
                    const float _ApertureBias = 0.03;
                    a *= 1.0 - smoothstep(-_IntroFrameFeather - _ApertureBias, -_ApertureBias, m);
                }
                if (a <= 0.002) return half4(0, 0, 0, 0);

                // ---- 模様の座標。**箱に 1 枚のシートを巻いた形**にする ----
                // 側面 4 枚を周長方向へ展開すると、縦の 4 辺は座標が連続する ＝ 継ぎ目で噛み合う。
                // 巻き終わり（u = 0 と u = 周長）が合うよう、六角の周期を**周長の約数へ丸める**。
                // ⚠ 天面と側面は 1 枚のシートにできない（直交する 2 方向の格子は原理的に繋がらない）。
                //    そこだけは模様を消して、人の補完に任せる（LEDGER 0006）。
                float bw = max(_BoxSize.x, 0.05);
                float bh = max(_BoxSize.y, 0.05);
                float bd = max(_BoxSize.z, 0.05);
                float3 lp = i.positionOS * _BoxSize.xyz;      // ローカル座標 (m)・中心が原点

                float3 n = abs(i.normalOS);
                bool topFace = n.y > max(n.x, n.z);

                float perim = 2.0 * (bw + bd);
                float hexU = perim / max(1.0, round(perim / max(_HexSizeM, 0.02)));

                float2 pm;
                float seam;   // 1 = 継ぎ目から離れている / 0 = 継ぎ目の上
                if (topFace)
                {
                    pm = lp.xz;
                    float toRim = min(bw * 0.5 - abs(lp.x), bd * 0.5 - abs(lp.z));
                    seam = smoothstep(0.0, max(_SeamFadeM, 1e-3), toRim);
                }
                else
                {
                    // 反時計回り（真上から見て）に周長を辿る。角で必ず値が一致する。
                    float u;
                    if (n.x > n.z)
                        u = (i.normalOS.x > 0.0) ? (bw + (lp.z + bd * 0.5))
                                                 : (2.0 * bw + bd + (bd * 0.5 - lp.z));
                    else
                        u = (i.normalOS.z > 0.0) ? (bw + bd + (bw * 0.5 - lp.x))
                                                 : (lp.x + bw * 0.5);
                    pm = float2(u, lp.y);
                    seam = smoothstep(0.0, max(_SeamFadeM, 1e-3), bh * 0.5 - lp.y);
                }

                float2 gv, id;
                HexCell(pm / hexU, gv, id);
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
                float edge = smoothstep(0.5 - wdt - aa, 0.5 - wdt + aa, d) * seam;

                // ---- 光の走り。**動くのは光だけ**で、幾何は 1 ミリも動かない ----
                float t = _Time.y + _PhaseSec;
                float up = topFace ? pm.y : i.positionWS.y;   // 天面は world Y が一定なので横で代用
                float len = max(_WaveLenM, 0.05);

                // 主役は**箱の中心から広がる球面の輪**。中に何かが居て、そこから伝わってくる形。
                // ⚠ 面ごとの 2D 座標で輪を作ると、**角で輪が途切れる**。ローカルの 3D 距離なら
                //    面をまたいで連続する（平面波だと「横一列が順に点く看板」に見えた）。
                float rad = length(lp);
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
