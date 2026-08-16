#nullable enable
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>体験者の報告ボタンの面。</b> 左コントローラの「少し上・少し奥」に、
    /// 押し方（<c>(X,Yで異変を報告)</c>）と長押し中のゲージだけを出す小さな面。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0050（ユーザー逐語）:
    /// 「コントローラーの少し上、少し奥に、小さいスクリーンを置いておいて」
    /// 「文字だけで枠線も背景もいらない」。
    ///
    /// ⚠⚠ <b>これは体験者に見せる面で、<c>StatusHud.StaffViewing</c> の門を通さない。</b>
    /// 2026-08-07 の「体験者の視界に文字を出さない」は生きているが、0050 でユーザーが
    /// この 1 面だけを名指しで求めた（0037 の「ステータスくらいの大きさでちゃんと示す文字 or
    /// 体験の演出としての文字」に当たる）。<b>門を足さないこと</b> —
    /// <c>HmdTextGateTests.VisitorMarkPanel_IsNotGatedByStaffViewing</c> が固定している。
    ///
    /// ⚠ <b>枠も地も持たない</b>（<see cref="CommsPanel"/> は縁を持つが、こちらは持たない）。
    /// 手元に常時あるものなので、面が立つと視界が塞がる。文字だけなら暗い場所でも
    /// 「浮いている文字」として読めて、明るい場所では消える。
    ///
    /// ⚠ 追従・配置は <see cref="ControllerGuidePanel"/> と同じ流儀（コントローラ位置から上・奥へ、
    /// 回転は頭へ正対）。左右で違う癖にしない。OVRInput には依存せず、接続と長押しの進捗は
    /// <c>OvrControllerBridge</c> が push する。
    ///
    /// ⚠⚠ <b>描画順は既定（queue 3000）のまま ＝ 導入・終幕・タイトルのあいだは見えない。</b>
    /// 覆い（<c>IntroVeil</c> 4900・<c>Blend Zero SrcAlpha</c>）・殻（4910）・題字の黒（4950）は
    /// 全画面に掛かるので、その裏の文字は黒へ潰れる。**これは狙い** — 報告が要るのは本編で、
    /// 現実が割れている最中に操作説明が浮くと世界が壊れる。
    /// <b>導入中にも出したくなったら、`CommsPanel` のように queue を 4980 台へ上げて
    /// TMP の Overlay シェーダ（`ZTest Always`）へ差し替える</b>（5000 は超えない — URP の
    /// 透明パスは [2501, 5000] しか描かない）。
    /// </summary>
    public sealed class VisitorMarkPanel : MonoBehaviour
    {
        [Header("Output")]
        [Tooltip("出力先 TMP_Text (1 個)。枠も背景も持たない（文字だけ）。")]
        [SerializeField] private TMP_Text? text;

        [Header("Anchors")]
        [Tooltip("追従先の左コントローラアンカー（LeftHandAnchor）。null なら非表示。")]
        [SerializeField] private Transform? controller;

        [Tooltip("正対させる頭（CenterEyeAnchor）。null なら Camera.main。")]
        [SerializeField] private Transform? head;

        [Header("Placement（現場調整可）")]
        [Tooltip("コントローラ上方向へのオフセット (m)。")]
        [SerializeField] private float heightOffset = 0.10f;

        [Tooltip("頭→コントローラの水平方向へさらに奥へ出すオフセット (m)。")]
        [SerializeField] private float awayOffset = 0.07f;

        [Tooltip("位置追従の SmoothDamp 時定数 (秒)。大きいほどゆっくり追う。")]
        [SerializeField] private float smoothTime = 0.12f;

        // 左コントローラの状態。既定 true（push 前に「未接続で非表示」を誤発しない）。
        //
        // ⚠⚠ **繋がっていることと、位置が取れていることは別**（2026-08-16 実機で踏んだ）。
        //    `OVRInput.IsControllerConnected` は電源が入っていれば true を返すが、
        //    カメラから見えていない（伏せてある・体の陰・起動直後）と姿勢は無効で、
        //    `OVRCameraRig` は **LeftHandAnchor をトラッキング原点（床の中心）へ置く**。
        //    接続だけで判定していたので、面が**床の原点に出て「遠くに小さく」見えていた**。
        private bool _connected = true;
        private bool _posValid = true;

        private float _progress01;
        private bool _confirming;

        private Vector3 _posVel;       // SmoothDamp の速度状態
        private bool _seeded;          // 初回配置済みか（初回はスナップして寄せる）
        private string _lastBody = ""; // SetText の GC を避けるための直近本文

        /// <summary>実体（TMP）を組めたか。<b>false なら一生出ない</b>。</summary>
        public bool IsBuilt => text != null;

        /// <summary>いま面に出している文字（テスト・診断用）。</summary>
        public string CurrentBody => _lastBody;

        /// <summary>
        /// 左コントローラの状態を反映する。<b>繋がっている ＋ 位置が取れている</b>のときだけ出す。
        /// ⚠ <paramref name="positionValid"/> を落とすと**アンカーは原点へ飛ぶ**ので、
        /// 出したままにすると「手元の面が床に落ちている」ように見える。
        /// </summary>
        public void SetControllerState(bool connected, bool positionValid)
        {
            _connected = connected;
            _posValid = positionValid;
        }

        /// <summary>左コントローラが繋がっているか（テレメトリ用）。</summary>
        public bool ControllerConnected => _connected;

        /// <summary>左コントローラの位置が取れているか（テレメトリ用）。<b>false なら面は出ない。</b></summary>
        public bool ControllerTracked => _posValid;

        /// <summary>
        /// 長押しの状態を反映する。<c>OvrControllerBridge</c> が
        /// <c>VisitorMarkHoldLogic</c> の値を毎フレーム push する。
        /// </summary>
        /// <param name="progress01">長押しの進捗 [0,1]。0 なら待ちの 1 行に戻る。</param>
        /// <param name="confirming">発火直後の余韻の最中か。</param>
        public void SetMarkState(float progress01, bool confirming)
        {
            _progress01 = Mathf.Clamp01(progress01);
            _confirming = confirming;
            ApplyBody();
        }

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            // 既定 LiberationSans SDF は CJK グリフを持たない（本文が豆腐化する）。
            // 同梱の日本語 TMP フォントへ差し替える。
            if (text != null)
            {
                var jp = JapaneseHudFont.TryGet();
                if (jp != null) text.font = jp;
                // 面ごとに色を決めない（HmdTextStyle が唯一の正）＝ 同じ装置が喋っていると読める。
                text.color = HmdTextStyle.Ink;
                // ゲージと見出しの大きさをリッチテキストで組む（VisitorMarkGuidance）。
                text.richText = true;
            }

            ApplyBody();
        }

        private void LateUpdate()
        {
            if (text == null) return;

            // 未接続 / 位置が取れていない / アンカー欠落なら非表示（復帰時は再配置スナップする）。
            if (!_connected || !_posValid || controller == null || head == null)
            {
                if (text.enabled) text.enabled = false;
                _seeded = false;
                return;
            }
            if (!text.enabled) text.enabled = true;

            // 配置: コントローラ位置 + 上 heightOffset + (頭→コントローラの水平単位ベクトル) * awayOffset。
            Vector3 toController = controller.position - head.position;
            toController.y = 0f;
            Vector3 horiz = toController.sqrMagnitude > 1e-6f
                ? toController.normalized
                : Flatten(head.forward);

            Vector3 target = controller.position + Vector3.up * heightOffset + horiz * awayOffset;

            if (!_seeded)
            {
                transform.position = target;
                _posVel = Vector3.zero;
                _seeded = true;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position, target, ref _posVel, Mathf.Max(0.0001f, smoothTime),
                    Mathf.Infinity, Time.deltaTime);
            }

            // 回転: 頭へ正対（billboard）。手を捻っても文字が回らない。
            Vector3 faceDir = transform.position - head.position;
            if (faceDir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(faceDir, Vector3.up);
        }

        // 文字列の組み立ては純関数へ。変化したときだけ SetText する（毎フレームの GC を作らない）。
        private void ApplyBody()
        {
            if (text == null) return;
            string composed = VisitorMarkGuidance.Line(_progress01, _confirming);
            if (composed == _lastBody) return;
            text.SetText(composed);
            _lastBody = composed;
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
