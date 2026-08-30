#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// course space（フロア中心原点・+Z 北・y=0 が床）→ トラッキング空間ワールドへの
    /// **剛体変換 4 DOF**（XZ 平行移動 + **床の高さ** + yaw）を保持するコンポーネント。
    /// ゾーン群・ワイヤー・CG 人形はこの 1 変換を通して配置される。
    ///
    /// identity デフォルト + persistentDataPath/registration.json の永続化を持つ。
    /// HMD N 点登録は <see cref="CourseRegistrationController"/> が
    /// <see cref="SetRegistration"/> と <see cref="SetFloorY"/> を叩く。
    ///
    /// 形状は show.json layout 側（PC で編集）、位置合わせだけをこの 1 変換で持つ設計。
    ///
    /// <b>y は 2026-08-02 に足した</b>（それまでは 3 DOF で床の高さを捨てていた）。トラッキング原点は
    /// FloorLevel 設定だが実測とは食い違い、「床や壁のワイヤーが地面より下に出る」状態が続いていた。
    /// 解は登録点の y から測る（<see cref="CourseRegistrationController"/>）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CourseFrame : MonoBehaviour
    {
        [Tooltip("保存ファイル名（Application.persistentDataPath 直下）。")]
        [SerializeField] private string registrationFileName = "registration.json";

        [Tooltip("course 原点のワールド XZ 位置 (m)。identity は (0,0)。")]
        [SerializeField] private Vector2 originXZ = Vector2.zero;

        [Tooltip("床（course y=0）のワールド高さ (m)。identity は 0。登録で実測して入る。")]
        [SerializeField] private float originY = 0f;

        [Tooltip("course +Z をワールドへ向ける yaw 角 (deg)。identity は 0。")]
        [SerializeField] private float yawDeg = 0f;

        [Tooltip("Awake 時に registration.json があれば読み込んで適用する。")]
        [SerializeField] private bool loadOnAwake = true;

        /// <summary>変換（原点 or yaw）が変わった時に発火。ZoneLayoutApplier が購読して再配置する。</summary>
        public event Action? Changed;

        private bool _needsReRegistration;

        // 登録の有無・品質・鮮度（Review フェーズ着地判定 + StatusHud 表示に使う）。
        // registration.json ロード成功 or 確定保存で有効化する（Verify プレビュー中は立てない）。
        private bool _hasRegistration;
        private float _maxResidualM;
        private int _pointCount;
        private float _floorSpreadM;
        private int _regSchema;
        private string _savedAtIso = "";

        /// <summary>床の高さを測るようになった版。旧ファイル（測っていない）は 0 で読まれる。</summary>
        public const int FloorSchema = 2;

        // プレビューセッション（登録の Verify プレビューをトランザクション化する）。
        // BeginPreviewSession で現在の確定 state を退避し、コミット経路（B 確定）以外の退場
        // （トリガー長押しキャンセル / Review-B）は RollbackPreviewSession で確定済み state へ戻す。
        // _sessionDirty はプレビューが実際に適用された時のみ true（未適用のキャンセルで Changed を無駄撃ちしない）。
        private bool _sessionActive;
        private bool _sessionDirty;
        private RegistrationSnapshot _snapshot;

        private struct RegistrationSnapshot
        {
            public Vector2 originXZ;
            public float originY;
            public float yawDeg;
            public bool needsReReg;
            public bool hasReg;
            public float maxResidualM;
            public int pointCount;
            public float floorSpreadM;
            public int regSchema;
            public string savedAtIso;
        }

        /// <summary>
        /// OS recenter（Oculus ボタン長押し等）でトラッキング原点が変わり、登録が無効になった状態。
        /// <see cref="MarkNeedsReRegistration"/> で立ち、<see cref="SetRegistration"/>/<see cref="ResetRegistration"/>
        /// で降りる。CourseRegistrationController がこれを見て「要再登録」警告を視界に出す。
        /// ゾーン動作自体は継続する（黙ってズレたまま動かない、が目的）。
        /// </summary>
        public bool NeedsReRegistration => _needsReRegistration;

        /// <summary>
        /// 有効な登録が存在するか（registration.json をロード済み or 今セッションで確定保存済み）。
        /// CourseRegistrationController が登録開始時に「Review 着地か Capture 着地か」を分岐するのに使い、
        /// StatusHud が「未登録 / 登録済」バッジを出すのに使う。identity 既定のままでは false。
        /// </summary>
        public bool HasRegistration => _hasRegistration;

        /// <summary>直近登録の剛体フィット最大残差 (m)。未記録（旧ファイル or 未登録）は 0。</summary>
        public float MaxResidualM => _maxResidualM;

        /// <summary>直近登録に使った基準点数。未記録（旧ファイル or 未登録）は 0。</summary>
        public int PointCount => _pointCount;

        /// <summary>直近登録の保存日時（ローカル時刻 ISO 文字列）。未記録（旧ファイル or 未登録）は空。</summary>
        public string SavedAtIso => _savedAtIso;

        /// <summary>course 原点のワールド XZ。</summary>
        public Vector2 OriginXZ => originXZ;

        /// <summary>床（course y=0）のワールド高さ (m)。</summary>
        public float FloorY => originY;

        /// <summary>登録点の y のばらつき (m)。大きいほど「床に着けていない点があった」。未測定は 0。</summary>
        public float FloorSpreadM => _floorSpreadM;

        /// <summary>
        /// 登録データの版。<see cref="FloorSchema"/> 未満なら**床の高さを測っていない**登録
        /// （旧ファイル）。0 の originY が「一致している」のか「測っていない」のか区別するために要る。
        /// 動作はブロックしない — Review 画面で再登録を促すだけ。
        /// </summary>
        public int RegSchema => _regSchema;

        /// <summary>床の高さを実測済みの登録か。</summary>
        public bool HasFloorY => _hasRegistration && _regSchema >= FloorSchema;

        /// <summary>course +Z → ワールドの yaw (deg)。</summary>
        public float YawDeg => yawDeg;

        /// <summary>course → ワールドの回転（yaw のみ）。ゾーン OBB の向きに使う。</summary>
        public Quaternion Rotation => Quaternion.Euler(0f, yawDeg, 0f);

        private string RegistrationPath => Path.Combine(Application.persistentDataPath, registrationFileName);

        [Serializable]
        private struct RegistrationData
        {
            public float originX;
            public float originZ;
            public float yawDeg;
            // 品質・鮮度（v2）。旧ファイルには無く、JsonUtility が既定 0 / null で埋める（後方互換）。
            public float maxResidualM;
            public int pointCount;
            public string savedAtIso;
            // 床の高さ（2026-08-02）。regSchema=0 の旧ファイルは originY を「未測定」として扱う。
            public float originY;
            public float floorSpreadM;
            public int regSchema;
        }

        private void Awake()
        {
            if (loadOnAwake) LoadRegistration();
        }

        /// <summary>
        /// course space の XZ 点を、**床からの高さ** <paramref name="heightAboveFloor"/> でワールド座標へ変換する。
        ///
        /// ⚠ 2026-08-02 に第 2 引数の意味が変わった（旧: ワールド y の直指定）。呼び出し側が渡していた
        /// 定数（ワイヤー 0.03 / タイル 0.015 / ゾーン中心 1.0 / CG 人形の 0）はすべて
        /// 「床からの高さ」のつもりの値だったので、意味の変更で全部が正しく持ち上がる。
        /// </summary>
        public Vector3 CourseToWorld(Vector2 courseXZ, float heightAboveFloor)
        {
            Vector3 rotated = Rotation * new Vector3(courseXZ.x, 0f, courseXZ.y);
            return new Vector3(originXZ.x + rotated.x, originY + heightAboveFloor, originXZ.y + rotated.z);
        }

        /// <summary>ワールド座標を course space の XZ へ逆変換する（heartbeat のプレイヤードット用）。</summary>
        public Vector2 WorldToCourse(Vector3 world)
        {
            Vector3 d = new Vector3(world.x - originXZ.x, 0f, world.z - originXZ.y);
            Vector3 local = Quaternion.Euler(0f, -yawDeg, 0f) * d;
            return new Vector2(local.x, local.z);
        }

        /// <summary>ワールド y を床からの高さへ直す（テレメトリの頭の高さ・接地判定に使う）。</summary>
        public float HeightAboveFloor(float worldY) => worldY - originY;

        /// <summary>
        /// 登録変換を設定する。HMD 2 点登録フェーズやナッジ微調整から呼ぶ。
        /// save=true なら registration.json へ即保存。常に <see cref="Changed"/> を発火する。
        /// </summary>
        public void SetRegistration(Vector2 newOriginXZ, float newYawDeg, bool save = true)
        {
            originXZ = newOriginXZ;
            yawDeg = newYawDeg;
            _needsReRegistration = false;
            // プレビューセッション中の適用はダーティ化（ロールバック時に復元 + Changed 発火の対象になる）。
            // 4 引数版もこの 2 引数版を呼ぶので、ダーティ記録はここ 1 箇所で足りる。
            if (_sessionActive) _sessionDirty = true;
            if (save) SaveRegistration();
            Changed?.Invoke();
        }

        /// <summary>
        /// 登録変換を品質メタ（剛体フィット最大残差・基準点数）付きで設定する。N 点登録の Verify プレビュー
        /// （save=false）が呼び、確定は <see cref="SaveRegistration"/> が担う。品質メタは記録しておき、確定時に
        /// json へ焼き込む（Review フェーズ / StatusHud での表示に使う）。<see cref="HasRegistration"/> は
        /// プレビューでは立てず、確定保存 or ロードでのみ立てる。
        /// </summary>
        public void SetRegistration(Vector2 newOriginXZ, float newYawDeg, float maxResidualM, int pointCount, bool save = true)
        {
            _maxResidualM = maxResidualM;
            _pointCount = pointCount;
            SetRegistration(newOriginXZ, newYawDeg, save);
        }

        /// <summary>
        /// 床の高さ（course y=0 のワールド高さ）を設定する。
        ///
        /// XZ + yaw を解く剛体フィットとは**別の測定**なので、設定も別にする
        /// （1 つのメソッドに詰めると引数 3 連続 float で「どれが yaw か」が読めなくなる）。
        /// 登録フローは <see cref="SetFloorY"/> → <see cref="SetRegistration"/> の順に呼び、
        /// 保存は B 確定時の <see cref="SaveRegistration"/> が両方まとめて書く。
        /// </summary>
        /// <param name="spreadM">登録点の y のばらつき (m)。品質メタとして残す（大きい＝床に着けていない点がある）。</param>
        public void SetFloorY(float newOriginY, float spreadM, bool save = false)
        {
            originY = newOriginY;
            _floorSpreadM = spreadM;
            _regSchema = FloorSchema;
            if (_sessionActive) _sessionDirty = true;
            if (save) SaveRegistration();
            Changed?.Invoke();
        }

        /// <summary>
        /// プレビューセッションを開始し、現在の確定 state（変換 + 要再登録 + 品質メタ）を丸ごと退避する。
        /// 以後の <see cref="SetRegistration"/>（Verify プレビュー）はダーティ記録され、
        /// <see cref="RollbackPreviewSession"/> で確定済み state へ戻せるようになる。
        /// 二重呼び出しは無害（再入場ごとに最新の確定 state で退避を上書きする）。
        /// </summary>
        public void BeginPreviewSession()
        {
            _snapshot = new RegistrationSnapshot
            {
                originXZ = originXZ,
                originY = originY,
                yawDeg = yawDeg,
                needsReReg = _needsReRegistration,
                hasReg = _hasRegistration,
                maxResidualM = _maxResidualM,
                pointCount = _pointCount,
                floorSpreadM = _floorSpreadM,
                regSchema = _regSchema,
                savedAtIso = _savedAtIso,
            };
            _sessionActive = true;
            _sessionDirty = false;
        }

        /// <summary>
        /// プレビューセッションを確定する（スナップショットを破棄し、現在の live state を確定として保持）。
        /// B 確定経路で <see cref="SaveRegistration"/> の後に呼ぶ。冪等（セッション外は no-op）。
        /// </summary>
        public void CommitPreviewSession()
        {
            _sessionActive = false;
        }

        /// <summary>
        /// プレビューセッションを破棄し、開始時の確定 state へ戻す。B 確定以外の退場
        /// （トリガー長押しキャンセル / Review-B）で呼ぶ。コミット済み or セッション外は no-op（例外なし）。
        /// プレビューが実際に適用された（ダーティ）時のみ復元し、<see cref="Changed"/> を 1 回だけ発火する
        /// （未適用のキャンセルで ZoneLayoutApplier の不要な再生成フリッカを避ける）。
        /// </summary>
        public void RollbackPreviewSession()
        {
            if (!_sessionActive) return;
            _sessionActive = false;
            if (!_sessionDirty) return;

            originXZ = _snapshot.originXZ;
            originY = _snapshot.originY;
            yawDeg = _snapshot.yawDeg;
            _needsReRegistration = _snapshot.needsReReg;
            _hasRegistration = _snapshot.hasReg;
            _maxResidualM = _snapshot.maxResidualM;
            _pointCount = _snapshot.pointCount;
            _floorSpreadM = _snapshot.floorSpreadM;
            _regSchema = _snapshot.regSchema;
            _savedAtIso = _snapshot.savedAtIso;
            Changed?.Invoke();
        }

        /// <summary>
        /// トラッキング原点が変わって登録が無効になったことを記録する（OS recenter 検知時に呼ぶ）。
        /// フラグを立てて警告ログを出すだけ。ゾーン再配置はしない（次の登録まで現状のまま動かす）。
        /// </summary>
        public void MarkNeedsReRegistration()
        {
            if (_needsReRegistration) return;
            _needsReRegistration = true;
            Debug.LogWarning("[CourseFrame] OS recenter 検知 — 登録が無効化されました。右トリガー 2 秒長押しで再登録してください。");
        }

        /// <summary>登録を identity へ戻し、保存ファイルを削除する。</summary>
        public void ResetRegistration(bool deleteFile = true)
        {
            originXZ = Vector2.zero;
            originY = 0f;
            yawDeg = 0f;
            _needsReRegistration = false;
            _hasRegistration = false;
            _maxResidualM = 0f;
            _pointCount = 0;
            _floorSpreadM = 0f;
            _regSchema = 0;
            _savedAtIso = "";
            if (deleteFile)
            {
                try { if (File.Exists(RegistrationPath)) File.Delete(RegistrationPath); }
                catch (Exception e) { Debug.LogWarning($"[CourseFrame] registration 削除失敗: {e.Message}"); }
            }
            Changed?.Invoke();
        }

        /// <summary>registration.json を読み込んで適用する（無ければ identity のまま）。</summary>
        public void LoadRegistration()
        {
            try
            {
                if (!File.Exists(RegistrationPath)) return;
                var data = JsonUtility.FromJson<RegistrationData>(File.ReadAllText(RegistrationPath));
                originXZ = new Vector2(data.originX, data.originZ);
                yawDeg = data.yawDeg;
                _maxResidualM = data.maxResidualM;   // 旧ファイルは 0（JsonUtility 欠損 default）
                _pointCount = data.pointCount;
                _savedAtIso = data.savedAtIso ?? "";
                _regSchema = data.regSchema;         // 旧ファイルは 0 ＝ 床の高さを測っていない
                // 床の高さは測った登録でのみ効かせる。旧ファイルの originY=0 を「床が一致している」と
                // 読むと、実際にはズレたまま「合っている」と表示することになる。
                originY = _regSchema >= FloorSchema ? data.originY : 0f;
                _floorSpreadM = _regSchema >= FloorSchema ? data.floorSpreadM : 0f;
                _hasRegistration = true;
                Debug.Log($"[CourseFrame] registration 適用: origin=({originXZ.x:F3},{originXZ.y:F3}) yaw={yawDeg:F1}°" +
                          $" 床y={(_regSchema >= FloorSchema ? originY.ToString("F3") + "m" : "未測定")}" +
                          $" (残差 {_maxResidualM:F3}m / {_pointCount}点 / 保存 {(_savedAtIso.Length > 0 ? _savedAtIso : "記録なし")})");
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CourseFrame] registration 読込失敗: {e.Message}");
            }
        }

        /// <summary>現在の登録を registration.json へ保存する。</summary>
        public void SaveRegistration()
        {
            _savedAtIso = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _hasRegistration = true; // 確定＝有効な登録あり（disk 書込が失敗してもセッション内は有効）
            try
            {
                var data = new RegistrationData
                {
                    originX = originXZ.x,
                    originZ = originXZ.y,
                    yawDeg = yawDeg,
                    maxResidualM = _maxResidualM,
                    pointCount = _pointCount,
                    savedAtIso = _savedAtIso,
                    originY = originY,
                    floorSpreadM = _floorSpreadM,
                    regSchema = _regSchema,
                };
                // ⚠⚠ `.tmp` へ書いてから差し替える（2026-08-30）。素の WriteAllText は既存ファイルを
                //    先に truncate するので、そこで電源が落ちると**壊れた registration.json が残る**。
                //    次の起動は読み込みに失敗して未登録扱いになり、course 変換が identity へ落ちる ＝
                //    **通過ライン・開始位置・接近の判定が原理的に成立せず、導入が自動では二度と始まらない**
                //    （現場では「立っても始まらない」としか見えず、出口はスタッフの ⏭ だけになる）。
                //    同じ手当てが ShowControlClient.WriteAtomic にもある（設定キャッシュ側）。
                string tmp = RegistrationPath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data));
                if (!File.Exists(RegistrationPath)) File.Move(tmp, RegistrationPath);
                else
                {
                    try { File.Replace(tmp, RegistrationPath, null); }
                    catch { File.Delete(RegistrationPath); File.Move(tmp, RegistrationPath); }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CourseFrame] registration 保存失敗: {e.Message}");
            }
        }
    }
}
