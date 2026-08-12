// タイトルのロゴタイプ「廻リ視」。
//
// 字形と質感は**焼いた版**（`Assets/Resources/Title/MawarimiTitle.png`）が持つ。焼くのは
// `tools/make-title-art.py`。チャンネルの意味は生成器と対で、片方だけ直すと沈黙して食い違う:
//
//     R = 主の白墨（廻・リ）  G = 朱の墨（視）  B = 焼ける順  A = 添えの白墨（払い・ルビ・罫）
//
// ⚠ **これはマスクであって絵ではない。** sRGB 変換が掛かると墨の量が変わる。
// 取り込み設定は `TitleArtImporter` が機械で固定している（`sRGBTexture = false`）。
//
// 版は 1 枚だが、**3 つの層を別の Z に置いて描く**（添えを奥・主を中・朱を手前）。
// どの層かは頂点色で選ぶ: (1,0,0)=主 / (0,1,0)=朱 / (0,0,1)=添え。
// 押し出さずに層を離すのは、**押し出すと明朝の細い横画が潰れる**から。VR の立体感は
// 両眼視差が主役なので、平らな版のままでも層が分かれれば奥行きは出る。
//
// ⚠⚠ **閉じ方は「中心へ巻き込みながら焼く」**（2026-08-13・LEDGER 0022）。それまでは
// 奥へ 0.55m 退きながら溶けていたが、z を引くと題字は視界の中で**一様に小さくなる**ので、
// 消滅ではなく「遠ざかって箱に入った」と読まれた。行き先を画面の外（奥）ではなく
// **字そのものの中**へ移すため、動かすのは版の中の座標だけで、奥行きは 1 ミリも触らない。
//
// ⚠ Queue は Overlay+960 = 4960。タイトルの黒（4950）の後、5000 を超えない。
Shader "FixedCamVr/TitleGlyph"
{
    Properties
    {
        _Art("Artwork (R=main G=accent B=order A=deco)", 2D) = "black" {}
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0

        // 白は**わずかに暖かい生成り**。純白だと紙ではなく発光板に見える。
        _InkColor("Ink color", Color) = (0.88, 0.82, 0.71, 1)
        // 朱。暗い地の上で 1 文字だけが持つ色なので、彩度は高く明度は抑える。
        _AccentColor("Accent ink color", Color) = (0.62, 0.030, 0.035, 1)
        // 走る光・閃光・熾の色。墨の上に加算する。**灯りの色**（LEDGER 0010）。
        // ⚠ ここを朱にしない。赤は「視」の 1 字だけが持つアクセントで、光まで赤くすると
        //    字の朱が地に埋もれる。灯り側は橙に置いて、赤との差で朱を立てる。
        //    焼け際の熾もこの色を強めて作る（新しい赤を増やさない）。
        _GlowColor("Glow color", Color) = (0.82, 0.52, 0.24, 1)
        // 焦げ。焼け際の内側に残る暗褐色。**黒にしない** — 黒は「消えた」であって「焦げた」ではない。
        _CharColor("Char color", Color) = (0.115, 0.062, 0.042, 1)
        // 灰。冷めた粉の色。彩度をわずかに残さないと「白い点々」に見える。
        _AshColor("Ash color", Color) = (0.44, 0.40, 0.36, 1)

        _Reveal("Reveal (0..1)", Range(0, 1)) = 1
        _Dissolve("Burn (0..1)", Range(0, 1)) = 0
        _Swirl("Swirl (0..1)", Range(0, 1)) = 0

        // ⚠ **版の縦横比**。正は `TitleScreen.ArtAspect` で、そこから毎回書き込む。
        //    ここが版と食い違うと渦が楕円になり、字が横へ流れて見える。
        _ArtAspect("Art aspect (W/H)", Float) = 2.0

        _SweepGain("Running light gain", Range(0, 2)) = 0.35
        _SweepSec("Running light period (s)", Float) = 7.5
        _SweepSharp("Wave crest sharpness", Range(1, 10)) = 6.0

        _FlashPos("Flash position (0..1)", Range(-0.5, 1.5)) = 0
        _FlashAmt("Flash amount (0..1)", Range(0, 1)) = 0

        _PhaseSec("Preview phase (s)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+960" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "TitleGlyph"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define TAU 6.2831853

            // ---- 渦（閉じるときだけ効く）--------------------------------------------
            // ⚠⚠ **巻き取りには「前線」がある。全体を一度に回さない**（2026-08-13）。
            //    半径で強さを変えるだけだと、どの輪も最初から回っているので
            //    **字が回転している**としか読めない。ユーザーの見立ては
            //    「文字は回転に逆らいその場にとどまろうとしているが、逆らえずに巻き取られる」。
            //
            //    だから渦は**中心から外へ広がる前線**として持つ:
            //      前線に届いていない所は **1 ミリも動かない**（＝ 逆らってその場に居る）
            //      届いた瞬間から巻かれ始め、**巻かれていた時間ぶん**ねじれる
            //    ⇒ 同じ画の中に「まだ止まっている部分」と「もう何周も巻かれた部分」が同居し、
            //      その境目で墨が引き延ばされる。**引き延ばしは差から生まれる**。
            //
            // 前線が sw=1 で届く半径（墨の外縁は 0.79）。大きいほど早く全部が捕まる。
            #define SPOOL_REACH 1.15
            // 前線の柔らかさ。硬いと「ここから内側だけ回っている」円が見える。
            #define SPOOL_SOFT 0.14
            // 巻かれた量あたりのねじれ (rad)。中心は最後に 11rad ＝ 630° 回る。
            #define TWIST_RATE 11.0
            // 巻かれた量あたり中心へ寄る割合。1 - これが最終の大きさ。
            // ⚠ 0 へ潰さない。点まで縮めると結局「吸い込まれた」になる。
            // ⚠ **強くすると中心に墨が溜まって明るくなる**（2026-08-13 の指摘）。
            //    寄せは控えめにして、溜まる前に消す（下の EAT_*）方で抑える。
            #define PULL_RATE 0.30

            // ---- 引き延ばし ---------------------------------------------------------
            // **渦の道筋を遡って重ねる** ＝ 墨が通ってきた跡がそのまま尾になる。
            // 方向を決め打ちしたブラーではないので、尾は必ず渦に沿って曲がる。
            // ⚠ 渦が止まっている間（`_Swirl` が 0）は 1 タップへ落ちる。段 0〜Hold で
            //    6 タップ払う理由が無いし、分岐は画面全体で揃うので実質ただ。
            #define SMEAR_TAPS 14
            // ⚠⚠ **尾は「巻かれた履歴」を遡る。** 長さを巻き取り量で測るので、
            //    捕まったばかりの墨は尾が短く（まだ動いていない）、長く巻かれた墨は尾が長い。
            //    **どこまで遡っても止まっていた時刻より前へは行かない**（`SpoolWound` が 0 で止まる）
            //    ＝ 尾の端はその墨の「元居た場所」で必ず終わる。
            //
            // ⚠⚠ **タップの刻みは見える。** 明朝の横画は数画素しか無いので、刻みが線の太さを
            //    超えると**複製が梯子状に並ぶ**（2026-08-13 実測。刻み 12px で梯子になった）。
            //    タップを増やすのは払えない（3 層 × 全画面で texel 帯域が線形に効く）ので、
            //    **画素ごとにタップ位置をずらして刻みを雑音へ散らす**。
            //    燃えているものの尾なので、雑音は主題に合う（灰と熾の粒が既に居る）。
            //
            // ⚠ **刻みは尾の中で一定にしない。** 長い尾を等間隔で刻むと、払える範囲の
            //    タップ数では先頭まで粗くなる。先頭は明るいので刻みが見え、端は薄いので見えない。
            //    だから**先頭を細かく、端を粗く**する（u^1.5。端の刻みは先頭の 3.2 倍）。
            #define SMEAR_WOUND 0.17
            // 尾の端の濃さ。
            #define SMEAR_TAIL 0.24
            // 尾が墨より遅れて消える猶予（eat 単位）。
            // ⚠ **0 にすると尾が墨と同時に切れて、燃えた跡が 1 フレームも残らない。**
            //    大きくすると焼け落ちた所に残像が居座って「消し忘れ」に見える。
            #define SMEAR_LAG 0.10

            // ---- 焼け ---------------------------------------------------------------
            // ⚠⚠ **焼ける順は「巻かれた量」で持つ。半径で持たない**（2026-08-13・LEDGER 0027）。
            //    半径で持つと**外から欠けていく**ので、渦に入る前に墨が失われる。
            //    ユーザー指示は「外側から消えていくのはやめて、全部巻き込まれて消えるように」。
            //    巻き取りに結びつければ、消えるのは**渦に入ったものだけ**になり、
            //    しかも**中心に溜まる前に消える**ので明るくならない（同日のもう 1 つの指摘）。
            //
            // 焼けの座標 `eat` は「その墨自身の巻き取りの進み」（0 = 捕まったばかり / 1 = 巻き切り）。
            // 半径で正規化してあるので、**外周の墨も自分の番が来れば必ず消える**。
            //
            // ⚠⚠ **食い始めを遅らせない。** 0.50 から欠け始めるようにしたら、中心の墨が
            //    巻き終わるまで無傷で残って**画のいちばん明るい塊**になった（2026-08-13 実測。
            //    画面平均が題字の 1.6 倍）。ユーザー指示は「適度に消しながらまくことで
            //    明るくなるのを抑えて」。**捕まった瞬間から焦げ始め、巻きの半ばで消え切る**。
            #define EAT_B0 0.20      // ここから欠け始める
            #define EAT_B1 0.70      // ここで消え切る
            #define EAT_JITTER 0.30  // 版の B で際を散らす（定規で引いた円にしない）
            #define EAT_CHAR 0.34    // 焦げが乗り始める手前の幅
            #define EMBER_AT 0.44    // 熾がいちばん光る所（欠け始めと消え切りの中ほど）
            // ⚠ **熾は焦げよりずっと狭くする。** 同じ幅にすると熾の加算が焦げを塗り潰し、
            //    墨が「焦げてから失われる」ではなく「光って消える」に見える（2026-08-13 実測）。
            #define EMBER_W 0.055
            #define EMBER_GAIN 1.7
            // 焼けてから灰が消えるまで（eat 単位）と、灰が舞い上がる高さ（uv）。
            #define ASH_LIFE 0.35
            #define ASH_RISE 0.055

            TEXTURE2D(_Art);
            SAMPLER(sampler_Art);

            float _Opacity;
            float4 _InkColor;
            float4 _AccentColor;
            float4 _GlowColor;
            float4 _CharColor;
            float4 _AshColor;
            float _Reveal;
            float _Dissolve;
            float _Swirl;
            float _ArtAspect;
            float _SweepGain;
            float _SweepSec;
            float _SweepSharp;
            float _FlashPos;
            float _FlashAmt;
            float _PhaseSec;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;      // 版の中の座標
                float2 local : TEXCOORD1;   // 版の中の 0..1（光の走り・出現のワイプ用）
                float4 color : COLOR;       // 層の選択 (主, 朱, 添え)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.local = v.local;
                o.color = v.color;
                return o;
            }

            float Hash21(float2 p)
            {
                float3 q = frac(float3(p.xyx) * 0.1031);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            /// この層が受け持つ墨だけを取り出す（層の選択は頂点色）。
            float LayerInk(float4 t, float3 sel)
            {
                return t.r * sel.r + t.g * sel.g + t.a * sel.b;
            }

            /// <summary>この半径の墨が前線に捕まる時刻（渦の進み単位）。</summary>
            float SpoolCaught(float radius)
            {
                return radius / SPOOL_REACH;
            }

            /// <summary>
            /// この半径の墨が、渦の進み <paramref name="amt"/> の時点で<b>どれだけ巻かれたか</b>。
            /// 前線（中心から外へ広がる）に届くまでは <b>0 ＝ その場を動かない</b>。
            /// </summary>
            float SpoolWound(float radius, float amt)
            {
                float x = amt - SpoolCaught(radius);
                // 前線を柔らかく。硬いと「ここから内側だけ回っている」円が絵に出る。
                return max(0.5 * (x + sqrt(x * x + SPOOL_SOFT * SPOOL_SOFT)) - 0.5 * SPOOL_SOFT, 0.0);
            }

            /// <summary>
            /// 焼けの座標。<b>その墨自身の巻き取りの進み</b>（0 = 捕まったばかり / 1 = 巻き切り）。
            ///
            /// ⚠ 半径で正規化する。生の巻き量のままだと中心（巻き量 1）と外周（同 0.31）で
            /// 桁が違い、**外周の墨は自分の番が来ても消えない**。
            /// </summary>
            float EatCoord(float radius, float amt)
            {
                float span = max(1.0 - SpoolCaught(radius), 0.08);
                return saturate(SpoolWound(radius, amt) / span);
            }

            /// <summary>
            /// 渦の進みが <paramref name="amt"/> のとき、この画素へ来る版の座標。
            /// <b>回してから中心へ寄せる</b>（順序を入れ替えると尾が渦に沿わない）。
            /// </summary>
            float2 WarpAt(float2 pOut, float2 aspect, float radius, float amt)
            {
                float wound = SpoolWound(radius, amt);
                float sa, ca;
                sincos(TWIST_RATE * wound, sa, ca);
                float2 p = float2(ca * pOut.x - sa * pOut.y, sa * pOut.x + ca * pOut.y);
                // 外から引いてくる ＝ 描かれる版が中心へ縮む。
                p /= max(1.0 - PULL_RATE * min(wound, 1.0), 0.2);
                return p / aspect + 0.5;
            }

            float InArt(float2 uv)
            {
                return step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float op = saturate(_Opacity);
                if (op <= 0.002) return half4(0, 0, 0, 0);

                float sw = saturate(_Swirl);
                float burning = step(0.003, _Dissolve);

                // ---- 渦。**版の中で**中心へ寄せてねじる（奥行きは動かさない）----
                // 縦横比を戻してから回す。戻さないと渦が楕円になり、字が横へ流れて見える。
                float2 aspect = float2(max(_ArtAspect, 0.01), 1.0);
                float2 pOut = (i.uv - 0.5) * aspect;
                float radius = length(pOut);
                float2 wuv = WarpAt(pOut, aspect, radius, sw);
                // 版の外を引きに行った所は空。Clamp の縁を引き伸ばさないよう明示的に切る。
                float inArt = InArt(wuv);

                float4 t = SAMPLE_TEXTURE2D(_Art, sampler_Art, wuv);
                float ink = LayerInk(t, i.color.rgb) * inArt;

                // ---- 焼ける。**巻き取られたぶんだけ食われる**（半径では食わない）----
                //    際は版の B で散らす。散らさないと定規で引いた円が広がるだけになる。
                float eat = EatCoord(radius, sw) + (t.b - 0.5) * EAT_JITTER;
                // 1 = 墨が残っている。**火が無いときは必ず 1**（0.2 秒の溜めを守る堰）。
                float left = lerp(1.0, 1.0 - smoothstep(EAT_B0, EAT_B1, eat), burning);
                float charAmt = smoothstep(EAT_B0 - EAT_CHAR, EAT_B0 + 0.06, eat) * burning;
                float ember = exp(-((eat - EMBER_AT) / EMBER_W) * ((eat - EMBER_AT) / EMBER_W)) * burning;
                // 尾は墨より**遅れて**消える（燃えた跡が少し残る）。
                float leftTail = lerp(1.0, 1.0 - smoothstep(EAT_B0 + SMEAR_LAG,
                                                            EAT_B1 + SMEAR_LAG, eat), burning);

                // ---- 引き延ばし。渦の道筋を遡って重ねる ----
                // ⚠ **尾も火で切る**（`leftTail`）。切らないと、焼け落ちた所に
                //    墨の残像だけが取り残されて「消し忘れ」に見える。
                float shape = ink * left;
                if (sw > 0.001)
                {
                    // 画素ごとの位相。これが無いと刻みが梯子として見える。
                    float jit = Hash21(floor(i.uv * float2(2048.0, 1024.0)));
                    [unroll]
                    for (int j = 1; j < SMEAR_TAPS; j++)
                    {
                        // 0 = 先頭 / 1 = 尾の端。位相ぶんずらし、先頭を細かく刻む。
                        float u = ((float)j - jit) / (float)(SMEAR_TAPS - 1);
                        float k = u * sqrt(u);
                        float2 uvj = WarpAt(pOut, aspect, radius, sw - SMEAR_WOUND * k);
                        float4 tj = SAMPLE_TEXTURE2D(_Art, sampler_Art, uvj);
                        float inkj = LayerInk(tj, i.color.rgb) * InArt(uvj);
                        // ⚠ 足さずに **max**。重なった所だけ濃くなると、尾ではなく塊に見える。
                        //    ここが加算だと、渦の中心で尾が何重にも重なって**明るい塊**になる。
                        shape = max(shape, inkj * leftTail * (1.0 - k * (1.0 - SMEAR_TAIL)));
                    }
                }
                if (shape <= 0.003 && burning <= 0.0) return half4(0, 0, 0, 0);

                // ---- 出現。下から墨が満ちていく ----
                float wipe = smoothstep(wuv.y - 0.32, wuv.y + 0.12, _Reveal * 1.42);

                // ---- 灰。焼けた所から少し上へ流れて消える ----
                // 元の墨は下にあるので、**下から引いて**上に出す（1 タップ余分に払う）。
                float2 auv = wuv - float2(0.0, ASH_RISE * sw);
                float4 ta = SAMPLE_TEXTURE2D(_Art, sampler_Art, auv);
                float aInk = LayerInk(ta, i.color.rgb)
                           * step(0.0, auv.y) * step(auv.y, 1.0) * inArt;
                // 灰の齢は「消え切ってからどれだけ巻かれたか」。
                float aAge = saturate((eat - EAT_B1) / ASH_LIFE);
                // 粒。セルごとに 2 割だけ点を立て、age で上へ流す。
                float2 q = wuv * float2(210.0, 105.0);
                q.y -= aAge * 5.0;
                float2 cell = floor(q);
                float h = Hash21(cell);
                float2 fr = frac(q) - 0.5;
                fr.x += (h - 0.5) * 0.55;
                float fleck = step(0.74, h) * saturate(1.0 - length(fr) * 2.9);
                float ash = aInk * fleck * (1.0 - aAge) * step(0.001, aAge) * burning;

                // ---- 走る光。**弱く**。版は印刷物なので、光り出すと紙に見えなくなる ----
                float tm = _Time.y + _PhaseSec;
                float w = 0.5 + 0.5 * sin(TAU * (wuv.y * 1.1 + wuv.x * 0.35
                                                 - tm / max(_SweepSec, 0.2)));
                float sheen = pow(w, _SweepSharp) * _SweepGain;

                // ---- A を押した瞬間に走り抜ける光 ----
                float bx = (wuv.x - (_FlashPos * 1.7 - 0.35)) / 0.13;
                float flash = exp(-bx * bx) * saturate(_FlashAmt);

                float3 rgb = lerp(_InkColor.rgb, _AccentColor.rgb, saturate(i.color.g));
                rgb *= 1.0 + sheen;
                // 焦げは色を**置き換える**（暗くするだけだと「影が差した」に見える）。
                rgb = lerp(rgb, _CharColor.rgb, charAmt * 0.92);
                rgb += _GlowColor.rgb * (flash * 1.6 + ember * EMBER_GAIN);

                // 尾は**焦がして**引く。素の墨のまま伸ばすと、燃えているものの跡ではなく
                // 「motion blur が掛かった文字」に見える。
                float tail = saturate((shape - ink * left) * 1.8);
                rgb = lerp(rgb, lerp(_CharColor.rgb, _GlowColor.rgb, 0.22), tail * 0.60);

                // 焼けたばかりの灰はまだ熾を含む。冷めるほど灰の色へ寄る。
                float3 ashRgb = lerp(_GlowColor.rgb * 1.1, _AshColor.rgb, saturate(aAge * 1.6));

                float aInkA = shape * wipe * op;
                float aAshA = ash * wipe * op * 0.9;
                float alpha = saturate(aInkA + aAshA);
                if (alpha <= 0.002) return half4(0, 0, 0, 0);
                // ⚠ rgb は straight（ブレンドの SrcAlpha が掛ける）。ここで alpha を掛けると二重になる。
                //    墨と灰は**それぞれの寄与で重み付け平均**する（加算すると縁が飛ぶ）。
                rgb = (rgb * aInkA + ashRgb * aAshA) / max(aInkA + aAshA, 1e-4);
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
