#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>終幕の報告。</b> 装置が力尽きて画が消えたあと、黒の中に浮かぶ 4 行
    /// （<c>canon/LEDGER.md</c> 0048）。文言は <see cref="OutroReportText"/> が持ち、
    /// 出す / 消すの判断は <see cref="OutroDirector.ReportAlpha"/> だけを読む。
    ///
    /// <b>体験前の注意書き（<see cref="TitleNotice"/>）と対になる面。</b> 置き方・大きさ・
    /// 深度の逃がし方はあちらと同じで、違うのは出る段だけ（あちらは真っ暗な待ち、こちらは終幕）。
    ///
    /// ⚠ <b>「体験者の視界に文字を出さない」（rules/show-design.md）の対象外。</b>
    /// 体験そのものは既に終わっていて、この 4 行が終わったことを伝える唯一の手段
    /// （黒だけだと「まだ何か起きるのか」に見え、HMD を外してよいことが伝わらない）。
    ///
    /// ⚠⚠ <b>深度で弾かれる面。</b> 本編のスクリーン（2.0m・不透明・ZWrite On）より奥（2.6m）に
    /// 立つので、TMP の既定シェーダ（<c>ZTest LEqual</c>）のままだと <b>1 文字も出ない</b>
    /// （2026-08-13 に注意書きが丸ごと消えていた実害・<c>canon/LEDGER.md</c> 0027）。
    /// Overlay 版（<c>ZTest Always</c>）へ差し替える。
    ///
    /// ⚠ <b>失敗したら黙って出さない側へ倒す。</b> 日本語フォントが解決できない・実体を組めない
    /// ときは 1 文字も出さない（豆腐が 4 行並ぶ方が悪い）。組めたかは
    /// <see cref="IsBuilt"/> がテレメトリへ出す — <b>組めていないことに気づく口がそこしかない</b>。
    ///
    /// ⚠ 文言に新しい漢字・記号を足したら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutroReport : MonoBehaviour
    {
        [Tooltip("終幕の実行体。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private OutroDirector? outro;

        [Tooltip("報告した数の供給元。null ならシーンから探す。居なければ 0 として出す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("頭からの距離 (m)。体験前の注意書き（TitleNotice.distanceM）と同じ所に立てる。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.6f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。注意書きと同じ据わりにする。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 2.0f;

        /// <summary>タイトルの黒・題字より後に描く Queue。<b>5000 を超えない</b>（URP の透明パスは [2501, 5000]）。</summary>
        private const int RenderQueue = 5000;

        /// <summary>版の中の字の大きさ。<b>倍率は <see cref="TextScale"/> が transform で掛ける。</b></summary>
        private const float FontSize = 0.07f;

        /// <summary>文字の並ぶ幅 (m)。注意書き（<see cref="TitleNotice"/>）と同じ枠。</summary>
        private const float TextWidthM = 1.70f;

        /// <summary>文字の並ぶ高さ (m)。</summary>
        private const float TextHeightM = 1.00f;

        /// <summary>
        /// 文字の拡大率。<b>距離から逆算する</b>（<see cref="HmdTextStyle"/> が唯一の正）。
        /// 手で持っていた 8.5 倍は 2026-08-15 に捨てた — 経緯は <see cref="TitleNotice"/> の同名。
        /// </summary>
        private float TextScale =>
            HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, Mathf.Max(distanceM, 0.5f), FontSize);

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。<b>Always Included に入っている。</b></summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";

        /// <summary>供給元が見つからないときの探し直しの間隔 (s)。毎フレーム探すと只では済まない。</summary>
        private const float ResolveRetrySec = 1f;

        private TMP_Text? _text;
        private float _alpha;
        private float _resolveWait;
        private int _shownCount = -1;

        /// <summary>実体（TMP）を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>いま実際に書いている不透明度（「段が進んだ」ではなく「画に出た」の側）。</summary>
        public float AppliedAlpha => _text != null ? _alpha : -1f;

        /// <summary>いま面に出している文字（テスト・診断用）。</summary>
        public string CurrentBody => _text != null ? _text.text : "";

        private void Awake()
        {
            ResolveRefs();
            Build();
            SetAlpha(0f);
        }

        private void OnDisable()
        {
            _alpha = 0f;
            SetAlpha(0f);
        }

        private void ResolveRefs()
        {
            if (outro == null) outro = FindObjectOfType<OutroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
        }

        private void Build()
        {
            // ⚠ 日本語が出せないなら何も出さない。豆腐（□）が 4 行並ぶ方が、無いより悪い。
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[OutroReport] 日本語フォントを解決できないので報告は出しません");
                return;
            }

            GameObject? go = null;
            try
            {
                go = new GameObject("Label");
                go.transform.SetParent(transform, worldPositionStays: false);
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.font = jp;
                tmp.text = OutroReportText.Compose(0);
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = FontSize;
                tmp.enableWordWrapping = true;
                tmp.richText = false;
                // 注意書きと同じ抑えた白。純白だと黒の中で浮いて掲示物に見える。
                tmp.color = HmdTextStyle.Ink;

                var rt = (RectTransform)go.transform;
                // ⚠ 折り返し幅も scale で割る（固定値にすると字を直したとき枠だけ取り残される）。
                float scale = TextScale;
                rt.sizeDelta = new Vector2(TextWidthM / scale, TextHeightM / scale);
                go.transform.localScale = Vector3.one * scale;

                float rad = pitchOffsetDeg * Mathf.Deg2Rad;
                float d = Mathf.Max(distanceM, 0.5f);
                go.transform.localPosition = new Vector3(0f, -Mathf.Sin(rad) * d, Mathf.Cos(rad) * d);
                go.transform.localRotation = Quaternion.identity;

                UseOverlayShader(tmp);
                tmp.fontMaterial.renderQueue = RenderQueue;
                _text = tmp;
                _shownCount = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[OutroReport] 実体を組めません — 報告は出しません: {e.Message}");
                if (go != null) Destroy(go);
                _text = null;
            }
        }

        /// <summary>
        /// TMP の <b>Overlay 版</b>（<c>ZTest Always</c>）へ差し替える。既定の
        /// <c>TextMeshPro/Distance Field</c> は ZTest をグローバル（<c>unity_GUIZTestMode</c>）で引くので
        /// マテリアルからは上書きできない。見つからないときは<b>差し替えずに続ける</b>
        /// （深度に負けて見えないかもしれないが、マテリアルを壊して字が化けるよりはよい）。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[OutroReport] {OverlayShaderName} が見つかりません。" +
                                 "報告が本編スクリーンの深度に隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        private void LateUpdate()
        {
            if (_text == null) return;

            if (outro == null || showControl == null)
            {
                _resolveWait += Time.unscaledDeltaTime;
                if (_resolveWait >= ResolveRetrySec)
                {
                    _resolveWait = 0f;
                    ResolveRefs();
                }
            }

            // 出す / 消すの判断は持たない（終幕の段がそのまま不透明度になる）。
            float target = outro != null ? Mathf.Clamp01(outro.ReportAlpha) : 0f;

            // ⚠ 文言の更新は**出る直前まで**。出ている最中に数が変わると、体験者の目の前で
            //    数字が書き換わる（報告は体験の終わりに確定した値であってライブの表示ではない）。
            if (target <= 0f)
            {
                int n = showControl != null ? showControl.VisitorMarkCount : 0;
                if (n != _shownCount)
                {
                    _shownCount = n;
                    _text.SetText(OutroReportText.Compose(n));
                }
            }

            _alpha = target;
            SetAlpha(_alpha);
        }

        private void SetAlpha(float a)
        {
            if (_text == null) return;
            _text.alpha = a;
            // 完全に消えている間は描画そのものを止める（体験中ずっと 0 の文字を描く理由が無い）。
            if (_text.gameObject.activeSelf != (a > 0.002f)) _text.gameObject.SetActive(a > 0.002f);
        }
    }
}
