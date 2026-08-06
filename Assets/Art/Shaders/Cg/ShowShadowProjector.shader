// CG 人形が床に落とす影（**平面投影シャドウ**）。人形メッシュをもう一度描き、頂点を光線方向で
// 床平面へ潰して「rgb=0 / a=濃さ」の断片として出す。
//
// URP のシャドウマップを使わない理由は 1 つ: 人形は **シーンのライトを一切参照しない**
// （ShowActor.shader の不変条件）。現場の照明・URP 設定・同居アプリの都合で人形の見えが変わると
// 演出が壊れるため、CG 用のライトをシーンへ置くことはできない（URP の Light は cullingMask を尊重せず
// HMD が見る実 VR 空間まで照らす）。平面投影ならライトが 1 つも要らない。
// セルフシャドウが出ない欠点は許容する — 640x480・JPEG q40・走査線とグレインの絵では観測できない。
//
// **premultiplied over が「乗算」を運ぶ**: ScreenComposite は col*(1-a*s) + rgb*s で合成するので、
// rgb=0 / a=d を書くだけで背景が (1-d) 倍される。影のための専用チャンネルも 2 枚目の RT も要らない。
//
// ⚠ ステンシルを外すと腕と胴の投影が重なった所だけ二重に暗くなり、影が「濃い斑」になる。
//    1 画素 1 回だけ描かせるのがこのシェーダの一番壊れやすい部分。
//    ステンシルを使うには RT の depth buffer が 24（depth24 + stencil8）でなければならない
//    （ShowCgLayer.EnsureRenderTexture がそう作っている。16 に戻すと影が斑に化ける）。
Shader "FixedCamVr/ShowShadowProjector"
{
    Properties
    {
        // 影の落ち先の**ワールド** y。ShowCgLayer が layout.room.floorY を course→world して毎フレーム入れる。
        _ShadowPlaneY("Shadow Plane Y (world)", Float) = 0
        // 光が来る向き（ワールド・正規化・上向き成分が正）。ShowCgLayer が course 相対で毎フレーム入れる
        // （ワールド固定にするとトラッキング原点の向き次第で影の向きが変わる）。
        _ShadowLightDir("Light Direction (world, upward positive)", Vector) = (0.35, 0.85, -0.40, 0)
        _ShadowDensity("Shadow Density", Range(0, 1)) = 0.55
        // にじみ（`layout.room.light.shadowSoftM`）を**濃さの高さ減衰**として受ける。
        //
        // 平面投影は人形の形をそのまま潰すので、縁を空間的にぼかす場所が無い（ステンシルで 1 回しか
        // 描けない制約もある）。代わりに「遮蔽している部位が床から高いほど薄い」を効かせる —
        // 実際の半影も遮蔽物と受光面の距離に比例して広がり、本影の割合はその分下がる。
        // 足元は濃く、頭の影は薄くなるので、**硬い縁が目立つのは接地点の近くだけ**になり、
        // そこは接地影 blob の `_BlobFeather` がぼかしている。
        // 0 なら減衰なし（＝以前と同じ一様な濃さ）。
        _ShadowSoftM("Shadow Softness (m)", Float) = 0.12
    }

    SubShader
    {
        // Geometry-100 = オクルーダ（-200）の後・人形本体（Geometry）の前。
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-100" }
        LOD 100

        Pass
        {
            Name "ShowShadowProjector"
            Blend One OneMinusSrcAlpha   // premultiplied over（rgb は既に 0 なので実質「背景を (1-a) 倍」）
            ZWrite Off
            // 潰した後の位置は床の上なので、**壁の裏に回った分は隠れるのが正しい**
            // （ShowRoomProxy が壁と箱の深度を先に書いている。床は描かないので z-fight しない）。
            // Always だと壁の手前に影が出て、人形が壁の裏に居ることを画が否定する。
            ZTest LEqual
            Cull Off                     // 潰れたメッシュは裏表が定まらない
            Stencil { Ref 1 Comp NotEqual Pass Replace }   // ★ 二重暗化の防止（上の警告参照）

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ShadowLightDir;
                float _ShadowPlaneY;
                float _ShadowDensity;
                float _ShadowSoftM;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float heightM : TEXCOORD0;   // 潰す前に床から何 m 上に居たか（濃さの減衰に使う）
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 l = normalize(_ShadowLightDir.xyz);
                o.heightM = max(0.0, posWS.y - _ShadowPlaneY);

                // 光が真横〜下から来ていると交点が無限遠へ飛ぶ。分母を守るだけだと
                // **画面いっぱいの黒帯**になるので、三角形を 1 点へ潰してクリップさせる（＝影を出さない）。
                if (l.y <= 0.02)
                {
                    o.positionHCS = float4(2, 2, 2, 1);   // NDC の外 = 描かれない
                    return o;
                }

                float t = (posWS.y - _ShadowPlaneY) / l.y;
                posWS -= l * t;
                // 床そのものは描いていないが、将来この平面に受け皿を置いた時に z-fight しない程度だけ浮かす。
                posWS.y = _ShadowPlaneY + 0.002;
                o.positionHCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 床から高い部位ほど薄い（半影の広がり）。_ShadowSoftM を「本影が半分になる高さ」
                // として使う。0 以下なら減衰なし＝一様な濃さ（以前の挙動）。
                float atten = _ShadowSoftM > 1e-4
                    ? 1.0 / (1.0 + input.heightM / _ShadowSoftM)
                    : 1.0;
                // rgb=0 / a=濃さ。**straight alpha で書くと合成側が乗算にならない**（黒を上塗りしてしまう）。
                return half4(0, 0, 0, saturate(_ShadowDensity * atten));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
