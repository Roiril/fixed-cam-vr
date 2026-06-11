#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// リモートプレイヤーの v1 アバター描画。席アンカー基準でプリミティブを駆動する。
    /// - フル（席0）: 頭ボックス + 両手
    /// - 手だけ（席1）: 両手 + 最小の頭マーカー（社会的キュー用）
    /// 手は HandSkeletonLayout があれば 24 bone の関節球、無ければ手首キューブ。
    /// レイアウトは遅延構築（リモート接続時点で未キャプチャでも後から生える）。
    /// </summary>
    public sealed class RemoteAvatarView : MonoBehaviour
    {
        /// <summary>受信レート(30Hz)→描画レート(72-90Hz)の指数平滑係数。大きいほど追従が速い。</summary>
        private const float SmoothK = 20f;

        private Transform? _head;
        private Transform? _chest;
        private HandView? _left;
        private HandView? _right;
        private bool _handsOnly;

        private readonly AvatarPose _target = new();
        private bool _hasTarget;

        public static RemoteAvatarView Create(Transform seatAnchor, bool handsOnly)
        {
            var go = new GameObject(handsOnly ? "RemoteAvatar(HandsOnly)" : "RemoteAvatar(Full)");
            go.transform.SetParent(seatAnchor, worldPositionStays: false);
            var view = go.AddComponent<RemoteAvatarView>();
            view._handsOnly = handsOnly;
            view.Build();
            return view;
        }

        private static Material? _avatarMat;
        private static Material? _markerMat;
        private static bool _matsLoaded;

        private void Build()
        {
            if (!_matsLoaded)
            {
                // Setup メニューが Assets/TableDuo/Resources/ に生成する URP マテリアル。
                // ランタイム生成プリミティブの内蔵 Standard は URP 実機ビルドでマゼンタ化するため必須
                _avatarMat = Resources.Load<Material>("TableDuoAvatar");
                _markerMat = Resources.Load<Material>("TableDuoHeadMarker");
                _matsLoaded = true;
                if (_avatarMat == null)
                {
                    Debug.LogWarning("[TableDuo] TableDuoAvatar.mat が Resources に無い（Setup 未実行？）— 既定マテリアルで続行");
                }
            }

            if (_handsOnly)
            {
                // 頭マーカーは調査条件（StudyConfig）。既定 OFF —
                // 「どこに話しかけるか」の曖昧さ自体が RQ2/RQ3 の観察対象（study-design.md §2）
                if (StudyConfig.ShowHeadMarker)
                {
                    _head = CreatePrimitive(transform, PrimitiveType.Sphere, 0.06f, "HeadMarker", _markerMat);
                }
            }
            else
            {
                _head = CreatePrimitive(transform, PrimitiveType.Cube, 0.18f, "Head", _avatarMat);
                // 胴体（フルアバターのみ）。席に座る上半身として頭の下へ。
                // 腕 IK は意図的に無し（肘推定の破綻リスク — 要件 §5）
                _chest = CreatePrimitive(transform, PrimitiveType.Cube, 1f, "Chest", _avatarMat);
                _chest.localScale = new Vector3(0.34f, 0.42f, 0.16f);
            }
            _left = new HandView(transform, "HandL");
            _right = new HandView(transform, "HandR");
        }

        /// <summary>受信 pose をターゲットに記録。実際の追従は Update で平滑化して行う。</summary>
        public void Apply(AvatarPose pose)
        {
            _target.CopyFrom(pose);
            _hasTarget = true;
        }

        private void Update()
        {
            if (!_hasTarget) return;
            // 30Hz 受信を描画フレームへ指数平滑（フレームレート非依存）
            float a = 1f - Mathf.Exp(-SmoothK * Time.deltaTime);

            if (_head != null)
            {
                _head.localPosition = Vector3.Lerp(_head.localPosition, _target.HeadPos, a);
                _head.localRotation = Quaternion.Slerp(_head.localRotation, _target.HeadRot, a);
            }
            if (_chest != null && _head != null)
            {
                // 胴体は頭へ緩く追従（x/z は 6 割だけ・首 0.30m 下・yaw のみゆっくり）
                float chestA = 1f - Mathf.Exp(-6f * Time.deltaTime);
                var headP = _head.localPosition;
                var chestTarget = new Vector3(headP.x * 0.6f, headP.y - 0.30f - 0.21f, headP.z * 0.6f);
                _chest.localPosition = Vector3.Lerp(_chest.localPosition, chestTarget, chestA);
                float headYaw = _head.localEulerAngles.y;
                _chest.localRotation = Quaternion.Slerp(
                    _chest.localRotation, Quaternion.Euler(0f, headYaw, 0f), chestA);
            }
            _left?.Tick(a, _target.WristPosL, _target.WristRotL, _target.BonesL,
                _target.TrackedL, HandSkeletonLayout.CapturedL);
            _right?.Tick(a, _target.WristPosR, _target.WristRotR, _target.BonesR,
                _target.TrackedR, HandSkeletonLayout.CapturedR);
        }

        private static Transform CreatePrimitive(Transform parent, PrimitiveType type, float scale,
            string name, Material? mat = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            if (mat == null) mat = _avatarMat; // 既定はアバター色
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localScale = Vector3.one * scale;
            return go.transform;
        }

        /// <summary>片手の描画。bone 階層は layout が手に入った時点で遅延構築。</summary>
        private sealed class HandView
        {
            private readonly Transform _root;
            private readonly Transform _wristProxy;
            private Transform[]? _bones;
            private bool _everTracked;

            public HandView(Transform parent, string name)
            {
                _root = new GameObject(name).transform;
                _root.SetParent(parent, worldPositionStays: false);
                _root.gameObject.SetActive(false); // 一度トラッキングされるまで非表示（片手モードの左手対策）
                _wristProxy = CreatePrimitive(_root, PrimitiveType.Cube, 0.05f, "WristProxy");
            }

            public void Tick(float smooth, Vector3 wristPos, Quaternion wristRot, Quaternion[] boneRots,
                bool tracked, HandSkeletonLayout? layout)
            {
                // ロスト時は「最終姿勢でフリーズ」（調査仕様 — 消すと相手が
                // 無に向かって話す時間が混入し RQ2/RQ3 を汚染する。ロスト区間は SessionLogger が記録）
                if (!tracked)
                {
                    return; // 表示状態・姿勢を保持したまま何もしない
                }
                if (!_everTracked)
                {
                    _everTracked = true;
                    _root.gameObject.SetActive(true);
                }

                _root.localPosition = Vector3.Lerp(_root.localPosition, wristPos, smooth);
                _root.localRotation = Quaternion.Slerp(_root.localRotation, wristRot, smooth);

                if (_bones == null && layout != null)
                {
                    BuildBones(layout);
                    _wristProxy.gameObject.SetActive(false);
                }
                if (_bones == null || layout == null) return;

                int n = layout.BoneCount;
                for (int i = 0; i < n; i++)
                {
                    _bones[i].localRotation = Quaternion.Slerp(_bones[i].localRotation, boneRots[i], smooth);
                }
            }

            private void BuildBones(HandSkeletonLayout layout)
            {
                _bones = new Transform[layout.BoneCount];
                for (int i = 0; i < layout.BoneCount; i++)
                {
                    var bone = new GameObject($"Bone{i}").transform;
                    int parent = layout.ParentIndex[i];
                    bone.SetParent(parent >= 0 && parent < i ? _bones[parent] : _root,
                        worldPositionStays: false);
                    bone.localPosition = layout.BindLocalPos[i];
                    bone.localRotation = layout.BindLocalRot[i];
                    CreatePrimitive(bone, PrimitiveType.Sphere, 0.014f, "Joint");
                    _bones[i] = bone;
                }
            }
        }
    }
}
