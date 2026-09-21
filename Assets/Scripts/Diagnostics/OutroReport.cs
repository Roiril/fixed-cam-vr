#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>結果表示の開始時に数と結末を固定する。次の体験まで表示する。</summary>
    [DisallowMultipleComponent]
    public sealed class OutroReport : MonoBehaviour
    {
        [SerializeField] private OutroDirector? outro;
        [SerializeField] private ShowControlClient? showControl;
        [SerializeField] private TypeAudioCue? typeSfx;
        [SerializeField, Min(.5f)] private float distanceM = 2.6f;
        [SerializeField, Range(-20, 20)] private float pitchOffsetDeg = 2;
        public const float PanelWidth = 2.42f, PanelHeight = 1.78f;
        private readonly List<TMP_Text> _fields = new List<TMP_Text>();
        private readonly List<bool[]> _visible = new List<bool[]>();
        private readonly List<Material> _materials = new List<Material>();
        private TMP_Text? _text;
        private Transform? _card;
        private HeadYawFollow? _follow;
        private Material? _plate;
        private ShowRunDirector? _run;
        private OutroResultMusic? _music;
        private bool _presented;
        private float _elapsed, _resolveWait, _alpha;
        private int _lastTyped;
        private int _typedAtStart;
        private string _body = "";
        public bool IsBuilt => _text != null && _plate != null;
        public float AppliedAlpha => IsBuilt ? _alpha : -1;
        public string CurrentBody => _body;
        public int VisibleChars { get; private set; }
        public int ReportChars { get; private set; }
        public int TypedCount => typeSfx != null ? Mathf.Max(0, typeSfx.PlayedCount - _typedAtStart) : 0;
        public bool TypeSfxBuilt => typeSfx != null && typeSfx.HasClips;
        public int CapturedReports { get; private set; }
        public int CapturedTotal { get; private set; }
        public ShowEndingOutcome CapturedOutcome { get; private set; }
        public IReadOnlyList<TMP_Text> Fields => _fields;
        public bool MusicHasClip => _music != null && _music.HasClip;
        public bool MusicPlaying => _music != null && _music.IsPlaying;
        public float MusicVolume => _music != null ? _music.Volume : 0;
        public float MusicSeconds => _music != null ? _music.PlaybackSeconds : 0;

        private void Awake()
        {
            ResolveRefs();
            Build();
            Capture(0, 0, ShowEndingOutcome.Interrupted, ShowLanguage.Current);
            SetVisible(false);
        }
        private void ResolveRefs()
        {
            if (outro == null) outro = FindObjectOfType<OutroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (typeSfx == null) typeSfx = GetComponent<TypeAudioCue>();
            if (typeSfx == null) typeSfx = gameObject.AddComponent<TypeAudioCue>();
            if (_music == null) _music = GetComponent<OutroResultMusic>();
            if (_music == null) _music = gameObject.AddComponent<OutroResultMusic>();
        }
        private void Build()
        {
            if (_card != null) return;
            var font = Resources.Load<TMP_FontAsset>("Fonts/OutroReport SDF") ?? JapaneseHudFont.TryGet();
            var shader = Resources.Load<Shader>("Shaders/OutroReportPlate");
            if (font == null || shader == null)
            {
                Debug.LogError("[OutroReport] 結果画面のフォントまたは材質がありません");
                return;
            }
            _follow = HeadYawFollow.Attach(transform, "ReportYawFollow");
            _card = new GameObject("ObservationRecord").transform;
            _card.SetParent(_follow.transform, false);
            float rad = pitchOffsetDeg * Mathf.Deg2Rad;
            _card.localPosition = new Vector3(0, -Mathf.Sin(rad) * distanceM, Mathf.Cos(rad) * distanceM);
            var plateGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plateGo.name = "RecordPlate";
            plateGo.transform.SetParent(_card, false);
            plateGo.transform.localScale = new Vector3(PanelWidth, PanelHeight, 1);
            var collider = plateGo.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider);
            _plate = new Material(shader) { name = "Observation record", renderQueue = 4998 };
            plateGo.GetComponent<MeshRenderer>().sharedMaterial = _plate;
            AddField("Archive", font, .73f, .11f, HmdTextStyle.MinorDeg);
            AddField("Outcome", font, .54f, .18f, 2.8f);
            _text = AddField("Message", font, .28f, .22f, HmdTextStyle.MinorDeg);
            AddField("MeasureLabel", font, -.065f, .12f, HmdTextStyle.MinorDeg);
            AddField("Measure", font, -.19f, .24f, 3.8f);
            AddField("MeasureNote", font, -.50f, .13f, HmdTextStyle.MinorDeg);
            AddField("Exit", font, -.72f, .14f, HmdTextStyle.MinorDeg);
        }
        private TMP_Text AddField(string name, TMP_FontAsset font, float y, float h, float deg)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_card, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSize = .07f;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;
            tmp.richText = false;
            tmp.color = HmdTextStyle.Ink;
            float scale = HmdTextStyle.MeshScale(deg, distanceM, .07f);
            var rt = (RectTransform)go.transform;
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(2.04f / scale, h / scale);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = new Vector3(-.99f, y, -.005f);
            var mat = tmp.fontMaterial;
            mat.shader = Shader.Find("TextMeshPro/Distance Field Overlay");
            mat.renderQueue = 5000;
            _materials.Add(mat);
            _fields.Add(tmp);
            return tmp;
        }
        private void Capture(int reports, int total, ShowEndingOutcome outcome, ShowLang lang)
        {
            CapturedReports = Mathf.Max(0, reports);
            CapturedTotal = Mathf.Max(0, total);
            CapturedOutcome = outcome;
            _body = OutroReportText.Compose(reports, total, outcome, lang);
            string[] texts = { OutroReportText.Header(lang), OutroReportText.Title(outcome, lang),
                OutroReportText.Body(outcome, lang), OutroReportText.CountLabelOf(lang),
                OutroReportText.Ratio(reports, total), OutroReportText.Note(lang), OutroReportText.Footer(lang) };
            _visible.Clear();
            ReportChars = 0;
            bool active = _card != null && _card.gameObject.activeSelf;
            if (_card != null) _card.gameObject.SetActive(true);
            for (int i = 0; i < _fields.Count; i++)
            {
                var tmp = _fields[i];
                tmp.SetText(texts[i]);
                tmp.maxVisibleCharacters = int.MaxValue;
                tmp.ForceMeshUpdate(true, true);
                tmp.ForceMeshUpdate(true, true);
                var info = tmp.textInfo;
                var visible = new bool[info.characterCount];
                for (int c = 0; c < visible.Length; c++)
                {
                    visible[c] = info.characterInfo[c].isVisible;
                    if (visible[c]) ReportChars++;
                }
                _visible.Add(visible);
                tmp.maxVisibleCharacters = 0;
                tmp.color = i == 1 && outcome == ShowEndingOutcome.Trapped ? HmdTextStyle.Alert : HmdTextStyle.Ink;
            }
            if (_card != null) _card.gameObject.SetActive(active);
            if (_plate != null) _plate.SetFloat("_Failed", outcome == ShowEndingOutcome.Trapped ? 1 : 0);
            _elapsed = 0;
            _lastTyped = VisibleChars = 0;
            _typedAtStart = typeSfx != null ? typeSfx.PlayedCount : 0;
        }
        private void LateUpdate()
        {
            if (!IsBuilt) return;
            _resolveWait += Time.unscaledDeltaTime;
            if (_resolveWait >= 1) { _resolveWait = 0; ResolveRefs(); }
            bool wanted = outro != null && outro.ReportAlpha > 0;
            _music?.SetPresented(wanted, Time.unscaledDeltaTime);
            if (!wanted)
            {
                _presented = false;
                _elapsed = 0;
                _lastTyped = VisibleChars = 0;
                SetVisible(false);
                typeSfx?.StopAll();
                _follow?.SnapToHead();
                return;
            }
            if (!_presented)
            {
                Capture(showControl != null ? showControl.VisitorMarkCount : 0,
                    showControl != null ? showControl.TotalAnomalyCount : 0,
                    _run != null ? _run.EndingOutcome : ShowEndingOutcome.Interrupted, ShowLanguage.Current);
                _presented = true;
            }
            SetVisible(true);
            _elapsed += Time.unscaledDeltaTime;
            Reveal(Mathf.CeilToInt(_elapsed * CommsPanelLogic.CharsPerSecFor(ShowLanguage.Current)), true);
        }
        private void Reveal(int budget, bool sound)
        {
            int seen = 0;
            for (int f = 0; f < _fields.Count; f++)
            {
                var flags = _visible[f];
                int n = Mathf.Clamp(budget, 0, flags.Length);
                _fields[f].maxVisibleCharacters = n;
                for (int c = 0; c < n; c++) if (flags[c]) seen++;
                budget -= flags.Length;
            }
            if (sound && seen > _lastTyped && _text != null) typeSfx?.Play(_text.transform.position);
            _lastTyped = VisibleChars = seen;
        }
        private void SetVisible(bool visible)
        {
            _alpha = visible ? 1 : 0;
            if (_card != null && _card.gameObject.activeSelf != visible) _card.gameObject.SetActive(visible);
        }
        public void PresentPreview(int reports, int total, ShowEndingOutcome outcome, ShowLang lang, int characters = int.MaxValue)
        {
            Build();
            SetVisible(true);
            Capture(reports, total, outcome, lang);
            Reveal(characters, false);
        }
        private void OnDisable()
        {
            _presented = false;
            SetVisible(false);
            _music?.SetPresented(false, 0);
            typeSfx?.StopAll();
        }
        private void OnDestroy()
        {
            foreach (var mat in _materials)
                if (Application.isPlaying) Destroy(mat); else DestroyImmediate(mat);
            if (_plate != null)
                if (Application.isPlaying) Destroy(_plate); else DestroyImmediate(_plate);
        }
    }
}
