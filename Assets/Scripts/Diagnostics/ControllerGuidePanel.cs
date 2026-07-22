#nullable enable
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// スタッフ専用のコントローラ操作ガイド。右コントローラの「少し上・少し奥」に、
    /// 現在モード（Normal / Registration）の操作方法を常時表示する小パネル。
    ///
    /// 体験者はコントローラを持たないため、これは<b>スタッフだけが見る</b>手元の早見表。
    /// StatusHud（視線前方・トグル式・情報表示）とは役割が違い、こちらは操作説明を
    /// コントローラに貼り付けて常時出す。
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
            "A：カメラを次へ送る\n" +
            "B：ステータス表示 切/入\n" +
            "グリップ2秒：新しい体験者（周回リセット）\n" +
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

        private Vector3 _posVel;      // SmoothDamp の速度状態
        private bool _seeded;         // 初回配置済みか（初回はスナップして寄せる）
        private string _lastBody = ""; // SetText の GC を避けるための直近本文

        /// <summary>操作モード（"NORMAL"/"REG"）を切り替えてパネル本文を差し替える。</summary>
        public void SetMode(string label)
        {
            _modeLabel = label ?? "";
            ApplyBody();
        }

        /// <summary>右コントローラ接続状態を反映する（未接続時はパネル非表示）。</summary>
        public void SetControllerConnected(bool connected) => _controllerConnected = connected;

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

            // 未接続 or アンカー欠落なら非表示（次に接続復帰したら再配置スナップする）。
            if (!_controllerConnected || controller == null || head == null)
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

        // 現在ラベルに対応する本文を text へ反映（変化時のみ・GC を出さない）。
        private void ApplyBody()
        {
            if (text == null) return;
            string body = _modeLabel == "REG" ? RegBody : NormalBody;
            if (body == _lastBody) return;
            text.SetText(body);
            _lastBody = body;
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
