// 目の視界ジャック（canon/LEDGER.md 0099）— 当日撮った写真で視界を乗っ取る面。
//
// **形はここで描き、いつ・どの写真かは C# が決める**（EyeJackLogic → AnomalyEyes が
//   `_JackTex` / `_JackOn` / `_JackUv` を配る。AnomalyEyes.shader と同じ流儀）。
//
// ⚠ **Queue は Transparent+1940（= 4940）。** 隔離殻（ContainmentShell 4910）より後・
//    文字（TMP 5000）より手前。本編のスクリーン（Geometry / 不透明）も目（Background+100）も
//    ZTest Always で全部覆う ＝「視界がジャックされる」。5000 を超えると URP は 1 度も描かない
//    （rules/unity-vr.md の 2026-07-31 実害）。
//
// ⚠ **alpha は 1 を書く**（Blend One Zero）。乗っ取りの面なので、パススルーが万一出ていても
//    現実を覗かせない。位置合わせ中は C# 側（AnomalyEyes）が面ごと消す。
//
// ⚠ **実行時 Shader.Find で引く。** ProjectSettings/GraphicsSettings.asset の Always Included に
//    登録してある（外すと Editor では出て実機だけ剥がれる — rules/unity-vr.md）。
Shader "FixedCamVr/EyeJack"
{
    Properties
    {
        // C# が毎フレーム書く（Inspector の値は Editor プレビューの初期値でしかない）。
        _JackTex("Photo", 2D) = "black" {}
        _JackOn("On", Range(0, 1)) = 0
        // 明るさ。暗順応した視界へ室内写真をそのまま出すと眩しい（不快にしない — LEDGER 0015 の視覚版）。
        _JackGain("Gain", Range(0, 2)) = 0.85
        // cover-fit の uv 変換（xy = scale / zw = offset）。C# が写真と面の縦横比から計算する。
        _JackUv("UV Scale/Offset", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+1940" }
        LOD 100

        Pass
        {
            Name "EyeJack"
            Blend One Zero      // 置き換え（乗っ取り）。下に何が居ても関係ない
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // ⚠⚠ **Single Pass Instanced（Quest の既定）で両眼へ正しく描くために要る。**
            //    無いと Editor では出るのに実機で片眼にしか出ない / 位置がずれる。
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _JackOn;
                float _JackGain;
                float4 _JackUv;
            CBUFFER_END

            TEXTURE2D(_JackTex);
            SAMPLER(sampler_JackTex);

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
                o.uv = v.uv * _JackUv.xy + _JackUv.zw;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 col = SAMPLE_TEXTURE2D(_JackTex, sampler_JackTex, i.uv).rgb * _JackGain;
                // _JackOn は保険（C# は 0 のとき renderer ごと消す）。0 なら黒を書かず消えているべきだが、
                // 万一 renderer が残っても黒 = 闇と同じ見えに倒れる。
                return half4(col * _JackOn, 1.0h);
            }
            ENDHLSL
        }
    }
}
