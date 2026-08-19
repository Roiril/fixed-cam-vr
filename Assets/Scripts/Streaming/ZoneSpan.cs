#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>体験者がいまの区間をどこまで来たか</b>（2026-08-19・<c>canon/LEDGER.md</c> 0093）。
    ///
    /// 中身を作るのは <c>FixedCamVr.Tracking</c> 側（ゾーンの箱を持っているのはあちら）で、
    /// <see cref="ShowControlClient.ZoneSpanProvider"/> に注入されて渡ってくる
    /// —— 頭の course 座標・ゾーンラベルと同じ経路（Streaming → Tracking の参照を作らないため）。
    ///
    /// ⚠ <b><see cref="valid"/> が false のときは位置で測らない。</b> 未登録・layout 不在・
    /// 区間が短すぎる（入った所が既に奥の端）はどれもここへ落ちる。演出はカットの終わりに任せる
    /// ＝ 位置が使えない現場でも従来どおり動く。
    /// </summary>
    public readonly struct ZoneSpan
    {
        /// <summary>位置で測れるか。false なら他の値は読まない。</summary>
        public readonly bool valid;

        /// <summary>いまの区間のカメラ index。<b>変わったら区間が切り替わった</b>。</summary>
        public readonly int camera;

        /// <summary>
        /// この区間へ入ってからの通し番号。同じカメラの区間へ<b>もう一周して戻ってきた</b>ことは
        /// <see cref="camera"/> では分からないので、数えを別に持つ。
        /// </summary>
        public readonly int visit;

        /// <summary>進み 0..1（0 = 入った所 / 0.5 = 入った所と奥の端の中間 / 1 = 奥の端）。</summary>
        public readonly float progress01;

        public ZoneSpan(bool valid, int camera, int visit, float progress01)
        {
            this.valid = valid;
            this.camera = camera;
            this.visit = visit;
            this.progress01 = progress01;
        }
    }
}
