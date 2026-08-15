#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 体験の視界を黒で閉じる。閉じる理由は 2 つあり、どちらを取るかは
    /// <see cref="ShowRunDirector.ShouldBlackout"/> が判断する（この面は描くだけで判断を持たない）。
    ///
    /// - **終了**: 企画書 3 章の「体験全体は導入を含め 3 分以内」を成立させるには、終わったことが
    ///   体験者に分かる必要がある。文字は出さない（黒だけで伝わる）
    /// - **導入の中止**（トラッキング原点がずれた）: ずれた世界を見せたまま歩かせない。
    ///   <see cref="ShowRunDirector.BlackoutMessage"/> の 1 行は<b>スタッフが被っているときだけ</b>
    ///   黒の上に出す（<see cref="StatusHud.StaffViewing"/>）
    ///
    /// ⚠ **2026-08-07 に「少しお待ちください」を体験者へ出すのをやめた**（ユーザー指摘・世界観）。
    /// 体験者から見えるのは黒だけになる。黒だけだと終わったと誤解して HMD を外しうるので、
    /// **中止したら横のスタッフが声を掛ける運用**にする（中止はスタッフ側では StatusHud の
    /// 異常 1 件として「何が起きたか / どう直すか」が出るので、気づく経路は残っている）。
    ///
    /// <see cref="StartupFader"/> の逆再生に相当するが、あちらは解除後に自分を Destroy するので
    /// 再利用できない（別コンポーネントとして持つ）。
    ///
    /// ⚠ **文字はこの Canvas の中に持つ。** <see cref="IntroPrompt"/>（視線前方 1.5m）に置くと
    /// 黒（0.3m）が手前に来て隠れる。「黒で閉じる」と「待ってよいと伝える」は同じ面の仕事。
    ///
    /// ⚠ 相の変化イベントは購読しない。**中止は相の変化ではない**ので `PhaseChanged` では届かず、
    /// イベントとポーリングの 2 系統を持つと必ず片方を忘れる。毎フレーム ShouldBlackout を読む。
    ///
    /// 画面のカメラ切替は終了後も裏で回り続ける。見えなくなるのはこの黒のおかげで、
    /// 新しい凍結ラッチは足さない（解除されずに残る事故を作らないため）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowEndingFader : MonoBehaviour
    {
        [Tooltip("カメラからの距離 (m)。near clip より大きい値にする。")]
        [SerializeField, Min(0.05f)] private float distance = 0.3f;

        [Tooltip("Canvas のローカルサイズ (m 換算)。FOV 全体を覆うサイズに。")]
        [SerializeField] private Vector2 worldSize = new(2.0f, 2.0f);

        [Tooltip("Canvas の sortingOrder。起動フェードと同じ層に置く。")]
        [SerializeField] private int sortingOrder = 10000;

        [Tooltip("終了の黒（既定は真っ黒）。")]
        [SerializeField] private Color fadeColor = Color.black;

        /// <summary>Canvas の 1 unit あたりの世界サイズ (m)。<see cref="BuildCanvas"/> の localScale と対。</summary>
        private const float CanvasScale = 0.001f;

        /// <summary>
        /// 文字を置く距離 (m)。<b>黒（<see cref="distance"/> = 0.3m）とは別の面に置く。</b>
        ///
        /// ⚠ 黒が 0.3m なのは「視界を必ず覆い切る」ためで、<b>文字までそこに置く理由は無い</b>。
        /// 0.3m は輻輳の負担が大きく、両眼で読む文字を置く距離ではない。
        /// 隠れないのは深度ではなく<b>描画順</b>のおかげ（UI は深度を書かない）なので、
        /// 黒より後ろの sortingOrder に置けば、奥にあっても黒の上に出る。
        /// </summary>
        private const float MessageDistanceM = 1.5f;

        /// <summary>
        /// 黒の上のメッセージの文字高（Canvas units）。<b>距離から逆算する</b>
        /// （<see cref="HmdTextStyle"/> が唯一の正）。段は<b>注目</b> — 黒の中に 1 行だけ出て、
        /// 見落とすと「なぜ止まったか」に到達できないため。
        /// 旧値 14（＝ 0.3m で 2.67°）は面ごとに手で決めていた時代の名残。
        /// </summary>
        private float MessageFontSize =>
            HmdTextStyle.CanvasFontSize(HmdTextStyle.AlertDeg, MessageDistanceM, CanvasScale);

        /// <summary>メッセージが完全に見えるまでの時間 (秒)。黒より少し遅らせて浮かび上がらせる。</summary>
        private const float MessageFadeSec = 0.35f;

        private ShowRunDirector? _run;
        private StatusHud? _hud;
        private Canvas? _canvas;
        private Canvas? _labelCanvas;
        private Image? _image;
        private TextMeshProUGUI? _label;
        private float _alpha;
        private float _messageAlpha;

        private void Awake() => BuildCanvas();

        private void OnEnable()
        {
            Resolve();
            SetAlpha(_run != null && _run.ShouldBlackout ? _alpha : 0f);
        }

        private void OnDisable()
        {
            SetAlpha(0f);
            SetMessage("", 0f);
        }

        // ShowRunDirector は ShowControlClient が実行時に自動生成することがあるので、居るまで毎フレーム試す。
        private void Resolve()
        {
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_hud == null) _hud = FindObjectOfType<StatusHud>();
        }

        /// <summary>スタッフが被っているか。解決できないときは false ＝ 文字を出さない側へ倒す。</summary>
        // ⚠ 位置合わせ中は譲る（この面は 0.3m ＝ 全部の文字面のうち最も手前）。
        //   「原点がずれた → 中止 → その場で位置合わせ」は現地でいちばん自然な流れなので、
        //   譲らないと登録ガイダンスが黒の上の 1 行に隠される。
        private bool StaffViewing() => _hud != null && _hud.StaffViewing && !_hud.RegistrationActive;

        private void Update()
        {
            Resolve();
            bool closing = _run != null && _run.ShouldBlackout;

            if (!closing)
            {
                // 次のランが始まったら即座に明ける（黒からゆっくり戻すと「まだ終わっていない」に見える）。
                if (_alpha > 0f) SetAlpha(0f);
                if (_messageAlpha > 0f) SetMessage("", 0f);
                return;
            }

            float fadeSec = _run != null ? _run.BlackoutFadeSec : ShowRunDefaults.EndFadeSec;
            float step = fadeSec > 0f ? Time.unscaledDeltaTime / fadeSec : 1f;
            SetAlpha(Mathf.Min(1f, _alpha + step));

            // 文字は黒が乗り切ってから浮かべる（黒の途中で出すと本編の画に重なって読めない）。
            // ⚠ 体験者には出さない（黒だけ）。スタッフが見ているときだけ 1 行が浮かぶ。
            string msg = _run != null && StaffViewing() ? _run.BlackoutMessage : "";
            float target = string.IsNullOrEmpty(msg) || _alpha < 0.95f ? 0f : 1f;
            float mStep = Time.unscaledDeltaTime / MessageFadeSec;
            SetMessage(msg, target > _messageAlpha
                ? Mathf.Min(target, _messageAlpha + mStep)
                : Mathf.Max(target, _messageAlpha - mStep));
        }

        private void SetAlpha(float a)
        {
            _alpha = a;
            if (_image != null) _image.color = new Color(fadeColor.r, fadeColor.g, fadeColor.b, a);
            if (_canvas != null) _canvas.enabled = a > 0.001f;
        }

        private void SetMessage(string text, float a)
        {
            _messageAlpha = a;
            if (_label == null) return;
            if (_label.text != text) _label.text = text;
            // 出るのは「止まった理由」だけなので警告色（面の色は 2 色しか無い — HmdTextStyle）。
            Color c = HmdTextStyle.Alert;
            _label.color = new Color(c.r, c.g, c.b, a);
            bool on = a > 0.001f && !string.IsNullOrEmpty(text);
            _label.enabled = on;
            if (_labelCanvas != null) _labelCanvas.enabled = on;
        }

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("ShowEndingFadeCanvas");
            canvasGo.transform.SetParent(transform, worldPositionStays: false);
            canvasGo.transform.localPosition = new Vector3(0f, 0f, distance);
            canvasGo.transform.localRotation = Quaternion.identity;

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = sortingOrder;
            _canvas.enabled = false;

            var rt = (RectTransform)canvasGo.transform;
            rt.sizeDelta = new Vector2(worldSize.x / CanvasScale, worldSize.y / CanvasScale);
            rt.localScale = Vector3.one * CanvasScale;

            var imgGo = new GameObject("FadeImage");
            imgGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            var imgRt = imgGo.AddComponent<RectTransform>();
            imgRt.anchorMin = Vector2.zero;
            imgRt.anchorMax = Vector2.one;
            imgRt.anchoredPosition = Vector2.zero;
            imgRt.sizeDelta = Vector2.zero;
            imgRt.localScale = Vector3.one;
            imgRt.localPosition = Vector3.zero;

            _image = imgGo.AddComponent<Image>();
            _image.color = new Color(fadeColor.r, fadeColor.g, fadeColor.b, 0f);
            _image.raycastTarget = false;

            // メッセージは**別の Canvas**（黒より奥・sortingOrder は 1 つ上）。
            // ⚠ 黒と同じ面に置くと 0.3m で読ませることになる（輻輳の負担）。UI は深度を書かないので、
            //    奥に置いても sortingOrder が上なら黒の上に出る。
            var labelCanvasGo = new GameObject("BlackoutMessageCanvas");
            labelCanvasGo.transform.SetParent(transform, worldPositionStays: false);
            labelCanvasGo.transform.localPosition = new Vector3(0f, 0f, MessageDistanceM);
            labelCanvasGo.transform.localRotation = Quaternion.identity;
            _labelCanvas = labelCanvasGo.AddComponent<Canvas>();
            _labelCanvas.renderMode = RenderMode.WorldSpace;
            _labelCanvas.sortingOrder = sortingOrder + 1;
            _labelCanvas.enabled = false;
            var labelCanvasRt = (RectTransform)labelCanvasGo.transform;
            // 黒と同じ見かけの幅（距離に比例させる）＝ 文字が枠から出ない。
            float wUnits = worldSize.x * (MessageDistanceM / Mathf.Max(distance, 0.05f)) / CanvasScale;
            labelCanvasRt.sizeDelta = new Vector2(wUnits, wUnits * 0.25f);
            labelCanvasRt.localScale = Vector3.one * CanvasScale;

            var labelGo = new GameObject("BlackoutMessage");
            labelGo.transform.SetParent(labelCanvasGo.transform, worldPositionStays: false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0.5f, 0.5f);
            labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.anchoredPosition = Vector2.zero;
            float fs = MessageFontSize;
            labelRt.sizeDelta = new Vector2(wUnits * 0.9f, fs * 3f);
            labelRt.localScale = Vector3.one;

            _label = labelGo.AddComponent<TextMeshProUGUI>();
            // ⚠ 既定フォント (LiberationSans SDF) に CJK グリフが無く、実機で 1 文字残らず豆腐になる。
            // 文言を足したら `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` を再実行すること。
            var jp = JapaneseHudFont.TryGet();
            if (jp != null) _label.font = jp;
            _label.fontSize = fs;
            // 1 行だけを黒の中に掲げる面なので中央（HmdTextStyle の規約の例外側）。
            _label.alignment = TextAlignmentOptions.Center;
            _label.color = new Color(HmdTextStyle.Alert.r, HmdTextStyle.Alert.g, HmdTextStyle.Alert.b, 0f);
            _label.raycastTarget = false;
            _label.enabled = false;
        }
    }
}
