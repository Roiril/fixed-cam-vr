// 導入演出の覆い。全開の現実と、破砕後に残る正確なスクリーン窓を 1 枚で持つ。
//
// Meta の Passthrough Windows 方式を使う。`Blend Zero SrcAlpha` により
// alpha 0 だけで現実が透け、alpha 1 では先に描かれたアプリの画が残る。
// UI や通常の透明ブレンドへ置き換えると compositor の意味が逆になる。
//
// 面は常に 1 枚の quad。破片は同位置の IntroFracture が担当する。
Shader "FixedCamVr/IntroVeil"
{
    Properties
    {
        _Passthrough("Passthrough inside aperture (0..1)", Range(0, 1)) = 1
        _ScreenFade("Screen crossfade inside screen rect (0..1)", Range(0, 1)) = 0
        _FractureActive("Use final screen window", Range(0, 1)) = 0
        _FramePlane0("Aperture edge plane 0", Vector) = (0, 0, -1, 0)
        _FramePlane1("Aperture edge plane 1", Vector) = (0, 0, -1, 0)
        _FramePlane2("Aperture edge plane 2", Vector) = (0, 0, -1, 0)
        _FramePlane3("Aperture edge plane 3", Vector) = (0, 0, -1, 0)
        _ScreenPlane0("Screen edge plane 0", Vector) = (0, 0, -1, 0)
        _ScreenPlane1("Screen edge plane 1", Vector) = (0, 0, -1, 0)
        _ScreenPlane2("Screen edge plane 2", Vector) = (0, 0, -1, 0)
        _ScreenPlane3("Screen edge plane 3", Vector) = (0, 0, -1, 0)
        _VeilSize("Veil size m (xy) / distance (z)", Vector) = (2, 2, 0.3, 0)
        _FeatherAng("Edge feather (sin of angle)", Float) = 0.02
    }

    SubShader
    {
        // URP の透明範囲は 5000 まで。後続の構造線と案内を 5000 に置けるよう 4900 を保つ。
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
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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

            float _Passthrough;
            float _ScreenFade;
            float _FractureActive;
            float4 _FramePlane0;
            float4 _FramePlane1;
            float4 _FramePlane2;
            float4 _FramePlane3;
            float4 _ScreenPlane0;
            float4 _ScreenPlane1;
            float4 _ScreenPlane2;
            float4 _ScreenPlane3;
            float4 _VeilSize;
            float _FeatherAng;

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // 覆いとスクリーンを同じ距離へ置くため、中央眼から解いた平面は左右眼でも一致する。
                float3 dir = normalize(float3((i.uv - 0.5) * _VeilSize.xy, _VeilSize.z));

                float frameDistance = max(
                    max(dot(dir, _FramePlane0.xyz), dot(dir, _FramePlane1.xyz)),
                    max(dot(dir, _FramePlane2.xyz), dot(dir, _FramePlane3.xyz)));
                float screenDistance = max(
                    max(dot(dir, _ScreenPlane0.xyz), dot(dir, _ScreenPlane1.xyz)),
                    max(dot(dir, _ScreenPlane2.xyz), dot(dir, _ScreenPlane3.xyz)));
                // 破砕が始まった後の base quad は最終スクリーン窓だけを保つ。
                // 周囲の現実は IntroFracture の各片が透かすので、四辺から先に切り落とさない。
                float apertureDistance = lerp(frameDistance, screenDistance, saturate(_FractureActive));
                // 平面の幾何はそのまま保ち、画面上の局所ぼけだけを絞る。
                float feather = max(_FeatherAng * 0.25, 1e-4);
                float aperture = 1.0 - smoothstep(-feather, 0.0, apertureDistance);
                aperture = lerp(aperture, 1.0 - step(0.0, screenDistance), saturate(_FractureActive));

                // 開口の外はアプリの画を保ち、内側だけ現実を透かす。
                float alpha = lerp(1.0, 1.0 - saturate(_Passthrough), aperture);

                // 薄い透過帯を 2 段だけ置く。RGB は足さず、compositor alpha だけで静かに縁を出す。
                float wideEdge = smoothstep(-4.0 * feather, -1.0 * feather, apertureDistance);
                float fineEdge = smoothstep(-1.6 * feather, -0.15 * feather, apertureDistance);
                float edgeAlpha = aperture * saturate(_Passthrough)
                                * (1.0 - saturate(_FractureActive))
                                * (0.055 * wideEdge + 0.12 * fineEdge);
                alpha = saturate(alpha + edgeAlpha);

                // クロスフェードは本編スクリーンの 3D 矩形との交差部分だけに掛ける。
                // 開口がまだ大きい時も、周囲の現実を先に映像へ変えてしまわない。
                float onScreen = 1.0 - smoothstep(-feather, 0.0, screenDistance);
                onScreen = lerp(onScreen, 1.0 - step(0.0, screenDistance), saturate(_FractureActive));
                alpha = lerp(alpha, saturate(_ScreenFade), onScreen);

                // RGB は使わない。Passthrough Windows の compositor alpha だけを書く。
                return float4(0.0, 0.0, 0.0, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
