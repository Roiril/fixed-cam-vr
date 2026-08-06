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
    /// - **導入の中止**（トラッキング原点がずれた）: ずれた世界を見せたまま歩かせない。こちらは
    ///   <see cref="ShowRunDirector.BlackoutMessage"/> の 1 行を黒の上に出す — 黒だけだと体験者は
    ///   終わったと誤解して HMD を外し、スタッフが直す前に立ち去る
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

        /// <summary>
        /// 黒の上のメッセージの文字高（Canvas units）。Canvas は <see cref="worldSize"/> m を
        /// 1000 倍の units で持ち scale 0.001 なので、1 unit = 1mm。距離 0.3m で 14 units ≒ 見かけ 2.7 度。
        /// const にしてあるのは、既存シーンに焼かれた SerializeField が 0 で読まれて文字が消える罠を避けるため。
        /// </summary>
        private const float MessageFontSize = 14f;

        /// <summary>メッセージが完全に見えるまでの時間 (秒)。黒より少し遅らせて浮かび上がらせる。</summary>
        private const float MessageFadeSec = 0.35f;

        private ShowRunDirector? _run;
        private Canvas? _canvas;
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
        }

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
            string msg = _run != null ? _run.BlackoutMessage : "";
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
            _label.color = new Color(1f, 1f, 1f, a);
            _label.enabled = a > 0.001f && !string.IsNullOrEmpty(text);
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
            rt.sizeDelta = new Vector2(worldSize.x * 1000f, worldSize.y * 1000f);
            rt.localScale = Vector3.one * 0.001f;

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

            // メッセージは Image より後の子＝同じ Canvas の中で必ず前に描かれる（深度に依存しない）。
            var labelGo = new GameObject("BlackoutMessage");
            labelGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0.5f, 0.5f);
            labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.anchoredPosition = Vector2.zero;
            labelRt.sizeDelta = new Vector2(worldSize.x * 900f, MessageFontSize * 3f);
            labelRt.localScale = Vector3.one;

            _label = labelGo.AddComponent<TextMeshProUGUI>();
            // ⚠ 既定フォント (LiberationSans SDF) に CJK グリフが無く、実機で 1 文字残らず豆腐になる。
            // 文言を足したら `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` を再実行すること。
            var jp = JapaneseHudFont.TryGet();
            if (jp != null) _label.font = jp;
            _label.fontSize = MessageFontSize;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color = new Color(1f, 1f, 1f, 0f);
            _label.raycastTarget = false;
            _label.enabled = false;
        }
    }
}
