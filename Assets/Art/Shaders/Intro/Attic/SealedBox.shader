// 封印の箱。**体験エリアの外に立っている人から見た、隔離の外側**。
//
// 出どころは .claude/canon/LEDGER.md 0003 / 0004。中から見た同じ境界が ContainmentShell で、
// **箱の外面と内面は同じ 1 つの体積**（footprint は ContainmentShellLogic が持つ）。
//
// **幾何（六角格子）は手続き、地の質感は焼いた版**（`Assets/Resources/Intro/SealBoxWear.png`）。
// 幾何を手続きにしてある理由は 3 つ:
//   - どの距離でも縁がなまらない（体験者は 3m から 0.5m まで近づく）
//   - 六角の大きさ・線の太さ・光の速さを数値で振れる（絵を焼き直さなくてよい）
//   - 面をまたいで模様が連続する（巻いたシートの座標で引くので、箱が「削り出した塊」に見える）
//
// **線は細く、幾何は正確に、動くのは光だけ**（LEDGER 0004）。
// ⚠⚠ **ただし 0011 が「線だけ、赤だけの安っぽいデジタルな見た目はやめたい」で上書きしている。**
// いま持たせているのは 3 つ:
//   - **地**: 使い込まれた金属質（酸化のむら・研磨目・磨耗・掻き傷）。テカらせない
//     — 鏡面ハイライトを 1 点でも作ると「長年使い込まれた物」ではなくなる
//   - **溝**: 磨耗で欠ける。等間隔・等幅の線が全面に並ぶことが「デジタル」の主因だった
//   - **熾**: まばらに宿り、溝の外へにじみ、揺れる。くっきり光る所は少なく、大半は暗い
// **捨てていないのは幾何と継ぎ目**（0006 の巻き方）。0011 は幾何を否定していない。
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

        // ---- 地。使い込まれた金属質（LEDGER 0011） -----------------------------------
        // 焼いた版が持つ 4 チャンネル（`tools/make-sealbox-tex.py` と対）:
        //   R = 地のむら（酸化・古び）/ G = 研磨目・ざらつき / B = 磨耗（凹み・掻き傷）
        //   A = 熾が宿る場所（**大半は 0 に近い**）
        // ⚠ 既定を "gray" にしてあるので、版が剥がれても真っ黒にはならず、
        //    のっぺりした面に縮退するだけ（C# 側が警告を出す）。
        _WearTex("Wear map (R=patina G=grain B=wear A=hearth)", 2D) = "gray" {}
        // 版が 1 巡する長さ (m)。周長の約数へ丸めるので、巻き終わりで継ぎ目が出ない。
        // ⚠ 小さくすると反復が見える（1.35m だと 3m 四方の箱で 9 回回って、同じ汚れが並ぶ）。
        _WearScaleM("Wear tile size (m)", Float) = 2.4
        // 酸化して沈んだ地。**面のほとんどがこの色**。
        _BaseColor("Patina color (darkest)", Color) = (0.021, 0.0165, 0.0145, 1)
        // 磨り出て地金が残っている所。金属の色みだけを持ち、光らない。
        // ⚠ 白へ寄せない。灰白は暗い面の上で**カビ・霜**に見える（2026-08-12 実測）。
        _MetalColor("Worn metal color", Color) = (0.086, 0.074, 0.061, 1)
        // 研磨目・ざらつきの効き（明暗のみ。色は変えない）。
        _GrainAmt("Surface grain", Range(0, 1)) = 0.42
        // **テカらない金属**。斜めから見たときだけ広く弱く持ち上がる（ハイライトは作らない）。
        _Sheen("Grazing sheen", Range(0, 1)) = 0.40
        _SheenColor("Sheen color", Color) = (0.26, 0.235, 0.20, 1)
        _SheenPow("Sheen falloff", Range(1, 8)) = 3.5

        // ---- 線。彫られた溝であって、印刷された線ではない ----------------------------
        _LineColor("Hex groove color", Color) = (0.012, 0.0095, 0.0085, 1)
        // 溝が磨耗で欠ける量。0 で均一な線（＝「安っぽいデジタル」）、1 でほとんど消える。
        _GrooveWear("Groove wear", Range(0, 1)) = 0.55
        // 溝から染み出す汚れ。溝の中だけが暗いと「印刷した線」に見える。
        _GrimeAmt("Grime around grooves", Range(0, 1)) = 0.45
        _GrimeM("Grime spread", Range(0, 0.5)) = 0.30
        // 彫った壁の陰影（片側が明るく反対が暗い）。**幾何は動かさない**。
        _BevelAmt("Groove bevel", Range(0, 1)) = 0.55
        // 箱の稜が擦れて地金が出る量と幅 (m)。新品は角が均一、使った物は角から地が出る。
        _RimWear("Edge wear", Range(0, 1)) = 0.5
        _RimWearM("Edge wear width (m)", Float) = 0.09

        // ---- 光。**熾（おき）**。赤 1 色にしない（LEDGER 0011） ----------------------
        // 芯。宿った所だけが琥珀寄りの赤で立つ。面積は小さい。
        _GlowColor("Ember core color", Color) = (0.74, 0.30, 0.085, 1)
        // 溝にふだん宿っている赤。ほとんどの場所はこれだけ。
        _EmberColor("Ember rest color", Color) = (0.38, 0.055, 0.028, 1)
        // 溝の外へにじむ色。**深い赤**。芯より暗く、彩度は高い。
        _BleedColor("Bleed color", Color) = (0.30, 0.038, 0.022, 1)
        // にじみの幅（六角セルに対する比）。
        _BleedM("Bleed width", Range(0, 0.45)) = 0.20
        // これを超えた熾だけが「くっきり」光る。上げるほど光る面積が減る。
        // ⚠ 0.66 まで上げたら**芯が事実上出なくなった**（10 秒の連番で最大 0.35%）。
        //    「少なめ」であって「無い」ではないので、芯だけ少し戻す（LEDGER 0016）。
        _HotFrac("Hot threshold", Range(0, 1)) = 0.60
        // 不安定さ（ゆらぎの深さ）。0 で一定、1 で消えかける。
        _Unrest("Instability", Range(0, 1)) = 0.45
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

            TEXTURE2D(_WearTex);
            SAMPLER(sampler_WearTex);

            float _Opacity;
            float _WearScaleM;
            float4 _BaseColor;
            float4 _MetalColor;
            float _GrainAmt;
            float _Sheen;
            float4 _SheenColor;
            float _SheenPow;
            float4 _LineColor;
            float _GrooveWear;
            float _GrimeAmt;
            float _GrimeM;
            float _BevelAmt;
            float _RimWear;
            float _RimWearM;
            float4 _GlowColor;
            float4 _EmberColor;
            float4 _BleedColor;
            float _BleedM;
            float _HotFrac;
            float _Unrest;
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
                float seam;      // 1 = 継ぎ目から離れている / 0 = 継ぎ目の上
                // 箱**自身の稜**までの距離 (m)。角は人と物が当たる所なので、そこだけ擦れて地金が出る。
                // 「使い込まれた物」に見えるかは、面の汚れよりここで決まる（新品は角が均一）。
                float rimDist;
                if (topFace)
                {
                    pm = lp.xz;
                    rimDist = min(bw * 0.5 - abs(lp.x), bd * 0.5 - abs(lp.z));
                    seam = smoothstep(0.0, max(_SeamFadeM, 1e-3), rimDist);
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
                    // 縦の稜（面の左右端）と、天面・床との稜。3 つのうち一番近いもの。
                    float toCorner = (n.x > n.z) ? (bd * 0.5 - abs(lp.z)) : (bw * 0.5 - abs(lp.x));
                    rimDist = min(toCorner, min(bh * 0.5 - lp.y, lp.y + bh * 0.5));
                }

                // ---- 使い込まれた地。焼いた版を**同じシートの上で**タイルする（LEDGER 0011）----
                // ⚠ 六角と同じく**周長の約数へ丸める**。丸めないと巻き終わりに地の継ぎ目が出て、
                //    0006 で噛み合わせた縦 4 辺に別の縫い目を作ることになる。
                float wearU = perim / max(1.0, round(perim / max(_WearScaleM, 0.05)));
                // 2 つの倍率で重ねる。1 枚だと 12m の周長に 9 回同じ模様が回って**反復が見える**。
                float4 wf = SAMPLE_TEXTURE2D(_WearTex, sampler_WearTex, pm / wearU);
                float4 wc = SAMPLE_TEXTURE2D(_WearTex, sampler_WearTex,
                                             pm / (wearU * 2.63) + float2(0.37, 0.11));
                float patina = saturate(lerp(wf.r, wc.r, 0.45));
                float grain  = lerp(wf.g, wc.g, 0.25);
                float worn   = saturate(max(wf.b, wc.b * 0.7));
                // 熾は**粗い方の形**を主にする（細かい方で混ぜると宿りがまだらに散って
                // 「暗い所が多い」が崩れる）。
                float hearth = saturate(wc.a * (0.55 + 0.45 * wf.a));

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
                // ⚠ **溝は磨耗で欠ける。** 均一な太さの線が全面に等間隔で並ぶことが、
                //    「安っぽいデジタル」の主因（LEDGER 0011）。地の磨耗（worn）と酸化（patina）で
                //    深さを変える。幾何は動かさない — 消えるのは深さだけ。
                float groove = edge * saturate(1.0 - _GrooveWear * (0.75 * worn + 0.45 * (1.0 - patina)));

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
                float pulse = saturate(pow(w1, _WaveSharp) * 0.9 + pow(w2, _WaveSharp) * 0.45);

                // セルごとに位相をずらす。揃うと「面全体が点滅する看板」に見える。
                float r = Hash21(id + float2(31.7, 17.3));
                pulse *= 0.72 + 0.28 * (0.5 + 0.5 * sin(TAU * (t / 6.3 + r)));

                // ⚠ **不安定さ**（LEDGER 0011「外側に少しだけにじみ出て不安定な様子」）。
                //    ゆっくりの息（6.3s）に速い揺らぎ（1.7s）を重ねる。熾が濃い所ほど強く揺れる
                //    ＝ 消えかけている所は静かに、燃えている所が落ち着かない。
                float unrest = 1.0 - _Unrest * (0.5 - 0.5 * sin(TAU * (t / 1.73 + r * 3.1)))
                                             * (0.35 + 0.65 * hearth);

                // 熾の量。**ふだんはこれだけ**（面のほとんどで小さい）。
                // ⚠ 第 1 項（熾が宿っていない溝にも乗る下駄）を小さくすると、**赤い溝の本数**が減る。
                //    第 2 項の係数を触ると宿った所の明るさが変わる。**別の効き方をする**ので混ぜない。
                //    2026-08-12 に 0.22 → 0.10 へ（ユーザー「減らしてみて」）。
                float breath = pulse * (0.10 + 0.90 * hearth) * unrest * _GlowGain;
                // くっきり光る芯。**熾が濃く、かつ波が来ている所だけ**なので面積が小さい。
                float hot = smoothstep(_HotFrac, 1.0, hearth * (0.5 + 0.5 * pulse)) * unrest * _GlowGain;

                // ---- 地を組む。**面のほとんどはここで決まる** ----
                // 酸化して沈んだ地 ↔ 磨り出た地金。2 乗しているのは「暗い所が多く」のため。
                float3 surf = lerp(_BaseColor.rgb, _MetalColor.rgb, patina * patina);
                // 磨耗した所は地金が擦れて少し出る。
                // ⚠⚠ **明るい色で置き換えない。** 最初 `lerp(surf, _MetalColor*1.5, worn*0.6)` と
                //    書いたら、暗い面の上で淡い塊になって**白いカビか霜**に見えた（2026-08-12 実測）。
                //    擦れて出るのは「地金の色が少し強く出る」だけで、白くはならない。
                //    しかも **研磨目に沿って出る**（擦れる方向が決まっているので、丸い斑にならない）。
                surf += _MetalColor.rgb * worn * 0.42 * (0.35 + 0.65 * grain);
                // 研磨目・ざらつきは**明暗だけ**。色を動かすと金属ではなく塗装に見える。
                surf *= 1.0 + (grain - 0.5) * 2.0 * _GrainAmt;

                // ⚠ **テカらせない**（LEDGER 0011「けどテカリはしない」）。ハイライトを作らず、
                //    斜めから見たときだけ面全体が広く弱く持ち上がる。これだけで金属に見える。
                //    鏡面反射を足すと一点が白く光り、その瞬間「使い込まれた物」ではなくなる。
                // ⚠ **照りは研磨目に沿わせる**（0.55 + 0.9 * grain）。一様な照りは塗装した板に見え、
                //    目に沿って強弱が付くと圧延・研磨した金属に見える。異方性反射の安い代用。
                float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);
                float fres = pow(saturate(1.0 - abs(dot(normalize(i.normalWS), V))), _SheenPow);
                surf += _SheenColor.rgb * (_Sheen * fres * (0.3 + 0.7 * patina)
                                           * (0.55 + 0.9 * grain));

                // ---- 溝を「彫られたもの」に見せる 2 つ（LEDGER 0016「安っぽくないように」）----
                // ① 汚れは溝から染み出す。凹んだ所に溜まって周りへ広がるのが古い物の顔で、
                //    溝の中だけが暗い状態は**印刷された線**に見える。
                float grime = smoothstep(0.5 - wdt - _GrimeM, 0.5 - wdt, d)
                              * (0.45 + 0.55 * (1.0 - patina));
                surf *= 1.0 - _GrimeAmt * grime * grime * seam;

                // ② 彫った壁は片側が明るく反対側が暗い。**幾何は動かさず陰影だけ**で深さを出す。
                //    セル中心から見た向き（gv）が壁の向きなので、決めた光の向きとの内積で符号が付く。
                //    これが無いと、どれだけ地を作り込んでも線は「描いた線」のまま。
                float2 gdir = gv / max(length(gv), 1e-5);
                float lipW = wdt * 1.8;
                float lipBand = smoothstep(0.5 - wdt - lipW, 0.5 - wdt, d) * (1.0 - edge) * seam;
                float facing = dot(gdir, normalize(float2(0.42, 0.91)));   // シート空間の光の向き
                surf *= 1.0 + _BevelAmt * lipBand * facing;

                // ③ 箱の稜は擦れて地金が出る。**面の汚れより「使い込まれた物」に効く**
                //    （新品は角が均一で、使った物は角から地が出る）。
                // ⚠ **稜に沿った一様な明るい帯にしない。** 均等な縁取りは「面取りしたモデル」に見え、
                //    かえって新品らしくなる。地のむらと研磨目を掛けて、擦れ方を不揃いにする。
                float rim = 1.0 - smoothstep(0.0, max(_RimWearM, 1e-3), rimDist);
                surf += _MetalColor.rgb * (rim * rim * _RimWear
                                           * (0.25 + 0.75 * grain) * (0.30 + 0.95 * patina));

                // 溝。**印刷した線ではなく彫った溝**なので、地のむらを保ったまま沈める。
                float3 rgb = lerp(surf, surf * 0.28 + _LineColor.rgb, groove);

                // ---- 熾。溝に宿り、外へにじむ ----
                // 溝の上: ふだんは深い赤、宿った所だけ琥珀の芯が立つ。
                rgb += (_EmberColor.rgb * breath + _GlowColor.rgb * hot) * groove;

                // にじみ。溝の**外側**へ広がる帯。幅も熾の濃さで揺れる（＝縁が定まらない）。
                float bleed = max(_BleedM * (0.45 + 0.9 * hearth) * unrest, 1e-4);
                float halo = smoothstep(0.5 - wdt - bleed, 0.5 - wdt, d) * (1.0 - edge) * seam;
                rgb += _BleedColor.rgb * (halo * halo) * (breath * 1.1 + hot * 0.55);

                // 面そのものがわずかに熾を持つ。**線だけを光らせない**（LEDGER 0011）。
                rgb += _BleedColor.rgb * (hearth * hearth) * breath * 0.35 * seam;

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
