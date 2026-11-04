// デジタルツインの部屋の面（壁・床・カーテン・パイプ）。裸電球（URP の追加ライト・影つき）だけで照らす。
// 法線はアルベドの明暗から作る（剥がれた塗膜の縁・織り目・骨材の凹凸）。AO は頂点色に焼いてある。
Shader "FixedCamVr/Twin/Surface"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
        _TexScale ("Tiles per meter", Float) = 1
        _Bump ("Relief from albedo", Float) = 1
        _Spec ("Specular", Float) = 0.04
        _Gloss ("Gloss exponent", Float) = 24
        _Translucency ("Light through cloth", Float) = 0
        _Grime ("Grime near floor", Float) = 0
        _EmissionColor ("Emission", Color) = (0,0,0,0)
        _EmissionMap ("Emission image", 2D) = "white" {}
        _Sheen ("Glass sheen on the emission image", Float) = 0
        _EmissionFit ("Screen aspect / image aspect (pillarbox)", Float) = 1
        _Desat ("Desaturate albedo", Float) = 0
        _LightGain ("Direct light gain", Float) = 1
        _EmissionCore ("Emission hot core (0 = flat)", Float) = 0
        _Band ("Grime band at waist", Float) = 0
        _Peel ("Large peeled patches", Float) = 0
        _PeelScale ("Peel noise per meter", Float) = 2
        _PeelColor ("Bare plaster", Color) = (0.25,0.27,0.25,1)
        _Rim ("Fresnel rim toward the bulb", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _Mold ("Mold along corners", Float) = 0
        _RingStain ("Water rings around the bulb", Float) = 0
        _Damp ("Damp floor stain", Float) = 0
        _FloorWear ("Concrete floor wear", Float) = 0
        _DampCenter ("Damp center xz / radius w", Vector) = (0,0,0,0.6)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "TwinCommon.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _TexScale, _Bump, _Spec, _Gloss, _Translucency, _Grime, _LightGain, _EmissionCore, _Band, _Peel, _PeelScale;
                float4 _PeelColor;
                float _Rim, _Mold, _RingStain, _Damp, _FloorWear, _Sheen, _Desat, _EmissionFit;
                float4 _DampCenter;
                float4 _EmissionColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float4 color : TEXCOORD4;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = v.uv * _TexScale;
                o.color = v.color;
                return o;
            }

            float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

            // 剥がれの場（大きいほど剥がれている）。湿気と擦れが集まる所（腰・天井際・部屋の角）ほど剥がれやすい
            float PeelField(float3 q, float y, float ao, float2 xz)
            {
                q.y *= 0.6;     // 水が下へ流れた向きに縦長（1.5〜2 倍）
                float m = TwinFbm(q) + 0.15 * TwinNoise3(q * 9.0) + 0.07 * TwinNoise3(q * 31.0);
                float low = 1.0 - smoothstep(0.85, 1.15, y);                                 // 床から 1m（湿気が上がる）
                float top = smoothstep(2.7, 2.9, y);                                         // 天井際 40cm（雨漏り）
                // 電球の光が当たる所（電球から水平 1.5m 以内・高さ 1〜2.2m）。参考で最も読める質感はここ
                float near = 1.0 - smoothstep(1.2, 1.7, distance(xz, _TwinKeyPos.xz));
                float lit = near * smoothstep(0.9, 1.1, y) * (1.0 - smoothstep(2.1, 2.3, y));
                float bias = saturate(1.1 * low + 1.1 * top + 1.0 * lit + (1.0 - ao) * 0.7);
                m += 0.1 * bias - 0.075;
                m += 0.13 * smoothstep(0.66, 0.82, TwinNoise3(q * 0.3));    // 1m 級の大きな欠損（数か所）
                return m;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                albedo = lerp(albedo, dot(albedo, float3(0.2126, 0.7152, 0.0722)).xxx, _Desat);
                // アルベドの明暗を高さとみなして法線を起こす（剥がれた塗膜は下地より高い = 明るい漆喰は低い、どちらでも縁が立つ）
                float2 texel = float2(1.0 / 1024.0, 1.0 / 1024.0) * 1.5;
                float hL = Luma(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(texel.x, 0)).rgb);
                float hR = Luma(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(texel.x, 0)).rgb);
                float hD = Luma(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(0, texel.y)).rgb);
                float hU = Luma(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(0, texel.y)).rgb);
                float3 nTS = normalize(float3((hL - hR) * _Bump, (hD - hU) * _Bump, 1.0));
                float3 N0 = normalize(i.normalWS);
                float3 T = normalize(i.tangentWS.xyz - N0 * dot(i.tangentWS.xyz, N0));
                float3 B = cross(N0, T) * i.tangentWS.w;
                float3 N = normalize(T * nTS.x + B * nTS.y + N0 * nTS.z);

                float ao = i.color.r;
                // 大きな剥がれ（10〜60cm）: 塗膜が島状に落ちて下地の漆喰が出る。縁は塗膜の厚みで影になる
                if (_Peel > 0.0)
                {
                    float3 q = i.positionWS * _PeelScale;
                    // 剥がれの輪郭はなめらかな場で決める（腰・光の当たる高さ・天井際・部屋の角で剥がれやすい）
                    float field = PeelField(q, i.positionWS.y, ao, i.positionWS.xz);
                    // 輪郭のすぐ近くだけ、横長の多角形の欠片（ボロノイ）単位で出入りさせて縁を角張らせる。
                    // 輪郭から離れた所の欠片は動かない（紙吹雪の離れ小島を作らない）
                    float2 wuv = float2((i.positionWS.x + i.positionWS.z) / 0.085, i.positionWS.y / 0.042);
                    float2 cid; float edge;
                    TwinVoronoi(wuv, cid, edge);
                    float h = TwinHash13(float3(cid, 3.1));
                    float h2 = TwinHash13(float3(cid, 8.7));
                    const float Thr = 0.6;
                    float f = field + 0.035 * (h - 0.5);
                    float bare = step(Thr, f);
                    // 上塗りだけ落ちて淡い緑の中塗りが出た所。幅はまちまちで、無い所も多い（同じ幅の輪は地図の海岸線に見える）
                    float midW = 0.03 * saturate(TwinNoise3(q * 5.0) * 2.2 - 0.7);
                    float mid = step(Thr - midW, f) * (1.0 - bare);
                    float mott = floor(TwinNoise3(q * 2.6) * 3.0) / 2.0;            // 2〜3 段のむら（剥がれた時期の違い）
                    float3 plaster = _PeelColor.rgb * lerp(0.72, 1.12, mott) * (0.82 + 0.36 * TwinNoise3(q * 23.0))
                                   * (1.0 - 0.3 * step(0.8, TwinNoise3(q * 41.0)));
                    float3 midCoat = float3(0.19, 0.26, 0.22) * (0.8 + 0.4 * TwinNoise3(q * 17.0));
                    albedo = lerp(albedo, midCoat, mid * _Peel);
                    albedo = lerp(albedo, plaster, bare * _Peel);
                    // めくれた塗膜の落ち影（剥がれ側の縁の内側）と、残った塗膜の縁が光を受ける明るい線
                    float inBare = saturate((f - Thr) / 0.014);
                    albedo *= lerp(1.0, lerp(0.45, 1.0, inBare), bare * _Peel);
                    float3 qUp = q + float3(0.0, 0.012 * _PeelScale, 0.0);
                    float fUp = PeelField(qUp, i.positionWS.y + 0.012, ao, i.positionWS.xz);
                    float topEdge = saturate((field - fUp) * 60.0);     // 剥がれが下にある縁（塗膜の切り口が電球を向く）
                    float bottomEdge = saturate((fUp - field) * 60.0);
                    float lipW = 0.006 * TwinNoise3(q * 7.0) * step(0.5, TwinNoise3(q * 3.3 + 7.0));
                    float lip = (1.0 - bare) * (1.0 - mid) * (1.0 - saturate((Thr - midW - f) / max(lipW, 1e-4))) * step(1e-4, lipW);
                    albedo *= 1.0 + (0.45 * topEdge - 0.35 * bottomEdge) * lip * _Peel;
                    // ひび: 輪郭から数 cm 以内の、欠片の辺の 2〜3 割だけ
                    float nearEdge = 1.0 - saturate(abs(f - Thr) / 0.06);
                    float crack = (1.0 - saturate(edge * 0.06 / 0.0012)) * step(h2, 0.28) * nearEdge * (1.0 - bare);
                    albedo *= 1.0 - 0.5 * crack * _Peel;
                    // 剥がれの下へ垂れる水と汚れの筋（30〜60cm）。光の当たる高さで強く
                    float streak = TwinNoise3(float3((i.positionWS.x + i.positionWS.z) * 9.0, i.positionWS.y * 0.6, 0.5));
                    float below = smoothstep(Thr - 0.12, Thr - 0.02, PeelField(q + float3(0.0, 0.35 * _PeelScale, 0.0), i.positionWS.y + 0.35, ao, i.positionWS.xz));
                    albedo *= 1.0 - (0.36 + 0.5 * below) * smoothstep(0.55, 0.85, streak) * saturate((i.positionWS.y - 0.6) / 1.6) * _Peel;
                }

                // 入隅と天井際の黒い黴（遮蔽の強い所ほど・まだらに）
                float moldN = TwinNoise3(i.positionWS * 5.0) * 0.7 + 0.3 * TwinNoise3(i.positionWS * 19.0);
                albedo *= 1.0 - _Mold * saturate((0.75 - ao) * 2.2) * smoothstep(0.35, 0.65, moldN);
                // 天井の座の周りの水の輪染み（半径 25〜45cm の輪が 2〜3 重）
                // 輪は歪ませて途切れさせる（同心円のままだと的に見える）
                float2 dxz = i.positionWS.xz - _TwinKeyPos.xz + 0.12 * (float2(TwinNoise3(i.positionWS * 2.0), TwinNoise3(i.positionWS * 2.0 + 9.0)) - 0.5);
                float rr = length(dxz * float2(1.0, 1.25)) + 0.05 * TwinNoise3(i.positionWS * 7.0);
                float gap = smoothstep(0.35, 0.6, TwinNoise3(i.positionWS * 3.0));
                float rings = (smoothstep(0.016, 0.0, abs(rr - 0.32)) + 0.6 * smoothstep(0.012, 0.0, abs(rr - 0.43))) * gap;
                float inside = 1.0 - smoothstep(0.25, 0.45, rr);
                albedo *= 1.0 - _RingStain * (0.35 * rings + 0.18 * inside);
                albedo *= lerp(float3(1, 1, 1), float3(1.0, 0.82, 0.62), saturate(_RingStain * rings));   // 輪の縁は茶色く
                // 床の湿った染み: 輪郭はノイズで揺らぎ、縁は 5〜15cm でぼける。暗さは 2 割まで、少し艶が出る
                float spec = _Spec, gloss = _Gloss;
                if (_FloorWear > 0.0)
                {
                    float3 pw = i.positionWS;
                    // 黒く埋まったひび: ノイズの尾根（細く・途切れる）
                    float cr = abs(TwinNoise3(pw * float3(1.6, 1.0, 1.6)) - 0.5);
                    // 短く途切れる髪の毛ほどのひび（濃いと床に落ちた電線に見える）
                    float crackF = (1.0 - smoothstep(0.0, 0.0035, cr)) * step(0.62, TwinNoise3(pw * 4.0 + 4.0));
                    albedo *= 1.0 - 0.3 * crackF * _FloorWear;
                    // 壁際の埃の帯（遮蔽の強い所が白っぽくくすむ）
                    float dust = saturate((0.85 - ao) * 3.0) * (0.6 + 0.4 * TwinNoise3(pw * 8.0));
                    albedo = lerp(albedo, albedo * 0.6 + float3(0.045, 0.047, 0.045), dust * 0.6 * _FloorWear);
                    // 歩く所の擦れ: 体験者の歩く範囲（1.8m 四方）の内側は少し磨かれて艶が出る
                    float walk = (1.0 - smoothstep(0.75, 0.95, max(abs(pw.x), abs(pw.z))));
                    spec = lerp(spec, 0.22, walk * 0.7 * _FloorWear);
                    gloss = lerp(gloss, 55.0, walk * 0.7 * _FloorWear);
                    albedo *= 1.0 - 0.06 * walk * _FloorWear;
                }
                if (_Damp > 0.0)
                {
                    float2 dd = i.positionWS.xz - _DampCenter.xz;
                    float dn = length(dd * float2(0.75, 1.0)) + 0.18 * (TwinFbm(i.positionWS * 2.2) - 0.45);
                    float damp = 1.0 - smoothstep(_DampCenter.w * 0.55, _DampCenter.w, dn);
                    albedo *= 1.0 - 0.2 * damp * _Damp;
                    spec = lerp(spec, 0.35, damp * _Damp);
                    gloss = lerp(gloss, 70.0, damp * _Damp);
                }
                float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);
                float3 lit = 0;
                uint count = GetAdditionalLightsCount();
                for (uint k = 0u; k < count; k++)
                {
                    Light l = GetAdditionalLight(k, i.positionWS, half4(1, 1, 1, 1));
                    float3 radiance = l.color * l.distanceAttenuation;
                    float ndl = dot(N, l.direction);
                    float diffuse = saturate(ndl);
                    float3 H = normalize(l.direction + V);
                    float sp = spec * pow(saturate(dot(N, H)), gloss) * diffuse * (gloss + 8.0) / 25.0;
                    lit += radiance * l.shadowAttenuation * (albedo * diffuse + sp);
                    // 布の裏へ抜ける光（影は表で済んでいるので掛けない。弱く）
                    lit += radiance * albedo * saturate(-dot(N0, l.direction)) * _Translucency;
                }
                float sky = 0.55 + 0.45 * N.y;
                float3 ambient = albedo * _TwinAmbient.rgb * sky * ao;
                // 発光するガラス: 芯（視線と正対する所）は明るく、縁は暗い
                float facing = saturate(dot(N0, V));
                float core = lerp(1.0, 0.25 + 1.4 * pow(facing, 5.0), saturate(_EmissionCore));
                float2 euv = float2((i.uv.x - 0.5) * _EmissionFit + 0.5, i.uv.y);
                float inImage = step(0.0, euv.x) * step(euv.x, 1.0);
                float3 emission = _EmissionColor.rgb * SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, saturate(euv)).rgb * inImage;
                // 液晶のガラスに斜めの淡い映り込み
                float sheenBand = smoothstep(0.06, 0.0, abs(i.uv.x * 0.55 + i.uv.y - 0.95)) + 0.5 * smoothstep(0.03, 0.0, abs(i.uv.x * 0.55 + i.uv.y - 1.12));
                emission += _Sheen * sheenBand * float3(0.8, 0.86, 0.88);
                float3 color = lit * _LightGain * lerp(1.0, ao, 0.6) + ambient + emission * core;
                float towardBulb = saturate(dot(N0, normalize(_TwinKeyPos.xyz - i.positionWS)) + 0.25);
                color += _Rim * float3(0.10, 0.093, 0.082) * pow(1.0 - facing, 3.0) * towardBulb;
                color = TwinApplyFog(color, i.positionWS);
                return float4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            float4 ShadowVert(A v) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }
            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 DepthVert(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
            half DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
