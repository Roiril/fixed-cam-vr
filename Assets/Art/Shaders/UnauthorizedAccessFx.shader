Shader "FixedCamVr/UnauthorizedAccessFx"
{
    // 2026-10-21（canon/LEDGER.md 0285「デジタル感が強すぎる」「のっぺりしている」）:
    // 単色の塗り＋暗い縁だった警告を「装置の画面の中で光っている字」へ描き直した。
    //   Mode 2（文字・図形）: 芯の明るさのむら・にじみ（右へ尾を引く）・時間で動く細かい粒・
    //                         まれな細い帯の横ずれ・焼いていない図形の乾いたかすれ（_Wear）
    //   Mode 1（空間の要素）: 0 と 1 の字をやめ、砂嵐の断片（主画面の砂嵐と同じ語彙）を描く
    //   Mode 0（信号の裂け目）: 従来どおり
    // 模様は画面のローカル座標（positionOS・メートル）で決める。ワールド座標だと画面が
    // 頭のヨーへ追従するたびに模様が字の上を滑る。左右の眼で同じ値になるので立体視も崩れない。
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1, 1, 1, 1)
        _Mode("Mode", Float) = 0
        _Strength("Strength", Float) = 1
        _ReadMaskStrength("Read area attenuation", Float) = 0
        _Wear("Procedural dryness", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent+450" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "UnauthorizedAccessFx"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Mode;
                float _Strength;
                float _ReadMaskStrength;
                float _Wear;
                float4x4 _ReadWorldToLocal;
                float4 _ReadRect0;
                float4 _ReadRect1;
                float4 _ReadRect2;
                float4 _ReadRect3;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 glyph : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 glyph : TEXCOORD1;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD2;
                float3 positionOS : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;
                output.uv = input.uv;
                output.glyph = input.glyph;
                output.color = input.color;
                return output;
            }
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3 - 2 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }
            float ReadRectCoverage(float2 planePoint, float4 rect)
            {
                if (rect.z <= 0 || rect.w <= 0) return 0;
                float2 outside = abs(planePoint - rect.xy) - rect.zw;
                return 1 - smoothstep(0, .06, max(outside.x, outside.y));
            }
            float ReadMask(float3 positionWS)
            {
                if (_ReadMaskStrength < .001) return 1;
                // Project through the fragment onto the text plane from the current eye.
                // This preserves depth while protecting letters even after head movement.
                float3 eye = mul(_ReadWorldToLocal, float4(GetCameraPositionWS(), 1)).xyz;
                float3 fragmentLocal = mul(_ReadWorldToLocal, float4(positionWS, 1)).xyz;
                float3 ray = fragmentLocal - eye;
                if (abs(ray.z) < .0001) return 1;
                float travel = -eye.z / ray.z;
                if (travel < 0) return 1;
                float2 onPlane = eye.xy + ray.xy * travel;
                float coverage = max(ReadRectCoverage(onPlane, _ReadRect0), ReadRectCoverage(onPlane, _ReadRect1));
                coverage = max(coverage, max(ReadRectCoverage(onPlane, _ReadRect2), ReadRectCoverage(onPlane, _ReadRect3)));
                return 1 - coverage * saturate(_ReadMaskStrength);
            }
            half InkAt(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a;
            }
            half4 FragGlowingInk(Varyings input, half4 tint)
            {
                float2 os = input.positionOS.xy;
                // まれな細い帯の横ずれ（主画面の乱れと同じ語彙）。帯は 1.1cm、1 秒に 9 回だけ引き直す。
                // 量は字幅の 2% まで・帯の 2.5% だけ ＝ 読む邪魔をしない頻度に留める。
                float band = floor(os.y / .011);
                float tick = floor(_Time.y * 9.0);
                float slip = Hash21(float2(band, tick)) > .975 ? (Hash21(float2(tick, band * 1.7)) - .5) * .04 : 0;
                float2 uv = input.uv + float2(slip, 0);
                float2 t = _MainTex_TexelSize.xy;

                half ink = InkAt(uv);
                // 焼いていない図形（三角・感嘆符・バー）だけ乾いた筆のかすれで抜く。横に長い斑。
                if (_Wear > .001)
                {
                    float w = ValueNoise(os * float2(55, 240)) * .65 + ValueNoise(os * float2(260, 90)) * .35;
                    ink *= smoothstep(_Wear - .03, _Wear + .03, w);
                }

                // 暗い縁（明るい背景の上で字形を保つ）。従来より細く・薄く。
                float2 d = t * 4;
                half edge = InkAt(uv + float2(d.x, 0));
                edge = max(edge, InkAt(uv - float2(d.x, 0)));
                edge = max(edge, InkAt(uv + float2(0, d.y)));
                edge = max(edge, InkAt(uv - float2(0, d.y)));
                float2 dd = d * .7071;
                edge = max(edge, InkAt(uv + dd));
                edge = max(edge, InkAt(uv - dd));
                edge = max(edge, InkAt(uv + float2(dd.x, -dd.y)));
                edge = max(edge, InkAt(uv + float2(-dd.x, dd.y)));

                // にじみ: 周囲の平均 ＋ 右へ尾を引く成分（管の残光）。
                float2 r = t * 14;
                float2 rd = r * .7071;
                half halo = InkAt(uv + float2(r.x, 0)) + InkAt(uv - float2(r.x, 0))
                    + InkAt(uv + float2(0, r.y)) + InkAt(uv - float2(0, r.y))
                    + InkAt(uv + rd) + InkAt(uv - rd)
                    + InkAt(uv + float2(rd.x, -rd.y)) + InkAt(uv + float2(-rd.x, rd.y));
                halo *= .125;
                half trail = InkAt(uv - float2(t.x * 6, 0)) * .5 + InkAt(uv - float2(t.x * 12, 0)) * .3
                    + InkAt(uv - float2(t.x * 20, 0)) * .2;
                halo = saturate(halo * .8 + trail * .55);

                // 芯: 太い所ほど明るく白へ寄る（発光体の中心）。
                half nearAvg = (InkAt(uv + float2(t.x * 1.5, 0)) + InkAt(uv - float2(t.x * 1.5, 0))
                    + InkAt(uv + float2(0, t.y * 1.5)) + InkAt(uv - float2(0, t.y * 1.5))) * .25;
                half core = smoothstep(.85, 1, min(ink, nearAvg));

                // 明るさのむら（3cm 程度の斑）と、時間で動く細かい粒（1.4mm・24Hz）。
                float density = .72 + .42 * ValueNoise(os * 38);
                float frame = floor(_Time.y * 24.0);
                float g = Hash21(floor(os * 700) + frame * 7.13);
                float grainK = .86 + .28 * g;

                half3 hot = lerp(tint.rgb, half3(1, .86, .70), .45);
                half3 inkCol = lerp(tint.rgb, hot, core * .4) * density * grainK;
                half3 glowCol = tint.rgb * .75;
                half3 dark = half3(.008, .003, .005);

                half aHalo = halo * .32 * (.85 + .3 * g);
                half aEdge = edge * .7;
                half3 col = glowCol;
                half a = aHalo;
                col = lerp(col, dark, saturate(aEdge));
                a = a + aEdge * (1 - a);
                col = lerp(col, inkCol, ink);
                a = a + ink * (1 - a);
                return half4(col, saturate(a * tint.a * _Strength));
            }
            half4 FragSnowFragment(Varyings input, half4 tint)
            {
                // 砂嵐の断片。1 本または 2 本の横帯の中を、主画面の砂嵐と同じ粒が走る。
                float2 uv = input.uv;
                float blur = saturate(input.glyph.y);
                bool twoRows = input.glyph.x > .5;
                float frame = floor(_Time.y * 18.0);
                float rowH = lerp(.07, .12, blur);
                float soft = .03 + blur * .08;
                float yA = twoRows ? .40 : .5;
                float rowA = 1 - smoothstep(rowH * .5, rowH * .5 + soft, abs(uv.y - yA));
                float rowB = twoRows ? 1 - smoothstep(rowH * .5, rowH * .5 + soft, abs(uv.y - .62)) : 0;
                float row = max(rowA, rowB);
                // 帯の長さは場所と時間でゆっくり変わる（同じ形が並ばないように）。
                float lenSeed = ValueNoise(input.positionWS.xz * 3 + input.positionWS.y * 2 + _Time.y * .7);
                float len = .45 + .5 * lenSeed;
                float xFade = 1 - smoothstep(len * .5 - .08, len * .5, abs(uv.x - .5));
                // 粒は断片の大きさに合わせて uv で刻む（手前の大きい断片が縦縞の板にならない）。
                // 断片ごとの違いは発火位置に固定された座標から取る。
                float seed = Hash21(floor(input.positionWS.xz * 2.0) + floor(input.positionWS.y * 2.0) * 3.0);
                float speck = Hash21(floor(uv * float2(40, 12)) + seed * 97.0 + frame * 3.1);
                float snow = smoothstep(.35, .9, speck);
                float opacity = row * xFade * (.25 + .75 * snow);
                float2 rim = min(uv, 1 - uv);
                opacity *= smoothstep(0, .08, min(rim.x, rim.y));
                half3 light = lerp(tint.rgb, half3(.86, .80, .72), snow * .35 * (1 - blur));
                return half4(light, opacity * tint.a * _Strength);
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tint = input.color * _Color;
                tint.a *= ReadMask(input.positionWS);
                if (_Mode > 1.5) return FragGlowingInk(input, tint);
                if (_Mode < .5)
                {
                    half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                    return half4(texel.rgb * tint.rgb, saturate(texel.a * tint.a * _Strength));
                }
                return FragSnowFragment(input, tint);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
