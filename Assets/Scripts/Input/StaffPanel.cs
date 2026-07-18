#nullable enable
using UnityEngine;

namespace FixedCamVr.Input
{
    /// <summary>
    /// Staff モード中だけ視界に出す head-locked なチートシート（バインディング一覧）。
    /// CourseRegistrationController の [CourseRegGuidance] と同じ TextMesh パターンで、
    /// HMD（CenterEyeAnchor）に貼り付いた 1 個の TextMesh を Start で作り、
    /// <see cref="SetVisible"/> で OvrControllerBridge から表示を切り替える。
    ///
    /// 表示内容は Staff の操作割当（計画 2026-07-19_controller-roles.md のバインディング表）。
    /// このコンポーネント自体は入力を持たず、可視状態だけを担う（OVRInput 非依存）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StaffPanel : MonoBehaviour
    {
        [Tooltip("head-lock 先（CenterEyeAnchor）。null なら Camera.main。")]
        [SerializeField] private Transform? headTransform;

        [Tooltip("視界内でのローカル位置（head 基準）。ガイダンスと重ならないよう下寄せ。")]
        [SerializeField] private Vector3 localOffset = new(0f, -0.28f, 1.1f);

        [Tooltip("文字サイズ（TextMesh.characterSize）。")]
        [SerializeField] private float characterSize = 0.014f;

        // Staff モードのバインディング一覧（日本語）。計画のバインディング表と一致させる。
        private const string SheetText =
            "== STAFF モード ==\n" +
            "右 A / B      カメラ 次 / 前\n" +
            "左 X          スクリーン追従の凍結\n" +
            "左 Y          診断 HUD の表示切替\n" +
            "右グリップ単押し  演出 cue 試射\n" +
            "右スティック押込  登録モードへ\n" +
            "両グリップ 3 秒   Run へ戻る";

        private TextMesh? _text;
        private MeshRenderer? _renderer;
        private bool _visible;

        private void Start()
        {
            Build();
            SetVisible(false); // 既定は非表示（Staff 入場時に Bridge が ON）
        }

        private void OnDestroy()
        {
            if (_text != null) Destroy(_text.gameObject);
        }

        /// <summary>チートシートの表示・非表示を切り替える（Bridge がモード遷移で呼ぶ）。</summary>
        public void SetVisible(bool v)
        {
            _visible = v;
            if (_renderer != null) _renderer.enabled = v;
        }

        /// <summary>現在の表示状態。</summary>
        public bool IsVisible => _visible;

        private void Build()
        {
            Transform? head = headTransform != null
                ? headTransform
                : (Camera.main != null ? Camera.main.transform : null);

            var go = new GameObject("[StaffCheatSheet]");
            if (head != null) go.transform.SetParent(head, worldPositionStays: false);
            go.transform.localPosition = localOffset;
            go.transform.localRotation = Quaternion.identity;

            _text = go.AddComponent<TextMesh>();
            _text.anchor = TextAnchor.UpperCenter;
            _text.alignment = TextAlignment.Left;
            _text.characterSize = characterSize;
            _text.fontSize = 90;
            _text.color = new Color(0.85f, 0.95f, 1f, 1f);
            _text.text = SheetText;

            Font? font = BuiltinFont();
            _renderer = go.GetComponent<MeshRenderer>();
            if (font != null)
            {
                _text.font = font;
                if (_renderer != null) _renderer.sharedMaterial = font.material;
            }
        }

        private static Font? BuiltinFont()
        {
            Font? f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { /* older Unity */ }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* ignore */ } }
            return f;
        }
    }
}
