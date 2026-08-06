#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ソース映像の明るさを「全画面平均」と「粗い場所別」の両方で持つ純ロジック
    /// （UnityEngine 非依存・サンプル点を注入する）。
    ///
    /// <b>なぜ場所別が要るか</b>: CG 人形の光量を映像へ寄せるとき、基準が全画面平均だと
    /// 「黒いカーテンが画の大半を占め、人形は明るい床に立っている」という画で人形が暗くなりすぎる。
    /// 逆に明るい壁が大半なら、暗がりに立つ人形が明るく浮く。実測（2026-08-07・カメラ A/B/C の
    /// 無人プレート）で、人形が立つ場所の輝度は全画面平均の **26〜68%** しかなかった。
    ///
    /// 格子を粗く（<see cref="Tiles"/> 四方）保っているのは、これが「その辺りの明るさ」であって
    /// 人形の背後 1 点の色ではないから。細かくすると、人形の後ろをたまたま横切る明るい物で
    /// 光量が跳ね、フレームごとに人形の明るさが暴れる。
    /// </summary>
    public sealed class SourceLumaMap
    {
        /// <summary>場所別の格子の一辺。粗さそのものが仕様（上の理由）。</summary>
        public const int Tiles = 4;

        private readonly double[] _sum = new double[Tiles * Tiles];
        private readonly int[] _count = new int[Tiles * Tiles];
        private readonly float[] _tile = new float[Tiles * Tiles];
        private float _mean = -1f;
        private bool _resolved;

        /// <summary>全画面の平均輝度 0..1。まだ測れていなければ -1。</summary>
        public float Mean => _mean;

        /// <summary>1 回の走査を始める。</summary>
        public void BeginFrame()
        {
            Array.Clear(_sum, 0, _sum.Length);
            Array.Clear(_count, 0, _count.Length);
        }

        /// <summary>サンプルを 1 点足す（u,v はソース映像の 0..1 座標・v は下が 0）。</summary>
        public void Add(float u, float v, float luma)
        {
            int tx = TileIndex(u);
            int ty = TileIndex(v);
            int i = ty * Tiles + tx;
            _sum[i] += luma;
            _count[i]++;
        }

        /// <summary>走査を確定する。1 点も入らなかったタイルは全体平均で埋める。</summary>
        public void EndFrame()
        {
            double total = 0;
            int n = 0;
            for (int i = 0; i < _sum.Length; i++)
            {
                total += _sum[i];
                n += _count[i];
            }
            if (n == 0) return;   // 測れなかった。前回の値を残す（0 で塗ると人形が真っ黒になる）

            _mean = (float)(total / n);
            for (int i = 0; i < _tile.Length; i++)
                _tile[i] = _count[i] > 0 ? (float)(_sum[i] / _count[i]) : _mean;
            _resolved = true;
        }

        /// <summary>
        /// その場所あたりの明るさ 0..1（タイル中心のバイリニア補間）。まだ測れていなければ -1。
        /// タイル境界で人形の明るさが段になるのを避けるために補間する。
        /// </summary>
        public float Sample(float u, float v)
        {
            if (!_resolved) return -1f;
            float fx = Clamp01(u) * Tiles - 0.5f;
            float fy = Clamp01(v) * Tiles - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            float a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
            return (a * (1f - tx) + b * tx) * (1f - ty) + (c * (1f - tx) + d * tx) * ty;
        }

        private float At(int x, int y)
        {
            if (x < 0) x = 0; else if (x >= Tiles) x = Tiles - 1;
            if (y < 0) y = 0; else if (y >= Tiles) y = Tiles - 1;
            return _tile[y * Tiles + x];
        }

        private static int TileIndex(float t)
        {
            int i = (int)(Clamp01(t) * Tiles);
            return i >= Tiles ? Tiles - 1 : (i < 0 ? 0 : i);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
