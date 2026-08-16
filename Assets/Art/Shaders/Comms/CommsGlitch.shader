// 連絡の面が、周を重ねるごとに壊れていく（canon/LEDGER.md 0068）。
//
// ⚠ この面は「地・縁・文字の上に重ねる 1 枚」で、下の絵を読めない（サンプルしていない）。
//    だから「ずらす」ではなく **描き足す** ことでしか壊れて見せられない。
//    面を横へ飛ばすのは C# 側（CommsGlitchLogic.OffsetXAt）。
//
// ⚠⚠ **層は 2 つあり、uniform も 2 本ある。**
//      _Level = 常時（走査線）の濃さ。周回の進みの関数で、ゆっくり上がる
//      _Burst = 発作（ブロックと帯）が立っているか。0/1。**これが無いと常時でもブロックが出る**
//        （2026-08-17 の 1 周目でそうなっていた — 強さ 1 本では「たまに壊れる」を作れない）。
//
// ⚠⚠ 強さ 1.0 でも文字は読める側へ倒してある（被覆率と不透明度に上限）。
//    ③「異常が検出されました。記録してください。」は 4 周目 A の締めで出て、
//    読まれないと締めのカットが進まない。
//
// ⚠ 実行時に Shader.Find で掴むので **Always Included に登録してある**
//    （ProjectSettings/GraphicsSettings.asset。忘れると Editor では出て実機で剥がれる）。
Shader "FixedCamVr/CommsGlitch"
{
    Properties
    {
        // 0 = 1 画素も描かない。1 = 3 周目 A 以降。
        _Level ("Level", Range(0,1)) = 0
        // 発作（ブロックと帯）。0/1。C# の CommsGlitchLogic.BurstAt が決める。
        _Burst ("Burst", Range(0,1)) = 0
        // 刻みごとに変わる種。同じ刻みのあいだブロックは動かない（毎フレーム動かすと砂嵐になる）。
        _Seed ("Seed", Float) = 0
        // 面の縦横比（w/h）。ブロックを正方形に近づけるためだけに要る。
        _Aspect ("Aspect", Float) = 2.4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            // 地・縁・文字より手前に、素直に重ねる。深度は書かないし見ない
            // （面は頭に追従して動くので、深度で弾かれると走行ごとに出たり出なかったりする）。
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            Lighting Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Level;
            float _Burst;
            float _Seed;
            float _Aspect;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // 2 次元 → 0..1。種を混ぜて刻みごとに配置が変わる。
            float hash21(float2 p, float s)
            {
                p = frac(p * float2(127.31, 311.7) + s * 43.13);
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 破損の色。実物の意匠（暖色）ではなく **符号化が壊れたときの色**。
            // ⚠ ここを暖色へ寄せない — 装置の飾りに見えると「壊れている」が伝わらない。
            // ⚠ **明度は抑える。** 初版は 0.95 前後で、暗い面の上に貼るとパステルの色紙に見えた。
            // ⚠ **黒（欠落）を混ぜる。** 色だけだと「足された」に見える。落ちた画素が要る。
            float3 breakColor(float h)
            {
                if (h < 0.18) return float3(0.015, 0.015, 0.022);  // 欠落（データが落ちた）
                if (h < 0.38) return float3(0.06, 0.66, 0.70);     // シアン
                if (h < 0.56) return float3(0.66, 0.09, 0.60);     // マゼンタ
                if (h < 0.72) return float3(0.10, 0.66, 0.32);     // 緑
                if (h < 0.88) return float3(0.72, 0.12, 0.16);     // 赤
                return float3(0.66, 0.66, 0.70);                   // 白
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float lv = saturate(_Level);
                if (lv <= 0.002) return fixed4(0, 0, 0, 0);

                float2 uv = i.uv;

                // ---- 常時: 走査線 ----------------------------------------------------
                // ⚠ **薄く。** 初版は 0.09 で、面ぜんたいが灰色のすりガラスになり
                //    「暗い漆の面」という地の顔が消えた。壊れは**差**でしか見えない。
                float scan = 0.5 + 0.5 * sin(uv.y * 240.0 + _Seed * 6.283);
                float3 col = float3(0.42, 0.58, 0.66);   // わずかに青い（管の走査に寄せる）
                float a = scan * 0.038 * lv;

                // ---- 発作: 矩形のブロックと帯 -----------------------------------------
                if (_Burst > 0.5)
                {
                    // 面は横長なので、縦の分割は縦横比から決める（正方形に近いブロックにする）。
                    float cols = 48.0;
                    float rows = max(4.0, floor(cols / max(1.0, _Aspect)));
                    float row = floor(uv.y * rows);
                    // ⚠ **行ごとに横へずらす。** ずらさないとブロックが整列して「模様」に見える
                    //    （符号化の破損は行ごとに独立に落ちる）。
                    float shift = (hash21(float2(3.1, row), _Seed) - 0.5) * 2.4;
                    // ⚠ **行ごとに 1〜3 セル束ねてから抽選する。** 1 セルずつ抽選すると
                    //    ばらけた点になって「砂」に見える。束ねると横長の矩形が出る。
                    float run = 1.0 + floor(hash21(float2(0.0, row), _Seed) * 3.0);
                    float cx = floor((uv.x * cols) + shift);
                    float2 gid = float2(floor(cx / run) * run, row);
                    float h = hash21(gid, _Seed);
                    // 被覆率の上限。⚠ **ここが「文字が読めるか」を決める唯一の値。**
                    float cover = 0.30 * lv;
                    if (h < cover)
                    {
                        col = breakColor(hash21(gid + 91.0, _Seed));
                        // 濃さもばらつかせる（全部が同じ濃さだと「模様」に見える）。
                        a = 0.55 + 0.37 * hash21(gid + 53.0, _Seed);
                    }

                    // 面を横断する帯（走査が飛んだように見せる）。ブロックより粗い刻み。
                    float bandRows = 17.0;
                    float band = floor(uv.y * bandRows);
                    float bh = hash21(float2(band, 7.3), _Seed);
                    if (bh < 0.22 * lv)
                    {
                        float start = hash21(float2(band, 19.1), _Seed) * 0.6;
                        float len = 0.22 + hash21(float2(band, 31.7), _Seed) * 0.55;
                        if (uv.x >= start && uv.x <= start + len)
                        {
                            col = breakColor(hash21(float2(band, 5.9), _Seed));
                            a = max(a, 0.50 + 0.35 * hash21(float2(band, 71.3), _Seed));
                        }
                    }
                }

                // ⚠ 全部を覆わない。文字が読めなくなると、③の締めで体験者が押せない。
                a = min(a, 0.92);
                return fixed4(col, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
