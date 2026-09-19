// スクリーンの外の闇に開く**目**（canon/LEDGER.md 0075 / 0076 / 0237 / **0238**）。
//
// 1 つの目 = 中心（体験者の頭）を向いた quad 1 枚。**形（瞼の包絡）はここで描き、進み方は C# が決める**
//   （AnomalyEyesLogic が `_EyeBig` / `_EyeField` / `_EyeIntensity` を配る。OutroLogic.FlickerPower と同じ流儀）。
//
// ⚠⚠ **2026-09-19（0238）に、目の中身を「焼いた版」へ替えた。** 手続きで描いた帯（0237 の第 1 版）は
//    ユーザー判定「整然としすぎ・質感とデジタル感がチープ」。参考 `tools/eyes-ref/dejime.jpg` は
//    **写真の目が壊れた画像データ**（JPEG のマクロブロック・行のずれ・複製・欠け・1 画素の色ノイズ）で、
//    手続きの 1 色のセルではその粒に届かない。だから Codex に同じ画風で描かせた目を
//    `tools/make-eye-glitch.py` が版（`Resources/Eyes/EyeGlitch.png`・2×2 の 4 個体）へ焼き、ここは
//    **瞼の包絡で切って、帯ごとにずらして、視線で滑らせて引く**だけにした。
//    筋は 0237 のまま —「高次元の存在が、次元のハザマからのぞき込んでいる」。壊れた画像データは
//    この作品の乱れの語彙（本編の `_Glitch`・連絡の面の乱れ）そのもので、目は「画面の乱れの向こうに居るもの」。
//    ⚠ **段の進み（0076 / 0094 / 0138）と瞼の包絡（笑い・瞬き・視線）は 1 つも変えていない。**
//
// ⚠⚠ **2026-09-19（0240）に、出現と消失を「行が届く・ブロックが落ちる」へ替えた。**
//    それまでは縦の潰し率（squash）で、版の行を中心線へ連続に潰していた ＝ **生き物の瞼**の語彙。
//    中身が壊れた画像データになった今、外形だけが滑らかに伸縮するのは合っていない。参考画像に
//    連続な変形は 1 つも無く、あるのは**届いた行と届いていない行・ずれたまま止まった行・欠けたブロック**だけ。
//    ⇒ 縦に伸縮させず、`open`（0..1）を**中心線から外へ何行届いたか**へ写す。
//    ⚠ **時計は 1 ビットも触っていない**（AnomalyEyesLogic の尺・EyeOpen・瞬き・閉じの幅はそのまま）。
//
// 版の約束（`make-eye-glitch.py` の TILE / EYE_W / ratio と対）:
//   - 2×2 のタイル。各タイルの中央に目 1 つ。目の箱は横 TEX_EYE_W・縦はその TEX_RATIO 倍
//   - 黒の地は alpha 0（目の中の黒い欠けも 0 ＝ 闇がそのまま透ける）。rgb は alpha を掛けてある（前乗算）
//   - 目の上下にデータの柱が付いていることがある（タイルの余白に描かれている）
//
// ⚠⚠ **前乗算アルファ（Blend One OneMinusSrcAlpha）。** 2026-08-17 に加算から変えた（0076）。
//    加算では**重なった 2 つが必ず 1 つの塊に融ける**ので、参考画像のような密度にできない。
//    ⚠ 手前 / 奥は**メッシュの並び順**で決まる（座席表を小さい順に並べてある ＝ 大きい ＝ 近い目が後）。
//
// ⚠ **Queue は Background+100（1100）。** 本編のスクリーン（ScreenComposite = Geometry / 不透明）が
//    後から上書きするので、**目はスクリーンの外にしか出ない**（0075「スクリーンの外の黒い背景を」）。
//
// ⚠ **実行時 Shader.Find で引く。** ProjectSettings/GraphicsSettings.asset の Always Included に
//    登録してある（外すと Editor では出て実機だけ剥がれる — rules/unity-vr.md の 2026-07-31 実害）。
//
// ⚠ **版の mip の段は、ずらす前の uv の微分で選ぶ**（SAMPLE_TEXTURE2D_GRAD）。帯ごとに uv が跳ぶので、
//    素直に引くと帯の継ぎ目で微分が発散して 1 画素の細い線に別の段が混ざる。
Shader "FixedCamVr/AnomalyEyes"
{
    Properties
    {
        // C# が毎フレーム書く（Inspector の値は Editor プレビューの初期値でしかない）。
        _EyeBig("Big Eye Open", Range(0, 1)) = 0
        _EyeField("Field Open", Range(0, 1)) = 0
        _EyeSpan("Swarm Span", Range(0.005, 1)) = 0.035
        _EyeDensity("Density", Range(0, 1)) = 1
        _EyeFade("Fade", Range(0, 1)) = 0
        _EyeIntensity("Intensity", Range(0, 1)) = 0
        _EyeTime("Time", Float) = 0
        _EyeGain("Gain", Range(0, 2)) = 1
        _EyeBlink("Blink Amount", Range(0, 1)) = 1
        // 待機中の視線移動の強さ（AnomalyEyesLogic.Gaze）。0 = 正面を見たまま。
        _EyeGaze("Gaze Amount", Range(0, 1)) = 0
        // 笑い（下瞼が持ち上がる。大きい目だけ）と、閉じ中か（開きかけの散らばりを止める）。
        _EyeSmile("Smile", Range(0, 1)) = 0
        _EyeClosing("Closing", Range(0, 1)) = 0
        // 版に掛ける色。既定は白（版の色をそのまま出す）。C# の AnomalyEyes.DefaultColor と対（本番はシーンの値）。
        _EyeColor("Tint", Color) = (1, 1, 1, 1)
        // 焼いた版（Resources/Eyes/EyeGlitch.png）。C# が起動時に 1 度だけ書く。無ければ黒 ＝ 目は出ない。
        _EyeTex("Glitch Atlas", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background+100" }
        LOD 100

        Pass
        {
            Name "AnomalyEyes"
            Blend One OneMinusSrcAlpha     // 前乗算アルファ（体は隠す / 何も足さない）
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // ⚠⚠ **Single Pass Instanced（Quest の既定）で両眼へ正しく描くために要る。**
            //    無いと Editor では出るのに**実機で片眼にしか出ない / 位置がずれる**。
            //    プレビューは単眼カメラなので、この誤りは絵からは絶対に分からない。
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_EyeTex);
            SAMPLER(sampler_EyeTex);

            CBUFFER_START(UnityPerMaterial)
                float _EyeBig;
                float _EyeField;
                float _EyeSpan;
                float _EyeDensity;
                float _EyeFade;
                float _EyeIntensity;
                float _EyeTime;
                float _EyeGain;
                float _EyeBlink;
                float _EyeGaze;
                float _EyeSmile;
                float _EyeClosing;
                float4 _EyeColor;
            CBUFFER_END

            // ---- 目の形（すべて quad ローカル -1..1）------------------------------------
            // 目が quad の中で占める割合（横）。⚠ AnomalyEyesMesh.ShapeScale と対。
            // ⚠⚠ 0.74 → 0.90（0239）。余白は版画の暈のためのもので、版の目には要らない。quad ≒ 版のタイルになり塗る面積が 32% 減る。
            #define SHAPE_SCALE 0.90
            // quad の縦の余白（上下の柱のぶん）。⚠ AnomalyEyesMesh.RiftExtend と対 — 片方だけ直すと目が縦に潰れるか伸びる。
            // ⚠⚠ 1.5 → 0.8（0239）。版の柱は目の丈の ±0.67 にしか無いのに 1.5 まで張ると塗る面積が倍で、実機が 35 fps だった。
            #define RIFT_EXTEND 0.8

            // ---- 版（tools/make-eye-glitch.py と対）------------------------------------------
            #define TEX_COLS 2.0
            #define TEX_ROWS 2.0
            #define TEX_EYE_W 0.92      // タイル幅に対する目の箱の横幅
            #define TEX_RATIO 0.60      // 目の箱の 縦/横（Codex の目は 0.51〜0.55 なので、伸ばすのは 2 割まで）
            // 目の箱の外（上下）へ、箱の縁の行を縦に引き伸ばして出す柱。参考画像の「データの行を積んだ柱」。
            // 版にもともと柱が描いてあればそれが優先（max で合流）。
            #define SMEAR_REACH 1.1     // 目の丈に対して、どこまで届くか
            #define SMEAR_COLS 14.0     // 目の横幅あたりの柱の候補の数
            #define SMEAR_DENSITY 0.45  // 目のすぐ上での柱の密度（離れるほど疎ら）
            #define SMEAR_LUM 0.65
            // 柱が立ちうる帯（タイルの中心からの半幅）。Codex の 4 個体の柱は ±0.25 の中。外は版も柱も無いので引かずに捨てる。
            #define SMEAR_HALF_W 0.30
            // 視線で平行移動する範囲（目の箱の楕円に対する比）。0.55 まで版ごと動く・1.0（輪郭）で止まる・間は薄れる。
            #define GAZE_E1 0.35
            #define GAZE_E2 1.0
            // 動く量の上限（タイルの単位）と、薄れの段数（滑らかだと動きぼかしに見える）。
            #define GAZE_MAX_T 0.10
            #define GAZE_STEPS 5.0

            // ---- 帯（視線の縁の刻み直し）------------------------------------------------------
            // 帯の刻み（目の丈に対する本数）。版の粒より粗い刻みで、版そのものを行ごとにずらす。
            #define BANDS_MIN 14.0
            #define BANDS_MAX 96.0
            // 開き切った後のずれ（目の半幅に対する比）。版が既に壊れているので僅かでよい。
            #define SHEAR_HOLD 0.03

            // ---- 出現と消失（0240）------------------------------------------------------------
            // ⚠⚠ **目は開かない、復号される。目は閉じない、脱落する。**
            //    縦の潰し率（squash）を廃した。版の 1 画素は常に同じ場所に描き、見えるのは
            //    **どの行が届いているか**だけ。動く単位は画像データの単位（行・ブロック・横ずれ）で、
            //    値は 2 状態（届いた／届いていない・ずれている／いない）。**連続の補間をしない。**
            //    出現 ＝ 中心線から外へ行が届く / 消失 ＝ 外から内へブロックが落ちる（最後は中心の 1 行）。
            #define ROWS_MIN 6.0        // 片側の行数（小さい目）
            #define ROWS_MAX 16.0       // 同（視界を埋める目）。sizeN で補間する
            #define BLOCK_COLS 10.0     // 目の横幅あたりのブロック数
            #define ARRIVE_JITTER 0.6   // 同じ行の中で届く時刻がずれる幅（行の単位）
            #define TEAR_WIN 1.5        // 届いてからずれている長さ（行の単位）
            #define TEAR_MAX 0.12       // 横ずれの最大（タイルの単位・GAZE_MAX_T と同じ桁）
            #define DUP_P 0.25          // 内側の行を写すブロックの割合
            #define STUCK_P 0.12        // 居残るブロックの割合（閉じるときだけ）
            #define STUCK_EXTRA 0.25    // 居残る長さ（open の単位）
            #define PILLAR_LEAD 3.0     // 柱が行より先に立つ倍率

            // ---- 動き ----------------------------------------------------------------------
            // 瞬き（周期の逆数 / 鋭さ）。9 秒に 1 度・0.22 秒。**大きい目には掛けない**
            //（あちらは C# が段の中で 1 度だけ瞬かせる）。
            #define BLINK_RATE 0.11
            #define BLINK_SHARP 40.0
            // 待機中の視線（canon/LEDGER.md 0084「待機中は目がぎょろぎょろ動く感じ」）。
            // ⚠⚠ **滑らかに動かさない。** 実物の目は止まって一瞬で飛ぶ（サッカード）。
            //    滑らかに回すと「機械のスキャン」に見える。開き方（0076）とまったく同じ理屈。
            // ⭐ 帯の刻み直しも**この飛びと同じ縁**で起きる。別の時計で刻み直すと群れ全体が常にちらつく。
            #define GAZE_RATE 0.80      // 1 秒あたりの停留の数 ＝ 1.25 秒に 1 度飛ぶ
            #define GAZE_HOLD 0.94      // 停留の割合。残り 0.06 ＝ **0.075 秒で飛ぶ**（実物は 0.03〜0.08）
            #define GAZE_REACH 0.72
            #define GAZE_IRIS_KEEP 0.45
            #define GAZE_TILT 0.45      // 縦の動きは横より狭い（実物の目と同じ）
            // ⚠ 目ごとに位相を散らす種の掛け数。**素数どうしにする** — 揃うと群れが 1 匹に見える。
            #define GAZE_SEED_T 11.3
            #define GAZE_SEED_X 3.1
            #define GAZE_SEED_Y 7.7
            #define GAZE_STAGGER 1.0
            // 笑い。**下瞼が中央ほど持ち上がり、上瞼も少し下りる**（実物の笑い目 ^ ^ と同じ）。
            #define SMILE_LIFT 0.95     // 下瞼（1.0 で中心線まで）
            #define SMILE_TOP  0.25     // 上瞼（大きくすると眠そうな目に寄る）

            float h11(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }
            float h21(float2 p)
            {
                float3 p3 = frac(p.xyx * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 attr : TEXCOORD1;    // (順位, 大きい目か, 種, 籤)
                float4 form : TEXCOORD2;    // (縦横比, 上瞼, 下瞼, 虹彩半径)
                float4 form2 : TEXCOORD3;   // (開き切る量, 目尻の傾き, 瞳孔のずれ, 大きさ 0..1)
                float4 form3 : TEXCOORD4;   // (上瞼の弧, 下瞼の弧, 横の歪み, -)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 attr : TEXCOORD1;
                float4 form : TEXCOORD2;
                float4 form2 : TEXCOORD3;
                float4 form3 : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.attr = input.attr;
                o.form = input.form;
                o.form2 = input.form2;
                o.form3 = input.form3;
                return o;
            }

            // 版の 1 タイルの中の uv（0..1）を、アトラスの uv へ。タイルの外へは出さない（Clamp が
            // 反対側のタイルを漏らさないよう、ここで切る）。
            float2 AtlasUv(float2 t, float variant)
            {
                float cx = fmod(variant, TEX_COLS);
                float cy = floor(variant / TEX_COLS);
                t = clamp(t, 0.0015, 0.9985);
                // PNG の 0 行目が上なので、タイルの行は上から数える（v は下が 0）。
                return float2((cx + t.x) / TEX_COLS, 1.0 - (cy + 1.0 - t.y) / TEX_ROWS);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float rank = i.attr.x;
                float isBig = i.attr.y;
                float seed = i.attr.z;
                float presence = i.attr.w;
                float aspect = i.form.x;      // quad（目の部分）の 縦/横
                float lidUp = i.form.y;
                float lidDn = i.form.z;
                float irisR = i.form.w;
                float openMax = i.form2.x;
                float skew = i.form2.y;
                float sizeN = i.form2.w;      // 0 = 小さい目 / 1 = 視界を埋める目
                float arcUp = i.form3.x;
                float arcDn = i.form3.y;
                float warpX = i.form3.z;

                // 籤に外れた目は最初から居ない（大きい目は籤に関係なく必ず出る）。
                if (isBig < 0.5 && presence > _EyeDensity) discard;

                // 開き具合。**AnomalyEyesLogic.EyeOpen と同じ式**（片方だけ直すと、
                // 数えた本数（eyesN）と画が黙って食い違う）。
                float span = max(_EyeSpan, 0.005);
                float prog = saturate((_EyeField * (1.0 + span) - rank) / span);
                prog = lerp(prog, _EyeBig, isBig);
                float open = prog * lerp(openMax, 1.0, isBig);

                // 瞬き。⚠ **動かすものは 1 つに絞る**（開眼が主・瞬きは稀・刻み直しは視線の縁だけ）。
                if (isBig < 0.5)
                {
                    float bp = frac(_EyeTime * BLINK_RATE + seed);
                    open *= 1.0 - _EyeBlink * saturate(1.0 - abs(bp - 0.5) * BLINK_SHARP);
                }

                if (open <= 0.002 || _EyeFade <= 0.002) discard;

                // 目の座標。quad は縦に RIFT_EXTEND 倍長いので、戻してから目の式へ入れる。
                float2 p = float2(i.uv.x, i.uv.y * RIFT_EXTEND);

                // ---- 待機中の視線。**止まって、一瞬で飛ぶ。**
                float gt = _EyeTime * GAZE_RATE + seed * GAZE_SEED_T;
                float gi = floor(gt);
                float gm = smoothstep(GAZE_HOLD, 1.0, frac(gt));
                float2 gA = float2(h21(float2(gi, seed * GAZE_SEED_X)),
                                   h21(float2(gi, seed * GAZE_SEED_Y))) * 2.0 - 1.0;
                float2 gB = float2(h21(float2(gi + 1.0, seed * GAZE_SEED_X)),
                                   h21(float2(gi + 1.0, seed * GAZE_SEED_Y))) * 2.0 - 1.0;
                float gGate = step(h11(seed * 5.7) * GAZE_STAGGER, _EyeGaze * (1.0 + GAZE_STAGGER));
                float ir = irisR * (0.45 + 0.55 * open) * SHAPE_SCALE;
                float reach = max(SHAPE_SCALE - ir * GAZE_IRIS_KEEP, 0.0) * GAZE_REACH * gGate * open;
                float2 gaze = lerp(gA, gB, gm) * reach * float2(1.0, GAZE_TILT);
                float epoch = gi * gGate;       // 帯の刻み直しは視線が飛んだ縁

                // ---- 帯（版を行ごとにずらす）。
                // ⚠ 0240 から、開きかけの散らばりと欠けはここではなく**行のゲート**（下）が持つ。
                //   ここに残るのは開き切った後の僅かなずれだけ。
                float eyeH = (lidUp + lidDn) * SHAPE_SCALE;
                float pitch = eyeH / lerp(BANDS_MIN, BANDS_MAX, sizeN);
                float bi = floor((p.y + RIFT_EXTEND) / pitch);
                float hb = h21(float2(bi + epoch * 97.0, seed * 13.1));
                float hb2 = frac(hb * 17.31 + 0.37);
                float shear = (hb2 * 2.0 - 1.0) * SHEAR_HOLD * SHAPE_SCALE;
                float2 q = float2(p.x - shear, p.y);

                // ---- 瞼の包絡（0076 / 0077 の式そのまま）。
                // ⚠⚠ **0239 からは版を切らない。** 包絡が与えるのは**中心線**（目尻の傾き・笑いの曲がり）と
                //    **笑いで縮む片側の丈**だけ。見える形は常に版の目そのものの alpha。切ると、閉じる途中に
                //    手続きの綺麗な弧の縁が現れ、開き切っても版の縁と包絡の縁の 2 種類が混ざる（判定「破綻」）。
                // ⚠⚠ **0240 から縦に伸縮させない。** 開く ＝ 中心線から外へ行が届く（線から膨らむのではない）。
                //    閉じる ＝ 外から内へブロックが落ちる（中心線へ潰れるのではない）。
                //    笑い（0085）は中心線が上へ曲がるので、最後に残る 1 行が上に凸の三日月になる。
                float px = q.x / SHAPE_SCALE;
                float pu = clamp(px + warpX * px * (1.0 - abs(px)), -1.0, 1.0);
                float bU = sqrt(max(arcUp * arcUp - 1.0, 1e-4));
                float bD = sqrt(max(arcDn * arcDn - 1.0, 1e-4));
                float lidU = saturate((sqrt(max(arcUp * arcUp - pu * pu, 0.0)) - bU) / (arcUp - bU));
                float lidD = saturate((sqrt(max(arcDn * arcDn - pu * pu, 0.0)) - bD) / (arcDn - bD));
                float lid = max(lidU, lidD);
                float tilt = skew * pu * lid * SHAPE_SCALE;
                float up = lidUp * open * lidU * SHAPE_SCALE + tilt;
                float dn = -lidDn * open * lidD * SHAPE_SCALE + tilt;
                float smile = _EyeSmile * isBig;
                up -= smile * SMILE_TOP * lidUp * lidU * open * SHAPE_SCALE;
                dn += smile * SMILE_LIFT * lidDn * lidD * open * SHAPE_SCALE;
                dn = min(dn, up);
                float mid = 0.5 * (up + dn);

                // ---- 版を引く。横は目の箱（横幅 2×SHAPE_SCALE）を版の目の箱へ。縦は等方の尺（quad の縦の単位は
                //    横の aspect 倍）を**そのまま**。⚠ 0240 から潰し率で割らない ＝ 版の 1 画素は常に同じ場所。
                float variant = floor(h11(seed * 7.7) * (TEX_COLS * TEX_ROWS - 0.001));
                float flip = step(0.5, h11(seed * 2.1)) * 2.0 - 1.0;   // 個体の半分は左右反転
                float Kv = 0.5 * TEX_EYE_W * aspect / SHAPE_SCALE;
                float2 t0 = float2(0.5 + 0.5 * TEX_EYE_W * flip * p.x / SHAPE_SCALE,
                                   0.5 + (p.y - mid) * Kv);
                // ⚠ mip の段は、ずらす前・跳ばす前の uv の微分で選ぶ（帯ごと・ブロックごとに uv が跳ぶ）。
                float2 dtx = ddx(t0) / float2(TEX_COLS, TEX_ROWS);
                float2 dty = ddy(t0) / float2(TEX_COLS, TEX_ROWS);
                float2 t = float2(0.5 + 0.5 * TEX_EYE_W * flip * q.x / SHAPE_SCALE, t0.y);

                // ---- どの行が届いているか（0240）。中心線から外へ数えた行 row と、横 BLOCK_COLS の
                //    ブロック colB でブロックを決め、届く時刻をブロックごとに散らす。
                float rowsN = lerp(ROWS_MIN, ROWS_MAX, sizeN);
                float boxH = TEX_EYE_W * TEX_RATIO;             // 目の箱の丈（版の単位）
                float rowH = 0.5 * boxH / rowsN;                // 1 行の丈（同）
                float upper = step(0.5, t.y);                   // 1 = 上側 / 0 = 下側
                // 笑いで縮むのは片側だけ（上 SMILE_TOP / 下 SMILE_LIFT）。旧 hNow の片側ぶん。
                float hSide = lerp(1.0 - smile * SMILE_LIFT, 1.0 - smile * SMILE_TOP, upper);
                float openSide = open * hSide;
                float rowF = abs(t.y - 0.5) / max(rowH, 1e-5);
                float row = floor(rowF);
                float colB = floor((t.x - 0.5) / TEX_EYE_W * BLOCK_COLS + BLOCK_COLS * 0.5);
                float hbk = h21(float2(row * 31.0 + colB, seed * 7.9 + upper));
                float hbk2 = frac(hbk * 17.31 + 0.37);
                float hbk3 = frac(hbk * 29.73 + 0.11);
                float hbk4 = frac(hbk * 7.13 + 0.61);
                // 届く時刻。同じ行でも最大 ARRIVE_JITTER 行ぶん違うので、届く縁はぎざぎざになる。
                float thr = (row + hbk * ARRIVE_JITTER) / rowsN;
                // 閉じるときだけ、1 割のブロックが閾値を下げて**同じ場所に**残り、遅れて落ちる
                //（凍ったマクロブロック）。動かさない・薄めない。
                thr -= _EyeClosing * step(hbk2, STUCK_P) * STUCK_EXTRA;
                // 箱の外（柱の行）は行の順に入れない —— 柱は open > 0 で最初のコマから立つ（§6）。
                float inBox = step(rowF, rowsN);
                float arrived = max(1.0 - inBox, step(thr, openSide));
                // 届いた瞬間はずれている。**補間しない**（ずれている／いない の 2 状態で、
                //    窓を過ぎたコマに正しい場所へ跳ぶ）。閉じるときは掛けない（落ちるだけ・0085）。
                // ⚠ 外の行は thr + TEAR_WIN が開き切りを超えるので、そのままだと**永久にずれたまま**（小さい目の
                //    最外行で実際に起きた）。跳ぶ時刻は「開き切る手前」で必ず頭打ちにする。
                float openTop = lerp(openMax, 1.0, isBig) * hSide;
                float snapAt = min(thr + TEAR_WIN / rowsN, openTop - 0.002);
                float torn = inBox * (1.0 - _EyeClosing) * step(openSide, snapAt);
                t.x += torn * (hbk2 < 0.5 ? -1.0 : 1.0) * TEAR_MAX * hbk3;
                // 壊れたストリームが前の行を使い回す形。1 行ぶん中心線へ寄せて**同じ 1 回のサンプル**で引く。
                t.y -= torn * step(hbk4, DUP_P) * (upper * 2.0 - 1.0) * rowH;

                // ---- 視線（0084「目玉ぎょろぎょろ」）。⚠⚠ **版ごと滑らせない** — 輪郭が動くと目玉ではなく目そのものが漂う。
                //    眼球が瞼の下で回るのと同じ形: 目の中（楕円の 0.55 まで）は版ごと平行移動、そこから輪郭（1.0）へ向けて
                //    薄れ、輪郭は止まる。間の白目の粒が片側で詰まり片側で伸びるが、壊れた画像データなので乱れとして読める。
                //    ⚠ 「虹彩の周りだけ」（半径 0.24〜0.42 の円）で試したら、動いた先の縁で渦を巻いた（v1 の絵）。
                //    ⚠ 動く量はタイルの 0.13 まで（目の半幅の 3 割）。それ以上は白目の詰まりが渦に見える。
                float2 gT = float2(flip * gaze.x, gaze.y * aspect) * (0.5 * TEX_EYE_W / SHAPE_SCALE);
                float gl = length(gT);
                gT *= min(gl, GAZE_MAX_T) / max(gl, 1e-5);
                float2 te = (t - 0.5) / float2(0.5 * TEX_EYE_W, 0.5 * TEX_EYE_W * TEX_RATIO);
                float wI = 1.0 - smoothstep(GAZE_E1, GAZE_E2, length(te));
                // ⚠ 滑らかに薄れさせると、白目の伸びが**動きぼかし**に見える（v2 の絵・輪郭から放射する筋）。
                //    段に量子化すると、伸びではなく**ブロックが飛んだ**形になり、壊れた画像データの語彙に乗る。
                wI = floor(wI * GAZE_STEPS + 0.5) / GAZE_STEPS;
                t -= gT * wI;

                // ---- 目の箱の上下へ、箱の縁の行を縦に引き伸ばした柱（参考画像の「データの行を積んだ柱」）。
                float vlo = 0.5 - 0.5 * TEX_EYE_W * TEX_RATIO;
                float vhi = 0.5 + 0.5 * TEX_EYE_W * TEX_RATIO;
                float outV = max(t.y - vhi, vlo - t.y);                 // 箱からの縦の距離（版の単位）

                // ⚠⚠ **版を引く前に捨てる。** quad は目より縦に 1.5 倍長く、目の外の画素が過半を占める。
                //    そこで版を 2 回引いてから捨てると、89 個が開いた 2 秒で実機が 34 fps まで落ちた
                //    （走行 `20260919_192053`）。タイルの外・帯の欠け・柱の帯の外は、何も引かずにここで終える。
                if (arrived < 0.5) discard;
                if (abs(t.x - 0.5) > 0.5 || abs(t.y - 0.5) > 0.5) discard;
                if (outV > 0.0 && abs(t.x - 0.5) > SMEAR_HALF_W) discard;

                float rn = saturate(outV / (TEX_EYE_W * TEX_RATIO * SMEAR_REACH));
                float colx = floor((t.x - 0.5) * SMEAR_COLS + 100.0);
                float hcol = h21(float2(colx, seed * 9.1 + epoch * 2.0));
                float smearP = SMEAR_DENSITY * pow(1.0 - rn, 1.8);
                float smearOn = step(0.0, outV) * step(1.0 - smearP, hcol);
                // 柱の幅は候補の 0.35〜0.8（隣と隙間を空ける）。
                float fcol = frac((t.x - 0.5) * SMEAR_COLS + 100.0);
                float cw = lerp(0.35, 0.8, frac(hcol * 7.3));
                smearOn *= step(fcol, cw);
                // 行ごとの明滅で「積んだ行」に見せる（1〜2 画素の行）。
                float rowOn = step(0.25, h21(float2(bi * 3.1, colx + seed * 5.5)));
                // ⚠ **柱は行より先に立ち、行より後に消える**（0240 §6）。最初のコマは
                //   「中心の 1 行 ＋ 上下の柱」＝ 引き伸ばされた画素の先に目が復号されていく形。
                float smearW = SMEAR_LUM * smearOn * rowOn * (1.0 - rn) * saturate(open * PILLAR_LEAD);

                half4 tex = SAMPLE_TEXTURE2D_GRAD(_EyeTex, sampler_EyeTex, AtlasUv(t, variant), dtx, dty);
                half4 smear = half4(0, 0, 0, 0);
                if (smearW > 0.002)
                {
                    float2 tEdge = float2(t.x, clamp(t.y, vlo + 0.01, vhi - 0.01));
                    smear = SAMPLE_TEXTURE2D_GRAD(_EyeTex, sampler_EyeTex, AtlasUv(tEdge, variant), dtx, dty) * smearW;
                }

                // ---- 合成。形は版の alpha そのもの（版に描いてある柱も含む）。引き伸ばした柱は版の無い所だけ。
                float a = saturate(tex.a + smear.a * (1.0 - tex.a));
                float3 col = tex.rgb + smear.rgb * (1.0 - tex.a);
                if (a <= 0.002) discard;

                a *= _EyeFade;
                col *= _EyeFade * _EyeColor.rgb * _EyeGain;
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
