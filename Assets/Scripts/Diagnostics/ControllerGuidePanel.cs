#nullable enable
using TMPro;
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
    /// 体験者に文字が見えていた。いまは <see cref="StatusHud.StaffViewing"/>（右 B の表示 or
    /// 位置合わせ作業中）が立っている間だけ出す。位置合わせ中は自動で立つので REG 本文は従来どおり読める。
    ///
    /// ⚠ 「押しても振動しないときはガイドパネルが出ているか見る」という切り分けは、
    /// <b>先に右 B を押してから</b>になった（B を押せばパネルもステータスも出る）。
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
        private const string NormalBody =
            "A：タイトルを閉じて始める\n" +
            "B：ステータス表示 切/入\n" +
            // 「周回リセット」は開発語で、スタッフには何が起きるか分からない（廃語）。
            "グリップ2秒：新しい体験者にする\n" +
            "トリガー2秒：位置合わせを開始";

        private const string RegBody =
            "A：この点を記録（押しながら静止）\n" +
            "B：この位置合わせで確定\n" +
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

        // 右コントローラ接続状態。既定 true（push 前に「未接続で非表示」を誤発しない）。
        private bool _controllerConnected = true;

        // 一時メッセージ（本文の上に赤 1 行で数秒だけ出す。期限で本文へ戻る）。
        private const string TransientColor = "FF6655"; // TMP リッチテキストの赤
        private string _transient = "";
        private float _transientUntil;

        private Vector3 _posVel;      // SmoothDamp の速度状態
        private bool _seeded;         // 初回配置済みか（初回はスナップして寄せる）
        private string _lastBody = ""; // SetText の GC を避けるための直近本文

        /// <summary>操作モード（"NORMAL"/"REG"）を切り替えてパネル本文を差し替える。</summary>
        public void SetMode(string label)
        {
            _modeLabel = label ?? "";
            ApplyBody();
        }

        /// <summary>
        /// 本文の上に赤 1 行で一時メッセージを出す（既定 2 秒）。期限で本文へ自動的に戻る。
        /// transient 中に <see cref="SetMode"/> が来ても本文だけ差し替わり、赤行は維持される。
        /// </summary>
        public void ShowTransient(string message, float seconds = 2f)
        {
            _transient = message ?? "";
            _transientUntil = Time.unscaledTime + Mathf.Max(0f, seconds);
            ApplyBody();
        }

        /// <summary>右コントローラ接続状態を反映する（未接続時はパネル非表示）。</summary>
        public void SetControllerConnected(bool connected) => _controllerConnected = connected;

        /// <summary>スタッフが被っているか。解決できないときは false ＝ 文字を出さない側へ倒す。</summary>
        private bool StaffViewing()
        {
            if (statusHud == null) statusHud = FindObjectOfType<StatusHud>();
            return statusHud != null && statusHud.StaffViewing;
        }

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            // 既定 LiberationSans SDF は日本語グリフを持たない（本文が豆腐化する）。
            // 同梱の日本語 TMP フォントへ差し替える（失敗時は既定のまま＝ASCII 断片のみ表示）。
            if (text != null)
            {
                var jp = JapaneseHudFont.TryGet();
                if (jp != null) text.font = jp;
                text.color = new Color(0.9f, 1f, 0.95f, 1f); // StatusHud と同系の明色
            }

            ApplyBody();
        }

        private void LateUpdate()
        {
            if (text == null) return;

            // 一時メッセージの期限切れで本文へ戻す（接続状態に関わらず状態を畳んでおく）。
            if (_transient.Length > 0 && Time.unscaledTime >= _transientUntil)
            {
                _transient = "";
                ApplyBody();
            }

            // 未接続 or アンカー欠落 or スタッフが見ていないなら非表示（復帰時は再配置スナップする）。
            if (!_controllerConnected || controller == null || head == null || !StaffViewing())
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
                    transform.position, target, ref _posVel, Mathf.Max(0.0001f, smoothTime), Mathf.Infinity, Time.deltaTime);
            }

            // 回転: 頭へ正対（billboard）。パネル法線（forward）を頭視線に揃え、コントローラ回転には追従しない。
            Vector3 faceDir = transform.position - head.position;
            if (faceDir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(faceDir, Vector3.up);
        }

        // 現在ラベル（+ 一時メッセージ）に対応する本文を text へ反映（変化時のみ・毎フレームは走らない）。
        // 文字列連結は mode 切替 / transient の出入りという稀なイベント時だけ起きる（毎フレームの GC ではない）。
        private void ApplyBody()
        {
            if (text == null) return;
            string body = _modeLabel == "REG" ? RegBody : NormalBody;
            string composed = (_transient.Length > 0 && Time.unscaledTime < _transientUntil)
                ? "<color=#" + TransientColor + ">" + _transient + "</color>\n" + body
                : body;
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
