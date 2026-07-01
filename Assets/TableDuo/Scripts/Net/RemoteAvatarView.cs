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
        /// <summary>受信レート(60Hz)→描画レート(72-90Hz)の指数平滑係数（時定数 τ=1/K 秒）。大きいほど追従が速い＝遅延が小さい。
        /// 送信を 60Hz に上げた（TableDuoPlayer.sendRate / NetworkConfig.TickRate）ぶんパケット間隔が 16.6ms に詰まったので、
        /// τ を 50ms(K=20)→約31ms(K=32) に短縮して収束遅れを削る。60Hz 入力なら段差が小さく K を上げても jitter は出にくい。
        /// 平滑は描画のみに作用し、SessionLogger は受信生 pose を記録するので調査データには影響しない。</summary>
        private const float SmoothK = 32f;

        /// <summary>胴（頭への緩い追従）の平滑係数。胴は無追跡の cosmetic なので頭より遅い τ で揺れを抑える。</summary>
        private const float ChestSmoothK = 9f;

        // 肩の付け根（胴ローカル）。腕（袖）はここから手首へ伸びる。+X=アバターの右手側
        private static readonly Vector3 ShoulderOffsetR = new(0.17f, 0.24f, 0f);
        private static readonly Vector3 ShoulderOffsetL = new(-0.17f, 0.24f, 0f);

        // 接続直後（未トラッキング）に右手を置く休めポーズ（avatar-root=席ローカル。卓上に手を置いた自然な構え）。
        // トラッキングが来たら実手へスナップして追従する。片手モードの左手は従来どおり「使われるまで非表示」。
        private static readonly Vector3 HandRestLocalPosR = new(0.20f, -0.42f, 0.32f);
        private static readonly Quaternion HandRestLocalRotR = Quaternion.Euler(15f, -90f, 0f);

        private Transform? _head;
        private Transform? _chest;
        private Transform? _armL;
        private Transform? _armR;
        private HandView? _left;
        private HandView? _right;
        private RemyAvatarRig? _remy; // フル（人側）= Mixamo Remy 駆動。無ければ procedural 人型へフォールバック
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
                _left = new HandView(transform, "HandL", isRight: false);
                _right = new HandView(transform, "HandR", isRight: true);
                // 接続＝アバター出現。右手（片手モードの主役）を休めポーズで即表示し、
                // トラッキングが来たら追従する（手が視界外でも「居る」ことが伝わる）。
                _right.ShowAtRest(HandRestLocalPosR, HandRestLocalRotR);
            }
            else
            {
                // 人側フル: Remy（Mixamo）があれば IK 駆動、無ければ procedural 人型＋手メッシュへフォールバック
                var remyPrefab = Resources.Load<GameObject>("RemyFullAvatar");
                if (remyPrefab != null)
                {
                    _remy = new RemyAvatarRig(transform, remyPrefab);
                }
                else
                {
                    BuildHumanUpperBody();
                    _left = new HandView(transform, "HandL", isRight: false);
                    _right = new HandView(transform, "HandR", isRight: true);
                }
            }

            // 手の見た目切替（実機トグル/フラグ）で手メッシュを作り直す。Remy（IK フル手）は対象外。
            StudyConfig.HandVariantChanged += OnHandVariantChanged;
        }

        private void OnHandVariantChanged()
        {
            _left?.MarkVariantDirty();
            _right?.MarkVariantDirty();
        }

        private void OnDestroy()
        {
            StudyConfig.HandVariantChanged -= OnHandVariantChanged;
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
            // 60Hz 受信を描画フレームへ指数平滑（フレームレート非依存）
            float dt = Time.deltaTime;
            ApplyToTransforms(1f - Mathf.Exp(-SmoothK * dt), 1f - Mathf.Exp(-ChestSmoothK * dt));
        }

        /// <summary>ターゲット pose をパーツへ反映。a=頭/手の平滑係数、chestA=胴の平滑係数（1=即時）。</summary>
        private void ApplyToTransforms(float a, float chestA)
        {
            if (_remy != null)
            {
                _remy.Drive(_target); // Remy は IK で即解（平滑は IK 入力＝受信 pose 側に委ねる）
                return;
            }
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

            public HandView(Transform parent, string name, bool isRight)
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
