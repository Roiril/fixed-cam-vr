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
        private Transform? _head;
        private HandView? _left;
        private HandView? _right;
        private bool _handsOnly;

        public static RemoteAvatarView Create(Transform seatAnchor, bool handsOnly)
        {
            var go = new GameObject(handsOnly ? "RemoteAvatar(HandsOnly)" : "RemoteAvatar(Full)");
            go.transform.SetParent(seatAnchor, worldPositionStays: false);
            var view = go.AddComponent<RemoteAvatarView>();
            view._handsOnly = handsOnly;
            view.Build();
            return view;
        }

        private void Build()
        {
            if (_handsOnly)
            {
                // 頭マーカー: 小さな球。視線・頷きの社会的キューだけ残す
                _head = CreatePrimitive(transform, PrimitiveType.Sphere, 0.06f, "HeadMarker");
            }
            else
            {
                _head = CreatePrimitive(transform, PrimitiveType.Cube, 0.18f, "Head");
            }
            _left = new HandView(transform, "HandL");
            _right = new HandView(transform, "HandR");
        }

        public void Apply(AvatarPose pose)
        {
            if (_head != null)
            {
                _head.localPosition = pose.HeadPos;
                _head.localRotation = pose.HeadRot;
            }
            _left?.Apply(pose.WristPosL, pose.WristRotL, pose.BonesL, pose.TrackedL, HandSkeletonLayout.CapturedL);
            _right?.Apply(pose.WristPosR, pose.WristRotR, pose.BonesR, pose.TrackedR, HandSkeletonLayout.CapturedR);
        }

        private static Transform CreatePrimitive(Transform parent, PrimitiveType type, float scale, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
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

            public HandView(Transform parent, string name)
            {
                _root = new GameObject(name).transform;
                _root.SetParent(parent, worldPositionStays: false);
                _wristProxy = CreatePrimitive(_root, PrimitiveType.Cube, 0.05f, "WristProxy");
            }

            public void Apply(Vector3 wristPos, Quaternion wristRot, Quaternion[] boneRots,
                bool tracked, HandSkeletonLayout? layout)
            {
                // v1 はフェード無しの即時表示切替（点滅はしない: tracked は受信側で安定している前提）
                if (_root.gameObject.activeSelf != tracked)
                {
                    _root.gameObject.SetActive(tracked);
                }
                if (!tracked) return;

                _root.localPosition = wristPos;
                _root.localRotation = wristRot;

                if (_bones == null && layout != null)
                {
                    BuildBones(layout);
                    _wristProxy.gameObject.SetActive(false);
                }
                if (_bones == null || layout == null) return;

                int n = layout.BoneCount;
                for (int i = 0; i < n; i++)
                {
                    _bones[i].localRotation = boneRots[i];
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
