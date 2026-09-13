// 連絡の面の左に立つ AI エージェントの顔。
//
// 出どころは `canon/LEDGER.md` 0071（ユーザー指定・2026-08-17）:
// 「左に角丸の四角い枠線をつけて、その中にAIの顔を入れれるようにしてほしい」。
//
// 1 枚の面が顔を出す。角丸の輪郭は切り抜きにだけ使い、通常は枠線を描かない。顔は焼いた版
// （`Assets/Resources/Comms/SuiFace.png`・焼くのは `tools/make-comms-face.py`）の
// **A だけ**を使う。RGB は捨てる — 色は `_Color` が持ち、地・文字と同じ墨の色で、
// 同じように明滅する（`CommsPanel.Apply` が毎フレーム書く）。
//
// ⚠⚠ **面は正方形であること。** 距離場を uv 空間（0..1）で解くので、縦横比が 1 でないと
// 角の丸みが楕円になり、枠線の太さも上下と左右で変わる。`CommsPanel` の
// `FaceCellM` が 1 辺で、`CommsPanelGeometryTests` が正方形を機械で固定する。
//
// ⚠ **顔は枠の内側で必ず切る**（`inner`）。切らないと、周回の壊れで面が横へ飛んだとき
// 顔だけが枠からはみ出して「別の絵が貼ってある」ように見える。
//
// ⚠ **`_Stroke = 0` なら枠線は 1 画素も出ない**（切り抜きだけが残る）。表示側のバグ
// （赤とシアンへ分離した複製）がこの形で使う — 枠は装置の意匠なので分離させない。
//
// ⚠ Queue は Overlay+985 = 4985。5000 を超えると URP の透明パスに入らず 1 画素も出ない
// （`rules/unity-vr.md`）。実際の値は `CommsPanel` が material へ書く。
Shader "FixedCamVr/CommsAvatar"
{
    Properties
    {
        _Face("Face (A = ink)", 2D) = "black" {}
        // 侵食の行き先（市松人形）。⚠ **同じ大きさ・同じ座り**で焼いてあること。
        _Face2("Face taken over (A = ink)", 2D) = "black" {}
        _Color("Ink color", Color) = (0.8196, 0.7804, 0.7216, 1)
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0

        // 侵食の進み。0 = スイのまま / 1 = 完全に人形。`CommsPanel` が周回の壊れと同じ値を書く。
        _FaceMix("Taken over (0..1)", Range(0, 1)) = 0
        // 侵食の斑の大きさ（1 辺あたりの区画数）と、境目の柔らかさ。
        _MixCells("Patch cells", Float) = 4.5
        _MixSoft("Patch edge softness", Range(0.01, 0.5)) = 0.12

        // 角の丸み（1 辺に対する割合。0.5 で円）。
        _Radius("Corner radius (0..0.5)", Range(0, 0.5)) = 0.20
        // 枠線の太さ（1 辺に対する割合）。**0 なら枠線を描かない**。
        _Stroke("Frame stroke (0 = none)", Range(0, 0.25)) = 0
        // 顔を出すか。0 にすると枠線だけになる（版を掴めなかったときの姿）。
        _FaceOn("Draw face (0/1)", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+985" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "CommsAvatar"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            BlendOp Add
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_Face);
            SAMPLER(sampler_Face);
            TEXTURE2D(_Face2);
            SAMPLER(sampler_Face2);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Opacity;
                float _FaceMix;
                float _MixCells;
                float _MixSoft;
                float _Radius;
                float _Stroke;
                float _FaceOn;
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

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // 角丸矩形の符号付き距離（中心が原点・半分の大きさ 0.5 の正方形）。負が内側。
            float RoundedBox(float2 p, float r)
            {
                float2 q = abs(p) - (0.5 - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            /// <summary>
            /// 侵食の斑。**乱数を実行時に振らない** — uv だけで決まるので、
            /// 同じ進みなら毎フレーム同じ形。ちらつかせると「侵食」ではなく「ノイズ」に見える。
            /// </summary>
            float ValueNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv - 0.5;
                float r = clamp(_Radius, 0.0, 0.5);
                float d = RoundedBox(p, r);
                // 縁のなめらかさは画素の大きさから取る（実機では 1 辺 110 画素そこそこ）。
                float aa = max(fwidth(d), 1e-5);

                float outer = 1.0 - smoothstep(-aa, aa, d);
                // 枠線の内側。⚠ `_Stroke = 0` なら inner == outer ＝ 枠線は消えて切り抜きだけ残る。
                float inner = 1.0 - smoothstep(-aa, aa, d + _Stroke);
                float ring = saturate(outer - inner);

                // ⚠ 使うのは A だけ。版は RGB が白の**マスク**で、絵ではない。
                //
                // ⚠⚠ **侵食は溶暗（クロスフェード）ではなく斑で置き換える**
                //    （`canon/LEDGER.md` 0073）。混ぜると alpha が中間の灰へ落ちて、
                //    **侵されているのではなく薄くなっている**ように見える（2 通り焼いて比べた）。
                //    斑なら、その画素は必ずどちらかの顔で、境目だけが柔らかい。
                float n = ValueNoise(i.uv * _MixCells);
                float k = saturate((_FaceMix * (1.0 + _MixSoft) - n) / max(_MixSoft, 1e-4));
                float a1 = SAMPLE_TEXTURE2D(_Face, sampler_Face, i.uv).a;
                float a2 = SAMPLE_TEXTURE2D(_Face2, sampler_Face2, i.uv).a;
                float face = lerp(a1, a2, k) * _FaceOn * inner;

                float a = saturate(max(ring, face)) * _Opacity;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
