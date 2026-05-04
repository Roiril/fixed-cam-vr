#nullable enable
using FixedCamVr.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// Play 開始直後の OVR 初期化 / 砂時計表示 / MJPEG 接続待ち / 未確定フレームを
    /// 黒フェードで覆い隠す起動フェーダ。
    ///
    /// 配置: CenterEyeAnchor の子に置く。<see cref="Awake"/> 時点で全視界を覆う黒 Canvas を生成し、
    /// 解除条件を満たしたら <see cref="fadeDuration"/> 秒で alpha を 1→0 にしてから自身を破棄する。
    ///
    /// 解除条件 = (経過 >= <see cref="minHoldSec"/>) かつ
    ///            (<see cref="registry"/> の任意ストリームが接続済み or 経過 >= <see cref="maxWaitSec"/>)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartupFader : MonoBehaviour
    {
        [Tooltip("接続待ちの参照先。null でも minHold/maxWait の単純タイマで動く。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("カメラからの距離 (m)。near clip より大きい値にする。")]
        [SerializeField, Min(0.05f)] private float distance = 0.3f;

        [Tooltip("Canvas のローカルサイズ (m 換算)。FOV 全体を覆うサイズに。")]
        [SerializeField] private Vector2 worldSize = new(2.0f, 2.0f);

        [Tooltip("最低保持時間 (秒)。早すぎる解除での Pop-in を防ぐ。")]
        [SerializeField, Min(0f)] private float minHoldSec = 0.5f;

        [Tooltip("接続を待つ最大時間 (秒)。スマホがスリープ等で繋がらない時のフォールバック。")]
        [SerializeField, Min(0.1f)] private float maxWaitSec = 4f;

        [Tooltip("フェードアウト時間 (秒)。")]
        [SerializeField, Min(0f)] private float fadeDuration = 0.5f;

        [Tooltip("黒の代わりに任意色を使う場合（暗灰など）。alpha は強制的に 1 から始める。")]
        [SerializeField] private Color fadeColor = Color.black;

        [Tooltip("Canvas の sortingOrder。HUD より大きい値で上に被せる。")]
        [SerializeField] private int sortingOrder = 10000;

        private Canvas? _canvas;
        private Image? _image;
        private float _elapsed;
        private float _fadeOutAccum;
        private enum Phase { Holding, FadingOut, Done }
        private Phase _phase = Phase.Holding;

        private void Awake()
        {
            BuildCanvas();
        }

        private void BuildCanvas()
        {
            // Canvas（World-space, head-lock のため CenterEyeAnchor 子オブジェクトとして作る前提）
            var canvasGo = new GameObject("StartupFadeCanvas");
            canvasGo.transform.SetParent(transform, worldPositionStays: false);
            canvasGo.transform.localPosition = new Vector3(0f, 0f, distance);
            canvasGo.transform.localRotation = Quaternion.identity;

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = sortingOrder;

            var rt = (RectTransform)canvasGo.transform;
            // distance 0.3m で FOV 全体を覆うため worldSize 2m × 2m を使う想定。
            // Canvas の RectTransform は pixel 換算なので 1px = 1mm 相当でスケールを調整する。
            rt.sizeDelta = new Vector2(worldSize.x * 1000f, worldSize.y * 1000f);
            rt.localScale = Vector3.one * 0.001f;

            // 黒 Image
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
            _image.color = new Color(fadeColor.r, fadeColor.g, fadeColor.b, 1f);
            _image.raycastTarget = false;
        }

        private void Update()
        {
            _elapsed += Time.unscaledDeltaTime;

            if (_phase == Phase.Holding)
            {
                if (ShouldRelease())
                {
                    _phase = Phase.FadingOut;
                    _fadeOutAccum = 0f;
                }
                return;
            }

            if (_phase == Phase.FadingOut)
            {
                _fadeOutAccum += Time.unscaledDeltaTime;
                float t = fadeDuration <= 0f ? 1f : Mathf.Clamp01(_fadeOutAccum / fadeDuration);
                if (_image != null)
                {
                    var c = _image.color;
                    c.a = 1f - t;
                    _image.color = c;
                }

                if (t >= 1f)
                {
                    _phase = Phase.Done;
                    Destroy(gameObject);
                }
            }
        }

        private bool ShouldRelease()
        {
            if (_elapsed < minHoldSec) return false;
            if (_elapsed >= maxWaitSec) return true;
            if (registry == null) return _elapsed >= minHoldSec;

            for (int i = 0; i < registry.Count; i++)
            {
                var s = registry.Get(i);
                if (s != null && s.IsConnected) return true;
            }
            return false;
        }
    }
}
