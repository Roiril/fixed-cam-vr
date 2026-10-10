#nullable enable
using TMPro;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// スタッフ専用のコントローラ操作ガイド。右コントローラの「少し上・少し奥」に、
    /// 現在モード（Normal / Registration）の操作方法を常時表示する小パネル。
    ///
    /// 体験者はコントローラを持たないため、これは<b>スタッフだけが読む</b>手元の早見表。
    /// StatusHud（視線前方・トグル式・情報表示）とは役割が違い、こちらは操作説明を
    /// コントローラに貼り付けて出す。
    ///
    /// ⚠ **2026-08-07 に「接続していれば常時」をやめた**（ユーザー指摘・世界観）。読み手はスタッフでも、
    /// パネルが浮くのはコントローラの位置＝<b>体験者の視界の中</b>で、スタッフが横で持っていれば
    /// 体験者に文字が見えていた。いまは <see cref="StatusHud.StaffViewing"/>（<b>右 B を押している
    /// あいだ</b> or 位置合わせ作業中）が立っている間だけ出す。位置合わせ中は自動で立つので
    /// REG 本文は従来どおり読める。
    ///
    /// ⚠ 「押しても振動しないときはガイドパネルが出ているか見る」という切り分けは、
    /// <b>右 B を押しながら</b>になった（B を押しているあいだパネルもステータスも出る）。
    ///
    /// 配置（<see cref="LateUpdate"/>）: コントローラ位置から上へ <see cref="heightOffset"/>、
    /// 頭→コントローラの水平方向へさらに <see cref="awayOffset"/> 奥へずらす。位置は
    /// <see cref="smoothTime"/> の SmoothDamp で滑らかに追う。回転はコントローラの手首回転には
    /// 追従させず、常に頭へ正対（billboard）させ、手を捻っても文字が回らないようにする。
    ///
    /// OVRInput には依存しない（Diagnostics asmdef の方針）。接続状態・モードは
    /// OvrControllerBridge が <see cref="SetControllerConnected"/> / <see cref="SetMode"/> で push する。
    /// </summary>
    public sealed class ControllerGuidePanel : MonoBehaviour
    {
        // 本文（このまま使う。装飾記号や英語見出しを足さない）。
        // ⚠ 書式は `入力：動作` で固定（<see cref="HmdTextStyle"/> の規約）。
        //   同じ操作を RecoveryGuidance も同じ名前で呼ぶ（「トリガー2秒」「A」「B」）。
        private const string NormalBody =
            "A2秒：新しい体験者にする\n" +
            // ⚠ 2026-09-14 にトグルをやめた（押しているあいだだけ出る）ので、
            //   「切り替える」と書いてあると挙動と食い違う。
            "B：押している間ステータスを見る\n" +
            "トリガー2秒：位置合わせを開始";

        private const string RegBody =
            // 押下か長押しかが読めない旧文（「A：この点を記録（押しながら静止）」）を、
            // 動作の側へ条件を寄せて書き直した。
            // ⚠ この行は位置合わせのガイダンス（CourseRegistrationController）と **1 字まで同じ**にする。
            "A：押したまま 0.5 秒静止で記録\n" +
            // ⚠ B の意味は Verify（確定して終える）と Review（保存せず終える）で違う。
            //   どちらでも真になる言い方にする（旧「この位置合わせで確定」は Review で嘘になっていた）。
            "B：位置合わせを終える\n" +
            "トリガー2秒：中止して戻る";

        [Header("Output")]
        [Tooltip("出力先 TMP_Text (1 個)。")]
        [SerializeField] private TMP_Text? text;

        [Header("Anchors")]
        [Tooltip("追従先の右コントローラアンカー（RightHandAnchor 等）。null なら非表示。")]
        [SerializeField] private Transform? controller;

        [Tooltip("正対させる頭（CenterEyeAnchor）。null なら Camera.main。")]
        [SerializeField] private Transform? head;

        [Tooltip("スタッフが被っているかの判定元。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private StatusHud? statusHud;

        [Header("Placement (現場調整可)")]
        [Tooltip("コントローラ上方向へのオフセット (m)。")]
        [SerializeField] private float heightOffset = 0.12f;

        [Tooltip("頭→コントローラの水平方向へさらに奥へ出すオフセット (m)。")]
        [SerializeField] private float awayOffset = 0.06f;

        [Tooltip("位置追従の SmoothDamp 時定数 (秒)。大きいほどゆっくり追う。")]
        [SerializeField] private float smoothTime = 0.15f;

        // 現在モードのラベル（"NORMAL"/"REG"）。OvrControllerBridge が push。
        private string _modeLabel = "";

        // 右コントローラの状態。既定 true（push 前に「未接続で非表示」を誤発しない）。
        //
        // ⚠⚠ **繋がっていることと、位置が取れていることは別**（2026-08-16 実機で踏んだ）。
        //    カメラから見えていないコントローラは接続 true のまま姿勢が無効になり、
        //    `OVRCameraRig` は **アンカーをトラッキング原点（床の中心）へ置く**。
        //    接続だけで判定していると、**早見表が床に落ちて「遠くに小さく」見える**
        //    （位置合わせ中はこれが唯一の操作説明なので、作業がそのまま止まる）。
        private bool _connected = true;
        private bool _posValid = true;
        private CourseRegistrationController? _registration;

        // ⚠ 2026-08-15 に一時メッセージ（`ShowTransient` / 赤 1 行）を消した。
        //    唯一の呼び出し元だった「演出中に A を押してカメラ手送りを拒否する」経路が
        //    2026-08-12 に無くなっていた（`canon/LEDGER.md`「カメラの手送り機能は要らないです」）。
        //    しかも**この面の TMP は richText=false で組まれていた**ので、仮に呼ばれていたら
        //    `<color=#FF6655>` という文字列がそのまま実機に出ていた。

        private Vector3 _posVel;      // SmoothDamp の速度状態
        private bool _seeded;         // 初回配置済みか（初回はスナップして寄せる）
        private string _lastBody = ""; // SetText の GC を避けるための直近本文
        private bool _baseLayoutCaptured;
        private Vector2 _baseRectSize;
        private Vector2 _baseAnchoredPosition;
        private float _baseFontSize;
        private bool _baseRichText;
        private Color _baseColor;
        private Vector4 _baseMargin;

        private const float HandDistanceM = 0.45f;
        private const float RegistrationMarginEm = 0.5f;

        /// <summary>操作モード（"NORMAL"/"REG"）を切り替えてパネル本文を差し替える。</summary>
        public void SetMode(string label)
        {
            _modeLabel = label ?? "";
            ApplyBody();
        }

        /// <summary>
        /// 位置合わせの案内元。Bridge が既存参照をそのまま渡す。
        /// SerializeField を増やさず、案内の正を <see cref="CourseRegistrationController"/> 1 か所に保つ。
        /// </summary>
        public void SetRegistration(CourseRegistrationController? registration)
        {
            _registration = registration;
            ApplyBody();
        }

        /// <summary>
        /// 右コントローラの状態を反映する。<b>繋がっている ＋ 位置が取れている</b>のときだけ出す。
        /// ⚠ <paramref name="positionValid"/> を落とすと**アンカーは原点へ飛ぶ**ので、
        /// 出したままにすると「早見表が床に落ちている」ように見える。
        /// </summary>
        public void SetControllerState(bool connected, bool positionValid)
        {
            _connected = connected;
            _posValid = positionValid;
        }

        /// <summary>右コントローラが繋がっているか（テレメトリ用）。</summary>
        public bool ControllerConnected => _connected;

        /// <summary>右コントローラの位置が取れているか（テレメトリ用）。<b>false なら面は出ない。</b></summary>
        public bool ControllerTracked => _posValid;

        /// <summary>スタッフが被っているか。解決できないときは false ＝ 文字を出さない側へ倒す。</summary>
        private bool StaffViewing()
        {
            // 位置合わせはスタッフの作業。メインの StaffSetupPanel が出ていても、手元の現在案内は隠さない。
            if (_registration != null && _registration.IsActive) return true;
            if (StaffSetupPanel.Instance != null && StaffSetupPanel.Instance.Visible) return false;
            if (statusHud == null) statusHud = FindObjectOfType<StatusHud>();
            return statusHud != null && statusHud.StaffViewing;
        }

        /// <summary>
        /// いま書く濃さ。<b>ステータスと同じ値</b>（<see cref="StatusHud.StaffAlpha01"/>）を使う —
        /// 2 枚は同じ B で同時に出るので、別々に薄れると 2 通りの速さで消える。
        /// 解決できないときは 1（門が閉じていれば面ごと出ないので、濃さは効かない）。
        /// </summary>
        private float StaffAlpha() => statusHud != null ? statusHud.StaffAlpha01 : 1f;

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            // 既定 LiberationSans SDF は日本語グリフを持たない（本文が豆腐化する）。
            // 同梱の日本語 TMP フォントへ差し替える（失敗時は既定のまま＝ASCII 断片のみ表示）。
            if (text != null)
            {
                var jp = JapaneseHudFont.TryGet();
                if (jp != null) text.font = jp;
                text.color = HmdTextStyle.Ink;   // 面ごとに色を決めない（HmdTextStyle が唯一の正）

                // ⚠⚠ **題字と導入の黒の上に描く**（2026-09-14）。ステータスと同時に出る面なので、
                //    片方だけ潰れると「B は効いているのに早見表だけ消えた」に見える。
                //    手順も理由も StatusHud.UseOverlayShader と同じ（あちらが正本）。
                UseOverlayShader(text);
                text.fontMaterial.renderQueue = RenderQueue;
            }

            ApplyBody();
        }

        /// <summary>題字の黒（4950）・導入の覆い（4900）より後に描く。<b>5000 を超えない</b>。</summary>
        private const int RenderQueue = 5000;

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。</summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";

        /// <summary>
        /// TMP の Overlay 版（<c>ZTest Always</c>）へ差し替える。既定の
        /// <c>TextMeshPro/Distance Field</c> は ZTest をグローバルで引くのでマテリアルから上書きできない。
        /// 見つからないときは<b>差し替えずに続ける</b>（<see cref="StatusHud"/> と同文）。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[ControllerGuidePanel] {OverlayShaderName} が見つかりません。" +
                                 "題字や導入の黒に早見表が隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        private void LateUpdate()
        {
            if (text == null) return;

            // Feed と CourseRegistrationController.Update の後に現在案内を読む。
            // 採取中の 0.5 秒進捗も GuidanceText の変化ごとに反映される。
            ApplyBody();

            // 未接続 or アンカー欠落 or スタッフが見ていないなら非表示（復帰時は再配置スナップする）。
            if (!_connected || !_posValid || controller == null || head == null || !StaffViewing())
            {
                if (text.enabled) text.enabled = false;
                _seeded = false;
                return;
            }
            if (!text.enabled) text.enabled = true;
            // ⚠ 濃さは毎フレーム。色を書いたあとに書く（TMP_Text.color は alpha も上書きする）。
            text.color = _registration != null && _registration.IsActive && _registration.GuidanceIsAlert
                ? HmdTextStyle.Alert : HmdTextStyle.Ink;
            text.alpha = StaffAlpha();

            // 配置: コントローラ位置 + 上 heightOffset + (頭→コントローラの水平単位ベクトル) * awayOffset。
            Vector3 target = GuideTarget(controller, head);

            if (!_seeded)
            {
                transform.position = target;
                _posVel = Vector3.zero;
                _seeded = true;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position, target, ref _posVel, Mathf.Max(0.0001f, smoothTime), Mathf.Infinity, Time.deltaTime);
            }

            // 回転: 頭へ正対（billboard）。パネル法線（forward）を頭視線に揃え、コントローラ回転には追従しない。
            Vector3 faceDir = transform.position - head.position;
            if (faceDir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(faceDir, Vector3.up);
        }

        // 本文の SetText は変化時だけ。位置合わせ中の見かけ角と領域は手元距離に合わせて毎フレーム更新する。
        private void ApplyBody()
        {
            if (text == null) return;
            CaptureBaseLayout();
            bool registrationActive = _registration != null && _registration.IsActive;
            string body = registrationActive ? _registration!.GuidanceText
                : _modeLabel == "REG" ? RegBody : NormalBody;
            bool changed = body != _lastBody;
            if (registrationActive) ApplyRegistrationLayout(body);
            else if (changed) RestoreBaseLayout();
            if (changed)
            {
                text.SetText(body);
                _lastBody = body;
            }
        }

        private void CaptureBaseLayout()
        {
            if (_baseLayoutCaptured || text == null) return;
            _baseLayoutCaptured = true;
            _baseRectSize = text.rectTransform.sizeDelta;
            _baseAnchoredPosition = text.rectTransform.anchoredPosition;
            _baseFontSize = text.fontSize;
            _baseRichText = text.richText;
            _baseColor = text.color;
            _baseMargin = text.margin;
        }

        private void ApplyRegistrationLayout(string body)
        {
            if (text == null) return;
            float sy = Mathf.Max(1e-6f, Mathf.Abs(text.transform.lossyScale.y));
            float distance = head != null && controller != null
                ? Vector3.Distance(head.position, GuideTarget(controller, head))
                : HandDistanceM;
            float em = HmdTextStyle.WorldEm(HmdTextStyle.BodyDeg, distance);
            text.fontSize = em / (HmdTextStyle.MeshFontScale * sy);
            text.richText = true;
            float localEm = em / sy;
            float margin = localEm * RegistrationMarginEm;
            text.margin = new Vector4(margin, margin, margin, margin);

            // 文字数の概算では、Source Han Sans JP の字面の左ベアリングと実 line-height を
            // 含められない。TMP が同じ font / rich-text で返す実寸をそのまま領域に使う。
            Vector2 preferred = text.GetPreferredValues(body);
            Vector2 size = new Vector2(
                Mathf.Max(_baseRectSize.x, preferred.x),
                Mathf.Max(_baseRectSize.y, preferred.y));
            Vector2 delta = size - _baseRectSize;
            Vector2 pivot = text.rectTransform.pivot;
            // 下端と左端を通常ガイドと同じ位置に残し、長い案内は上と右へだけ伸ばす。
            text.rectTransform.anchoredPosition = _baseAnchoredPosition
                + new Vector2(delta.x * pivot.x, delta.y * pivot.y);
            text.rectTransform.sizeDelta = size;
        }

        private void RestoreBaseLayout()
        {
            if (!_baseLayoutCaptured || text == null) return;
            text.rectTransform.sizeDelta = _baseRectSize;
            text.rectTransform.anchoredPosition = _baseAnchoredPosition;
            text.fontSize = _baseFontSize;
            text.richText = _baseRichText;
            text.color = _baseColor;
            text.margin = _baseMargin;
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        private Vector3 GuideTarget(Transform hand, Transform viewer)
        {
            Vector3 toController = hand.position - viewer.position;
            toController.y = 0f;
            Vector3 horiz = toController.sqrMagnitude > 1e-6f
                ? toController.normalized
                : Flatten(viewer.forward);
            return hand.position + Vector3.up * heightOffset + horiz * awayOffset;
        }
    }
}
