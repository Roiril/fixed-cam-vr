#nullable enable
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 海底探検のサイコロ（物理転がし方式・2026-07-08 に乱数確定方式から置換）。
    /// 離すと Grabbable が投擲速度を与えて物理で転がり、**静止したらサーバが上面を読んで出目を確定**する。
    /// 出目は CSV に残る（DiceRolled → SessionLogger）。VR 内の数字表示は出さない
    /// （2026-07-10 ユーザー決定: 「同じサイコロを見る」体験だけでよい — 読み取りも対面の共同行為）。
    /// 縁立ち等で静止しない場合はタイムアウトで強制スリープ → その時点の上面で確定。
    /// Grabbable + Rigidbody と同じ GameObject に付ける（TableDuoSceneSetup が配線）。
    /// </summary>
    public sealed class DiceRoller : NetworkBehaviour
    {
        /// <summary>サーバ側で出目が確定した（dieName, rollerClientId, value）。SessionLogger が CSV に刻む。</summary>
        public static event System.Action<string, ulong, int>? DiceRolled;

        // ローカル軸 [+X, -X, +Y, -Y, +Z, -Z] が上を向いたときの出目。
        // 海底探検のダイスは 1/2/3 が 2 面ずつの d6 相当。
        // ⚠ die.glb の実テクスチャ面と要照合（初回実機/L0 検証で校正する）
        [Tooltip("上を向いたローカル軸 [+X,-X,+Y,-Y,+Z,-Z] → 出目")]
        [SerializeField] private int[] faceValues = { 1, 2, 3, 1, 2, 3 };

        // 静止判定: 速度が閾値未満のまま RestSeconds 継続（または PhysX スリープ）で確定。
        // RollTimeout 秒静止しなければ強制確定（縁立ち・他ピースに挟まる等のスタック回避）
        private const float RestLinearSpeed = 0.02f;   // m/s
        private const float RestAngularSpeed = 0.3f;   // rad/s
        private const float RestSeconds = 0.4f;
        private const float RollTimeout = 6f;

        // 0 = 未確定（表示なし）
        private readonly NetworkVariable<byte> _value = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Grabbable? _grab;
        private Rigidbody? _rb;
        private bool _wasHeld;
        private ulong _lastHolder;
        private bool _rolling;
        private float _rollStart;
        private float _restSince = -1f;

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
            {
                Debug.LogError($"[TableDuo] DiceRoller {name}: Rigidbody がありません（物理転がし前提。Setup TableDuo Scene を再実行）");
            }
        }

        private void Update()
        {
            if (!IsSpawned || !IsServer || _grab == null) return;

            bool held = _grab.IsHeld;
            if (held)
            {
                _lastHolder = _grab.HolderClientId;
                if (!_wasHeld)
                {
                    // 掴み直したら前回の出目・進行中のロールを破棄
                    _rolling = false;
                    _value.Value = 0;
                }
            }
            else if (_wasHeld)
            {
                // リリース = ロール開始（投擲速度は Grabbable が与えている）
                _rolling = true;
                _rollStart = Time.time;
                _restSince = -1f;
                _value.Value = 0;
                AddTumble();
            }
            if (_rolling && !held) TickRoll();
            _wasHeld = held;
        }

        /// <summary>
        /// リリース直後にランダムなタンブル回転を与える。ピンチ投げは手首回転がほぼ乗らず、
        /// Grabbable の角速度推定が ~0 のまま飛んで出目が保存される（＝ランダム感が無い）ため、
        /// 投げの強さ（線速度）に応じた回転をサーバが必ず追加する。そっと置いた時（低速）は乱さない。
        /// </summary>
        private void AddTumble()
        {
            if (_rb == null || _rb.isKinematic) return;
            float speed = _rb.velocity.magnitude;
            if (speed < 0.3f) return; // 置いただけ（投げていない）は回さない
            // 0.3m/s→約7rad/s、1.5m/s 以上→上限18rad/s（maxAngularVelocity=20 の内側）
            float tumble = Mathf.Clamp(4f + speed * 9f, 7f, 18f);
            _rb.angularVelocity = Random.onUnitSphere * tumble;
        }

        /// <summary>サーバ: 転がり中の静止監視。静止確定 or タイムアウトで出目を読む。</summary>
        private void TickRoll()
        {
            if (_rb == null || _rb.isKinematic)
            {
                Settle(); // 物理なし構成の保険: 現在姿勢の上面で即確定
                return;
            }
            bool resting = _rb.IsSleeping() ||
                (_rb.velocity.sqrMagnitude < RestLinearSpeed * RestLinearSpeed &&
                 _rb.angularVelocity.sqrMagnitude < RestAngularSpeed * RestAngularSpeed);
            if (resting)
            {
                if (_restSince < 0f) _restSince = Time.time;
                else if (Time.time - _restSince >= RestSeconds)
                {
                    Settle();
                    return;
                }
            }
            else
            {
                _restSince = -1f;
            }
            if (Time.time - _rollStart >= RollTimeout)
            {
                _rb.Sleep(); // 縁立ち等の中途半端な姿勢で確定させる前に運動を止める
                Settle();
            }
        }

        private void Settle()
        {
            _rolling = false;
            int v = ReadTopFace();
            _value.Value = (byte)v;
            Debug.Log($"[TableDuo] Dice {name} → {v}（client{_lastHolder}・静止面読取）");
            DiceRolled?.Invoke(name, _lastHolder, v);
        }

        /// <summary>world up に最も揃うローカル軸から出目を読む。</summary>
        private int ReadTopFace()
        {
            var t = transform;
            Vector3[] axes = { t.right, -t.right, t.up, -t.up, t.forward, -t.forward };
            int best = 0;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < axes.Length; i++)
            {
                float d = Vector3.Dot(axes[i], Vector3.up);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }
            if (faceValues == null || faceValues.Length != 6)
            {
                Debug.LogWarning($"[TableDuo] DiceRoller {name}: faceValues が 6 要素でないため出目 1 扱い");
                return 1;
            }
            return Mathf.Clamp(faceValues[best], 1, 9);
        }
    }
}
