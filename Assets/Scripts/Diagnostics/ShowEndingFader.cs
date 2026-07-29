#nullable enable
using FixedCamVr.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 体験の終わりを黒で閉じる。企画書 3 章の「体験全体は導入を含め 3 分以内」を成立させるには、
    /// <b>終わったことが体験者に分かる</b>必要がある。<see cref="StartupFader"/> の逆再生に相当するが、
    /// あちらは解除後に自分を Destroy するので再利用できない（別コンポーネントとして持つ）。
    ///
    /// 相の判定は持たない。<see cref="ShowRunDirector"/> の <c>PhaseChanged</c> を購読するだけ
    /// （Diagnostics → Streaming の依存方向は既存どおり）。
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

        private ShowRunDirector? _run;
        private Canvas? _canvas;
        private Image? _image;
        private float _alpha;
        private bool _fadingIn;
        private bool _subscribed;

        private void Awake() => BuildCanvas();

        private void OnEnable() => TrySubscribe();

        // ShowRunDirector は ShowControlClient が実行時に自動生成することがあるので、
        // OnEnable の時点でまだ居ないことがある。居るまで毎フレーム試す。
        private void TrySubscribe()
        {
            if (_subscribed) return;
            Resolve();
            if (_run == null) return;
            _run.PhaseChanged += OnPhaseChanged;
            _subscribed = true;
            // 現在の相へ同期（終了状態で再有効化されても黒が復元される）。
            _fadingIn = _run.Phase == ShowPhase.Finished;
            if (!_fadingIn) SetAlpha(0f);
        }

        private void OnDisable()
        {
            if (_run != null && _subscribed) _run.PhaseChanged -= OnPhaseChanged;
            _subscribed = false;
            SetAlpha(0f);
        }

        private void Resolve()
        {
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            _fadingIn = phase == ShowPhase.Finished;
            // 次のランが始まったら即座に明ける（黒からゆっくり戻すと「まだ終わっていない」に見える）。
            if (!_fadingIn) SetAlpha(0f);
        }

        private void Update()
        {
            TrySubscribe();

            if (!_fadingIn)
            {
                if (_alpha > 0f) SetAlpha(0f);
                return;
            }
            float fadeSec = _run != null ? _run.EndFadeSec : ShowRunDefaults.EndFadeSec;
            float step = fadeSec > 0f ? Time.unscaledDeltaTime / fadeSec : 1f;
            SetAlpha(Mathf.Min(1f, _alpha + step));
        }

        private void SetAlpha(float a)
        {
            _alpha = a;
            if (_image != null) _image.color = new Color(fadeColor.r, fadeColor.g, fadeColor.b, a);
            if (_canvas != null) _canvas.enabled = a > 0.001f;
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
        }
    }
}
