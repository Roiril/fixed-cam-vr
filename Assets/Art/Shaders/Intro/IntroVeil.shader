// 導入演出の覆い。破砕中は全面を塞ぎ、同位置の IntroFracture だけで現実を見せる。
//
// Meta の Passthrough Windows 方式を使う。alpha は `Blend Zero SrcAlpha` により
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
        _FractureActive("Full-field fracture active", Range(0, 1)) = 0
        // 0225: 着地した破片がその場所の映像を見せる区間。基底は RGB を残し、隙間だけを別の面（_GapsMode）が黒く塗る。
        _Reveal("Landed pieces reveal the video", Range(0, 1)) = 0
        _GapsMode("Paint only the gaps between pieces (stencil)", Range(0, 1)) = 0
        [HideInInspector] _StencilRef("Stencil ref", Float) = 0
        [HideInInspector] _StencilComp("Stencil comp", Float) = 8
        [HideInInspector] _StencilReadMask("Stencil read mask", Float) = 255
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
        [HideInInspector] _ZWrite("Reset depth for frozen fracture", Float) = 0
    }

    SubShader
    {
        // URP の透明範囲は 5000 まで。後続の構造線と案内を 5000 に置けるよう 4900 を保つ。
        Tags { "Queue" = "Overlay+900" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Name "IntroVeil"
            Blend Zero SrcColor, Zero SrcAlpha
            BlendOp Add
            ZWrite [_ZWrite]
            ZTest Always
            Cull Off
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
            }

            HLSLPROGRAM
            #pragma target 3.5
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
            float _Reveal;
            float _GapsMode;
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

            float4 frag(Varyings i, out float depth : SV_Depth) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
#if UNITY_REVERSED_Z
                depth = 0.0;
#else
                depth = 1.0;
#endif
                // 隙間だけを黒く塗る面（0225）。ステンシルが「破片のある所」を弾くので、ここへ来るのは隙間だけ。
                // RGB を 0 に、compositor alpha は閉じたまま（乗算で 1）。
                if (_GapsMode > 0.5)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // 覆いとスクリーンを同じ距離へ置くため、中央眼から解いた平面は左右眼でも一致する。
                float3 dir = normalize(float3((i.uv - 0.5) * _VeilSize.xy, _VeilSize.z));

                float frameDistance = max(
                    max(dot(dir, _FramePlane0.xyz), dot(dir, _FramePlane1.xyz)),
                    max(dot(dir, _FramePlane2.xyz), dot(dir, _FramePlane3.xyz)));
                float screenDistance = max(
                    max(dot(dir, _ScreenPlane0.xyz), dot(dir, _ScreenPlane1.xyz)),
                    max(dot(dir, _ScreenPlane2.xyz), dot(dir, _ScreenPlane3.xyz)));
                // 破砕中は中央にも保護窓を残さない。現実は IntroFracture の全片だけが返す。
                if (_FractureActive > 0.5)
                {
                    // 映像が出る前は先描きされた管の燐光も隠す。残すと破片の隙間だけが
                    // 茶色になり、完成前から中央の四角形が見える。alpha は閉じたまま。
                    // 混合開始後の RGB は維持し、混合量は後段の IntroFracture だけへ任せる。
                    // 0225: 着地した破片が映像を見せる区間（_Reveal）も RGB を残す。隙間は _GapsMode の面が黒く塗る。
                    float rgbGate = (_ScreenFade > 0.0 || _Reveal > 0.0) ? 1.0 : 0.0;
                    return float4(rgbGate, rgbGate, rgbGate, 1.0);
                }

                float apertureDistance = frameDistance;
                // 平面の幾何はそのまま保ち、画面上の局所ぼけだけを絞る。
                float feather = max(_FeatherAng * 0.25, 1e-4);
                float aperture = 1.0 - smoothstep(-feather, 0.0, apertureDistance);

                // 開口の外はアプリの画を保ち、内側だけ現実を透かす。
                float alpha = lerp(1.0, 1.0 - saturate(_Passthrough), aperture);

                // 薄い透過帯を 2 段だけ置く。RGB は足さず、compositor alpha だけで静かに縁を出す。
                float wideEdge = smoothstep(-4.0 * feather, -1.0 * feather, apertureDistance);
                float fineEdge = smoothstep(-1.6 * feather, -0.15 * feather, apertureDistance);
                float edgeAlpha = aperture * saturate(_Passthrough)
                                * (0.055 * wideEdge + 0.12 * fineEdge);
                alpha = saturate(alpha + edgeAlpha);

                // クロスフェードは本編スクリーンの 3D 矩形との交差部分だけに掛ける。
                // 開口がまだ大きい時も、周囲の現実を先に映像へ変えてしまわない。
                float onScreen = 1.0 - smoothstep(-feather, 0.0, screenDistance);
                alpha = lerp(alpha, saturate(_ScreenFade), onScreen);

                // 非破砕時は RGB と alpha を同率にして、従来の Passthrough Windows を保つ。
                return float4(alpha, alpha, alpha, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
