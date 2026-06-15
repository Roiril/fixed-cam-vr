#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// リモートプレイヤーの v1 アバター描画。席アンカー基準でプリミティブを駆動する。
    /// - フル（席0）: 簡易人型上半身（頭＝顔＋目 / 首 / 肩 / テーパー胴）+ 肩→手首の簡易腕（袖）+ 両手
    /// - 手だけ（席1）: 両手 + 最小の頭マーカー（社会的キュー用）
    /// 手は HandSkeletonLayout があれば 24 bone の関節球、無ければ手首キューブ。
    /// レイアウトは遅延構築（リモート接続時点で未キャプチャでも後から生える）。
    /// 追跡は頭＋手首＋指のみ（肩・肘・胴は無追跡）。腕は肘 IK を使わず肩→手首を直結した
    /// cosmetic な袖（破綻しない・手の位置を体の輪郭に反映）— 要件 §5 の「肘推定を持たない」を踏襲。
    /// </summary>
    public sealed class RemoteAvatarView : MonoBehaviour
    {
        /// <summary>受信レート(30Hz)→描画レート(72-90Hz)の指数平滑係数。大きいほど追従が速い。</summary>
        private const float SmoothK = 20f;

        // 肩の付け根（胴ローカル）。腕（袖）はここから手首へ伸びる。+X=アバターの右手側
        private static readonly Vector3 ShoulderOffsetR = new(0.17f, 0.24f, 0f);
        private static readonly Vector3 ShoulderOffsetL = new(-0.17f, 0.24f, 0f);

        private Transform? _head;
        private Transform? _chest;
        private Transform? _armL;
        private Transform? _armR;
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
        private static Material? _skinMat;
        private static Material? _shirtMat;
        private static Material? _eyeMat;
        private static bool _matsLoaded;

        // domain-reload 無効 Play で前回 Play の破棄済み Material 参照を引き継がないようリセット
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetMatCache()
        {
            _avatarMat = _markerMat = _skinMat = _shirtMat = _eyeMat = null;
            _matsLoaded = false;
        }

        private void Build()
        {
            if (!_matsLoaded)
            {
                // Setup メニューが Assets/TableDuo/Resources/ に生成する URP マテリアル（あれば優先＝アーティスト調整可）。
                // 無ければランタイムで URP/Lit を生成（Setup 未実行でも人型が肌色で出る・実機でもマゼンタ化しない）
                _avatarMat = Resources.Load<Material>("TableDuoAvatar");
                _markerMat = Resources.Load<Material>("TableDuoHeadMarker");
                // 研究中立な無個性配色（髪なしマネキン頭 + 無地の袖）。肌は単一の中間トーン
                _skinMat = GetOrCreateMat("TableDuoSkin", new Color(0.86f, 0.69f, 0.56f));
                _shirtMat = GetOrCreateMat("TableDuoShirt", new Color(0.32f, 0.40f, 0.52f));
                _eyeMat = GetOrCreateMat("TableDuoEye", new Color(0.12f, 0.12f, 0.14f));
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
                BuildHumanUpperBody();
            }
            _left = new HandView(transform, "HandL", isRight: false);
            _right = new HandView(transform, "HandR", isRight: true);
        }

        /// <summary>簡易人型の上半身（頭＝顔＋目 / 首 / 肩 / テーパー胴 / 肩→手首の袖）。フルアバター用。</summary>
        private void BuildHumanUpperBody()
        {
            // 頭グループ（位置・向きは Update で頭 pose 追従）。子に頭蓋・目を持たせ、向きが視線キューになる
            _head = new GameObject("Head").transform;
            _head.SetParent(transform, worldPositionStays: false);
            CreateShape(_head, PrimitiveType.Sphere, new Vector3(0.17f, 0.20f, 0.17f), Vector3.zero, _skinMat, "Skull");
            CreateShape(_head, PrimitiveType.Sphere, new Vector3(0.032f, 0.032f, 0.032f), new Vector3(0.040f, 0.015f, 0.082f), _eyeMat, "EyeR");
            CreateShape(_head, PrimitiveType.Sphere, new Vector3(0.032f, 0.032f, 0.032f), new Vector3(-0.040f, 0.015f, 0.082f), _eyeMat, "EyeL");

            // 胴グループ（頭へ緩く追従）。子に首・箱型の胴・丸めた肩（シンプル人間体型）
            _chest = new GameObject("Torso").transform;
            _chest.SetParent(transform, worldPositionStays: false);
            CreateShape(_chest, PrimitiveType.Cylinder, new Vector3(0.075f, 0.05f, 0.075f), new Vector3(0f, 0.27f, 0f), _skinMat, "Neck");
            CreateShape(_chest, PrimitiveType.Cube, new Vector3(0.34f, 0.46f, 0.20f), new Vector3(0f, 0.02f, 0f), _shirtMat, "Trunk");
            CreateShape(_chest, PrimitiveType.Sphere, new Vector3(0.17f, 0.16f, 0.18f), ShoulderOffsetR, _shirtMat, "ShoulderR");
            CreateShape(_chest, PrimitiveType.Sphere, new Vector3(0.17f, 0.16f, 0.18f), ShoulderOffsetL, _shirtMat, "ShoulderL");

            // 腕（袖）= 肩→手首を直結する cosmetic capsule。長さ・向きは Update で毎フレ更新。肘なし＝破綻しない
            _armR = CreateShape(transform, PrimitiveType.Capsule, Vector3.one * 0.05f, Vector3.zero, _shirtMat, "ArmR");
            _armL = CreateShape(transform, PrimitiveType.Capsule, Vector3.one * 0.05f, Vector3.zero, _shirtMat, "ArmL");
            _armR.gameObject.SetActive(false);
            _armL.gameObject.SetActive(false);
        }

        /// <summary>Resources に無ければ URP/Lit でランタイム生成する色マテリアル（共有・1回だけ）。</summary>
        private static Material? GetOrCreateMat(string resourceName, Color color)
        {
            var mat = Resources.Load<Material>(resourceName);
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return _avatarMat; // URP 不在の保険（通常起きない）
            return new Material(shader) { color = color };
        }

        /// <summary>受信 pose をターゲットに記録。実際の追従は Update で平滑化して行う。</summary>
        public void Apply(AvatarPose pose)
        {
            _target.CopyFrom(pose);
            _hasTarget = true;
        }

        /// <summary>pose を即時反映（平滑なしスナップ）。リプレイ seek・Editor プレビュー・スクショ用。</summary>
        public void PoseImmediate(AvatarPose pose)
        {
            _target.CopyFrom(pose);
            _hasTarget = true;
            ApplyToTransforms(1f, 1f);
        }

        private void Update()
        {
            if (!_hasTarget) return;
            // 30Hz 受信を描画フレームへ指数平滑（フレームレート非依存）
            float dt = Time.deltaTime;
            ApplyToTransforms(1f - Mathf.Exp(-SmoothK * dt), 1f - Mathf.Exp(-6f * dt));
        }

        /// <summary>ターゲット pose をパーツへ反映。a=頭/手の平滑係数、chestA=胴の平滑係数（1=即時）。</summary>
        private void ApplyToTransforms(float a, float chestA)
        {
            if (_head != null)
            {
                _head.localPosition = Vector3.Lerp(_head.localPosition, _target.HeadPos, a);
                _head.localRotation = Quaternion.Slerp(_head.localRotation, _target.HeadRot, a);
            }
            if (_chest != null && _head != null)
            {
                // 胴体は頭へ緩く追従（x/z は 6 割だけ・首ぶん下・yaw のみゆっくり）
                var headP = _head.localPosition;
                var chestTarget = new Vector3(headP.x * 0.6f, headP.y - 0.40f, headP.z * 0.6f);
                _chest.localPosition = Vector3.Lerp(_chest.localPosition, chestTarget, chestA);
                float headYaw = _head.localEulerAngles.y;
                _chest.localRotation = Quaternion.Slerp(
                    _chest.localRotation, Quaternion.Euler(0f, headYaw, 0f), chestA);
            }
            _left?.Tick(a, _target.WristPosL, _target.WristRotL, _target.BonesL,
                _target.TrackedL, HandSkeletonLayout.CapturedL);
            _right?.Tick(a, _target.WristPosR, _target.WristRotR, _target.BonesR,
                _target.TrackedR, HandSkeletonLayout.CapturedR);

            // 腕（袖）を肩→手首に張り直す。手が一度も出ていない間は隠す（片手モードの左手も自動で隠れる）
            UpdateArm(_armR, ShoulderOffsetR, _right);
            UpdateArm(_armL, ShoulderOffsetL, _left);
        }

        /// <summary>肩（胴ローカル offset）から手首（手 root のローカル位置）へ伸びる cosmetic な腕を更新。</summary>
        private void UpdateArm(Transform? arm, Vector3 shoulderOffset, HandView? hand)
        {
            if (arm == null || _chest == null || hand == null) return;
            bool show = hand.IsVisible;
            if (arm.gameObject.activeSelf != show) arm.gameObject.SetActive(show);
            if (!show) return;

            // すべて avatar-root ローカル空間で計算（肩は胴 transform で回り、手首は手 root のローカル位置）
            Vector3 shoulder = _chest.localPosition + _chest.localRotation * shoulderOffset;
            Vector3 wrist = hand.RootLocalPos;
            Vector3 dir = wrist - shoulder;
            float len = dir.magnitude;
            if (len < 1e-3f) return;
            arm.localPosition = (shoulder + wrist) * 0.5f;
            arm.localRotation = Quaternion.FromToRotation(Vector3.up, dir / len);
            arm.localScale = new Vector3(0.05f, len * 0.5f, 0.05f); // capsule 既定高 2 → 全長 = len
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

        /// <summary>非一様スケール + ローカル位置指定のプリミティブ（人型パーツ用）。</summary>
        private static Transform CreateShape(Transform parent, PrimitiveType type, Vector3 scale,
            Vector3 localPos, Material? mat, string name)
        {
            var t = CreatePrimitive(parent, type, 1f, name, mat);
            t.localScale = scale;
            t.localPosition = localPos;
            return t;
        }

        /// <summary>
        /// 片手の描画。Meta の白い手メッシュ（OVRCustomHandPrefab）を同期 bone で駆動するのを第一選択、
        /// 供給が無ければ関節を骨で繋ぐカプセル手にフォールバック。どちらも layout/プレハブが揃った時点で遅延構築。
        /// </summary>
        private sealed class HandView
        {
            private readonly Transform _root;
            private readonly Transform _wristProxy;
            private readonly bool _isRight;
            private bool _everTracked;
            private bool _built;
            private bool _meshTried; // メッシュ構築を一度試したか（失敗しても毎フレ再 Instantiate しないラッチ）

            // メッシュ手: bone Transform を BoneId 順（名前マッピング）に並べ、同期 bone で回す。
            // 要素は見つからなかった bone が null になりうる（適用時に null チェック）
            private Transform?[]? _meshBones;
            // カプセル手（フォールバック）
            private Transform[]? _bones;

            /// <summary>手 root のローカル位置（avatar-root 空間。腕の手首端に使う）。</summary>
            public Vector3 RootLocalPos => _root.localPosition;

            /// <summary>一度でもトラッキングされ表示中か（腕の表示ゲートに使う）。</summary>
            public bool IsVisible => _root.gameObject.activeSelf;

            public HandView(Transform parent, string name, bool isRight)
            {
                _isRight = isRight;
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

                if (!_built)
                {
                    // メッシュ構築は一度だけ試す（例外や失敗で毎フレ Instantiate を繰り返さないようラッチ）
                    if (!_meshTried)
                    {
                        _meshTried = true;
                        if (TryBuildMeshHandSafe())
                        {
                            _built = true;
                            _wristProxy.gameObject.SetActive(false);
                        }
                    }
                    if (!_built && layout != null) // メッシュ供給が無い/失敗 → カプセル（layout 待ち）
                    {
                        BuildBones(layout);
                        _built = true;
                        _wristProxy.gameObject.SetActive(false);
                    }
                }

                if (_meshBones != null)
                {
                    int n = Mathf.Min(boneRots.Length, _meshBones.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (_meshBones[i] != null)
                        {
                            _meshBones[i].localRotation = Quaternion.Slerp(_meshBones[i].localRotation, boneRots[i], smooth);
                        }
                    }
                    return;
                }

                if (_bones == null || layout == null) return;
                int m = layout.BoneCount;
                for (int i = 0; i < m; i++)
                {
                    _bones[i].localRotation = Quaternion.Slerp(_bones[i].localRotation, boneRots[i], smooth);
                }
            }

            /// <summary>
            /// Meta の白い手メッシュ（OVRCustomHandPrefab）をインスタンス化し、駆動系（OVRHand/OVRCustomSkeleton/
            /// Animator）を剥がして SkinnedMeshRenderer + bone Transform だけ残す。bone は同期データで回す。
            /// bone は <see cref="RemoteHandMeshProvider.MapHandBonesByName"/> で BoneId 順に名前検索する
            /// （OVRCustomSkeleton.CustomBones は package 同梱状態で未マッピング＝全 null のため使えない。
            /// これに頼ると指ボーンが一切回らず bind ポーズ＝開いた手で固定される）。供給が無ければ false。
            /// </summary>
            /// <summary>例外で Tick ループ全体を殺さない/中途半端な inst を残さないラッパ。</summary>
            private bool TryBuildMeshHandSafe()
            {
                try
                {
                    return TryBuildMeshHand();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[TableDuo] メッシュ手の構築に失敗 → カプセルにフォールバック: {e.Message}");
                    _meshBones = null;
                    return false;
                }
            }

            private bool TryBuildMeshHand()
            {
                var provider = RemoteHandMeshProvider.Instance;
                var prefab = provider != null ? provider.GetPrefab(_isRight) : null;
                if (provider == null || prefab == null) return false;

                var inst = Object.Instantiate(prefab, _root, worldPositionStays: false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.SetActive(true);

                // CustomBones に頼らず Meta の FBX 命名規則で bone Transform を実体検索（index = BoneId = 同期 boneRots）
                var mapped = RemoteHandMeshProvider.MapHandBonesByName(inst.transform, _isRight);
                bool anyMapped = false;
                foreach (var b in mapped) { if (b != null) { anyMapped = true; break; } }
                if (!anyMapped) { Object.Destroy(inst); return false; } // 名前不一致（SDK 改名等）→ カプセルへ
                _meshBones = mapped;

                // live トラッキング駆動を剥がす（このリモート手を相手のローカル手で上書きさせない）
                foreach (var s in inst.GetComponents<OVRSkeleton>()) Object.Destroy(s); // OVRCustomSkeleton 含む
                foreach (var h in inst.GetComponents<OVRHand>()) Object.Destroy(h);
                var anim = inst.GetComponent<Animator>();
                if (anim != null) Object.Destroy(anim);

                var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null)
                {
                    smr.enabled = true;
                    smr.updateWhenOffscreen = true;
                    if (provider.HandMaterial != null) smr.sharedMaterial = provider.HandMaterial;
                }
                return true;
            }

            private void BuildBones(HandSkeletonLayout layout)
            {
                _bones = new Transform[layout.BoneCount];
                for (int i = 0; i < layout.BoneCount; i++)
                {
                    var bone = new GameObject($"Bone{i}").transform;
                    int parent = layout.ParentIndex[i];
                    bool hasParentBone = parent >= 0 && parent < i;
                    bone.SetParent(hasParentBone ? _bones[parent] : _root, worldPositionStays: false);
                    bone.localPosition = layout.BindLocalPos[i];
                    bone.localRotation = layout.BindLocalRot[i];
                    CreatePrimitive(bone, PrimitiveType.Sphere, 0.017f, "Joint", _avatarMat);
                    _bones[i] = bone;

                    // 親関節→この関節を結ぶ骨（指の節・手のひら）。バインドオフセットは固定なので
                    // 親ボーン下に一度置けば、親の回転に追従して手の形を保つ（毎フレーム更新不要）
                    if (hasParentBone)
                    {
                        CreateBoneLink(_bones[parent], layout.BindLocalPos[i]);
                    }
                }
            }

            /// <summary>parentBone の原点から childLocalPos まで伸びる細い円柱（骨）を 1 個生成。</summary>
            private static void CreateBoneLink(Transform parentBone, Vector3 childLocalPos)
            {
                float len = childLocalPos.magnitude;
                if (len < 1e-4f) return;
                var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cyl.name = "BoneLink";
                var col = cyl.GetComponent<Collider>();
                if (col != null) Destroy(col);
                if (_avatarMat != null) cyl.GetComponent<Renderer>().sharedMaterial = _avatarMat;
                var t = cyl.transform;
                t.SetParent(parentBone, worldPositionStays: false);
                t.localPosition = childLocalPos * 0.5f;
                t.localRotation = Quaternion.FromToRotation(Vector3.up, childLocalPos.normalized);
                // 既定シリンダーは高さ 2・半径 0.5 → 高さ=len, 半径≈0.006
                t.localScale = new Vector3(0.012f, len * 0.5f, 0.012f);
            }
        }
    }
}
