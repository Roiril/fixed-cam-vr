#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>仮想体験専用の Remy。色と距離同期の歩行は本番プレハブへ保存しない。</summary>
    public sealed class VisitorAvatar : IDisposable
    {
        private const string AssetRoot = "Assets/Editor/VirtualWalkAssets/";
        private const int MeasureSamples = 120;
        private readonly GameObject _npc;
        private readonly GameObject _source;
        private readonly AnimationClip _clip;
        private readonly Bone[] _bones;
        private readonly Transform _hips, _sourceHips, _leftToe, _rightToe;
        private readonly Vector3 _hipsRest, _sourceHipsRest;
        private readonly float _hipScale, _strideM;
        private readonly Skin[] _skins;
        private readonly Skin _shoes;
        private readonly List<Vector2> _footprintPoints = new();
        private readonly List<Vector2> _footprintHull = new();
        private bool _disposed;

        private sealed class Bone
        {
            public readonly Transform Target, Source;
            public readonly Quaternion TargetRest, SourceRest, Idle;
            public Bone(Transform target, Transform source, Quaternion rest)
            {
                Target = target;
                Source = source;
                TargetRest = rest;
                SourceRest = source.localRotation;
                Idle = target.localRotation;
            }
        }

        // BakeMesh は EditMode の描画更新前に古い骨行列を返し得るので、計測は現在の Transform から行う。
        private sealed class Skin
        {
            public readonly Vector3[] Vertices;
            private readonly BoneWeight[] _weights;
            private readonly Matrix4x4[] _bindposes, _matrices;
            private readonly Transform[] _bones;

            public Skin(SkinnedMeshRenderer renderer)
            {
                Mesh mesh = renderer.sharedMesh;
                Vertices = mesh.vertices;
                _weights = mesh.boneWeights;
                _bindposes = mesh.bindposes;
                _bones = renderer.bones;
                _matrices = new Matrix4x4[_bones.Length];
                if (Vertices.Length == 0 || _weights.Length != Vertices.Length || _bindposes.Length != _bones.Length)
                    throw new InvalidOperationException($"[VirtualWalk] {renderer.name} のスキンを測定できません。");
            }

            public void UpdateMatrices()
            {
                for (int i = 0; i < _bones.Length; i++)
                    _matrices[i] = _bones[i].localToWorldMatrix * _bindposes[i];
            }

            public Vector3 WorldVertex(int i)
            {
                BoneWeight w = _weights[i];
                Vector3 v = Vertices[i];
                Vector3 result = _matrices[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0;
                if (w.weight1 > 0f) result += _matrices[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1;
                if (w.weight2 > 0f) result += _matrices[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2;
                if (w.weight3 > 0f) result += _matrices[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3;
                return result;
            }
        }

        public Transform Head { get; }
        public float AnimationTime { get; private set; }
        public float MaxBodyRadiusM { get; private set; }
        public float IdleBodyRadiusM { get; private set; }
        public float StandingHeightM { get; }
        public string Description { get; }

        /// <summary>現在の骨姿勢の全頂点から求めた身長。Editor の Renderer.bounds を使わない。</summary>
        public static float MeasurePoseHeight(GameObject actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            SkinnedMeshRenderer[] renderers = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var skins = new Skin[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) skins[i] = new Skin(renderers[i]);
            return MeasureHeight(skins);
        }

        private static float MeasureHeight(Skin[] skins)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (Skin skin in skins)
            {
                skin.UpdateMatrices();
                for (int v = 0; v < skin.Vertices.Length; v++)
                {
                    float y = skin.WorldVertex(v).y;
                    if (float.IsNaN(y) || float.IsInfinity(y))
                        throw new InvalidOperationException("[VirtualWalk] 身長の測定に不正な頂点があります。");
                    min = Mathf.Min(min, y);
                    max = Mathf.Max(max, y);
                }
            }
            float height = max - min;
            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
                throw new InvalidOperationException("[VirtualWalk] 人物の身長を測定できません。");
            return height;
        }

        public VisitorAvatar(GameObject npc, List<UnityEngine.Object> trash)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            if (trash == null) throw new ArgumentNullException(nameof(trash));
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetRoot + "WalkingBind.fbx")
                ?? throw new InvalidOperationException("[VirtualWalk] WalkingBind.fbx がありません。");
            AnimationClip? clip = null;
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(AssetRoot + "Walking.fbx"))
                if (asset is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal)
                    && c.length > 0.5f) { clip = c; break; }
            _clip = clip ?? throw new InvalidOperationException("[VirtualWalk] 歩行クリップがありません。");
            _source = UnityEngine.Object.Instantiate(model);
            _source.name = "Visitor walk sampling rig";
            _source.hideFlags = HideFlags.HideAndDontSave;
            trash.Add(_source);
            foreach (Renderer r in _source.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (Animator a in _source.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            foreach (Animator a in npc.GetComponentsInChildren<Animator>(true)) a.enabled = false;

            Dictionary<string, Transform> targets = Index(npc.transform);
            Dictionary<string, Transform> sources = Index(_source.transform);
            Head = Require(targets, "head");
            _hips = Require(targets, "hips");
            _sourceHips = Require(sources, "hips");
            _leftToe = Require(targets, "lefttoebase");
            _rightToe = Require(targets, "righttoebase");
            _hipsRest = _hips.localPosition;
            _sourceHipsRest = _sourceHips.localPosition;
            if (Mathf.Abs(_sourceHipsRest.y) < 0.001f)
                throw new InvalidOperationException("[VirtualWalk] 歩行の腰高が不正です。");
            _hipScale = _hipsRest.y / _sourceHipsRest.y;
            foreach (string side in new[] { "left", "right" })
            {
                Vector3 direction = _source.transform.InverseTransformDirection(
                    Require(sources, side + "forearm").position - Require(sources, side + "arm").position).normalized;
                if (Mathf.Abs(direction.y) > 0.3f)
                    throw new InvalidOperationException("[VirtualWalk] 歩行の基準骨が T ポーズではありません。WalkingBind の animation import を確認してください。");
            }
            var rest = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
            foreach (var pair in targets) rest[pair.Key] = pair.Value.localRotation;
            NpcWalkPose idle = NpcWalkPose.Bind(npc.transform)
                ?? throw new InvalidOperationException("[VirtualWalk] Remy の骨がありません。");
            idle.Apply(0f, 0f);
            var bones = new List<Bone>();
            foreach (var pair in sources)
                if (pair.Key != Normalize(_source.name) && targets.TryGetValue(pair.Key, out Transform target))
                    bones.Add(new Bone(target, pair.Value, rest[pair.Key]));
            _bones = bones.ToArray();
            if (_bones.Length < 50)
                throw new InvalidOperationException($"[VirtualWalk] 歩行の骨対応が不足しています: {_bones.Length}");

            _shoes = null!;
            var skins = new List<Skin>();
            foreach (SkinnedMeshRenderer renderer in npc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.updateWhenOffscreen = true;
                renderer.forceMatrixRecalculationPerRender = true;
                var skin = new Skin(renderer);
                skins.Add(skin);
                if (renderer.name == "Shoes") _shoes = skin;
            }
            _skins = skins.ToArray();
            if (_shoes == null) throw new InvalidOperationException("[VirtualWalk] Remy の靴がありません。");
            ApplyMaterials(npc, trash);
            foreach (Bone bone in _bones) bone.Target.localRotation = bone.TargetRest;
            _hips.localPosition = _hipsRest;
            float bindRadius = MeasureCurrentRadius();
            _strideM = MeasureStride();
            MeasureBodyRadius();
            SamplePose(0f, 1f);
            foreach (string side in new[] { "left", "right" })
            {
                Vector3 direction = npc.transform.InverseTransformDirection(
                    Require(targets, side + "forearm").position - Require(targets, side + "arm").position).normalized;
                if (direction.y > -0.5f)
                    throw new InvalidOperationException("[VirtualWalk] 歩行中の腕が下がっていません。基準姿勢と骨の対応を確認してください。");
            }
            Apply(0f, 0f);
            StandingHeightM = MeasureHeight(_skins);
            Description = string.Format(CultureInfo.InvariantCulture,
                "Remy embedded diffuse; Walking/mixamo.com {0:F3}s {1:F0}fps; stride={2:F3}m; radius={3:F3}m; bones={4}; tposeRadius={5:F3}m; idleRadius={6:F3}m; standingHeight={7:F3}m",
                _clip.length, _clip.frameRate, _strideM, MaxBodyRadiusM, _bones.Length, bindRadius, IdleBodyRadiusM, StandingHeightM);
        }

        public void Apply(float distanceWalkedM, float walk01)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VisitorAvatar));
            if (float.IsNaN(distanceWalkedM) || float.IsInfinity(distanceWalkedM)
                || float.IsNaN(walk01) || float.IsInfinity(walk01))
                throw new ArgumentOutOfRangeException(nameof(distanceWalkedM), "歩行距離と混合率は有限値にしてください。");
            AnimationTime = Mathf.Repeat(Mathf.Max(0f, distanceWalkedM) / _strideM, 1f) * _clip.length;
            SamplePose(AnimationTime, Mathf.Clamp01(walk01));
            // 靴底の実メッシュを床へ置く。root の位置・向きは呼び出し側のものを保つ。
            _shoes.UpdateMatrices();
            float minY = float.PositiveInfinity;
            for (int i = 0; i < _shoes.Vertices.Length; i++)
                minY = Mathf.Min(minY, _shoes.WorldVertex(i).y);
            if (float.IsInfinity(minY)) throw new InvalidOperationException("[VirtualWalk] 靴の頂点がありません。");
            _hips.position += Vector3.up * (_npc.transform.position.y - minY);
        }

        /// <summary>現在の全身メッシュを世界座標 XZ に投影した凸包。反時計回りで終点は重複しない。</summary>
        public Vector2[] Footprint()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VisitorAvatar));
            _footprintPoints.Clear();
            _footprintHull.Clear();
            foreach (Skin skin in _skins)
            {
                skin.UpdateMatrices();
                for (int v = 0; v < skin.Vertices.Length; v++)
                {
                    Vector3 world = skin.WorldVertex(v);
                    if (float.IsNaN(world.x) || float.IsInfinity(world.x)
                        || float.IsNaN(world.z) || float.IsInfinity(world.z))
                        throw new InvalidOperationException("[VirtualWalk] 人物の占有範囲に不正な頂点があります。");
                    _footprintPoints.Add(new Vector2(world.x, world.z));
                }
            }
            _footprintPoints.Sort(ComparePoint);
            int unique = 0;
            for (int i = 0; i < _footprintPoints.Count; i++)
            {
                Vector2 point = _footprintPoints[i];
                if (unique > 0 && point.x == _footprintPoints[unique - 1].x
                    && point.y == _footprintPoints[unique - 1].y) continue;
                _footprintPoints[unique++] = point;
            }
            if (unique < _footprintPoints.Count)
                _footprintPoints.RemoveRange(unique, _footprintPoints.Count - unique);
            if (unique <= 1) return _footprintPoints.ToArray();

            foreach (Vector2 point in _footprintPoints)
            {
                while (_footprintHull.Count >= 2 && Turn(_footprintHull[_footprintHull.Count - 2],
                    _footprintHull[_footprintHull.Count - 1], point) <= 0d)
                    _footprintHull.RemoveAt(_footprintHull.Count - 1);
                _footprintHull.Add(point);
            }
            int lower = _footprintHull.Count;
            for (int i = unique - 2; i >= 0; i--)
            {
                Vector2 point = _footprintPoints[i];
                while (_footprintHull.Count > lower && Turn(_footprintHull[_footprintHull.Count - 2],
                    _footprintHull[_footprintHull.Count - 1], point) <= 0d)
                    _footprintHull.RemoveAt(_footprintHull.Count - 1);
                _footprintHull.Add(point);
            }
            _footprintHull.RemoveAt(_footprintHull.Count - 1);
            return _footprintHull.ToArray();
        }

        private static int ComparePoint(Vector2 a, Vector2 b)
        {
            int x = a.x.CompareTo(b.x);
            return x != 0 ? x : a.y.CompareTo(b.y);
        }

        private static double Turn(Vector2 a, Vector2 b, Vector2 c)
            => ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

        private void SamplePose(float time, float weight)
        {
            _clip.SampleAnimation(_source, time);
            foreach (Bone bone in _bones)
            {
                Quaternion rotation = bone.TargetRest * Quaternion.Inverse(bone.SourceRest) * bone.Source.localRotation;
                bone.Target.localRotation = Quaternion.Slerp(bone.Idle, rotation, weight);
            }
            _hips.localPosition = _hipsRest + Vector3.up * ((_sourceHips.localPosition.y - _sourceHipsRest.y) * _hipScale * weight);
        }

        private float MeasureStride()
        {
            var left = new Vector3[MeasureSamples + 1];
            var right = new Vector3[MeasureSamples + 1];
            float leftMin = float.PositiveInfinity, rightMin = float.PositiveInfinity;
            for (int i = 0; i <= MeasureSamples; i++)
            {
                SamplePose(_clip.length * i / MeasureSamples, 1f);
                left[i] = _npc.transform.InverseTransformPoint(_leftToe.position);
                right[i] = _npc.transform.InverseTransformPoint(_rightToe.position);
                leftMin = Mathf.Min(leftMin, left[i].y);
                rightMin = Mathf.Min(rightMin, right[i].y);
            }
            var speeds = new List<float>();
            float yScale = _npc.transform.TransformVector(Vector3.up).magnitude;
            float zScale = _npc.transform.TransformVector(Vector3.forward).magnitude;
            float dt = _clip.length / MeasureSamples;
            CollectSupportSpeeds(left, leftMin, yScale, zScale, dt, speeds);
            CollectSupportSpeeds(right, rightMin, yScale, zScale, dt, speeds);
            if (speeds.Count < 4) throw new InvalidOperationException("[VirtualWalk] 接地足の歩幅を測れません。");
            speeds.Sort();
            float stride = speeds[speeds.Count / 2] * _clip.length;
            if (stride < 0.2f || stride > 3f)
                throw new InvalidOperationException($"[VirtualWalk] 歩幅の測定値が不正です: {stride:F3}m");
            return stride;
        }

        private static void CollectSupportSpeeds(Vector3[] positions, float floor, float yScale,
                                                  float zScale, float dt, List<float> speeds)
        {
            for (int i = 1; i < positions.Length; i++)
            {
                float y = (positions[i - 1].y + positions[i].y) * 0.5f;
                float speed = -(positions[i].z - positions[i - 1].z) * zScale / dt;
                if ((y - floor) * yScale <= 0.012f && speed > 0.05f) speeds.Add(speed);
            }
        }

        private void MeasureBodyRadius()
        {
            for (int i = -1; i < 32; i++)
            {
                SamplePose(i < 0 ? 0f : _clip.length * i / 32f, i < 0 ? 0f : 1f);
                float radius = MeasureCurrentRadius();
                if (i < 0) IdleBodyRadiusM = radius;
                MaxBodyRadiusM = Mathf.Max(MaxBodyRadiusM, radius);
            }
        }

        private float MeasureCurrentRadius()
        {
            float radius = 0f;
            foreach (Skin skin in _skins)
            {
                skin.UpdateMatrices();
                for (int v = 0; v < skin.Vertices.Length; v++)
                {
                    Vector3 offset = skin.WorldVertex(v) - _npc.transform.position;
                    radius = Mathf.Max(radius, new Vector2(offset.x, offset.z).magnitude);
                }
            }
            return radius;
        }

        private static void ApplyMaterials(GameObject npc, List<UnityEngine.Object> trash)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? throw new InvalidOperationException("[VirtualWalk] URP/Lit がありません。");
            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (Renderer renderer in npc.GetComponentsInChildren<Renderer>(true))
            {
                string part = renderer.name;
                string textureName = part switch
                {
                    "Shoes" => "Shoes", "Tops" => "Top", "Bottoms" => "Bottom", "Hair" => "Hair",
                    "Body" or "Eyes" or "Eyelashes" => "Body",
                    _ => throw new InvalidOperationException($"[VirtualWalk] Remy の未対応メッシュ: {part}")
                };
                string key = part == "Eyes" ? "Body" : part;
                if (!materials.TryGetValue(key, out Material material))
                {
                    Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetRoot + "Remy_" + textureName + "_Diffuse.png")
                        ?? throw new InvalidOperationException($"[VirtualWalk] {textureName} の色がありません。");
                    bool cutout = part == "Hair" || part == "Eyelashes";
                    if (cutout)
                    {
                        Texture2D opacity = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetRoot + "Remy_" + textureName + "_Opacity.png")
                            ?? throw new InvalidOperationException($"[VirtualWalk] {textureName} の透過画像がありません。");
                        Color32[] colors = texture.GetPixels32();
                        Color32[] alpha = opacity.GetPixels32();
                        if (colors.Length != alpha.Length)
                            throw new InvalidOperationException($"[VirtualWalk] {textureName} の透過画像の寸法が違います。");
                        for (int i = 0; i < colors.Length; i++) colors[i].a = alpha[i].r;
                        texture = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, true)
                        { name = "Visitor " + part + " cutout", hideFlags = HideFlags.HideAndDontSave };
                        texture.SetPixels32(colors);
                        texture.Apply(true, true);
                        trash.Add(texture);
                    }
                    material = new Material(shader) { name = "Visitor " + part, hideFlags = HideFlags.HideAndDontSave };
                    material.SetTexture("_BaseMap", texture);
                    material.SetColor("_BaseColor", Color.white);
                    material.SetFloat("_Smoothness", 0.15f);
                    material.SetFloat("_Cull", 0f);
                    if (cutout)
                    {
                        material.SetFloat("_AlphaClip", 1f);
                        material.SetFloat("_Cutoff", 0.5f);
                        material.EnableKeyword("_ALPHATEST_ON");
                        material.SetOverrideTag("RenderType", "TransparentCutout");
                        material.renderQueue = 2450;
                    }
                    materials.Add(key, material);
                    trash.Add(material);
                }
                Material[] slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++) slots[i] = material;
                renderer.sharedMaterials = slots;
            }
        }

        private static Dictionary<string, Transform> Index(Transform root)
        {
            var result = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                string key = Normalize(transform.name);
                if (!result.ContainsKey(key)) result.Add(key, transform);
            }
            return result;
        }

        private static string Normalize(string name)
        {
            int colon = name.LastIndexOf(':');
            return (colon < 0 ? name : name.Substring(colon + 1)).ToLowerInvariant();
        }

        private static Transform Require(Dictionary<string, Transform> bones, string name)
            => bones.TryGetValue(name, out Transform bone) ? bone
                : throw new InvalidOperationException($"[VirtualWalk] 骨 {name} がありません。");

        // Unity オブジェクトの寿命は呼び出し側の trash が管理する。
        public void Dispose() => _disposed = true;
    }
}
