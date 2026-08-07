#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// **導入演出の合図を出す面。スタッフが被っているときだけ出る。**
    ///
    /// ⚠ **2026-08-07 に読み手が変わった。** それまでは体験者に出していたが、機器の言葉が
    /// ホラー体験の入口に混ざって世界観を壊していた（ユーザー指摘「体験者が被っているときに
    /// 表示する文字を消して。スタッフの時は表示していい」）。いまは
    /// <see cref="StatusHud.StaffViewing"/>（右 B の表示 or 位置合わせ作業中 ＝ コントローラを
    /// 持っている人にしか起こせない）が立っている間だけ出す。体験者の視界には 1 文字も出ない。
    ///
    /// **体験者への合図は口頭に移った。** 段 5 の「右手をあげてください」は 3 周目の反転の伏線
    /// （画面の中の自分は上げるが、3 周目の背景は 1 周目の録画なので上がらない）で、体験の核心に
    /// 効く唯一の指示だった。HMD を被せる前にスタッフが伝える運用にする。
    ///
    /// 面として残してあるのは、現地のリハ・切り分けでスタッフが「いま何を待っているのか」を
    /// 読めるようにするため（段 0 の開始条件・段 5 の合図が出る瞬間が目で分かる）。
    ///
    /// ⚠ **覆い（<see cref="IntroVeil"/>）より後に描く。** 覆いは Passthrough Windows 方式で
    /// Queue 5000・`Blend Zero SrcAlpha`（結果 rgb = srcAlpha × 背景）を全画面に掛ける。段 2 / 段 3 は
    /// alpha が 0 なので、前に描いた文字は rgb ごと 0 に潰れて 1 文字も見えない
    /// （`IntroStructureWire` の線がまさにそれで消えていた）。
    ///
    /// ⚠ 文言に新しい漢字・記号を足したら `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` を
    /// 再実行する（静的ベイクなので忘れると実機で豆腐になる）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IntroPrompt : MonoBehaviour
    {
        [Tooltip("合図の供給元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private IntroDirector? director;

        [Tooltip("スタッフが被っているかの判定元。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private StatusHud? statusHud;

        [Tooltip("頭からの距離 (m)。本編のスクリーンより手前に置く。")]
        [SerializeField] private float distance = 1.5f;

        [Tooltip("視線中心からの下げ角 (deg)。正面に重ねると実物と枠の観察を邪魔する。")]
        [SerializeField] private float dropDeg = 16f;

        [Tooltip("文字の大きさ（ワールド m）。1.5m 先で読める最小に寄せてある。")]
        [SerializeField] private float fontSize = 0.075f;

        [Tooltip("出入りのフェード秒。ぱっと出ると実物の観察から注意を奪いすぎる。")]
        [SerializeField] private float fadeSec = 0.35f;

        /// <summary>覆いより後に描くための Queue。IntroVeil の 4900 と対で管理する。
        ///
        /// ⚠ **5000 を超えてはいけない**（2026-07-31 実害）。URP の透明パスが描くのは
        /// <c>RenderQueueRange.transparent</c> = [2501, 5000] だけなので、旧値 5100 では
        /// **この指示テキストが一度も描画されていなかった**。段 5 の「右手を上げてみてください」は
        /// 言葉でしか伝えないので、出ないと 3 周目の反転の伏線が丸ごと成立しない。</summary>
        private const int RenderQueue = 5000;

        private TMP_Text? _text;
        private string _shown = string.Empty;
        private float _alpha;

        private void Awake()
        {
            if (director == null) director = GetComponent<IntroDirector>();
            if (director == null) director = FindObjectOfType<IntroDirector>();
            Build();
        }

        private void Build()
        {
            var go = new GameObject("Label");
            go.transform.SetParent(transform, worldPositionStays: false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = fontSize;
            tmp.enableWordWrapping = true;
            tmp.richText = false;
            tmp.color = Color.white;
            var jp = JapaneseHudFont.TryGet();
            if (jp != null) tmp.font = jp;

            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(1.1f, 0.3f);
            // 頭の正面やや下。距離は本編のスクリーン（2.0m）より手前に置いて、枠と重なっても
            // 文字が後ろへ回り込まないようにする（深度ではなく Queue で解決しているが、
            // 物理的にも手前にあった方が破綻しにくい）。
            float rad = dropDeg * Mathf.Deg2Rad;
            go.transform.localPosition = new Vector3(0f, -Mathf.Sin(rad) * distance, Mathf.Cos(rad) * distance);
            go.transform.localRotation = Quaternion.Euler(dropDeg, 0f, 0f);

            // 覆いに潰されないように、覆いより後に描く。fontMaterial の getter が
            // インスタンスを作るので、共有マテリアルを汚さない。
            tmp.fontMaterial.renderQueue = RenderQueue;
            _text = tmp;
            SetAlpha(0f);
        }

        /// <summary>
        /// スタッフが被っているか。<b>解決できない環境では false ＝ 出さない側へ倒す。</b>
        /// 判定できないときに出す設計だと、StatusHud を持たないシーン・プレビューで
        /// 体験者向けの文字が復活する（世界観を壊す側の失敗を既定にしない）。
        /// </summary>
        private bool StaffViewing()
        {
            if (statusHud == null) statusHud = FindObjectOfType<StatusHud>();
            if (statusHud == null) return false;
            // ⚠ 位置合わせ中は譲る。StatusHud が登録ガイダンスを 1.6m に強制表示していて、
            //   この面は 1.5m ＝ 10cm 手前で重なるので、出すと作業中の文字を覆い隠す。
            //   導入の合図は「体験者に見せる」ためのもので、登録作業中に読む相手は居ない。
            if (statusHud.RegistrationActive) return false;
            return statusHud.StaffViewing;
        }

        private void LateUpdate()
        {
            if (_text == null) return;
            string want = StaffViewing() && director != null
                ? (director.PromptText ?? string.Empty)
                : string.Empty;
            if (want != _shown)
            {
                // 文言が変わる瞬間は一度消してから出す（読んでいる途中で差し替わると読み直しになる）。
                if (!string.IsNullOrEmpty(want)) { _text.text = want; _shown = want; }
                else _shown = string.Empty;
            }

            float target = string.IsNullOrEmpty(_shown) ? 0f : 1f;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.05f, fadeSec);
            _alpha = Mathf.MoveTowards(_alpha, target, step);
            SetAlpha(_alpha);
        }

        private void SetAlpha(float a)
        {
            if (_text == null) return;
            _text.alpha = a;
            // 完全に消えている間は描画そのものを止める（全画面の面ではないが、
            // 本編中ずっと 0 の文字を描く理由が無い）。
            if (_text.gameObject.activeSelf != (a > 0.002f)) _text.gameObject.SetActive(a > 0.002f);
        }
    }
}
