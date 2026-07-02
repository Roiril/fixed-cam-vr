#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// リモートプレイヤーの片手描画（RemoteAvatarView から分離）。
    /// Meta の白い手メッシュ（OVRCustomHandPrefab）を同期 bone で駆動するのを第一選択、
    /// 供給が無ければ関節を骨で繋ぐカプセル手にフォールバック。どちらも layout/プレハブが揃った時点で遅延構築。
    /// </summary>
    internal sealed class RemoteHandView
    {
        /// <summary>関節球・骨・wrist proxy の共有マテリアル（RemoteAvatarView.Build が設定）。</summary>
        internal static Material? AvatarMat;

        private readonly Transform _root;
        private readonly Transform _wristProxy;
        private readonly bool _isRight;
        private bool _everTracked;
        private bool _built;
        private bool _meshTried; // メッシュ構築を一度試したか（失敗しても毎フレ再 Instantiate しないラッチ）

        // メッシュ手: bone Transform を BoneId 順（名前マッピング）に並べ、同期 bone で回す。
        // 要素は見つからなかった bone が null になりうる（適用時に null チェック）
        private Transform?[]? _meshBones;
        private GameObject? _meshInstance;
        // 外部リグ（Realistic/Robot）のメッシュ側バインドローカル回転（BoneId 順）。
        // non-null ならバインド差分リターゲットで駆動、null なら Meta 直接代入。
        private Quaternion[]? _varBind;
        // カプセル手（フォールバック）
        private Transform[]? _bones;

        /// <summary>手 root のローカル位置（avatar-root 空間。腕の手首端に使う）。</summary>
        public Vector3 RootLocalPos => _root.localPosition;

        /// <summary>一度でもトラッキングされ表示中か（腕の表示ゲートに使う）。</summary>
        public bool IsVisible => _root.gameObject.activeSelf;

        public RemoteHandView(Transform parent, string name, bool isRight)
        {
            _isRight = isRight;
            _root = new GameObject(name).transform;
            _root.SetParent(parent, worldPositionStays: false);
            _root.gameObject.SetActive(false); // 既定は非表示（片手モードの左手対策）。右手は ShowAtRest で接続時に出す
            _wristProxy = CreatePrimitive(_root, PrimitiveType.Cube, 0.05f, "WristProxy");
        }

        /// <summary>接続直後に休めポーズで手を出現させる（未トラッキングでも「居る」ことを示す）。
        /// トラッキングが来たら <see cref="Tick"/> が実手へスナップして追従する。</summary>
        public void ShowAtRest(Vector3 restLocalPos, Quaternion restLocalRot)
        {
            _root.localPosition = restLocalPos;
            _root.localRotation = restLocalRot;
            _root.gameObject.SetActive(true);
            // 受信側の手 bind（layout）でメッシュ/カプセルを先に組む（bind=開いた手の休めポーズで見える）
            TryBuild(_isRight ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL);
            // 外部リグ（Realistic/Robot）は bind の手首向きが Meta と違い、休めで指が上を向く。
            // メッシュの指方向を測って「指=前やや下・手のひら下」に揃える（rest 限定。tracking では _root=wristRot で上書き）。
            if (_varBind != null && _meshBones != null) AlignRestForward();
        }

        /// <summary>休めポーズで外部リグ手の指が前（人役方向）やや下・手のひら下を向くよう _root を回す（rest 限定）。</summary>
        private void AlignRestForward()
        {
            var bones = _meshBones!;
            Transform? wrist = bones.Length > 0 ? bones[0] : null;
            Transform? tip = null;
            foreach (int i in new[] { 11, 10, 8, 9, 7 }) { if (i < bones.Length && bones[i] != null) { tip = bones[i]; break; } }
            if (wrist == null || tip == null) return;

            // メッシュ固有の「指方向」と「手の甲方向」を _root ローカルで測る（_root.localRotation に依存しない固定量）
            Vector3 fwd = _root.InverseTransformDirection(tip.position - wrist.position);
            if (fwd.sqrMagnitude < 1e-8f) return;
            fwd.Normalize();

            Transform? idx = bones.Length > 6 ? bones[6] : null;
            Transform? pnk = null;
            foreach (int i in new[] { 15, 16 }) { if (i < bones.Length && bones[i] != null) { pnk = bones[i]; break; } }
            Vector3 up = Vector3.up;
            if (idx != null && pnk != null)
            {
                Vector3 lateral = _root.InverseTransformDirection(pnk.position - idx.position);
                Vector3 n = Vector3.Cross(lateral, fwd); // 右手: 手の甲側の法線
                if (n.sqrMagnitude > 1e-8f) up = n.normalized;
            }

            // 望ましい向き（avatar 空間）: 指=前(人役,+Z)やや下, 手の甲=上（手のひら下）
            var desiredFwd = new Vector3(0f, -0.35f, 1f).normalized;
            // メッシュ local (fwd,up) → 望ましい (desiredFwd, up) へ写す回転を _root.localRotation に据える
            _root.localRotation = Quaternion.LookRotation(desiredFwd, Vector3.up)
                * Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
        }

        /// <summary>メッシュ手→（失敗時）カプセル手を一度だけ構築する。成功でラッチ。</summary>
        private void TryBuild(HandSkeletonLayout? layout)
        {
            if (_built) return;
            if (!_meshTried)
            {
                _meshTried = true;
                if (TryBuildMeshHandSafe())
                {
                    _built = true;
                    _wristProxy.gameObject.SetActive(false);
                    return;
                }
            }
            if (!_built && layout != null) // メッシュ供給が無い/失敗 → カプセル（layout 待ち）
            {
                BuildBones(layout);
                _built = true;
                _wristProxy.gameObject.SetActive(false);
            }
        }

        public void Tick(float smooth, Vector3 wristPos, Quaternion wristRot, Quaternion[] boneRots,
            bool tracked, HandSkeletonLayout? layout)
        {
            // ロスト時は「最終姿勢でフリーズ」（調査仕様 — 消すと相手が
            // 無に向かって話す時間が混入し RQ2/RQ3 を汚染する。ロスト区間は SessionLogger が記録）。
            // ただし接続時に ShowAtRest で出した休めポーズはそのまま保持（消さない）。
            if (!tracked)
            {
                return; // 表示状態・姿勢を保持したまま何もしない
            }
            if (!_everTracked)
            {
                _everTracked = true;
                _root.gameObject.SetActive(true);
                // 休めポーズ（卓上）→ 実手位置へは glide させずスナップ（離れた場所からスーッと寄るのを防ぐ）
                _root.localPosition = wristPos;
                _root.localRotation = wristRot;
            }

            _root.localPosition = Vector3.Lerp(_root.localPosition, wristPos, smooth);
            _root.localRotation = Quaternion.Slerp(_root.localRotation, wristRot, smooth);

            TryBuild(layout);

            // バリアント切替や teardown 順序で mesh 実体だけ先に破棄された場合、
            // 破棄済み bone へ代入して NRE/MissingReference にならないよう作り直しへ戻す
            if (_meshBones != null && _meshInstance == null)
            {
                _meshBones = null;
                _varBind = null;
                _built = false;
                _meshTried = false;
            }

            if (_meshBones != null)
            {
                int n = Mathf.Min(boneRots.Length, _meshBones.Length);
                if (_varBind == null)
                {
                    // Meta 白手: bind が OVR と同一なので同期 bone を直接代入。
                    // bone[0]=wrist の localRotation は「手アンカー(_root=wristRot)からの相対」で、
                    // 送信元 OVRSkeleton の Bones[0].localRotation と同じ合成（anchor × wrist.local）になる＝二重回転ではない。
                    for (int i = 0; i < n; i++)
                    {
                        if (_meshBones[i] != null)
                        {
                            _meshBones[i]!.localRotation = Quaternion.Slerp(_meshBones[i]!.localRotation, boneRots[i], smooth);
                        }
                    }
                }
                else
                {
                    // 外部リグ（Realistic/Robot）: バインドが違うのでバインド差分リターゲット。
                    // ovrBind は受信側の手 bind（layout）。OVR 手の bind ローカル回転は正規化された正準値なので、
                    // 送信元と受信側で一致する（Meta 直接代入経路が成立しているのと同じ前提）。
                    for (int i = 0; i < n; i++)
                    {
                        if (_meshBones[i] == null) continue;
                        var ovrBind = (layout != null && i < layout.BoneCount)
                            ? layout.BindLocalRot[i] : Quaternion.identity;
                        var target = HandRetarget.Solve(boneRots[i], ovrBind, _varBind[i]);
                        _meshBones[i]!.localRotation = Quaternion.Slerp(_meshBones[i]!.localRotation, target, smooth);
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

        /// <summary>
        /// Meta の白い手メッシュ（OVRCustomHandPrefab）をインスタンス化し、駆動系（OVRHand/OVRCustomSkeleton/
        /// Animator）を剥がして SkinnedMeshRenderer + bone Transform だけ残す。bone は同期データで回す。
        /// bone は <see cref="RemoteHandMeshProvider.MapHandBonesByName"/> で BoneId 順に名前検索する
        /// （OVRCustomSkeleton.CustomBones は package 同梱状態で未マッピング＝全 null のため使えない。
        /// これに頼ると指ボーンが一切回らず bind ポーズ＝開いた手で固定される）。供給が無ければ false。
        /// </summary>
        private bool TryBuildMeshHand()
        {
            var provider = RemoteHandMeshProvider.Instance;
            if (provider == null) return false;
            var variant = StudyConfig.SelectedHandVariant;

            if (HandVariantTable.IsExternalRig(variant))
            {
                // Realistic/Robot: パック手を生成（配置・スケール・材質は provider が処理）→ リターゲット駆動
                var built = provider.BuildExternalHand(_root, _isRight, variant);
                if (built == null) return false;
                _meshInstance = built.Instance;
                _meshBones = built.Bones;
                _varBind = built.VarBind;
                return true;
            }

            // Default: Meta 白手（OVRCustomHandPrefab）を同期 bone で直接駆動
            var prefab = provider.GetPrefab(_isRight, HandVariant.Default);
            if (prefab == null) return false;

            var inst = Object.Instantiate(prefab, _root, worldPositionStays: false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.SetActive(true);

            // CustomBones に頼らず Meta の FBX 命名規則で bone Transform を実体検索（index = BoneId = 同期 boneRots）
            var mapped = RemoteHandMeshProvider.MapHandBonesByName(inst.transform, _isRight, HandVariant.Default);
            bool anyMapped = false;
            foreach (var b in mapped) { if (b != null) { anyMapped = true; break; } }
            if (!anyMapped) { Object.Destroy(inst); return false; } // 名前不一致（SDK 改名等）→ カプセルへ
            _meshInstance = inst;
            _meshBones = mapped;
            _varBind = null; // Meta は bind 一致 → 直接代入

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

        /// <summary>手バリアント切替時に呼ぶ。現在の手メッシュ/カプセルを破棄し、次の Tick で作り直す。</summary>
        public void MarkVariantDirty()
        {
            if (_meshInstance != null) { Object.Destroy(_meshInstance); _meshInstance = null; }
            if (_bones != null)
            {
                foreach (var b in _bones) { if (b != null) Object.Destroy(b.gameObject); }
                _bones = null;
            }
            _meshBones = null;
            _varBind = null;
            _built = false;
            _meshTried = false;
            if (_wristProxy != null) _wristProxy.gameObject.SetActive(true);
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
                CreatePrimitive(bone, PrimitiveType.Sphere, 0.017f, "Joint", AvatarMat);
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
            if (col != null) Object.Destroy(col);
            if (AvatarMat != null) cyl.GetComponent<Renderer>().sharedMaterial = AvatarMat;
            var t = cyl.transform;
            t.SetParent(parentBone, worldPositionStays: false);
            t.localPosition = childLocalPos * 0.5f;
            t.localRotation = Quaternion.FromToRotation(Vector3.up, childLocalPos.normalized);
            // 既定シリンダーは高さ 2・半径 0.5 → 高さ=len, 半径≈0.006
            t.localScale = new Vector3(0.012f, len * 0.5f, 0.012f);
        }

        private static Transform CreatePrimitive(Transform parent, PrimitiveType type, float scale,
            string name, Material? mat = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            if (mat == null) mat = AvatarMat; // 既定はアバター色
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localScale = Vector3.one * scale;
            return go.transform;
        }
    }
}
