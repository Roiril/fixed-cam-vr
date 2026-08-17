// 連絡の面の左に立つ「AIエージェントの顔」の枠と顔。
//
// 出どころは `canon/LEDGER.md` 0071（ユーザー指定・2026-08-17）:
// 「左に角丸の四角い枠線をつけて、その中にAIの顔を入れれるようにしてほしい」。
//
// **1 枚の面が枠線と顔の両方を出す。** 枠は角丸の矩形を距離場で描き、顔は焼いた版
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
        _Color("Ink color", Color) = (0.82, 0.78, 0.72, 1)
        _Opacity("Opacity (0..1)", Range(0, 1)) = 0

        // 角の丸み（1 辺に対する割合。0.5 で円）。
        _Radius("Corner radius (0..0.5)", Range(0, 0.5)) = 0.20
        // 枠線の太さ（1 辺に対する割合）。**0 なら枠線を描かない**。
        _Stroke("Frame stroke (0 = none)", Range(0, 0.25)) = 0.038
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

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Opacity;
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
                float face = SAMPLE_TEXTURE2D(_Face, sampler_Face, i.uv).a * _FaceOn * inner;

                float a = saturate(max(ring, face)) * _Opacity;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
