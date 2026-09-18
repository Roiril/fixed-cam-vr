// 連絡の面の呪いの斑の中に、文字を切るためのステンシルを書く（色は書かない）。
//
// ⚠⚠ **地の双子（CommsPanelPlate.shader）とは別のシェーダ・別の quad にしてある。**
//    URP は 1 つのマテリアルの「LightMode の無いパス」を**最初の 1 つしか描かない**ので、
//    2 パスに分けて書くと第 2 パス（色）が 1 画素も出ない（2026-09-18 に踏んだ。地が消え、
//    文字だけが切れていた）。隔離の殻（ContainmentShellMask / ContainmentShell）が 2 つのシェーダに
//    分かれているのも同じ理由。
//
// 斑の場は `CommsCurse.hlsl`（顔・地と共有）。k ≥ CURSE_CUT の画素に bit 8 を立て、
// 本文と下段の TextMeshPro の材質が「bit 8 が立っていれば描かない」で読む（`CommsPanel.ApplyTextStencil`）。
// ⚠ 触るのは bit 8 だけ（隔離の殻は 1・導入の破砕は 32）。
// ⚠ 描画順は地（4980）の直後・顔（4985）と文字（4990）の前。
// ⚠ 実行時 `Shader.Find` で引くので Always Included に登録してある（外すと実機だけ剥がれる。
//   そのとき文字は「切れない ＝ 読める」側へ倒れ、斑の中の字は CPU の alpha 0 だけが消す）。
Shader "FixedCamVr/CommsCurseStencil"
{
    Properties
    {
        _Origin("Quad centre (panel-local m)", Vector) = (0, 0, 0, 0)
        _Size("Quad size (m)", Vector) = (1, 0.3, 0, 0)
        _RectHalf("Sharp rect half extents (m)", Vector) = (0.45, 0.12, 0, 0)
        _Curse("Curse amount (0..1)", Range(0, 1)) = 0
        // 塗り替わりの帯（0230 / 0231）: (進み, 矩形の上端 y, 矩形の下端 y, 帯の高さ)。反転した帯も切る。
        _Sweep("Sweep (progress, top, bottom, band)", Vector) = (0, 0, 0, 0.04)
        // 乱れ（0231）: 地と同じ値。飛んだ中身と同じ座標で斑を読み、**矩形の外へ飛んだ字は切る**。
        _Tear("Tear (strength, flicker, 0, 0)", Vector) = (0, 1, 0, 0)
        _TearShiftA("Tear shift bands 0-3 (m)", Vector) = (0, 0, 0, 0)
        _TearShiftB("Tear shift bands 4-7 (m)", Vector) = (0, 0, 0, 0)
        _TearDropA("Tear drop bands 0-3", Vector) = (0, 0, 0, 0)
        _TearDropB("Tear drop bands 4-7", Vector) = (0, 0, 0, 0)
        _TextCut("Write stencil for text cut (0/1)", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay+981" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "CommsCurseStencil"
            ColorMask 0
            ZWrite Off
            ZTest Always
            Cull Off
            Stencil
            {
                Ref 8
                ReadMask 8
                WriteMask 8
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "CommsCurse.hlsl"

            CBUFFER_START(UnityPerMaterial)
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
                float _TextCut;
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

            half4 frag(Varyings i) : SV_Target
            {
                float2 pRaw = _Origin.xy + (i.uv - 0.5) * _Size.xy;
                // 中身（斑）は帯ごとに飛んだ座標で読み、帯の反転は生の y で決める（地と同じ）。
                float2 p = pRaw;
                p.x -= CommsTearShiftAt(pRaw.y, _Sweep, _TearShiftA, _TearShiftB);
                float k = max(CurseK(CurseField(p), _Curse), CurseSweepK(pRaw, _Sweep));
                // 飛んだ字を枠の外へ出さない（0231・設計批評）: 本編は枠外を黒に落とすが、この面の外は
                // パススルーなので、矩形の外はいつも切る（この quad が有効なあいだ）。
                float2 q = abs(pRaw - _Origin.xy) - _RectHalf.xy;
                float outside = step(0.0, max(q.x, q.y));
                clip(_TextCut - 0.5);
                clip(max(k - CURSE_CUT, outside - 0.5));
                return half4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
