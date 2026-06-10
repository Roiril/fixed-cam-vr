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
                // 頭マーカー: 小さな球。視線・頷きの社会的キューだけ残す
                _head = CreatePrimitive(transform, PrimitiveType.Sphere, 0.06f, "HeadMarker", _markerMat);
            }
            else
            {
                _head = CreatePrimitive(transform, PrimitiveType.Cube, 0.18f, "Head", _avatarMat);
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
            /// <summary>トラッキングロスト時のフェード速度（スケール 1→0 が ~0.3s）。</summary>
            private const float FadePerSec = 3.5f;

            private readonly Transform _root;
            private readonly Transform _wristProxy;
            private Transform[]? _bones;
            private float _visibility = 1f;

            public HandView(Transform parent, string name)
            {
                _root = new GameObject(name).transform;
                _root.SetParent(parent, worldPositionStays: false);
                _wristProxy = CreatePrimitive(_root, PrimitiveType.Cube, 0.05f, "WristProxy");
            }

            public void Tick(float smooth, Vector3 wristPos, Quaternion wristRot, Quaternion[] boneRots,
                bool tracked, HandSkeletonLayout? layout)
            {
                // ロスト中はスケールフェードで消す（点滅させない — 要件 §4）
                _visibility = Mathf.MoveTowards(_visibility, tracked ? 1f : 0f,
                    FadePerSec * Time.deltaTime);
                bool visible = _visibility > 0.001f;
                if (_root.gameObject.activeSelf != visible)
                {
                    _root.gameObject.SetActive(visible);
                }
                if (!visible) return;
                _root.localScale = Vector3.one * _visibility;

                if (tracked)
                {
                    _root.localPosition = Vector3.Lerp(_root.localPosition, wristPos, smooth);
                    _root.localRotation = Quaternion.Slerp(_root.localRotation, wristRot, smooth);
                }

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
