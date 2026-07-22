#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// バンディド札のリリース時スナップ（サーバ権威・<see cref="Grabbable"/> のサイドカー。GeisterPieceSnap と同型）。
    /// IsHeld の true→false 遷移をサーバ側で検出し、<see cref="BandidoSnapLogic"/> の格子スナップ結果を transform に確定する:
    /// - XZ/yaw: 1×2 セル格子へ吸着（縦置き = 整数格子・横置き = 半セル格子。トンネルの道が繋がる）
    /// - 表裏: 現姿勢の up 向きを保持したまま平置き化（裏向きは長軸まわり 180° ロール）
    /// - Y: 同じ XZ フットプリントに重なる他カードがあればその上へ積む（山札へ戻す）。無ければ天板へ接地
    /// カードは Rigidbody なし（physics:false = kinematic 追従のみ）前提: リリース後は物理が介入しないため、
    /// スナップした姿勢がそのまま静止し続ける（転がり・沈み込み防止はこの構成自体で担保）。
    /// 保持中はスナップしない（手の中では自由に回して裏面を観察できる）。
    /// 各値は TableDuoSceneSetup.PlaceBandido が SerializedObject で焼き込む。
    /// </summary>
    public sealed class BandidoCardSnap : NetworkBehaviour
    {
        [Header("格子（TableDuoSceneSetup が設定。bandy 中心 = 格子原点）")]
        [SerializeField] private float gridOriginX;
        [SerializeField] private float gridOriginZ;
        [Tooltip("格子ピッチ = カード短辺（0.8 倍後 ≈35.2mm）。長辺はその 2 倍")]
        [SerializeField] private float cellPitch = 0.0352f;
        [Tooltip("天板上面 Y（積み先が無いときの接地面）")]
        [SerializeField] private float tableTopY;
        [Tooltip("スケール後のカード厚み（0.8 倍後 ≈1.2mm・積み重ねの参考値・接地は実 bounds で行う）")]
        [SerializeField] private float cardThickness = 0.0012f;

        // 積み重ね判定用の全カードレジストリ（都度 FindObjectsOfType を避ける）
        private static readonly List<BandidoCardSnap> All = new();

        // 積み重ねの微小空隙（z-fight・貫入回避）
        private const float StackGap = 0.0004f;

        private Grabbable? _grab;
        private Renderer[] _renderers = System.Array.Empty<Renderer>();
        private bool _wasHeld;

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            _renderers = GetComponentsInChildren<Renderer>();
            if (_grab == null)
            {
                Debug.LogWarning($"[TableDuo] BandidoCardSnap on {name}: Grabbable がありません（スナップ無効）");
            }
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void Update()
        {
            if (!IsSpawned || !IsServer || _grab == null) return;
            bool held = _grab.IsHeld;
            if (!held && _wasHeld) SnapNow();
            _wasHeld = held;
        }

        private BandidoSnapLogic.Config BuildConfig() => new()
        {
            gridOriginX = gridOriginX,
            gridOriginZ = gridOriginZ,
            cellPitch = cellPitch,
        };

        private void SnapNow()
        {
            var cfg = BuildConfig();
            var r = BandidoSnapLogic.SnapRelease(transform.position, transform.rotation, in cfg);

            // 表裏保持の平置き化: 表 = yaw のみ / 裏 = 長軸（local Z）まわり 180° ロールで裏面を上に保ったまま平置き
            var rot = Quaternion.Euler(0f, r.yawDeg, 0f);
            if (!r.faceUp) rot *= Quaternion.Euler(0f, 0f, 180f);
            transform.rotation = rot;
            // XZ 先行確定（Y は仮のまま）: この XZ・回転で自分のフットプリントを bounds から取れるようにする
            transform.position = new Vector3(r.x, transform.position.y, r.z);

            // 積み上げ: 自分の XZ フットプリントと重なる他カード（非保持）の上面最大値へ載せる
            var mine = CurrentWorldBounds();
            float baseTop = tableTopY;
            foreach (var other in All)
            {
                if (other == this || other._grab == null || other._grab.IsHeld) continue;
                var ob = other.CurrentWorldBounds();
                if (OverlapXZ(mine, ob)) baseTop = Mathf.Max(baseTop, ob.max.y);
            }
            float bottomOffset = mine.min.y - transform.position.y; // 回転確定後の pivot→最下点
            transform.position = new Vector3(r.x, baseTop + StackGap - bottomOffset, r.z);
        }

        /// <summary>現在の姿勢での全 Renderer ワールドバウンディング（Renderer 無しは中心ゼロ幅）。</summary>
        private Bounds CurrentWorldBounds()
        {
            bool has = false;
            var b = new Bounds(transform.position, Vector3.zero);
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>XZ 平面（軸整列）でのバウンディング重なり判定（yaw スナップ済みなので AABB で足りる）。</summary>
        private static bool OverlapXZ(in Bounds a, in Bounds b) =>
            a.min.x <= b.max.x && a.max.x >= b.min.x &&
            a.min.z <= b.max.z && a.max.z >= b.min.z;
    }
}
