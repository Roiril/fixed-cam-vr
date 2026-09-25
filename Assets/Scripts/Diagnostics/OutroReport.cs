#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Diagnostics
{
    /// <summary>エンドロール冒頭にロゴ、結末、報告数、結末写真を表示する。</summary>
    [DisallowMultipleComponent]
    public sealed class OutroReport : MonoBehaviour
    {
        [SerializeField] private OutroDirector? outro;
        [SerializeField] private ShowControlClient? showControl;
        [SerializeField] private TypeAudioCue? typeSfx;
        [SerializeField, Min(.5f)] private float distanceM = 2.6f;
        [SerializeField, Range(-20, 20)] private float pitchOffsetDeg = 2;

        public const float PanelWidth = 3.10f, PanelHeight = 1.60f;
        public const float PhotoWidth = 1.56f, PhotoHeight = PhotoWidth * 9f / 16f;

        private static readonly Color TitleInk = new Color32(222, 220, 214, 255);
        private static readonly Color RecordInk = new Color32(174, 177, 172, 255);
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SourceAspectId = Shader.PropertyToID("_SourceAspect");
        private static readonly int TargetAspectId = Shader.PropertyToID("_TargetAspect");

        private readonly List<TMP_Text> _fields = new List<TMP_Text>();
        private readonly List<Material> _materials = new List<Material>();
        private Transform? _card;
        private HeadYawFollow? _follow;
        private Material? _plate;
        private Material? _logo;
        private Material? _photo;
        private MeshRenderer? _photoRenderer;
        private ShowRunDirector? _run;
        private OutroResultMusic? _music;
        private Texture? _releasedImage;
        private Texture? _trappedImage;
        private Texture? _currentImage;
        private EndingFrameCapture? _endingFrames;
        private bool _presented;
        private float _resolveWait, _alpha;
        private string _body = "";

        public bool IsBuilt => _card != null && _plate != null && _logo != null && _photo != null;
        public float AppliedAlpha => IsBuilt ? _alpha : -1;
        public string CurrentBody => _body;
        public int VisibleChars { get; private set; }
        public int ReportChars { get; private set; }
        public int TypedCount => 0;
        public bool TypeSfxBuilt => false;
        public int CapturedReports { get; private set; }
        public int CapturedTotal { get; private set; }
        public ShowEndingOutcome CapturedOutcome { get; private set; }
        public IReadOnlyList<TMP_Text> Fields => _fields;
        public Texture? CurrentEndingImage => _currentImage;
        public bool PhotoVisible => _photoRenderer != null && _photoRenderer.enabled;
        public bool MusicHasClip => _music != null && _music.HasClip;
        public bool MusicPlaying => _music != null && _music.IsPlaying;
        public float MusicVolume => _music != null ? _music.Volume : 0;
        public float MusicSeconds => _music != null ? _music.PlaybackSeconds : 0;

        private void Awake()
        {
            ResolveRefs();
            Build();
            Capture(0, 0, ShowEndingOutcome.Interrupted, ShowLanguage.Current);
            ApplyAlpha(0);
        }

        private void ResolveRefs()
        {
            if (outro == null) outro = FindObjectOfType<OutroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_endingFrames == null) _endingFrames = FindObjectOfType<EndingFrameCapture>();
            if (_music == null) _music = GetComponent<OutroResultMusic>();
            if (_music == null) _music = gameObject.AddComponent<OutroResultMusic>();
        }

        private void Build()
        {
            if (_card != null) return;
            var font = Resources.Load<TMP_FontAsset>("Fonts/OutroReport SDF") ?? JapaneseHudFont.TryGet();
            var shader = Resources.Load<Shader>("Shaders/OutroReportMedia");
            var title = Resources.Load<Texture2D>("Title/MawarimiTitle");
            if (font == null || shader == null || title == null)
            {
                Debug.LogError("[OutroReport] エンド画面のフォント、シェーダ、ロゴのいずれかがありません");
                return;
            }

            _follow = HeadYawFollow.Attach(transform, "ReportYawFollow");
            _card = new GameObject("EndingLead").transform;
            _card.SetParent(_follow.transform, false);
            float rad = pitchOffsetDeg * Mathf.Deg2Rad;
            _card.localPosition = new Vector3(0, -Mathf.Sin(rad) * distanceM, Mathf.Cos(rad) * distanceM);

            _plate = AddQuad("EndingBlack", shader, Vector2.zero,
                new Vector2(PanelWidth, PanelHeight), 0, null, 4997, out _);
            _logo = AddQuad("MawarimiLogo", shader, new Vector2(-.83f, .47f),
                new Vector2(1.14f, .57f), 1, title, 4998, out _);
            _photo = AddQuad("EndingPhoto", shader, new Vector2(.67f, 0),
                new Vector2(PhotoWidth, PhotoHeight), 2, null, 4998, out _photoRenderer);

            AddField("Outcome", font, -1.37f, .08f, 1.15f, .23f, 3.2f, TitleInk);
            AddField("ReportCount", font, -1.37f, -.28f, 1.15f, .16f, 1.8f, RecordInk);
        }

        private Material AddQuad(string name, Shader shader, Vector2 position, Vector2 size, int mode,
            Texture? texture, int queue, out MeshRenderer renderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(_card, false);
            go.transform.localPosition = new Vector3(position.x, position.y, mode == 0 ? 0 : -.005f);
            go.transform.localScale = new Vector3(size.x, size.y, 1);
            var collider = go.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider);
            var mat = new Material(shader) { name = name, renderQueue = queue };
            mat.SetFloat(ModeId, mode);
            mat.SetFloat(AlphaId, 0);
            mat.SetFloat(TargetAspectId, size.x / size.y);
            if (texture != null)
            {
                mat.SetTexture(MainTexId, texture);
                mat.SetFloat(SourceAspectId, (float)texture.width / Mathf.Max(1, texture.height));
            }
            renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
            _materials.Add(mat);
            return mat;
        }

        private TMP_Text AddField(string name, TMP_FontAsset font, float x, float y, float w, float h,
            float deg, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_card, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSize = .07f;
            tmp.alignment = TextAlignmentOptions.Top;
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            tmp.color = color;
            float scale = HmdTextStyle.MeshScale(deg, distanceM, .07f);
            var rt = (RectTransform)go.transform;
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w / scale, h / scale);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = new Vector3(x, y, -.01f);
            var mat = tmp.fontMaterial;
            mat.shader = Shader.Find("TextMeshPro/Distance Field Overlay");
            mat.renderQueue = 5000;
            _materials.Add(mat);
            _fields.Add(tmp);
            return tmp;
        }

        /// <summary>通常の 2 結末に使う完成済みの 16:9 画像を受け取る。</summary>
        public void SetEndingImages(Texture? released, Texture? trapped)
        {
            if (ReferenceEquals(_releasedImage, released) && ReferenceEquals(_trappedImage, trapped)) return;
            _releasedImage = released;
            _trappedImage = trapped;
            ApplyEndingImage(CapturedOutcome);
        }

        private void ApplyEndingImage(ShowEndingOutcome outcome)
        {
            _currentImage = outcome == ShowEndingOutcome.Released ? _releasedImage
                : outcome == ShowEndingOutcome.Trapped ? _trappedImage : null;
            if (_photo == null || _photoRenderer == null) return;
            if (_currentImage != null)
            {
                _photo.SetTexture(MainTexId, _currentImage);
                _photo.SetFloat(SourceAspectId,
                    (float)_currentImage.width / Mathf.Max(1, _currentImage.height));
            }
            _photoRenderer.enabled = _alpha > 0 && _currentImage != null;
        }

        private void Capture(int reports, int total, ShowEndingOutcome outcome, ShowLang lang)
        {
            CapturedReports = Mathf.Max(0, reports);
            CapturedTotal = Mathf.Max(0, total);
            CapturedOutcome = outcome;
            _body = OutroReportText.Compose(reports, total, outcome, lang);
            string[] texts = { OutroReportText.Title(outcome, lang), OutroReportText.CountLine(reports, lang) };
            ReportChars = 0;
            bool active = _card != null && _card.gameObject.activeSelf;
            if (_card != null) _card.gameObject.SetActive(true);
            for (int i = 0; i < _fields.Count; i++)
            {
                TMP_Text tmp = _fields[i];
                tmp.characterSpacing = i == 0 && lang == ShowLang.Ja ? 4f : 0;
                tmp.SetText(texts[i]);
                tmp.maxVisibleCharacters = int.MaxValue;
                tmp.ForceMeshUpdate(true, true);
                for (int c = 0; c < tmp.textInfo.characterCount; c++)
                    if (tmp.textInfo.characterInfo[c].isVisible) ReportChars++;
            }
            ApplyEndingImage(outcome);
            if (_card != null) _card.gameObject.SetActive(active);
            VisibleChars = active ? ReportChars : 0;
        }

        private void LateUpdate()
        {
            if (!IsBuilt) return;
            _resolveWait += Time.unscaledDeltaTime;
            if (_resolveWait >= 1) { _resolveWait = 0; ResolveRefs(); }
            float alpha = outro != null ? Mathf.Clamp01(outro.ReportAlpha) : 0;
            if (_endingFrames != null)
                SetEndingImages(_endingFrames.ReleasedShot, _endingFrames.TrappedShot);
            bool wanted = alpha > 0;
            _music?.SetPresented(wanted, Time.unscaledDeltaTime);
            if (!wanted)
            {
                _presented = false;
                ApplyAlpha(0);
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
            ApplyAlpha(alpha);
        }

        private void ApplyAlpha(float alpha)
        {
            _alpha = Mathf.Clamp01(alpha);
            bool visible = _alpha > 0;
            if (_card != null && _card.gameObject.activeSelf != visible) _card.gameObject.SetActive(visible);
            _plate?.SetFloat(AlphaId, _alpha);
            _logo?.SetFloat(AlphaId, _alpha);
            _photo?.SetFloat(AlphaId, _alpha);
            for (int i = 0; i < _fields.Count; i++)
            {
                Color ink = i == 0 ? TitleInk : RecordInk;
                ink.a = _alpha;
                _fields[i].color = ink;
                _fields[i].maxVisibleCharacters = int.MaxValue;
            }
            if (_photoRenderer != null) _photoRenderer.enabled = visible && _currentImage != null;
            VisibleChars = visible ? ReportChars : 0;
        }

        public void PresentPreview(int reports, int total, ShowEndingOutcome outcome, ShowLang lang,
            int characters = int.MaxValue)
        {
            Build();
            Capture(reports, total, outcome, lang);
            ApplyAlpha(1);
        }

        private void OnDisable()
        {
            _presented = false;
            ApplyAlpha(0);
            _music?.SetPresented(false, 0);
            typeSfx?.StopAll();
        }

        private void OnDestroy()
        {
            foreach (Material mat in _materials)
                if (Application.isPlaying) Destroy(mat); else DestroyImmediate(mat);
        }
    }
}
