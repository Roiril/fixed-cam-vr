// 連絡の面の地（受信票の黒い半透明の面）と、その呪われた双子。
//
// 出どころは `canon/LEDGER.md` 0229（ユーザーのスケッチ・2026-09-18）:
// 「通常の画 ＋ 呪われた画（りんかくは不鮮明・行が波線）を、段階に応じたマスクで重ねる」。
//
// 層 A（通常）: 鋭い矩形の地。従来の URP Unlit の地と同じ色と濃さ。
// 層 B（呪われ）: 縁がノイズで毛羽立ち、外へ黒くにじむ。本文の行の位置に象牙の走り書き。
// 2 つを斑の場 k（`CommsCurse.hlsl`・顔と共有）で画素ごとに混ぜる。
//
// 文字（TextMeshPro）はこのシェーダでは描けない。文字を切るステンシルは **別のシェーダ・別の quad**
// （`CommsCurseStencil.shader`）が斑の中に書く。⚠⚠ 同じシェーダの第 2 パスにしてはいけない —
// URP は LightMode の無いパスを最初の 1 つしか描かないので、色のパスが 1 画素も出なくなる
// （2026-09-18 に踏んだ）。
//
// ⚠ 面のローカル座標 (m) は `_Origin`（面の根から見たこの quad の中心）と `_Size`（quad の実寸）から
//   uv で復元する。quad は毛羽立ちのぶん矩形（`_RectHalf`）より大きい。
// ⚠ alpha の混ぜ方は URP Unlit の地と同じ（`CommsPanelCompositorAlphaTests` が固定する）:
//   RGB は SrcAlpha / OneMinusSrcAlpha、compositor alpha は One / OneMinusSrcAlpha（0227）。
// ⚠ 実行時 `Shader.Find` で引くので Always Included に登録してある（外すと実機だけ剥がれる）。
Shader "FixedCamVr/CommsPanelPlate"
{
    Properties
    {
        _Color("Plate color (A = opacity)", Color) = (0.039, 0.035, 0.031, 0.72)
        _Ink("Scrawl ink", Color) = (0.8196, 0.7804, 0.7216, 1)
        _InkAlpha("Scrawl opacity (0..1)", Range(0, 1)) = 1
        _Origin("Quad centre (panel-local m)", Vector) = (0, 0, 0, 0)
        _Size("Quad size (m)", Vector) = (1, 0.3, 0, 0)
        _RectHalf("Sharp rect half extents (m)", Vector) = (0.45, 0.12, 0, 0)
        _Curse("Curse amount (0..1)", Range(0, 1)) = 0
        // 塗り替わりの帯（0230 / 0231・憑依の出し方）: (進み, 矩形の上端 y, 矩形の下端 y, 帯の高さ)。
        // 反転した帯は層 B（斑と同じ k に max で入る）。
        _Sweep("Sweep (progress, top, bottom, band)", Vector) = (0, 0, 0, 0.04)
        // 乱れ（0231・本編の乱れをまねたもの）: (強さ, 明滅, 0, 0) と帯ごとの飛び・脱落（C# が作る）。
        _Tear("Tear (strength, flicker, 0, 0)", Vector) = (0, 1, 0, 0)
        _TearShiftA("Tear shift bands 0-3 (m)", Vector) = (0, 0, 0, 0)
        _TearShiftB("Tear shift bands 4-7 (m)", Vector) = (0, 0, 0, 0)
        _TearDropA("Tear drop bands 0-3", Vector) = (0, 0, 0, 0)
        _TearDropB("Tear drop bands 4-7", Vector) = (0, 0, 0, 0)
        _CurseDensity("Cursed plate density gain", Float) = 1.12
        _Fray("Fray amplitude (m)", Float) = 0.012
        _FrayCell("Fray noise cell (m)", Float) = 0.022
        _Halo("Smoke halo reach (m)", Float) = 0.035
        _Scrawl("Draw scrawl (0/1)", Float) = 1
        _Line0("Line 0 (x0, x1, yc, h)", Vector) = (0, 0, 0, 0)
        _Line1("Line 1 (x0, x1, yc, h)", Vector) = (0, 0, 0, 0)
        _Line2("Line 2 (x0, x1, yc, h)", Vector) = (0, 0, 0, 0)
        _Line3("Line 3 (x0, x1, yc, h)", Vector) = (0, 0, 0, 0)
        _LineReveal("Per-line reveal (0..1)", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SrcBlend("Src blend", Float) = 5
        [HideInInspector] _DstBlend("Dst blend", Float) = 10
        [HideInInspector] _SrcBlendAlpha("Src blend alpha", Float) = 1
        [HideInInspector] _DstBlendAlpha("Dst blend alpha", Float) = 10
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+980" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "CommsCurse.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float4 _Ink;
            float _InkAlpha;
            float4 _Origin;
            float4 _Size;
            float4 _RectHalf;
            float _Curse;
            float4 _Sweep;
            float4 _Tear;
            float4 _TearShiftA;
            float4 _TearShiftB;
            float4 _TearDropA;
            float4 _TearDropB;
            float _CurseDensity;
            float _Fray;
            float _FrayCell;
            float _Halo;
            float _Scrawl;
            float4 _Line0;
            float4 _Line1;
            float4 _Line2;
            float4 _Line3;
            float4 _LineReveal;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings PlateVert(Attributes v)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
            o.uv = v.uv;
            return o;
        }

        // uv → 面のローカル座標 (m)。
        float2 PanelPos(float2 uv)
        {
            return _Origin.xy + (uv - 0.5) * _Size.xy;
        }

        // 矩形（中心 _Origin・半分の大きさ _RectHalf）の符号付き距離。負が内側。
        float RectDistance(float2 p)
        {
            float2 q = abs(p - _Origin.xy) - _RectHalf.xy;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
        }

        // 走り書き 1 行ぶんの濃さ 0..1。line = (x0, x1, yc, h)・reveal = 印字が進んだ割合。
        // 正弦波にしない（波形 ＝ 装置に戻る）。振幅・太さ・濃さを場所ごとに不規則にし、
        // ときどき掠れて切れる。線の太さは字の高さの 8〜13%（実機で 2〜3 画素）。
        // ⚠ `aaY`（画素の大きさ）は呼び手が分岐の外で `fwidth` から取る（分岐の中で微分を取らない）。
        float ScrawlLine(float2 p, float aaY, float4 rect, float reveal, float seed)
        {
            float x0 = rect.x, x1 = rect.y, yc = rect.z, h = rect.w;
            float on = step(x0 + 1e-5, x1) * step(1e-5, h) * step(1e-5, reveal);
            h = max(h, 1e-4);
            float xr = x0 + (x1 - x0) * saturate(reveal);
            float u = (p.x - x0) / h;

            // 主線: ゆっくりした揺れ ＋ 細かい震え ＋ 角のある折れ
            float amp = h * (0.14 + 0.24 * CurseValueNoise(float2(u * 0.35 + seed, 2.1)));
            float slow = (CurseValueNoise(float2(u * 0.9 + seed, 7.3)) - 0.5) * 2.0 * amp;
            float fine = (CurseValueNoise(float2(u * 3.1 + seed, 13.7)) - 0.5) * h * 0.16;
            float zig = (abs(frac(u * 1.6 + seed * 0.37) * 2.0 - 1.0) - 0.5) * h * 0.22
                        * CurseValueNoise(float2(u * 0.5 + seed, 31.0));
            float y1 = yc + slow + fine + zig;
            float w1 = h * (0.08 + 0.05 * CurseValueNoise(float2(u * 1.7 + seed, 4.4)));
            float s1 = 1.0 - smoothstep(w1 - aaY, w1 + aaY, abs(p.y - y1));
            // 端: 行頭より少し手前から始まり、印字の進んだ所で切れる
            float head = smoothstep(x0 - h * 0.35, x0 + h * 0.15, p.x);
            float tail = 1.0 - smoothstep(xr - h * 0.1, xr + h * 0.12, p.x);
            // 掠れ: 線に沿って濃さが揺れ、ときどき途切れる
            float dry = smoothstep(0.16, 0.55, CurseValueNoise(float2(u * 2.3 + seed, 21.0)));
            s1 *= head * tail * lerp(0.45, 1.0, dry);

            // 副線: 少し上を走る細い線（二度なぞった跡）
            float y2 = yc + h * 0.10
                       + (CurseValueNoise(float2(u * 0.7 + seed + 40.0, 17.9)) - 0.5) * h * 0.4;
            float w2 = h * 0.055;
            float dry2 = smoothstep(0.3, 0.7, CurseValueNoise(float2(u * 1.4 + seed, 45.0)));
            float s2 = (1.0 - smoothstep(w2 - aaY, w2 + aaY, abs(p.y - y2))) * head * tail * 0.6 * dry2;
            return max(s1, s2) * on;
        }

        float Scrawl(float2 p, float aaY)
        {
            float ink = ScrawlLine(p, aaY, _Line0, _LineReveal.x, 0.0);
            ink = max(ink, ScrawlLine(p, aaY, _Line1, _LineReveal.y, 7.31));
            ink = max(ink, ScrawlLine(p, aaY, _Line2, _LineReveal.z, 14.62));
            ink = max(ink, ScrawlLine(p, aaY, _Line3, _LineReveal.w, 21.93));
            return ink;
        }
        ENDHLSL

        // ---- 地（層 A と層 B の混ぜ）と走り書き ------------------------------------------
        Pass
        {
            Name "CommsPlate"
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            BlendOp Add
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex PlateVert
            #pragma fragment PlateFrag
            #pragma multi_compile_instancing

            half4 PlateFrag(Varyings i) : SV_Target
            {
                // 生の座標（枠と帯の格子はこれで決まる）と、乱れで飛んだ座標（中身はこれで引く・0231）。
                float2 pRaw = PanelPos(i.uv);
                float d = RectDistance(pRaw);
                float aa = max(fwidth(d), 1e-5);
                float2 p = pRaw;
                p.x -= CommsTearShiftAt(pRaw.y, _Sweep, _TearShiftA, _TearShiftB);

                // 層 A: 鋭い矩形。**枠そのものなので飛ばない**（本編の枠が乱れで動かないのと同じ）。
                float sharp = 1.0 - smoothstep(-aa, aa, d);

                // 層 B: 縁が毛羽立ち、外へ煙のようににじむ（「りんかくは不鮮明」）。中身なので帯ごとに飛ぶ。
                float dB = RectDistance(p);
                float2 e = p / max(_FrayCell, 1e-4);
                float fn = CurseValueNoise(e + 5.3) * 0.65 + CurseValueNoise(e * 2.7 + 9.1) * 0.35;
                float disp = (fn - 0.5) * 2.0 * _Fray;
                float db = dB + disp;
                float frayed = 1.0 - smoothstep(-0.004, 0.004, db);
                float halo = (1.0 - smoothstep(0.0, max(_Halo, 1e-4), db)) * (0.55 + 0.45 * fn) * 0.38;
                float cursed = saturate(frayed + halo * (1.0 - frayed)) * _CurseDensity;

                // 斑（0229）と、帯ごとに反転する塗り替わり（0230 / 0231）は同じ k に入る。どちらも層 B へ倒す。
                // ⚠ 帯の反転は**生の y**で決める（帯は面に固定された格子。飛ぶのは帯の中身）。
                float kSweep = CurseSweepK(pRaw, _Sweep);
                float k = max(CurseK(CurseField(p), _Curse), kSweep);
                float plateA = saturate(lerp(sharp, cursed, k)) * _Color.a;

                // 走り書きは斑の中だけ。境目は文字の切断（k ≥ 0.5）と同じ所で切り替わる。
                // ⚠ 分岐で飛ばさない（`fwidth` を非一様な分岐の中で取らない）。乗算で消す。
                float aaY = max(fwidth(pRaw.y), 1e-5);
                float ink = Scrawl(p, aaY) * step(0.5, _Scrawl) * step(0.001, max(_Curse, _Sweep.x))
                            * smoothstep(CURSE_CUT - 0.15, CURSE_CUT + 0.15, k) * _InkAlpha;
                // 毛羽立った縁に、途切れた細い糸くず（同じ象牙の墨）。地は黒い半透明なので、暗い背景の前では
                // 縁の毛羽立ちが読めない — ほつれた糸が縁をなぞることで、背景が何であれ輪郭が崩れて見える。
                float thread = 1.0 - smoothstep(0.0009, 0.0028, abs(db));
                float threadDry = smoothstep(0.42, 0.62, CurseValueNoise(e * 1.9 + 23.0));
                ink = max(ink, thread * threadDry * 0.42 * k * _InkAlpha);

                // 乱れ（0231）: 帯ごとの脱落（本編の「帯が砂になる」。ここでは**抜ける**）と全体の明滅。
                float drop = CommsTearDropAt(pRaw.y, _Sweep, _TearDropA, _TearDropB);
                plateA *= 1.0 - drop;
                ink *= 1.0 - drop;

                // 象牙を地の上に置く（over）。明滅は出力の alpha に掛ける（暗い側へだけ）。
                float outA = (ink + plateA * (1.0 - ink)) * _Tear.y;
                float3 outRgb = (ink * _Ink.rgb + plateA * (1.0 - ink) * _Color.rgb) / max(ink + plateA * (1.0 - ink), 1e-4);
                return half4(outRgb, outA);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
