#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace MyCobotHandVr
{
    /// <summary>
    /// 頭追従のワールド空間 HUD（WebXR 版 scripts/hand.html の HUD と同じ思想）。
    /// - 状態パネル: 緑=右手追従中 / 黄=右手ロスト / 紫=サーバ応答なし
    /// - 5 本の bend バー（親指→小指）
    /// - 各指の生 curl 値 (rad) の数値表示（実機校正用）
    ///
    /// Canvas は実行時に自前で組み立て、hudAnchor（CenterEyeAnchor）に子付けして視線追従させる。
    /// Japanese グリフを避けるため表記は ASCII（フォント資産を持ち込まない）。
    /// </summary>
    public sealed class HandTeleopHud : MonoBehaviour
    {
        [SerializeField] private FingerCurlSender? sender;
        [Tooltip("HUD の親（視線追従させる CenterEyeAnchor）。未設定なら自身に付く")]
        [SerializeField] private Transform? hudAnchor;
        [Tooltip("目の前 何 m に置くか")]
        [SerializeField] private float distance = 0.7f;
        [SerializeField] private float verticalOffset = -0.18f;

        private static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };

        private static readonly Color Green = new(0.24f, 0.86f, 0.36f);
        private static readonly Color Yellow = new(0.95f, 0.80f, 0.22f);
        private static readonly Color Purple = new(0.70f, 0.34f, 0.92f);

        private Text? _status;
        private readonly Image[] _fill = new Image[FingerCurlSender.FingerCount];
        private readonly Text[] _curl = new Text[FingerCurlSender.FingerCount];
        private Font? _font;

        private void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 16);
            BuildHud();
        }

        private void Update()
        {
            if (sender == null || _status == null) return;

            if (sender.ServerError)
            {
                _status.text = "SERVER: NO RESPONSE";
                _status.color = Purple;
            }
            else if (sender.RightTracked)
            {
                _status.text = "R HAND: TRACKING";
                _status.color = Green;
            }
            else
            {
                _status.text = "R HAND: LOST";
                _status.color = Yellow;
            }

            var bends = sender.LastBends;
            var curls = sender.LastCurls;
            for (int i = 0; i < FingerCurlSender.FingerCount; i++)
            {
                _fill[i].fillAmount = Mathf.Clamp01(bends[i]);
                float c = curls[i];
                _curl[i].text = float.IsNaN(c) ? "--" : c.ToString("F2");
            }
        }

        // ---- HUD 構築（ワールド空間 Canvas を 1m=1000px スケールで組む） ----
        private void BuildHud()
        {
            Transform parent = hudAnchor != null ? hudAnchor : transform;

            var canvasGo = new GameObject("HandTeleopHudCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var t = canvasGo.transform;
            t.SetParent(parent, false);
            t.localPosition = new Vector3(0f, verticalOffset, distance);
            t.localRotation = Quaternion.identity;

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            const float px = 0.0006f; // 1 canvas px = 0.6 mm → パネルは実寸 ~0.3m 幅
            var rt = (RectTransform)t;
            rt.sizeDelta = new Vector2(500f, 360f);
            rt.localScale = new Vector3(px, px, px);

            // 背景パネル
            var bg = AddImage(t, "BG");
            Stretch(bg.rectTransform);
            bg.color = new Color(0f, 0f, 0f, 0.55f);

            // 状態ラベル
            _status = AddText(t, "Status", 34, TextAnchor.MiddleCenter);
            Place(_status.rectTransform, 0f, 150f, 480f, 52f);
            _status.text = "R HAND: --";

            // 5 行: ラベル + バー + curl 値
            const float rowH = 46f;
            const float top = 96f;
            for (int i = 0; i < FingerCurlSender.FingerCount; i++)
            {
                float y = top - i * rowH;

                var label = AddText(t, $"Label{i}", 26, TextAnchor.MiddleLeft);
                Place(label.rectTransform, -170f, y, 130f, rowH - 8f);
                label.text = FingerNames[i];

                var barBg = AddImage(t, $"BarBg{i}");
                Place(barBg.rectTransform, 40f, y, 230f, 26f);
                barBg.color = new Color(1f, 1f, 1f, 0.15f);

                var fill = AddImage(t, $"Fill{i}");
                Place(fill.rectTransform, 40f, y, 230f, 26f);
                fill.color = new Color(0.30f, 0.75f, 1f, 0.95f);
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Horizontal;
                fill.fillOrigin = (int)Image.OriginHorizontal.Left;
                fill.fillAmount = 0f;
                _fill[i] = fill;

                var curl = AddText(t, $"Curl{i}", 24, TextAnchor.MiddleRight);
                Place(curl.rectTransform, 205f, y, 80f, rowH - 8f);
                curl.text = "--";
                _curl[i] = curl;
            }

            // 凡例（生 curl 値であることを明示）
            var legend = AddText(t, "Legend", 20, TextAnchor.MiddleCenter);
            Place(legend.rectTransform, 0f, top - FingerCurlSender.FingerCount * rowH - 6f, 480f, 30f);
            legend.color = new Color(1f, 1f, 1f, 0.6f);
            legend.text = "bar = bend 0..1   number = raw curl (rad)";
        }

        private Image AddImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            return go.GetComponent<Image>();
        }

        private Text AddText(Transform parent, string name, int size, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var txt = go.GetComponent<Text>();
            txt.font = _font;
            txt.fontSize = size;
            txt.alignment = anchor;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.color = Color.white;
            return txt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // 中心アンカー基準で (x,y) に w×h を配置。
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
        }
    }
}
