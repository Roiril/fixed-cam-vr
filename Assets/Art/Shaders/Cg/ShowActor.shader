// CG 人形（ShowCg レイヤ）専用のシェーディング。
//
// **シーンのライトを一切参照しない**のが要点。現場の照明・URP 設定・他アプリ（TableDuo）の都合で
// 人形の見えが変わると演出が壊れるため、方向・強さを固定したライティングをマテリアルに焼く。
// 逆に「CG レイヤ用のライトを置く」方式は採らない（URP は Light.cullingMask を尊重せず実シーンを汚す）。
//
// 合成は ScreenComposite の 3 層目（_CgTex）で、**ポスト FX の前**。露出・走査線・グレインは
// そちらで一括して浴びるので、ここでは素の色だけを出す。
Shader "FixedCamVr/ShowActor"
{
    Properties
    {
        // 実物をスキャンした人形はここへアルバイド（色）を入れる。**未設定なら白**なので、
        // テクスチャを持たない人形（Mixamo のマネキン等）は _BaseColor だけが効き従来と同じ絵になる。
        // 陰影は下の wrap lighting で作るので、テクスチャ側に焼き込まれた影は無い方がよい
        // （フォトグラメトリの出力は撮影時の照明が焼き込まれている。撮影で影を消す理由がこれ）。
        _BaseMap("Base Map (albedo)", 2D) = "white" {}
        // 布の襞をライトに反応させる。無ければ平ら（"bump" は法線の既定テクスチャ）。
        // ⚠ 撮影時の陰影はアルベドにも残っているので、強くすると二重に暗くなる。
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0, 2)) = 0.75
        // 鏡面。胡粉の顔・手も絹も実物には照りがある。リムは視線だけで決まるので
        // **光を動かしても動かず**、それ自体が「CG である」合図になっていた。
        _Spec("Specular", Range(0, 1)) = 0.10
        _Gloss("Glossiness", Range(4, 128)) = 26
        _BaseColor("Base Color", Color) = (0.72, 0.70, 0.67, 1)
        _ShadeColor("Shade Color", Color) = (0.10, 0.10, 0.12, 1)
        _RimColor("Rim Color", Color) = (0.85, 0.85, 0.90, 1)
        // 光が来る向き（ワールド・正規化）。**ShowCgLayer が毎フレーム course 空間基準で上書きする**
        // （MaterialPropertyBlock 経由。show.json の layout.room.light が正）。
        // ここの既定は Editor プレビュー / 単体確認用のフォールバックにすぎない。
        // ワールド固定のままだと Quest のトラッキング原点の向き次第で部屋に対する光の向きが変わる。
        _LightDir("Light Direction (world)", Vector) = (0.35, 0.85, -0.40, 0)
        // 主光源の色（**linear**・色温度 × 強さを掛け込んだもの）。ShowCgLayer が
        // layout.room.light の tempK / intensity から作って毎フレーム供給する。
        _LightColor("Light Color (linear, tempK x intensity)", Vector) = (1, 1, 1, 1)
        // 環境光: 影側をどれだけ持ち上げるか。0 = 影が _ShadeColor のまま沈む / 1 = 影が消える。
        _Ambient("Ambient", Range(0, 1)) = 0.35
        _Wrap("Light Wrap", Range(0, 1)) = 0.45
        _Rim("Rim Strength", Range(0, 1)) = 0.25
        _RimPower("Rim Power", Range(1, 8)) = 3
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "ShowActor"
            Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ⚠ テクスチャの宣言は CBUFFER の外（SRP Batcher 互換。中に入れるとバッチが壊れる）。
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _ShadeColor;
                float4 _RimColor;
                float4 _LightDir;
                float4 _LightColor;
                float _BumpScale;
                float _Spec;
                float _Gloss;
                float _Ambient;
                float _Wrap;
                float _Rim;
                float _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;   // xyz = 接線 / w = 従法線の符号
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionHCS = TransformWorldToHClip(posWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.viewWS = GetWorldSpaceViewDir(posWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                float3 tWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                o.tangentWS = float4(tWS, input.tangentOS.w * GetOddNegativeScale());
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 n = normalize(input.normalWS);
                float3 l = normalize(_LightDir.xyz);
                float3 v = normalize(input.viewWS);

                // 接空間の法線を world へ。布の襞がここで初めてライトに反応する。
                float3 tanWS = normalize(input.tangentWS.xyz);
                float3 bitWS = cross(n, tanWS) * input.tangentWS.w;
                float3 nTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                n = normalize(nTS.x * tanWS + nTS.y * bitWS + nTS.z * n);

                // wrap lighting: 影側を完全に潰さない（監視カメラの粗い絵で形が読める程度に残す）
                float ndl = dot(n, l);
                float t = saturate((ndl + _Wrap) / (1.0 + _Wrap));

                // アルベド = テクスチャ × 色。テクスチャ未設定なら白が返るので _BaseColor だけが効く。
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = _BaseColor.rgb * baseTex.rgb;
                // アルファは**部位ごとの光沢の強さ**（白磁 1.0 / 帯 0.7 / 髪 0.5 / 絹 0.28 / 絞り 0.16）。
                // 素材が違えば光り方が違う。全部を同じ鏡面で光らせると濡れたプラスチックに見える。
                float gm = baseTex.a;

                // 影側は「環境光がどれだけ持ち上げるか」、光側は「主光源の色 × 強さ」。
                // 卓（💡 CG 照明パネル）で著作した tempK / intensity / ambient がここで初めて絵に効く。
                // これを繋ぐまでは、著作者がスライダを動かしても何も変わらなかった。
                half3 shade = lerp(_ShadeColor.rgb, albedo, saturate(_Ambient));
                half3 lit = albedo * _LightColor.rgb;
                half3 col = lerp(shade, lit, t);

                // 鏡面（Blinn-Phong 1 ローブ）。**光を動かすとハイライトが動く**のが要点で、
                // これが無いと、どれだけ形を作っても「塗った絵」に見える。
                // 陶器は**鋭く強く**、布は**広く弱く**光る。1 枚のマスクで鋭さと強さを同時に振る。
                float3 h = normalize(l + v);
                float gloss = lerp(7.0, _Gloss, gm);
                float spec = pow(saturate(dot(n, h)), gloss) * _Spec * lerp(0.18, 1.0, gm)
                             * saturate(ndl + _Wrap);
                col += _LightColor.rgb * spec;

                // リム: 輪郭をわずかに立てる（映像に埋もれて「居るのに見えない」を防ぐ）
                float rim = pow(saturate(1.0 - saturate(dot(n, v))), _RimPower) * _Rim;
                col += _RimColor.rgb * rim;

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
