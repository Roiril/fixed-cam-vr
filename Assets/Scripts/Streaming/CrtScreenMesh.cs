#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーンの面を**ブラウン管の曲面**へ差し替える（中央が体験者側へ膨らむ凸面）。
    ///
    /// <b>なぜメッシュを曲げるのか</b>: VR だから。シェーダで uv を樽型に歪めるだけだと、
    /// 頭を動かしても視差が出ず「絵が歪んでいる」としか読めない。面そのものが曲がっていれば、
    /// 覗き込んだときに縁が回り込み、映り込みの角度も変わる。
    ///
    /// <b>角は動かさない</b>（膨らみが 0 になる双放物面）。スクリーンの 4 隅は
    /// <see cref="IntroVeil"/> の開口計算（眼と 4 辺を通る平面）と
    /// <see cref="MjpegScreen.ScreenAspect"/> の基準なので、動かすとそちらが黙ってずれる。
    ///
    /// 角の丸みと縁の暗さは面の中の話なので <c>ScreenComposite</c> が持つ（<c>_CrtRound</c> /
    /// <c>_CrtEdge</c>）。**形はここ、面はシェーダ**で分けてある。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    public sealed class CrtScreenMesh : MonoBehaviour
    {
        /// <summary>
        /// 中央の膨らみ（ローカル単位 ＝ Quad の 1 辺を 1 とした値）。
        /// スクリーンの localScale は 2.37 × 1.33 なので、0.055 ならワールドで約 5.5cm。
        /// 実物のブラウン管の曲率（対角の 2〜3 倍の球面）に近い控えめな値。
        /// </summary>
        [SerializeField] private float bulge = 0.055f;

        /// <summary>1 辺の分割数。曲面の滑らかさ。14 で 225 頂点（描画の負担にならない）。</summary>
        [SerializeField] private int segments = 14;

        private Mesh? _mesh;

        // ⚠ **組むのは実行時だけ。** 生成した Mesh はアセットではないので、Editor で差し替えたまま
        //   シーンを保存すると壊れた参照が焼かれる。シーンに残すのはこのコンポーネントが
        //   付いていることだけで、形は毎回ここで組み直す（`MainDemoSceneSetup` も Build を呼ばない）。
        //   ⚠ OnValidate から MeshFilter を触ると Unity が
        //   「SendMessage cannot be called during OnValidate」を出すので、そこでも組まない。
        private void Awake() => Build();

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
            _mesh = null;
        }

        /// <summary>曲面メッシュを組んで MeshFilter へ載せる。冪等。</summary>
        public void Build()
        {
            var filter = GetComponent<MeshFilter>();
            if (filter == null) return;

            int seg = Mathf.Clamp(segments, 2, 64);
            Mesh mesh = BuildMesh(seg, bulge);
            if (_mesh != null)
            {
                if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
            }
            _mesh = mesh;
            filter.sharedMesh = mesh;
        }

        /// <summary>
        /// 曲面メッシュを組む（純関数。テストが形を確かめられるように分けてある）。
        ///
        /// 膨らみは <b>双放物面</b> <c>(1 - 4x²)(1 - 4y²)</c> — 中央で最大、**4 辺すべてで 0**。
        /// 球面だと角が最も奥へ引っ込み、スクリーンの隅の位置が変わってしまう。
        /// </summary>
        public static Mesh BuildMesh(int segments, float bulge)
        {
            int seg = Mathf.Clamp(segments, 2, 64);
            int n = seg + 1;
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)seg;
                    float v = y / (float)seg;
                    float px = u - 0.5f;
                    float py = v - 0.5f;
                    float f = (1f - 4f * px * px) * (1f - 4f * py * py);
                    // Unity の Quad は法線 -Z。体験者はそちら側に居るので、膨らみも -Z へ。
                    verts[y * n + x] = new Vector3(px, py, -bulge * f);
                    uvs[y * n + x] = new Vector2(u, v);
                }
            }

            var tris = new int[seg * seg * 6];
            int t = 0;
            for (int y = 0; y < seg; y++)
            {
                for (int x = 0; x < seg; x++)
                {
                    int i = y * n + x;
                    // 巻き順は Unity の Quad primitive と同じ（法線が -Z を向く側）。
                    tris[t++] = i;
                    tris[t++] = i + n + 1;
                    tris[t++] = i + 1;
                    tris[t++] = i;
                    tris[t++] = i + n;
                    tris[t++] = i + n + 1;
                }
            }

            var mesh = new Mesh { name = "CrtScreen" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
