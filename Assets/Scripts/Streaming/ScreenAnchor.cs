#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ワールド空間に置いた Screen を「頭の前に追従」⇄「その場で凍結」を動的に切り替える。
    /// Locked=true: 毎 LateUpdate でカメラ前方へ追従（yawOnly 時は緩急付き＝減衰 + 速度上限）。
    ///
    /// <b>目標は常に頭の正面</b>で、滑らかに寄って<b>そこで止まる</b>（<see cref="YawTrailDeg"/> = 0）。
    /// 2026-08-01 まではヨー差 10° を「動き出す閾値」と「止まる位置」に兼用していたため、
    /// スクリーンは頭の正面へ一度も到達しなかった。
    /// Locked=false: 直前位置で凍結（ワールド固定）。
    /// 映像の向き・アスペクト補正は ScreenComposite シェーダ側（UV 空間）で完結するため、
    /// ここでは Transform の回転補正を一切持たない。
    ///
    /// 追従の緩急（計画 2026-07-19_viewer-ux.md）は <see cref="YawFollowLogic"/> に分離し、本クラスは
    /// SerializeField（現場調整前提）と再ロック/凍結/resume 明けの合流（スナップ禁止）を担う。
    /// </summary>
    public sealed class ScreenAnchor : MonoBehaviour
    {
        [SerializeField] private Transform? head;
        [Tooltip("スクリーンまでの距離 (m)。Meta 焦点快適域。")]
        [SerializeField] private float distance = 2.0f;
        [SerializeField] private bool locked = false;
        [SerializeField] private KeyCode toggleKey = KeyCode.Space;

        [Tooltip("true: 水平方向(ヨー)のみ追従し、頭の上下(ピッチ)/ロールには追従しない。" +
                 "見上げ/見下ろしで画面が上下にズレず一定の高さに留まる（酔い軽減）。")]
        [SerializeField] private bool yawOnly = true;

        [Header("Follow easing (yaw / yawOnly のみ)")]

        /// <summary>
        /// 止まっている状態から追従を始める閾値 (度)。**微小 jitter を吸収するためだけの値**で、
        /// ここを大きくすると「頭を向けてもスクリーンが来ない」死角になる。
        ///
        /// ⚠ <b>SerializeField にしない</b>。旧実装はこれを 10° で焼いており、しかも
        /// 「止まる位置」と兼用していたため<b>スクリーンは常に頭の 10° 手前で止まっていた</b>
        /// （2026-08-01 ユーザー指摘「頭の前までぎりぎり到達しないとかはやめて」）。
        /// SerializeField に戻すと既存シーン / prefab の 10 がそのまま効いて再発する。
        /// </summary>
        private const float YawDeadzoneDeg = 0.5f;

        /// <summary>
        /// 頭の手前どこで止まるか (度)。<b>0 = 頭の正面ちょうどを目指して、そこで止まる</b>。
        /// 導入演出の覆い（<see cref="IntroVeil"/>）は開口をスクリーンの実位置から逆算するので、
        /// ここに 0 以外を入れても枠はついてくる。それでも 0 にしているのは、
        /// 「見た方向に画がある」を体験の既定にするため。
        /// </summary>
        private const float YawTrailDeg = 0f;

        [Tooltip("臨界減衰の時定数 (秒)。大きいほどゆっくり行き過ぎず追う。")]
        [SerializeField] private float smoothTime = 0.30f;

        [Tooltip("full-field スライドの角速度上限 (度/秒)。vection 抑制。")]
        [SerializeField] private float maxYawSpeedDegPerSec = 110f;

        [Tooltip("この角度以上置いていかれたら速度上限を catchUpBoost 倍に上げて追いつく（逆走ガード）。")]
        [SerializeField] private float catchUpThresholdDeg = 45f;

        [Tooltip("逆走ガード時の速度上限倍率。")]
        [SerializeField] private float catchUpBoost = 2f;

        [Header("Placement")]
        [Tooltip("スクリーン中心を head からどれだけ上下へずらすか (m)。負で下げる（歩行時の自然な視線）。" +
                 "-distance·tan8° ≈ -0.28m で head から約 8 度下。")]
        [SerializeField] private float heightOffset = -0.28f;

        [Tooltip("この秒数を超える unscaledDeltaTime は pause / HMD 着脱明けとみなし、追従を advance せず" +
                 "現在ヨーから合流し直す（巨大 dt での SmoothDamp スナップを防ぐ）。")]
        [SerializeField] private float resumeGapSec = 0.5f;

        private readonly YawFollowLogic _yawFollow = new();
        private bool _yawSeeded;
        private bool _followFrozen;
        private bool _wasFrozen;
        private bool _setupFrozen;
        public void SetSetupFrozen(bool frozen) => _setupFrozen = frozen;
        /// <summary>停止中のスタッフ画面だけを正面へ戻す。コース座標は変更しない。</summary>
        public bool RepositionForSetup()
        {
            if (!_setupFrozen || head == null) return false;
            ApplyPose(head.eulerAngles.y);
            ReseatYawFromTransform();
            return true;
        }

        public bool Locked
        {
            get => locked;
            set => SetLocked(value);
        }

        public void Toggle() => SetLocked(!locked);
        public void Lock() => SetLocked(true);
        public void Unlock() => SetLocked(false);

        /// <summary>
        /// 追従を凍結する（トラッキングロスト / pause 明けの暴れ防止）。SignalLostFx が駆動する。
        /// 解除時は次の LateUpdate で現在ヨーを種にして減衰合流する（スナップ禁止）。
        /// </summary>
        public void SetFollowFrozen(bool frozen) => _followFrozen = frozen;

        /// <summary>現在追従を凍結中か。</summary>
        public bool FollowFrozen => _followFrozen;

        private void SetLocked(bool value)
        {
            if (locked == value) return;
            locked = value;
            // OFF→ON: 現在のスクリーンヨーを種にして減衰追従で合流（瞬間ジャンプ禁止）。
            if (value) ReseatYawFromTransform();
        }

        private void Awake()
        {
            if (head == null && Camera.main != null)
            {
                head = Camera.main.transform;
            }
        }

        private void OnEnable()
        {
            // 有効化時に locked なら現在ヨーを種にしておく（初回 LateUpdate のジャンプ防止）。
            if (locked) ReseatYawFromTransform();
        }

        private void ReseatYawFromTransform()
        {
            _yawFollow.Reseat(transform.eulerAngles.y);
            _yawSeeded = true;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) Toggle();
        }

        private void LateUpdate()
        {
            if (_setupFrozen) return;
            if (!locked || head == null) return;

            if (!yawOnly)
            {
                // フルフォロー（フォールバック）: 緩急なし。凍結中は保持。
                if (_followFrozen) { _wasFrozen = true; return; }
                Vector3 fwdFull = head.forward;
                Vector3 posFull = head.position + fwdFull * distance;
                posFull.y += heightOffset;
                transform.position = posFull;
                transform.rotation = head.rotation;
                return;
            }

            // 凍結中は最後の良ポーズを維持（head 追従を止める = 暴れ防止）。
            if (_followFrozen)
            {
                _wasFrozen = true;
                return;
            }

            float headYaw = head.eulerAngles.y;

            // 凍結明け / resume 明け / 未シード: 現在ヨーを種に合流し、この 1 フレームは advance しない
            // （現在ポーズを保ったまま位置だけ head 追従を再開 → 次フレームから滑らかに寄せる）。
            bool resumeGap = Time.unscaledDeltaTime > resumeGapSec;
            if (_wasFrozen || resumeGap || !_yawSeeded)
            {
                ReseatYawFromTransform();
                _wasFrozen = false;
                ApplyPose(_yawFollow.CurrentYaw);
                return;
            }

            float screenYaw = _yawFollow.Step(headYaw, Time.deltaTime, YawDeadzoneDeg, YawTrailDeg,
                                              smoothTime, maxYawSpeedDegPerSec,
                                              catchUpThresholdDeg, catchUpBoost);
            ApplyPose(screenYaw);
        }

        // 指定ヨーでスクリーンを頭の前 distance・高さ head.y+heightOffset に配置する。
        private void ApplyPose(float yaw)
        {
            if (head == null) return;
            Quaternion faceRot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 fwd = faceRot * Vector3.forward; // 水平前方（fwd.y=0）
            Vector3 pos = head.position + fwd * distance;
            pos.y = head.position.y + heightOffset;
            transform.position = pos;
            transform.rotation = faceRot;
        }
    }
}
