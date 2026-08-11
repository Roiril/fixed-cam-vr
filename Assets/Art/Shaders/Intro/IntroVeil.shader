// 導入演出の「覆い」。**現実を枠の中へ閉じ込める面**。
//
// 方式は Passthrough Windows（Meta 公式）: フレームバッファの alpha を書き、
// **alpha 0 の領域だけパススルーが透ける**。`Blend Zero SrcAlpha` なので
//   出力 = dst * srcAlpha
// になり、srcAlpha=1 の領域は VR の絵がそのまま（＝パススルーは見えない）、
// srcAlpha=0 の領域は dst ごと 0 になってパススルーが出る。
//
// ⚠ この方式では **暗くする方向にしか描けない**（RGB を足せない）。粒・走査線・乱れは
// 「alpha を落とす」＝ 明滅として出す。明るい粒が要るなら別の加算面が必要になるが、
// パススルーは元々明るいので暗くする側だけで質感の格下げは伝わる。
//
// ⚠⚠ **現実の画素は動かせない。動かせるのは「現実が覗く窓」の側だけ。**
// アプリはパススルーのテクスチャを持っていない（合成するのは OS）。だから段 4 の破砕
// （下記）は「破片が現実の絵を持って飛ぶ」のではなく「窓が割れて飛ぶ」。セルを細かく
// （中心で約 3.6°）取ると窓の中身がほぼ一様になり、破片として読める、という賭けで作ってある。
//
// ⚠ 手前に置くが深度で弾かれてはいけない（ZTest Always / ZWrite Off）。
// queue はスクリーンより後（Overlay+900 = 4900）— 枠の中で見せる映像は既に描かれている必要がある。
//
// 設計の正本: .claude/plans/2026-07-30_intro-passthrough-to-screen.md §6
//            破砕（段 4）は .claude/rules/show-design.md「現実が割れてスクリーンへ入る」
Shader "FixedCamVr/IntroVeil"
{
    Properties
    {
        // 枠の中のパススルーの見え。1 = 現実が見える / 0 = 枠の中も VR の絵（＝映像）になる
        _Passthrough("Passthrough inside frame (0..1)", Range(0, 1)) = 1
        // 枠の 4 辺の平面の法線（眼と辺を通る平面・**内側で dot(dir, n) < 0**）。
        // 本編のスクリーンの見かけの形から IntroVeil が毎フレーム組む。閉じ具合もここに畳んである。
        // ⚠ **覆いの面へ矩形として投影する方式ではない** — スクリーンは水平（ヨーだけ追従）なので、
        //    頭を上下に振ると必ず両面が傾き、矩形近似では四隅が数度ずれる（2026-08-01）。
        _FramePlane0("Frame edge plane 0", Vector) = (0, 0, -1, 0)
        _FramePlane1("Frame edge plane 1", Vector) = (0, 0, -1, 0)
        _FramePlane2("Frame edge plane 2", Vector) = (0, 0, -1, 0)
        _FramePlane3("Frame edge plane 3", Vector) = (0, 0, -1, 0)
        // 覆いの面の実寸 (m) と距離。uv → 視線ベクトルへ直すのに要る。
        _VeilSize("Veil size m (xy) / distance (z)", Vector) = (2, 2, 0.3, 0)
        // 縁のぼけ幅。単位は「辺の平面からの角度の sin」（IntroVeil が feather から換算する）。
        _FeatherAng("Edge feather (sin of angle)", Float) = 0.02
        // 粒と走査線（質感の格下げ）。alpha を揺らすので「暗い粒」になる。
        _Grain("Grain and scanline", Range(0, 1)) = 0
        _ScanlineCount("Scanline count", Float) = 240
        // 乱れ。帯ごとに alpha を落として伝送の劣化を装う（企画書 2.3）。
        _Glitch("Glitch", Range(0, 1)) = 0
        _GlitchSeed("Glitch seed", Float) = 0

        // ---- 破砕（段 4）------------------------------------------------------
        // 0 = 割れていない（覆いは 1 枚の面）/ 1 = 現実が全部スクリーンへ入り切った。
        // **セル格子のメッシュを張っているときだけ効く**（IntroVeil が mesh を差し替える）。
        // 数値は C# の `IntroShatterCurve` が正で、ここの既定値は Editor で見るためだけのもの。
        _Shatter("Shatter progress (0..1)", Range(0, 1)) = 0
        _Stagger("Peripheral-first spread (0..1)", Float) = 0.38
        _Jitter("Per-cell start jitter (0..1)", Float) = 0.14
        _Gap("Crack width (fraction of cell)", Float) = 0.14
        _Spin("Max spin (rad)", Float) = 0.35
        _Drift("Break-loose drift (local)", Float) = 0.004
        _PullAt("Travel starts at (0..1)", Float) = 0.18
        _TravelMax("Travel amount (0..1)", Float) = 0.85
        _ShrinkAt("Shrink starts at (0..1)", Float) = 0.72
        _CloseAt("Close starts at (0..1)", Float) = 0.60
        _Stretch("Stretch along travel", Float) = 1.6
        _CellSeed("Cell noise seed", Float) = 17.13
    }

    SubShader
    {
        // ⚠ **Queue は 4900 まで。5000 を超えてはいけない**（2026-07-31 実害）。
        // URP の透明パスが描くのは `RenderQueueRange.transparent` = **[2501, 5000]** だけ
        // （`UniversalRenderer.cs` / Unity 公式 API リファレンス「render queue value should be
        // in [0..5000] range to work properly」）。この覆いより**後**に描く必要があるもの
        // （構造の線 = IntroStructureWire / HMD 内の指示 = IntroPrompt）が 5000 に入るので、
        // 覆いはその手前の 4900 に置く。以前は覆い 5000 / 後続 5100 にしていて、
        // **後続が範囲外で 1 つも描画されていなかった**（段 3 の線も導入の指示も画に出ていなかった）。
        Tags { "Queue" = "Overlay+900" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroVeil"
            Blend Zero SrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // 破砕の時計と形は封印の箱と共有する（1 本しか無い）。
            #include "IntroShatter.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                // 破砕用。xy = このセルの中心（ローカル単位）/ z = 1 ならセル、0 なら動かない縁取り。
                // **1 枚 quad のメッシュにはこのストリームが無い**ので 0 が読まれ、破砕の枝へ入らない。
                float3 cell : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                // x = このセルが閉じた量（1 = 現実を返し終えた）/ y = 生きている量（1 - x）。
                // セル内で一定なので補間の誤差は出ない。
                float2 life : TEXCOORD1;
            };

            float _Passthrough;
            float4 _FramePlane0;
            float4 _FramePlane1;
            float4 _FramePlane2;
            float4 _FramePlane3;
            float4 _VeilSize;
            float _FeatherAng;
            float _Grain;
            float _ScanlineCount;
            float _Glitch;
            float _GlitchSeed;

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
            float _Stretch;
            float _CellSeed;

            // スクリーン矩形（覆いのローカル空間 m）。**IntroVeil が global で配る**ので、
            // 封印の箱（SealedBox）も同じ値を読み、同じ格子・同じ順番で割れる。
            // _IntroScreenHalf = (halfW, halfH, 覆いの面までの距離, 「遠い周縁」とみなす m)
            float4 _IntroScreenC;
            float4 _IntroScreenR;
            float4 _IntroScreenU;
            float4 _IntroScreenHalf;

            float hash12(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float2 pos = v.positionOS.xy;
                float2 uv = v.uv;
                float closed = 0.0;

                UNITY_BRANCH
                if (_Shatter > 0.0001 && v.cell.z > 0.5)
                {
                    float2 c = v.cell.xy;

                    // --- 行き先: スクリーン矩形（覆いのローカル空間 m）--------------------
                    float2 cM = c * _VeilSize.xy;
                    float2 qPlane;
                    float3 qLocal;
                    float d = IntroShardTarget(float3(cM, _VeilSize.z),
                                               _IntroScreenC.xyz, _IntroScreenR.xyz, _IntroScreenU.xyz,
                                               _IntroScreenHalf.xy, _VeilSize.z, qPlane, qLocal);

                    // 0 = スクリーンの上 / 1 = 遠い周縁。**周縁から先に割れる**ためのディレイの元。
                    // ⚠ スクリーンの上（far = 0）のセルは**割らない** — 段 4 の終わりに枠の中まで
                    //    消えると、枠がどこにあるか分からないまま段 5 の映像が点く。
                    float far = saturate(d / max(_IntroScreenHalf.w, 1e-3));
                    if (far > 1e-4)
                    {
                        float2 rnd = IntroShardHash2(c * 91.7, _CellSeed);
                        IntroShard s = IntroShardEval(far, rnd.x, rnd.y, _Shatter,
                                                      _Stagger, _Jitter, _Gap, _Spin, _Drift,
                                                      _PullAt, _TravelMax, _ShrinkAt, _CloseAt);

                        float2 qc = qPlane / max(_VeilSize.xy, float2(1e-4, 1e-4));
                        float2 dir = normalize(qc - c + float2(1e-5, 1e-5));
                        float2 off = (v.positionOS.xy - c) * (1.0 - s.gap) * s.shrink;
                        off = IntroShardSpin2(off, s.spin);
                        // 進む向きへ伸びる。窓の中身は動かないので、**形の伸びだけが速度を伝える**。
                        off += dir * dot(off, dir) * (_Stretch * s.travel);

                        pos = lerp(c, qc, s.travel) + s.drift + off;
                        closed = s.closed;
                        // uv は「いま画面のどこか」。走査線と粒は装置に貼り付いていてほしいので、
                        // セルに付いてくる元の uv ではなく**動いた後の位置**から引く。
                        uv = pos + 0.5;
                    }
                }

                o.positionCS = TransformObjectToHClip(float3(pos, 0.0));
                o.uv = uv;
                o.life = float2(closed, 1.0 - closed);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // この画素を見ている視線（覆いのローカル空間）。覆いの面はローカル z = _VeilSize.z。
                float3 dir = normalize(float3((i.uv - 0.5) * _VeilSize.xy, _VeilSize.z));

                // 枠の 4 辺の平面からの符号付き距離（角度の sin）。**すべて負なら枠の中**。
                // 除算が無いので、視線がスクリーン面と平行になっても壊れない。
                float m = max(max(dot(dir, _FramePlane0.xyz), dot(dir, _FramePlane1.xyz)),
                              max(dot(dir, _FramePlane2.xyz), dot(dir, _FramePlane3.xyz)));
                // inside = 1 が枠の中。縁は feather でぼかす（硬い矩形は「UI の窓」に見える）。
                float inside = 1.0 - smoothstep(-_FeatherAng, 0.0, m);

                // ⚠ **開口は破砕より遅れて閉じる**（`IntroLogic` の段 4 が frame を後半へ寄せてある）。
                //    だから生きている破片が開口に切られることは無い。ここで開口を無視すると、
                //    今度は**枠の外に空いた穴から体験エリアの中が覗ける**（封印の箱は開口で
                //    切られているので、穴の向こうに箱が居ない）。切る側に倒すのが正しい。
                float open = inside;

                // 枠の中は「パススルーを透かす」= alpha を落とす。外は 1（＝ VR の絵 = 黒）。
                float alpha = lerp(1.0, 1.0 - saturate(_Passthrough), open);

                // ---- 質感の格下げ（枠の中だけに掛ける）----------------------------
                // 走査線: 横縞で alpha を落とす。パススルーが縞に暗くなる。
                float scan = 0.5 + 0.5 * sin(i.uv.y * _ScanlineCount * 3.14159265);
                // 粒: 時間で流れる白色ノイズ。fract(_GlitchSeed) で毎フレーム位相を変える。
                float grain = hash12(i.uv * 480.0 + frac(_GlitchSeed) * 97.0);
                float degrade = saturate(_Grain) * open * (0.18 * scan + 0.22 * grain);

                // 乱れ: 帯ごとに大きく落とす。継ぎ目を隠すためのもので、常時は出さない。
                float band = floor(i.uv.y * 24.0 + frac(_GlitchSeed) * 13.0);
                float bandNoise = hash12(float2(band, floor(frac(_GlitchSeed) * 60.0)));
                float glitch = saturate(_Glitch) * open * step(0.72, bandNoise) * (0.35 + 0.4 * bandNoise);

                // 掛けるのは「枠の中のパススルーが見えている分」だけ。
                // 映像へ移った後（_Passthrough=0）に縞を残すと二重に掛かる（映像側の post FX が持つ）。
                float visible = open * saturate(_Passthrough);
                alpha = saturate(alpha + (degrade + glitch) * visible);

                // 破片が閉じ切ったら現実を返す（alpha 1 = VR の絵 = 黒）。
                alpha = lerp(alpha, 1.0, saturate(i.life.x));

                // RGB は使われない（dst * srcAlpha なので）。0 を返すのが Passthrough Windows の作法。
                return float4(0.0, 0.0, 0.0, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
