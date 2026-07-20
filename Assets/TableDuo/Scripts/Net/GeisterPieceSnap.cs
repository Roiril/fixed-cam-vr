#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ガイスター駒のリリース時スナップ（サーバ権威・<see cref="Grabbable"/> のサイドカー。DiceRoller と同型）。
    /// IsHeld の true→false 遷移をサーバ側で検出し、<see cref="GeisterSnapLogic"/> の結果を transform に確定する:
    /// - 盤上: 最寄り空きセル中心へ吸着・正面 = 相手方向（onBoardYawDeg。裏の色マーカーを相手に見せない）・盤上面へ接地
    /// - 盤外: XZ と yaw を維持して直立化（転倒防止）・天板へ接地 = 捕獲駒の裏面を自由に確認できる
    /// 駒は Rigidbody なし（kinematic 追従のみ）前提: リリース後は物理が介入しないため、
    /// スナップした姿勢がそのまま静止し続ける（転がり防止はこの構成自体で担保）。
    /// 保持中はスナップしない（手の中では自由に回して裏面を観察できる）。
    /// 各値は TableDuoSceneSetup.PlaceGeister が SerializedObject で焼き込む。
    /// </summary>
    public sealed class GeisterPieceSnap : NetworkBehaviour
    {
        [Header("盤グリッド（TableDuoSceneSetup が設定）")]
        [SerializeField] private float boardCenterX;
        [SerializeField] private float boardCenterZ;
        [Tooltip("盤上接地 Y（駒の最下点を載せる高さ）")]
        [SerializeField] private float boardTopY;
        [Tooltip("盤外接地 Y（天板上面）")]
        [SerializeField] private float tableTopY;
        [SerializeField] private float cellPitch = 0.065f;
        [SerializeField] private int cellsPerSide = 6;
        [Tooltip("盤上スナップ時の transform yaw。相手方向 + モデル正面補正を焼き込み済みの値")]
        [SerializeField] private float onBoardYawDeg;

        // 占有セル判定用の全駒レジストリ（8 体・都度 FindObjectsOfType を避ける）
        private static readonly List<GeisterPieceSnap> All = new();

        private Grabbable? _grab;
        private Renderer[] _renderers = System.Array.Empty<Renderer>();
        private bool _wasHeld;

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            _renderers = GetComponentsInChildren<Renderer>();
            if (_grab == null)
            {
                Debug.LogWarning($"[TableDuo] GeisterPieceSnap on {name}: Grabbable がありません（スナップ無効）");
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

        private GeisterSnapLogic.Config BuildConfig() => new()
        {
            boardCenterX = boardCenterX,
            boardCenterZ = boardCenterZ,
            cellPitch = cellPitch,
            cellsPerSide = cellsPerSide,
        };

        /// <summary>他の駒（非保持）が占有しているセルの bitmask。現在姿勢から都度導出（状態を持たない =
        /// BoardReset / GameSwitcher 復元で transform が動いても整合が崩れない）。</summary>
        private ulong OccupiedMask(in GeisterSnapLogic.Config cfg)
        {
            ulong mask = 0;
            foreach (var other in All)
            {
                if (other == this || other._grab == null || other._grab.IsHeld) continue;
                var p = other.transform.position;
                if (GeisterSnapLogic.TryGetCell(p.x, p.z, in cfg, out int col, out int row))
                {
                    mask |= 1UL << GeisterSnapLogic.CellIndex(col, row, in cfg);
                }
            }
            return mask;
        }

        private void SnapNow()
        {
            var cfg = BuildConfig();
            var r = GeisterSnapLogic.SnapRelease(transform.position, transform.rotation,
                in cfg, OccupiedMask(in cfg), onBoardYawDeg);
            transform.rotation = Quaternion.Euler(0f, r.yawDeg, 0f);
            // 接地: 直立化後の bounds から pivot→最下点オフセットを取り、面上に載せる
            float surfaceY = r.onBoard ? boardTopY : tableTopY;
            transform.position = new Vector3(r.x, surfaceY - CurrentBottomOffset(), r.z);
        }

        /// <summary>現在の姿勢での pivot→最下点オフセット（Grabbable.CurrentBottomOffset と同法）。</summary>
        private float CurrentBottomOffset()
        {
            float minY = float.PositiveInfinity;
            foreach (var r in _renderers)
            {
                if (r != null) minY = Mathf.Min(minY, r.bounds.min.y);
            }
            return float.IsPositiveInfinity(minY) ? 0f : minY - transform.position.y;
        }
    }
}
