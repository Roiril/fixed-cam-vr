#nullable enable
using UnityEngine;
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 帰還の別れの人形（2026-10-30・<c>canon/LEDGER.md</c> 0291）。
    ///
    /// 最後の異常を報告して体験者が人形から戻った後、締めで使った CG 人形を 1 体だけ、
    /// 画面の中で体験者の左に右向きで立たせる。少し見つめた後で後ろを向き、ゆっくり下がって消える。
    /// 帰還End の写真は人形が体験者の方を向いている間に撮る（右に体験者・左に人形）。
    ///
    /// 時刻表と段の進み、立ち位置の解き方をここに置く。Unity の状態は引数で受ける（EditMode で固定する）。
    /// 実行体は <see cref="EndingFarewellDoll"/>、人形を描くのは <see cref="Cg.ShowCgLayer"/> の別れのモード。
    /// </summary>
    public sealed class EndingFarewellLogic
    {
        public enum Phase
        {
            /// <summary>まだ始まっていない（報告前・入れ替わりの最中）。</summary>
            Idle = 0,
            /// <summary>人形を出した。立ち位置が解けるのを待っている（まだ見えない）。</summary>
            Anchoring = 1,
            /// <summary>体験者の方を向いたまま現れる。</summary>
            Appear = 2,
            /// <summary>体験者の方を向いて立っている。帰還End の写真はこの段で撮る。</summary>
            Face = 3,
            /// <summary>後ろを向く。</summary>
            Turn = 4,
            /// <summary>ゆっくり下がりながら消える。</summary>
            Retreat = 5,
            /// <summary>消えた。同じ体験者では 2 度出さない。</summary>
            Done = 6,
            /// <summary>立てられなかった（較正・位置合わせ・頭の追跡が無い）。写真は撮らない。再試行し、上限で Done へ進む。</summary>
            Unavailable = 7,
        }

        // ---- 時刻表 -------------------------------------------------------------------
        //
        // 写真待ちは Face の末尾で保持する。写真が成功した後だけ回転と退場へ進み、
        // TakeRunner は人形が消え切るまで締めのライブを保持する（LEDGER 0293）。

        /// <summary>現れる（不透明度 0 → 1）。</summary>
        public const float AppearSec = 0.35f;
        /// <summary>体験者の方を向いて立つ最短尺。写真未成立ならこの段を保持する。</summary>
        public const float FaceSec = 1.15f;
        /// <summary>後ろを向く。</summary>
        public const float TurnSec = 0.75f;
        /// <summary>下がる。最後の <see cref="FadeOutSec"/> で消える。</summary>
        public const float RetreatSec = 6.0f;
        /// <summary>下がりながら消える尺。</summary>
        public const float FadeOutSec = 4.5f;
        /// <summary>退場の名目進行パラメータ。PoseAt は 0..RetreatM を折れ線の全長へ写す。</summary>
        public const float RetreatM = 0.45f;
        /// <summary>立ち位置が解けるのを待つ上限。超えたら出さない。</summary>
        public const float AnchorTimeoutSec = 0.5f;
        /// <summary>
        /// スイの面が閉じるのを待つ上限。面が閉じなくても人形はここで出す。
        /// ⚠ 解除の返事は 2 枚を入れ替えで出す（0366）。2 枚で 6 秒ほど掛かるので、5 秒だと 2 枚目の裏に人形が出る。
        /// </summary>
        public const float NoticeWaitMaxSec = 8f;
        /// <summary>全身と向きを求める尺（見つめる段の頭から）。過ぎたら足が切れても撮る。</summary>
        public const float PhotoStrictSec = 4f;
        /// <summary>人の向きを求める尺（同）。過ぎたら向きを問わず撮る。</summary>
        public const float PhotoAnyYawSec = 8f;
        /// <summary>写真を諦めて退場へ進む尺（同）。人形を出せないときの諦めもこの値。報告した人を中断にしないため。</summary>
        public const float PhotoGiveUpSec = 15f;

        public const float TotalSec = AppearSec + FaceSec + TurnSec + RetreatSec;

        /// <summary>人形が後ろを向き始めてから消えるまでにオルゴールを下げる量 (dB・0312)。</summary>
        public const float MusicBoxDepartDb = -30f;
        /// <summary>人形が消え切る手前でオルゴールを無音へ閉じる尺 (秒)。</summary>
        public const float MusicBoxCloseSec = 1f;

        /// <summary>
        /// 別れのオルゴールの音量 0..1（<c>canon/LEDGER.md</c> 0312）。現れる間に立ち上がり、
        /// 見つめる間は保ち（写真待ちで伸びても同じ）、後ろを向いてから消えるまでで下がる。
        /// </summary>
        public static float MusicBoxGain(Phase phase, float t)
        {
            if (phase < Phase.Appear || phase > Phase.Retreat) return 0f;
            float gain = Mathf.Clamp01(t / AppearSec);
            float departAt = AppearSec + FaceSec;
            gain *= Mathf.Pow(10f, MusicBoxDepartDb * Mathf.Clamp01((t - departAt) / (TotalSec - departAt)) / 20f);
            return gain * Mathf.Clamp01((TotalSec - t) / MusicBoxCloseSec);
        }
        private const float RetreatEaseFraction = 0.25f;

        // ---- 立ち位置 -----------------------------------------------------------------

        /// <summary>画面の中で体験者の足元から左へずらす量（枠 UV の横幅に対する割合）。</summary>
        public const float ScreenGapU = 0.18f;
        /// <summary>人形の足元を寄せてよい左端（枠 UV）。これより左は画の縁に掛かる。</summary>
        public const float MinU = 0.10f;
        /// <summary>
        /// 人形の足元の下限（枠 UV の v・0 = 下端）。体験者がカメラに近く足元が画の外にあるときは、
        /// 人形を同じ奥行きに置くと頭だけが画の下端に覗く（2026-10-30 のプレビューで実際にそうなった）。
        /// その場合は人形を奥へ下げて全身を画に入れる。
        /// </summary>
        public const float MinFootV = 0.08f;
        /// <summary>人形の足元の上限（枠 UV の v）。これより上は奥の壁際になる。</summary>
        public const float MaxFootV = 0.60f;
        /// <summary>体験者との床の上の距離の範囲 (m)。外れたらカメラの左右方向へこの範囲で置き直す。</summary>
        public const float MinGapM = 0.5f;
        public const float MaxGapM = 2.5f;
        /// <summary>
        /// 下がる向きに混ぜる「カメラから遠ざかる」成分。0 なら体験者から真横へ離れるだけ。
        /// 0.6 では後ろを向いても左向きの横顔に近く（カメラの奥への向き 0.53）、背中が読めなかった
        /// （2026-10-30 のプレビュー）。1.5 で奥への向きが 0.8 を超え、背中を見せて奥へ遠ざかる。
        /// </summary>
        public const float AwayFromCameraMix = 1.5f;

        /// <summary>1 フレームの見え方。</summary>
        public struct Sample
        {
            public Phase phase;
            /// <summary>人形の不透明度 0..1（CG 層の強さ）。</summary>
            public float alpha;
            /// <summary>体験者の方 → 後ろ の回り具合 0..1（緩急つき）。</summary>
            public float turn01;
            /// <summary>退場の名目進行パラメータ。実距離は Anchor の折れ線長から求める。</summary>
            public float retreatM;
            /// <summary>写真の条件の緩め具合。<see cref="PhotoLevelStrict"/> から順に緩む。</summary>
            public int photoLevel;

            /// <summary>写真を撮ってよい（体験者の方を向いて不透明に立っている）。</summary>
            public bool Facing => phase == Phase.Face;
        }

        /// <summary>1 フレームの判定結果。実行体はこの指示どおりに人形を出す・消す。</summary>
        public struct Step
        {
            public Sample sample;
            /// <summary>この縁で人形を出す（<see cref="Cg.ShowCgLayer.BeginFarewell"/>）。</summary>
            public bool begin;
            /// <summary>この縁で人形を消す（<see cref="Cg.ShowCgLayer.Hide"/>）。</summary>
            public bool hide;
        }

        public Phase Current { get; private set; } = Phase.Idle;
        public float ElapsedSec { get; private set; }
        private float _anchorWaitSec;
        private bool _shotCaptured;
        private bool _photoGivenUp;
        private float _faceHeldSec;
        private float _noticeWaitSec;
        private float _noDollSec;

        /// <summary>見つめる段に入ってからの秒。</summary>
        public float FaceHeldSec => _faceHeldSec;
        /// <summary>写真を諦めて退場へ進んだ。</summary>
        public bool PhotoGivenUp => _photoGivenUp;
        /// <summary>いまの写真の条件の緩め具合。</summary>
        public int PhotoLevel => PhotoLevelAt(_faceHeldSec);

        public static int PhotoLevelAt(float faceHeldSec)
            => faceHeldSec < PhotoStrictSec ? PhotoLevelStrict
             : faceHeldSec < PhotoAnyYawSec ? PhotoLevelFeetOut : PhotoLevelAnyYaw;

        public void NotifyShotCaptured()
        {
            if (Current == Phase.Face) _shotCaptured = true;
        }

        /// <summary>写真の窓が別れの人形を待たずに開いてよいか（人形が出る前・出せなかった場合）。</summary>
        public bool AllowsPlainReleasedShot => Current == Phase.Idle || Current == Phase.Unavailable;

        /// <summary>人形が画に出ている段か（Appear〜Retreat）。</summary>
        public bool Showing => Current >= Phase.Appear && Current <= Phase.Retreat;

        /// <summary>体験者の交代・締めの外で呼ぶ。</summary>
        public void Reset()
        {
            Current = Phase.Idle;
            ElapsedSec = 0f;
            _anchorWaitSec = 0f;
            _shotCaptured = false;
            _photoGivenUp = false;
            _faceHeldSec = 0f;
            _noticeWaitSec = 0f;
            _noDollSec = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。
        /// </summary>
        /// <param name="dt">経過秒。</param>
        /// <param name="endingTakeActive">締めの take が走っているか。外れたら畳んで Idle へ戻す。</param>
        /// <param name="releasedLiveReady">報告済み・ライブのカット・入れ替わりと乱れの遷移が終わっている。</param>
        /// <param name="cgFree">CG 層が空いている（入れ替わりの人形が消え切った）。</param>
        /// <param name="beginAccepted">前フレームの <see cref="Step.begin"/> を CG 層が受けたか。</param>
        /// <param name="anchored">CG 層が立ち位置を解けたか。</param>
        /// <param name="cgShowing">CG 層が別れの人形をまだ持っているか（外から畳まれたら false）。</param>
        /// <param name="noticeOpen">スイの面が開いている。閉じるまで人形を出さない（上限 <see cref="NoticeWaitMaxSec"/>）。</param>
        public Step Tick(float dt, bool endingTakeActive, bool releasedLiveReady, bool cgFree,
                         bool beginAccepted, bool anchored, bool cgShowing, bool noticeOpen = false)
        {
            var step = new Step();
            dt = Mathf.Max(0f, dt);
            if (!endingTakeActive)
            {
                // 締めが終わった（電源断へ進んだ・中止・体験者の交代）。出ていれば畳む。
                step.hide = Current == Phase.Anchoring || Showing;
                Reset();
                step.sample = SampleAt(-1f);
                return step;
            }

            switch (Current)
            {
                case Phase.Idle:
                    if (releasedLiveReady) _noticeWaitSec += dt;
                    if (releasedLiveReady && cgFree && (!noticeOpen || _noticeWaitSec >= NoticeWaitMaxSec))
                    {
                        step.begin = true;
                        Current = Phase.Anchoring;
                        _anchorWaitSec = 0f;
                    }
                    break;

                case Phase.Anchoring:
                    _noDollSec += dt;
                    if (!beginAccepted)
                    {
                        Current = Phase.Unavailable;
                        break;
                    }
                    if (anchored)
                    {
                        Current = Phase.Appear;
                        ElapsedSec = 0f;
                        break;
                    }
                    _anchorWaitSec += dt;
                    if (_anchorWaitSec >= AnchorTimeoutSec)
                    {
                        step.hide = true;
                        Current = Phase.Unavailable;
                        _anchorWaitSec = 0f;
                    }
                    break;

                case Phase.Appear:
                case Phase.Face:
                case Phase.Turn:
                case Phase.Retreat:
                    if (!cgShowing)
                    {
                        // 締めが続いているのに器を失った。未完了を Done にせず再試行する。
                        Current = Phase.Unavailable;
                        _anchorWaitSec = 0f;
                        _shotCaptured = false;
                        break;
                    }
                    ElapsedSec += dt;
                    if (Current == Phase.Face && !_shotCaptured)
                    {
                        _faceHeldSec += dt;
                        _photoGivenUp = _faceHeldSec >= PhotoGiveUpSec;
                    }
                    // 写真が GPU へ写るまで、向き合った姿勢を保つ。上限を過ぎたら写真なしで退場する。
                    if (!_shotCaptured && !_photoGivenUp)
                        ElapsedSec = Mathf.Min(ElapsedSec, AppearSec + FaceSec - 0.0001f);
                    Current = SampleAt(ElapsedSec).phase;
                    if (Current == Phase.Done) step.hide = true;
                    break;

                case Phase.Unavailable:
                    _anchorWaitSec += dt;
                    _noDollSec += dt;
                    if (_noDollSec >= PhotoGiveUpSec)
                    {
                        // 出せないまま待ち続けない。報告は通っているので、人形なしで締めを終わらせる。
                        Current = Phase.Done;
                        break;
                    }
                    if (_anchorWaitSec >= 1f && releasedLiveReady && cgFree)
                    {
                        Current = Phase.Idle;
                        ElapsedSec = 0f;
                        _anchorWaitSec = 0f;
                        _shotCaptured = false;
                        _faceHeldSec = 0f;
                    }
                    break;
            }

            step.sample = Current == Phase.Anchoring
                ? new Sample { phase = Phase.Anchoring }
                : Showing ? SampleAt(ElapsedSec) : SampleAt(-1f);
            if (!Showing && Current != Phase.Anchoring) step.sample.phase = Current;
            step.sample.photoLevel = PhotoLevel;
            return step;
        }

        /// <summary>
        /// 時刻表の純関数。<paramref name="t"/> は現れ始めからの秒。負なら何も出ていない。
        /// Editor のプレビューも同じ関数で段を描く。
        /// </summary>
        public static Sample SampleAt(float t)
        {
            if (t < 0f) return new Sample { phase = Phase.Idle };
            if (t >= TotalSec) return new Sample { phase = Phase.Done };

            if (t < AppearSec)
                return new Sample { phase = Phase.Appear, alpha = Smooth(t / AppearSec) };
            t -= AppearSec;
            if (t < FaceSec)
                return new Sample { phase = Phase.Face, alpha = 1f };
            t -= FaceSec;
            if (t < TurnSec)
                return new Sample { phase = Phase.Turn, alpha = 1f, turn01 = Smooth(t / TurnSec) };
            t -= TurnSec;

            // 下がる。歩き出しだけ緩め、あとは一定の速さ（止まりながら消えると「置かれた」に見える）。
            float u = Mathf.Clamp01(t / RetreatSec);
            const float ease = RetreatEaseFraction;
            float dist = u < ease
                ? u * u / (2f * ease)
                : ease / 2f + (u - ease);
            dist /= 1f - ease / 2f;
            float fadeStart = RetreatSec - FadeOutSec;
            float alpha = t <= fadeStart ? 1f : 1f - Smooth((t - fadeStart) / FadeOutSec);
            return new Sample { phase = Phase.Retreat, alpha = alpha, turn01 = 1f, retreatM = RetreatM * dist };
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        // ---- 立ち位置を解く -------------------------------------------------------------

        /// <summary>解けた立ち位置。</summary>
        public struct Anchor
        {
            /// <summary>人形の足元（ワールド）。</summary>
            public Vector3 foot;
            /// <summary>L 字壁の内側の、体験者が写真を撮る位置。</summary>
            public Vector3 visitorTargetFoot;
            public Vector3 wallCorner, wallEndA, wallEndB;
            public float visitorHeightM, visitorRadiusM, wallHalfThicknessM, dollBodyRadiusM;
            /// <summary>下がる向き（床の上の単位ベクトル）。</summary>
            public Vector3 retreatDir;
            public Vector3 retreatCorner, retreatEnd, cameraPosition;
            public bool hasRetreatPath;
            /// <summary>後ろを向いた後のヨー（度）。</summary>
            public float awayYawDeg;
            /// <summary>人形と体験者の足元の枠 UV（観測用）。</summary>
            public Vector2 dollUv, visitorUv;
            /// <summary>画面のずらしで決まったか（false ならカメラの左右方向で置き直した）。</summary>
            public bool fromScreen;
            /// <summary>体験者がいま立っている所から解けたか（false なら L 字の内側の点を仮に置いた）。</summary>
            public bool fromVisitor;
            /// <summary>下がり終わりの像の高さ / 立っているときの像の高さ。</summary>
            public float retreatShrink;
        }

        /// <summary>全身が画に入り、互いに向き合う。</summary>
        public const int PhotoLevelStrict = 0;
        /// <summary>足が切れてもよい。頭と胸は画に入る。互いに向き合う。</summary>
        public const int PhotoLevelFeetOut = 1;
        /// <summary>胸が画に入れば撮る。人の向きを問わない。写真なしで終わるより切れた写真を残す。</summary>
        public const int PhotoLevelAnyYaw = 2;

        public const float PhotoYawToleranceDeg = 35f;
        /// <summary>写真で人形と体験者の足元を離す横幅の下限（ビューポート）。</summary>
        public const float PhotoMinOrderU = 0.05f;
        /// <summary>人形の足を画に入れる半径。回転全身の半径は足元には無い（足の実寸）。</summary>
        public const float DollFootFrameRadiusM = 0.06f;
        /// <summary>下がる道の長さ (m)。短いと動いて見えない。</summary>
        public const float RetreatMinM = 0.2f;
        public const float RetreatMaxM = 1.2f;
        /// <summary>下がる向きとカメラから遠ざかる向きの内積の下限。背を向けて奥へ下がる。</summary>
        public const float RetreatMinAwayDot = 0.5f;
        /// <summary>下がり終わりの像の高さの上限（立っているときの比）。縮まない道は採らない。</summary>
        public const float RetreatMaxShrink = 0.92f;
        public const float PhotoWallClearanceM = 0.04f;
        public const float PhotoFrameMargin = 0.005f;
        public const float RetreatMaxSpeedMps = 0.35f;
        // 本番人形 heightM=.4 の CPU bounds: x 最大 .15675569 / z 最大 .07268838。
        // 全回転の保守上限 .17278873m を包む値（2026-10-30 保存プレート計測）。
        public const float DefaultDollBodyRadiusM = 0.174f;

        /// <summary>
        /// L 字の内側に人形を立てる。壁の端点は床上のワールド座標。
        /// <paramref name="visitorFoot"/> を渡すと、まず体験者がいま立っている所に合わせて解く。
        /// 解けなければ（渡さなければ）L 字の内側から体験者の点も一緒に探す。
        /// </summary>
        public static bool TrySolveAtWall(Camera cam, Vector2 containScale, Vector3 corner,
                                          Vector3 endA, Vector3 endB, out Anchor anchor,
                                          float visitorHeightM = 1.65f, float visitorRadiusM = 0.36f,
                                          float dollHeightM = 0.4f, float wallHalfThicknessM = 0.02f,
                                          System.Func<Vector3, float, bool>? canStand = null,
                                          System.Func<Vector3, Vector3, bool>? pairAccepted = null,
                                          float dollBodyRadiusM = DefaultDollBodyRadiusM,
                                          System.Func<Anchor, bool>? trajectoryAccepted = null,
                                          Vector3? visitorFoot = null, float? visitorYawDeg = null)
        {
            anchor = default;
            Vector3 a = Flat(endA - corner), b = Flat(endB - corner);
            if (cam == null || a.magnitude < 0.5f || b.magnitude < 0.5f
                || Mathf.Abs(Vector3.Dot(a.normalized, b.normalized)) > 0.15f
                || visitorHeightM <= 0f || visitorRadiusM <= 0f || dollHeightM <= 0f) return false;
            Vector3 axisA = a.normalized, axisB = b.normalized;
            float dollRadius = Mathf.Max(0.08f, dollBodyRadiusM);
            var visitors = new List<Vector3>();
            var dolls = new List<Vector3>();
            // 端点より先の開いた側も候補に含める。壁際に寄せて頭を切る固定比率には戻さない。
            const float grid = 0.025f;
            for (float x = grid; x <= a.magnitude + 0.25f; x += grid)
                for (float z = grid; z <= b.magnitude + 0.25f; z += grid)
                {
                    Vector3 p = corner + axisA * x + axisB * z;
                    float clearance = Mathf.Min(DistanceToWall(p, corner, endA), DistanceToWall(p, corner, endB))
                                      - wallHalfThicknessM;
                    if (clearance >= PhotoWallClearanceM
                        && (canStand == null || canStand(p, 0.25f))
                        && PointFitsFrame(cam, p) && PointFitsFrame(cam, p + Vector3.up * visitorHeightM)) visitors.Add(p);
                    if (clearance >= dollRadius + PhotoWallClearanceM
                        && (canStand == null || canStand(p, dollRadius))
                        && FitsFrame(cam, p, dollHeightM, dollRadius)) dolls.Add(p);
                }
            var search = new Search
            {
                cam = cam, containScale = containScale, corner = corner, endA = endA, endB = endB,
                visitorHeightM = visitorHeightM, visitorRadiusM = visitorRadiusM, dollHeightM = dollHeightM,
                dollRadiusM = dollRadius, halfWall = wallHalfThicknessM, canStand = canStand,
                pairAccepted = pairAccepted, trajectoryAccepted = trajectoryAccepted, dolls = dolls,
                retreats = new List<RetreatPlan>?[dolls.Count],
            };
            bool fromVisitor = false;
            if (visitorFoot.HasValue && ChestInFrame(cam, visitorFoot.Value, visitorHeightM))
            {
                var here = new List<Vector3>(1) { visitorFoot.Value };
                fromVisitor = search.Run(here, true, visitorYawDeg);
            }
            // 体験者が枠の外か人形に近すぎるときは仮の点で人形だけ先に立てる。全身と壁の余白が解ける点を優先する。
            if (!fromVisitor && !search.Run(visitors, false, null) && !search.Run(visitors, true, null))
            {
                Debug.LogWarning($"[EndingFarewell] placement unavailable visitors={visitors.Count} dolls={dolls.Count} " +
                    $"separated={search.gapPairs} ordered={search.orderedPairs} framed={search.framedPairs} " +
                    $"noVisitorFit={search.visitorFitFails} noRetreat={search.retreatFails} " +
                    $"pairRejected={search.pairFails} pathRejected={search.pathFails}");
                return false;
            }
            Vector3 visitor = search.visitor, doll = search.doll;
            RetreatPlan retreat = search.retreat;
            Debug.Log($"[EndingFarewell] placement visitor=({visitor.x:0.000},{visitor.z:0.000}) " +
                $"doll=({doll.x:0.000},{doll.z:0.000}) gap={Vector3.Distance(visitor, doll):0.000}m " +
                $"end=({retreat.end.x:0.000},{retreat.end.z:0.000}) shrink={retreat.shrink:0.00} fromVisitor={(fromVisitor ? 1 : 0)}");
            anchor = search.MakeAnchor(visitor, doll, retreat);
            anchor.fromVisitor = fromVisitor;
            return true;
        }

        /// <summary>下がる道の候補。立つ点ごとに 1 度だけ解き、体験者ごとに通れる道を選ぶ。</summary>
        private readonly struct RetreatPlan
        {
            public readonly bool ok;
            public readonly Vector3 end;
            public readonly float shrink;
            public readonly float score;
            public RetreatPlan(Vector3 end, float shrink, float score)
            {
                ok = true; this.end = end; this.shrink = shrink; this.score = score;
            }
        }

        /// <summary>体験者の候補と人形の候補の組を点数で選ぶ。1 回の探索の状態を束ねる。</summary>
        private sealed class Search
        {
            public Camera cam = null!;
            public Vector2 containScale;
            public Vector3 corner, endA, endB;
            public float visitorHeightM, visitorRadiusM, dollHeightM, dollRadiusM, halfWall;
            public System.Func<Vector3, float, bool>? canStand;
            public System.Func<Vector3, Vector3, bool>? pairAccepted;
            public System.Func<Anchor, bool>? trajectoryAccepted;
            public List<Vector3> dolls = null!;
            public List<RetreatPlan>?[] retreats = null!;
            public Vector3 visitor, doll;
            public RetreatPlan retreat;
            public int gapPairs, orderedPairs, framedPairs, visitorFitFails, retreatFails, pairFails, pathFails;

            /// <param name="fixedVisitor">体験者の点は実際の立ち位置。身体の余白や全身の枠は問わない。</param>
            public bool Run(List<Vector3> visitors, bool fixedVisitor, float? visitorYawDeg)
            {
                float best = float.PositiveInfinity;
                foreach (Vector3 v in visitors)
                {
                    Cg.ShowCgLayer.TryFrameUv(cam, v, containScale, out Vector2 vu);
                    for (int i = 0; i < dolls.Count; i++)
                    {
                        Vector3 d = dolls[i];
                        float gap = Vector3.Distance(v, d);
                        // 人形の高さに重なる足・下腿の断面と、人形の円断面の表面間を測る。
                        // 最大身体半径に任意の余裕を足した距離で、対面写真の候補を捨てない。
                        float footGap = gap - FootHalfDepth(visitorHeightM, visitorRadiusM) - dollRadiusM;
                        float legGap = gap - 0.13f - dollRadiusM;
                        // 実際の立ち位置は動かせないので、表面が触れない所まで寄ってよい。
                        if (Mathf.Min(footGap, legGap) < (fixedVisitor ? 0f : PhotoWallClearanceM)) continue;
                        gapPairs++;
                        Cg.ShowCgLayer.TryFrameUv(cam, d, containScale, out Vector2 du);
                        if (du.x >= vu.x - PhotoMinOrderU) continue;
                        orderedPairs++;
                        float score = Mathf.Abs(gap - 0.75f) + Mathf.Abs(vu.y - du.y) + Mathf.Abs(du.x - 0.38f);
                        if (!fixedVisitor) score += Mathf.Abs(vu.x - 0.65f);
                        if (visitorYawDeg.HasValue)
                            score += Mathf.Abs(Mathf.DeltaAngle(visitorYawDeg.Value, FaceYawDeg(v, d))) / 180f;
                        if (score >= best) continue;
                        if (!fixedVisitor && !VisitorFits(cam, v, visitorHeightM, visitorRadiusM, FaceYawDeg(v, d),
                                corner, endA, endB, halfWall, canStand))
                        {
                            visitorFitFails++;
                            continue;
                        }
                        retreats[i] ??= SolveRetreats(cam, d, corner, endA, endB, dollRadiusM, dollHeightM, halfWall, canStand);
                        // 下がる途中で体験者の下腿に触れない。人形は横へ抜けるので、前後に長い足の寸法は使わない。
                        float pathGap = 0.13f + dollRadiusM;
                        RetreatPlan plan = PickRetreat(retreats[i]!, d, v, pathGap);
                        if (!plan.ok)
                        {
                            retreatFails++;
                            continue;
                        }
                        // 像がよく縮む点を選び、体験者から離れる道を少しだけ好む。
                        score += plan.shrink + 0.5f * Mathf.Max(0f, gap - Vector3.Distance(plan.end, v));
                        if (score >= best) continue;
                        if (pairAccepted != null && !pairAccepted(v, d))
                        {
                            pairFails++;
                            continue;
                        }
                        if (trajectoryAccepted != null && !trajectoryAccepted(MakeAnchor(v, d, plan)))
                        {
                            pathFails++;
                            continue;
                        }
                        framedPairs++;
                        best = score; visitor = v; doll = d; retreat = plan;
                    }
                }
                return !float.IsPositiveInfinity(best);
            }

            public Anchor MakeAnchor(Vector3 v, Vector3 d, in RetreatPlan plan)
            {
                Vector3 dir = Flat(plan.end - d).normalized;
                Cg.ShowCgLayer.TryFrameUv(cam, d, containScale, out Vector2 dollUv);
                Cg.ShowCgLayer.TryFrameUv(cam, v, containScale, out Vector2 visitorUv);
                return new Anchor
                {
                    foot = d, visitorTargetFoot = v, retreatDir = dir,
                    awayYawDeg = AwayYaw(d, dir, cam.transform.position), dollUv = dollUv,
                    visitorUv = visitorUv, fromScreen = false,
                    wallCorner = corner, wallEndA = endA, wallEndB = endB,
                    visitorHeightM = visitorHeightM, visitorRadiusM = visitorRadiusM,
                    wallHalfThicknessM = halfWall, dollBodyRadiusM = dollRadiusM,
                    retreatCorner = Vector3.Lerp(d, plan.end, 0.5f), retreatEnd = plan.end,
                    cameraPosition = cam.transform.position, hasRetreatPath = true,
                    retreatShrink = plan.shrink,
                };
            }
        }

        /// <summary>本番と保存プレートが共有する退場の位置と向き。</summary>
        public static void PoseAt(in Anchor anchor, in Sample sample, float faceYawDeg,
                                  out Vector3 foot, out float yawDeg)
        {
            foot = anchor.foot;
            if (anchor.hasRetreatPath)
            {
                float first = Vector3.Distance(anchor.foot, anchor.retreatCorner);
                float second = Vector3.Distance(anchor.retreatCorner, anchor.retreatEnd);
                float progress = sample.phase == Phase.Done ? 1f : Mathf.Clamp01(sample.retreatM / RetreatM);
                float distance = progress * (first + second);
                foot = distance <= first
                    ? Vector3.Lerp(anchor.foot, anchor.retreatCorner, first > 1e-5f ? distance / first : 1f)
                    : Vector3.Lerp(anchor.retreatCorner, anchor.retreatEnd, second > 1e-5f ? (distance - first) / second : 1f);
            }
            else foot += anchor.retreatDir * (sample.phase == Phase.Done ? RetreatM : sample.retreatM);
            float away = anchor.hasRetreatPath
                ? AwayYaw(foot, anchor.retreatDir, anchor.cameraPosition) : anchor.awayYawDeg;
            yawDeg = TurnYawDeg(faceYawDeg, away, sample.phase == Phase.Done ? 1f : sample.turn01);
        }

        /// <summary>進む向きとカメラから遠ざかる向きの中間。背中をカメラへ見せたまま進む方へ向く。</summary>
        private static float AwayYaw(Vector3 foot, Vector3 retreatDir, Vector3 camera)
        {
            Vector3 fromCamera = Flat(foot - camera);
            Vector3 away = retreatDir + (fromCamera.sqrMagnitude > 1e-6f ? fromCamera.normalized : Vector3.zero);
            return YawOf(away.sqrMagnitude > 1e-6f ? away : fromCamera);
        }

        /// <summary>
        /// 立つ点から L 字の奥へ下がる直線を解く。全身を画に残し、背中をカメラへ向け、像が縮む道だけを採る。
        /// 立つ点と終点の両方で全身が枠に入れば、直線の途中も枠に入る（枠は凸で、投影は直線を直線へ写す）。
        /// 点数のよい順に返す。
        /// </summary>
        private static List<RetreatPlan> SolveRetreats(Camera cam, Vector3 foot, Vector3 corner, Vector3 endA, Vector3 endB,
                                                float radius, float height, float halfWall,
                                                System.Func<Vector3, float, bool>? canStand)
        {
            const int directions = 24;
            const float stepM = 0.05f;
            Vector3 axisA = Flat(endA - corner).normalized, axisB = Flat(endB - corner).normalized;
            Vector3 away = Flat(foot - cam.transform.position).normalized;
            float clearance = radius + halfWall + PhotoWallClearanceM;
            float startHeight = ProjectedHeight(cam, foot, height);
            float startU = cam.WorldToViewportPoint(foot).x;
            var result = new List<RetreatPlan>();
            for (int k = 0; k < directions; k++)
            {
                float angle = k * Mathf.PI * 2f / directions;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                if (Vector3.Dot(dir, away) < RetreatMinAwayDot) continue;
                for (float length = RetreatMinM; length <= RetreatMaxM + 1e-4f; length += stepM)
                {
                    Vector3 end = foot + dir * length;
                    if (Vector3.Dot(end - corner, axisA) <= 0f || Vector3.Dot(end - corner, axisB) <= 0f
                        || !SegmentClearsWalls(foot, end, corner, endA, endB, clearance)
                        || !FitsFrame(cam, end, height, radius)
                        || (canStand != null && !canStand(end, radius))) break;
                    float shrink = ProjectedHeight(cam, end, height) / Mathf.Max(1e-5f, startHeight);
                    if (shrink > RetreatMaxShrink) continue;
                    float score = shrink + 0.5f * Mathf.Abs(cam.WorldToViewportPoint(end).x - startU);
                    result.Add(new RetreatPlan(end, shrink, score));
                }
            }
            result.Sort((x, y) => x.score.CompareTo(y.score));
            return result;
        }

        /// <summary>体験者の足元から minGapM 以上離れて通れる道のうち、いちばん点数のよいもの。無ければ ok=false。</summary>
        private static RetreatPlan PickRetreat(List<RetreatPlan> plans, Vector3 doll, Vector3 visitor, float minGapM)
        {
            foreach (RetreatPlan plan in plans)
                if (DistanceToWall(visitor, doll, plan.end) >= minGapM) return plan;
            return default;
        }

        private static float ProjectedHeight(Camera cam, Vector3 foot, float height)
            => Mathf.Abs(cam.WorldToViewportPoint(foot + Vector3.up * height).y - cam.WorldToViewportPoint(foot).y);

        private static bool SegmentClearsWalls(Vector3 from, Vector3 to, Vector3 corner,
                                                Vector3 endA, Vector3 endB, float clearance)
        {
            return !CrossesWall(from, to, corner, endA) && !CrossesWall(from, to, corner, endB)
                   && DistanceBetweenSegments(from, to, corner, endA) >= clearance - 1e-5f
                   && DistanceBetweenSegments(from, to, corner, endB) >= clearance - 1e-5f;
        }

        private static float DistanceBetweenSegments(Vector3 from, Vector3 to, Vector3 a, Vector3 b)
            => Mathf.Min(Mathf.Min(DistanceToWall(from, a, b), DistanceToWall(to, a, b)),
                         Mathf.Min(DistanceToWall(a, from, to), DistanceToWall(b, from, to)));

        private static float DistanceToWall(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 d = Flat(b - a);
            if (d.sqrMagnitude < 1e-8f) return Flat(p - a).magnitude;
            return Flat(p - a - d * Mathf.Clamp01(Vector3.Dot(Flat(p - a), d) / d.sqrMagnitude)).magnitude;
        }

        private static bool FitsFrame(Camera cam, Vector3 foot, float height, float radius)
        {
            // 上下端と胴幅を投影。足先や頭まで胴の最大半径を使う円柱は実際より過大になるため、
            // 頭と足は実寸の上限で、胴だけ全半径で調べる。
            for (int layer = 0; layer < 3; layer++)
            {
                float y = layer == 0 ? 0f : layer == 1 ? height * 0.5f : height;
                float r = layer == 0 ? Mathf.Min(radius, DollFootFrameRadiusM) : layer == 1 ? radius : Mathf.Min(radius, 0.11f);
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * Mathf.PI * 0.25f;
                    Vector3 p = cam.WorldToViewportPoint(foot + new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r));
                    if (p.z <= cam.nearClipPlane || p.x < PhotoFrameMargin || p.x > 1f - PhotoFrameMargin
                        || p.y < PhotoFrameMargin || p.y > 1f - PhotoFrameMargin)
                        return false;
                }
            }
            return true;
        }

        private static bool PointFitsFrame(Camera cam, Vector3 point)
        {
            Vector3 uv = cam.WorldToViewportPoint(point);
            return uv.z > cam.nearClipPlane && uv.x >= PhotoFrameMargin && uv.x <= 1f - PhotoFrameMargin
                   && uv.y >= PhotoFrameMargin && uv.y <= 1f - PhotoFrameMargin;
        }

        // 全身の最大半径（肩や下げた手を含む）は足の長さではない。
        private static float FootHalfDepth(float height, float bodyRadius)
            => Mathf.Min(bodyRadius, 0.20f * height / 1.65f);

        private static void BodyLayer(int layer, float height, float footExtent,
                                      out float y, out float halfWidth, out float halfDepth)
        {
            y = layer == 0 ? 0f : layer == 1 ? height * 0.45f : layer == 2 ? height * 0.82f : height;
            halfWidth = layer == 0 ? 0.11f : layer == 3 ? 0.10f : 0.25f;
            halfDepth = layer == 0 ? FootHalfDepth(height, footExtent) : layer == 3 ? 0.11f : 0.13f;
        }

        /// <summary>頭・肩と胴・足を別の幅と厚みにして、体験者の向きで回した実寸を確認する。</summary>
        private static bool VisitorFits(Camera? cam, Vector3 foot, float height, float footExtent,
                                          float yaw, Vector3 corner, Vector3 endA, Vector3 endB, float halfWall,
                                          System.Func<Vector3, float, bool>? canStand = null)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int layer = 0; layer < 4; layer++)
            {
                BodyLayer(layer, height, footExtent, out float y, out float halfWidth, out float halfDepth);
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 point = foot + rotation * new Vector3(x * halfWidth, y, z * halfDepth);
                        if (cam != null && !PointFitsFrame(cam, point)) return false;
                        if (canStand != null && !canStand(point, 0f)) return false;
                        float clearance = Mathf.Min(DistanceToWall(point, corner, endA), DistanceToWall(point, corner, endB));
                        if (clearance < halfWall + PhotoWallClearanceM
                            || CrossesWall(foot, point, corner, endA) || CrossesWall(foot, point, corner, endB)) return false;
                    }
            }
            return true;
        }

        private static bool CrossesWall(Vector3 from, Vector3 to, Vector3 a, Vector3 b)
        {
            Vector3 p = Flat(to - from), q = Flat(b - a), r = Flat(a - from);
            float cross = p.x * q.z - p.z * q.x;
            if (Mathf.Abs(cross) < 1e-6f) return false;
            float t = (r.x * q.z - r.z * q.x) / cross;
            float u = (r.x * p.z - r.z * p.x) / cross;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>左に人形・右に体験者の構図で、互いに向き合ったときに写真を撮る。</summary>
        public static bool IsPhotoReady(in Anchor anchor, Vector3 visitorFoot,
                                         float visitorYawDeg, float dollYawDeg, Camera? cam = null,
                                         int photoLevel = PhotoLevelStrict)
            => PhotoBlock(anchor, visitorFoot, visitorYawDeg, dollYawDeg, cam, photoLevel).Length == 0;

        /// <summary>
        /// 写真を止めている条件の名前（揃っていれば空）。左右の並び・枠・人の向き・人形の向きの順に見る。
        /// 体験者の実際の位置は壁で判定しない（映像の中に実物が立っている）。
        /// </summary>
        public static string PhotoBlock(in Anchor anchor, Vector3 visitorFoot,
                                        float visitorYawDeg, float dollYawDeg, Camera? cam = null,
                                        int photoLevel = PhotoLevelStrict)
        {
            if (cam != null)
            {
                if (cam.WorldToViewportPoint(anchor.foot).x
                    >= cam.WorldToViewportPoint(visitorFoot).x - PhotoMinOrderU) return "order";
                bool framed = photoLevel == PhotoLevelStrict
                    ? BodyFitsFrame(cam, visitorFoot, anchor.visitorHeightM, anchor.visitorRadiusM, visitorYawDeg)
                    : photoLevel == PhotoLevelFeetOut
                        ? HeadInFrame(cam, visitorFoot, anchor.visitorHeightM) && ChestInFrame(cam, visitorFoot, anchor.visitorHeightM)
                        : ChestInFrame(cam, visitorFoot, anchor.visitorHeightM);
                if (!framed) return "framing";
            }
            if (photoLevel < PhotoLevelAnyYaw
                && Mathf.Abs(Mathf.DeltaAngle(visitorYawDeg, FaceYawDeg(visitorFoot, anchor.foot))) > PhotoYawToleranceDeg)
                return "visitorYaw";
            if (Mathf.Abs(Mathf.DeltaAngle(dollYawDeg, FaceYawDeg(anchor.foot, visitorFoot))) > PhotoYawToleranceDeg)
                return "dollYaw";
            return "";
        }

        private static bool BodyFitsFrame(Camera cam, Vector3 foot, float height, float footExtent, float yaw)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int layer = 0; layer < 4; layer++)
            {
                BodyLayer(layer, height, footExtent, out float y, out float halfWidth, out float halfDepth);
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                        if (!PointFitsFrame(cam, foot + rotation * new Vector3(x * halfWidth, y, z * halfDepth))) return false;
            }
            return true;
        }

        private static bool HeadInFrame(Camera cam, Vector3 foot, float height)
            => PointFitsFrame(cam, foot + Vector3.up * (height - 0.10f));

        private static bool ChestInFrame(Camera cam, Vector3 foot, float height)
            => PointFitsFrame(cam, foot + Vector3.up * (height * 0.70f));

        /// <summary>
        /// 体験者の足元から、画面の中で左へ <see cref="ScreenGapU"/> ずらした床の点に人形を立てる。
        /// 足元の高さ（v）は体験者と揃えるが、<see cref="MinFootV"/>〜<see cref="MaxFootV"/> に収める。
        /// 床は体験者の足元と同じ高さの水平面。
        /// </summary>
        /// <param name="cam">人形を描く仮想カメラ（実カメラの較正済み姿勢）。</param>
        /// <param name="containScale">ライブの contain-fit（<c>MjpegScreen.ContainScale</c>）。</param>
        /// <param name="visitorFoot">体験者の足元（ワールド）。</param>
        public static bool TrySolve(Camera cam, Vector2 containScale, Vector3 visitorFoot, out Anchor anchor)
        {
            anchor = default;
            if (cam == null) return false;
            if (!Cg.ShowCgLayer.TryFrameUv(cam, visitorFoot, containScale, out Vector2 visitorUv)) return false;

            Vector3 camPos = cam.transform.position;
            Vector3 camRight = Flat(cam.transform.right);
            if (camRight.sqrMagnitude < 1e-6f) return false;
            camRight.Normalize();

            bool fromScreen = false;
            Vector3 foot = visitorFoot;
            float u = Mathf.Max(MinU, visitorUv.x - ScreenGapU);
            float v = Mathf.Clamp(visitorUv.y, MinFootV, MaxFootV);
            if (TryFloorAtFrameUv(cam, containScale, new Vector2(u, v), visitorFoot.y, out Vector3 hit))
            {
                float gap = Flat(hit - visitorFoot).magnitude;
                if (gap >= MinGapM && gap <= MaxGapM && Vector3.Dot(hit - visitorFoot, camRight) < 0f)
                {
                    foot = hit;
                    fromScreen = true;
                }
                else if (Vector3.Dot(hit - visitorFoot, camRight) < 0f)
                {
                    foot = visitorFoot - camRight * Mathf.Clamp(gap, MinGapM, MaxGapM);
                }
                else
                {
                    foot = visitorFoot - camRight * MinGapM;
                }
            }
            else
            {
                foot = visitorFoot - camRight * MinGapM;
            }
            foot.y = visitorFoot.y;

            Vector3 awayFromVisitor = Flat(foot - visitorFoot);
            Vector3 awayFromCamera = Flat(foot - camPos);
            if (awayFromVisitor.sqrMagnitude < 1e-6f) awayFromVisitor = -camRight;
            Vector3 dir = awayFromVisitor.normalized
                          + (awayFromCamera.sqrMagnitude > 1e-6f ? awayFromCamera.normalized * AwayFromCameraMix : Vector3.zero);
            if (dir.sqrMagnitude < 1e-6f) dir = -camRight;
            dir.Normalize();

            Cg.ShowCgLayer.TryFrameUv(cam, foot, containScale, out Vector2 dollUv);
            anchor = new Anchor
            {
                foot = foot,
                retreatDir = dir,
                awayYawDeg = YawOf(dir),
                dollUv = dollUv,
                visitorUv = visitorUv,
                fromScreen = fromScreen,
            };
            return true;
        }

        /// <summary>人形から体験者を向くヨー（度）。</summary>
        public static float FaceYawDeg(Vector3 dollFoot, Vector3 visitorFoot)
        {
            Vector3 d = Flat(visitorFoot - dollFoot);
            return d.sqrMagnitude < 1e-6f ? 0f : YawOf(d);
        }

        /// <summary>立っている向きと後ろを向いた向きの間を回る（短い側）。</summary>
        public static float TurnYawDeg(float faceYawDeg, float awayYawDeg, float turn01)
            => Mathf.LerpAngle(faceYawDeg, awayYawDeg, Mathf.Clamp01(turn01));

        /// <summary>
        /// 枠 UV の点を床（高さ <paramref name="floorY"/>）へ戻す。
        /// <see cref="Cg.ShowCgLayer.TryFrameUv"/> の逆（<c>p = (uv - 0.5) / s + 0.5</c>）。
        /// </summary>
        public static bool TryFloorAtFrameUv(Camera cam, Vector2 containScale, Vector2 frameUv, float floorY,
                                             out Vector3 hit)
        {
            hit = Vector3.zero;
            if (cam == null || containScale.x <= 1e-4f || containScale.y <= 1e-4f) return false;
            var vp = new Vector3((frameUv.x - 0.5f) / containScale.x + 0.5f,
                                 (frameUv.y - 0.5f) / containScale.y + 0.5f, 0f);
            Ray ray = cam.ViewportPointToRay(vp);
            if (ray.direction.y > -1e-4f) return false;
            float t = (floorY - ray.origin.y) / ray.direction.y;
            if (t <= 0f) return false;
            hit = ray.origin + ray.direction * t;
            return true;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
        private static float YawOf(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
    }
}
