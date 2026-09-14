#nullable enable

namespace FixedCamVr.Input
{
    /// <summary>
    /// <b>スタッフのステータス表示（右 B）が、いま画に出ているか</b>を数える純ロジック。
    /// UnityEngine / OVRInput へ一切依存せず、押している / 離したと経過時間だけを
    /// <see cref="Tick"/> で受ける。
    ///
    /// ⚠⚠ <b>トグル（ラッチ）ではない。</b> 2026-09-14 に「押しているあいだだけ出す」へ作り替えた。
    /// ラッチが残る限り<b>開いたまま体験者へ渡る</b>経路が構造的に消えない —
    /// 自然な手順（A 2 秒でリセット → B でカメラの○×を確認 → 被せる）だと、
    /// 消す縁（ラン開始）が B より先に来るので、導入から終幕までずっと出たままになる。
    /// 机の上の HMD で押した場合・本編中に誤って押した場合も同じ。
    /// 押下中の表示なら、誤押しの害は<b>押していた長さに有界</b>で、しかも秒で数えられる。
    ///
    /// ⚠ <b>押した瞬間から出す</b>（不表示の頭を作らない）。出るまでの間を置くと、
    /// スタッフには「B が効いていない」としか読めない（ちらつき対策は
    /// <see cref="FadeInSec"/> の立ち上がりが担う）。
    ///
    /// ⚠ <b>離してもすぐには消さない</b>（<see cref="ReleaseGraceSec"/>）。指がわずかに浮いた
    /// 1 フレームで面が消えると、読んでいる最中に明滅する。
    /// </summary>
    public sealed class StatusViewLogic
    {
        /// <summary>押してから完全に見えるまで (秒)。</summary>
        public const float FadeInSec = 0.15f;

        /// <summary>離してから消え切るまでの猶予 (秒)。この間は <see cref="Visible"/> のまま。</summary>
        public const float ReleaseGraceSec = 0.3f;

        private bool _visible;
        private float _alpha;
        private float _graceLeft;   // 離してからの残り (秒)。> 0 のあいだ消えない
        private float _fadeOutFrom; // 離した瞬間の不透明度（そこから線形に 0 へ）
        private bool _justShown;

        /// <summary>いま出ているか（押下中 ＋ 離してからの猶予中）。</summary>
        public bool Visible => _visible;

        /// <summary>いま実際に書くべき不透明度 [0,1]。</summary>
        public float Alpha01 => _alpha;

        /// <summary>
        /// 直前の <see cref="Tick"/> で「見えない → 見える」になったか。
        /// <b>その 1 回だけ true</b>（振動を 1 粒鳴らす縁）。
        /// ⚠ 猶予中に押し直したときは出ない — 面は消えていないので、出したら鳴りっぱなしになる。
        /// </summary>
        public bool JustShown => _justShown;

        /// <summary>全部落とす（体験者の交代・面の再配置など）。</summary>
        public void Reset()
        {
            _visible = false;
            _alpha = 0f;
            _graceLeft = 0f;
            _fadeOutFrom = 0f;
            _justShown = false;
        }

        /// <summary>
        /// 1 フレーム進める。
        /// </summary>
        /// <param name="held">右 B を押しているか（Normal のときだけ true が来る）。</param>
        /// <param name="dt">このフレームの経過時間 (秒)。負は 0 として扱う。</param>
        public void Tick(bool held, float dt)
        {
            float step = dt < 0f ? 0f : dt;
            _justShown = false;

            if (held)
            {
                if (!_visible)
                {
                    _visible = true;
                    _justShown = true;
                }
                _graceLeft = 0f;
                // 押している間は 1 へ向かって立ち上がる（0.15 秒で到達）。
                _alpha += FadeInSec <= 0f ? 1f : step / FadeInSec;
                if (_alpha > 1f) _alpha = 1f;
                return;
            }

            if (!_visible)
            {
                _alpha = 0f;
                return;
            }

            // 離した最初のフレームで猶予を張る（そこからの不透明度を覚えて線形に落とす）。
            if (_graceLeft <= 0f)
            {
                _graceLeft = ReleaseGraceSec;
                _fadeOutFrom = _alpha;
            }

            _graceLeft -= step;
            if (_graceLeft <= 0f)
            {
                _graceLeft = 0f;
                _visible = false;
                _alpha = 0f;
                return;
            }

            _alpha = ReleaseGraceSec <= 0f ? 0f : _fadeOutFrom * (_graceLeft / ReleaseGraceSec);
        }
    }
}
