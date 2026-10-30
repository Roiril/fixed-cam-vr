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
            /// <summary>立てられなかった（較正・位置合わせ・頭の追跡が無い）。写真は人形なしで撮る。</summary>
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

        public const float TotalSec = AppearSec + FaceSec + TurnSec + RetreatSec;
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
        public Step Tick(float dt, bool endingTakeActive, bool releasedLiveReady, bool cgFree,
                         bool beginAccepted, bool anchored, bool cgShowing)
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
                    if (releasedLiveReady && cgFree)
                    {
                        step.begin = true;
                        Current = Phase.Anchoring;
                        _anchorWaitSec = 0f;
                    }
                    break;

                case Phase.Anchoring:
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
                    // 写真が GPU へ写るまで、向き合った姿勢を保つ。
                    if (!_shotCaptured)
                        ElapsedSec = Mathf.Min(ElapsedSec, AppearSec + FaceSec - 0.0001f);
                    Current = SampleAt(ElapsedSec).phase;
                    if (Current == Phase.Done) step.hide = true;
                    break;

                case Phase.Unavailable:
                    _anchorWaitSec += dt;
                    if (_anchorWaitSec >= 1f && releasedLiveReady && cgFree) Reset();
                    break;
            }

            step.sample = Current == Phase.Anchoring
                ? new Sample { phase = Phase.Anchoring }
                : Showing ? SampleAt(ElapsedSec) : SampleAt(-1f);
            if (!Showing && Current != Phase.Anchoring) step.sample.phase = Current;
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
        }

        public const float PhotoPositionToleranceM = 0.20f;
        public const float PhotoYawToleranceDeg = 35f;
        public const float PhotoWallClearanceM = 0.04f;
        public const float PhotoFrameMargin = 0.005f;
        public const float RetreatMaxSpeedMps = 0.35f;
        // 本番人形 heightM=.4 の CPU bounds: x 最大 .15675569 / z 最大 .07268838。
        // 全回転の保守上限 .17278873m を包む値（2026-10-30 保存プレート計測）。
        public const float DefaultDollBodyRadiusM = 0.174f;

        /// <summary>L 字の内側に対面する 2 点を固定する。壁の端点は床上のワールド座標。</summary>
        public static bool TrySolveAtWall(Camera cam, Vector2 containScale, Vector3 corner,
                                          Vector3 endA, Vector3 endB, out Anchor anchor,
                                          float visitorHeightM = 1.65f, float visitorRadiusM = 0.36f,
                                          float dollHeightM = 0.4f, float wallHalfThicknessM = 0.02f,
                                          System.Func<Vector3, float, bool>? canStand = null,
                                          System.Func<Vector3, Vector3, bool>? pairAccepted = null,
                                          float dollBodyRadiusM = DefaultDollBodyRadiusM,
                                          System.Func<Anchor, bool>? trajectoryAccepted = null)
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
            float best = float.PositiveInfinity;
            Vector3 doll = default, visitor = default;
            Vector3 retreatCorner = default, retreatEnd = default;
            int gapPairs = 0, orderedPairs = 0, framedPairs = 0;
            foreach (Vector3 v in visitors)
            {
                Cg.ShowCgLayer.TryFrameUv(cam, v, containScale, out Vector2 vu);
                foreach (Vector3 d in dolls)
                {
                    float gap = Vector3.Distance(v, d);
                    // 人形の高さに重なる足・下腿の断面と、人形の円断面の表面間を測る。
                    // 最大身体半径に任意の余裕を足した距離で、対面写真の候補を捨てない。
                    float footGap = gap - FootHalfDepth(visitorHeightM, visitorRadiusM) - dollRadius;
                    float legGap = gap - 0.13f - dollRadius;
                    if (Mathf.Min(footGap, legGap) < PhotoWallClearanceM) continue;
                    gapPairs++;
                    Cg.ShowCgLayer.TryFrameUv(cam, d, containScale, out Vector2 du);
                    if (du.x >= vu.x) continue;
                    orderedPairs++;
                    float score = Mathf.Abs(gap - 0.75f) + Mathf.Abs(vu.y - du.y) * 2f
                                  + Mathf.Abs(vu.x - 0.65f) + Mathf.Abs(du.x - 0.40f);
                    if (score >= best) continue;
                    if (!VisitorFits(cam, v, visitorHeightM, visitorRadiusM, FaceYawDeg(v, d),
                        corner, endA, endB, wallHalfThicknessM, canStand)) continue;
                    if (!TryRetreatPath(cam, d, v, corner, endA, endB, dollRadius, dollHeightM,
                        wallHalfThicknessM, canStand, out Vector3 bend, out Vector3 end)) continue;
                    if (pairAccepted != null && !pairAccepted(v, d)) continue;
                    if (trajectoryAccepted != null)
                    {
                        var trial = new Anchor
                        {
                            foot = d, visitorTargetFoot = v, retreatCorner = bend, retreatEnd = end,
                            cameraPosition = cam.transform.position, hasRetreatPath = true,
                            retreatDir = Flat(bend - d).normalized,
                            awayYawDeg = AwayYaw(d, v, cam.transform.position),
                            wallCorner = corner, wallEndA = endA, wallEndB = endB,
                            visitorHeightM = visitorHeightM, visitorRadiusM = visitorRadiusM,
                            wallHalfThicknessM = wallHalfThicknessM, dollUv = du, visitorUv = vu,
                            dollBodyRadiusM = dollRadius,
                        };
                        if (!trajectoryAccepted(trial)) continue;
                    }
                    framedPairs++;
                    best = score; visitor = v; doll = d; retreatCorner = bend; retreatEnd = end;
                }
            }
            if (float.IsPositiveInfinity(best))
            {
                Debug.LogWarning($"[EndingFarewell] placement unavailable visitors={visitors.Count} dolls={dolls.Count} " +
                    $"separated={gapPairs} ordered={orderedPairs} framed={framedPairs}");
                return false;
            }
            Debug.Log($"[EndingFarewell] placement visitor=({visitor.x:0.000},{visitor.z:0.000}) " +
                $"doll=({doll.x:0.000},{doll.z:0.000}) gap={Vector3.Distance(visitor, doll):0.000}m");
            Vector3 retreat = Flat(retreatCorner - doll).normalized;
            Cg.ShowCgLayer.TryFrameUv(cam, doll, containScale, out Vector2 dollUv);
            Cg.ShowCgLayer.TryFrameUv(cam, visitor, containScale, out Vector2 visitorUv);
            anchor = new Anchor
            {
                foot = doll, visitorTargetFoot = visitor, retreatDir = retreat,
                awayYawDeg = AwayYaw(doll, visitor, cam.transform.position), dollUv = dollUv,
                visitorUv = visitorUv, fromScreen = false,
                wallCorner = corner, wallEndA = endA, wallEndB = endB,
                visitorHeightM = visitorHeightM, visitorRadiusM = visitorRadiusM,
                wallHalfThicknessM = wallHalfThicknessM,
                dollBodyRadiusM = dollRadius,
                retreatCorner = retreatCorner, retreatEnd = retreatEnd,
                cameraPosition = cam.transform.position, hasRetreatPath = true,
            };
            return true;
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
                ? AwayYaw(foot, anchor.visitorTargetFoot, anchor.cameraPosition) : anchor.awayYawDeg;
            yawDeg = TurnYawDeg(faceYawDeg, away, sample.phase == Phase.Done ? 1f : sample.turn01);
        }

        private static float AwayYaw(Vector3 foot, Vector3 visitor, Vector3 camera)
        {
            Vector3 away = Flat(foot - visitor).normalized + Flat(foot - camera).normalized;
            return YawOf(away.sqrMagnitude > 1e-6f ? away : Flat(foot - camera));
        }

        private static bool TryRetreatPath(Camera cam, Vector3 foot, Vector3 visitor, Vector3 corner,
                                            Vector3 endA, Vector3 endB, float radius, float height, float halfWall,
                                            System.Func<Vector3, float, bool>? canStand,
                                            out Vector3 bend, out Vector3 end)
        {
            bend = end = default;
            float best = float.PositiveInfinity;
            float clearance = radius + halfWall + PhotoWallClearanceM + 0.01f;
            for (int exit = 0; exit < 2; exit++)
            {
                Vector3 endpoint = exit == 0 ? endA : endB;
                Vector3 other = exit == 0 ? endB : endA;
                Vector3 along = Flat(endpoint - corner).normalized;
                Vector3 side = Flat(other - corner).normalized;
                Vector3 p = endpoint + along * clearance
                            + side * Mathf.Max(Vector3.Dot(foot - corner, side), clearance);
                Vector3 q = endpoint + along * clearance - side * (clearance * 2f);
                if (Vector3.Distance(q, cam.transform.position) <= Vector3.Distance(foot, cam.transform.position) + 0.04f)
                    continue;
                if (ProjectedHeight(cam, q, height) >= ProjectedHeight(cam, foot, height) * 0.97f) continue;
                if (!GrowsAway(foot, p, visitor) || !GrowsAway(p, q, visitor)) continue;
                if (!SegmentClearsWalls(foot, p, corner, endA, endB, clearance)
                    || !SegmentClearsWalls(p, q, corner, endA, endB, clearance)) continue;
                if (canStand != null && (!canStand(p, radius) || !canStand(q, radius))) continue;
                float length = Vector3.Distance(foot, p) + Vector3.Distance(p, q);
                // SampleAt の加速曲線の最大速度。名目 RetreatM の速度で代用しない。
                if (length / (RetreatSec * (1f - RetreatEaseFraction * 0.5f)) > RetreatMaxSpeedMps) continue;
                if (length >= best) continue;
                best = length; bend = p; end = q;
            }
            return !float.IsPositiveInfinity(best);
        }

        private static bool GrowsAway(Vector3 from, Vector3 to, Vector3 origin)
            => Vector3.Dot(Flat(from - origin), Flat(to - from)) >= -1e-6f;

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
            // 頭は 0.11m・足は 0.14m を上限とし、胴だけ全半径で調べる。
            for (int layer = 0; layer < 3; layer++)
            {
                float y = layer == 0 ? 0f : layer == 1 ? height * 0.5f : height;
                float r = layer == 0 ? Mathf.Min(radius, 0.14f) : layer == 1 ? radius : Mathf.Min(radius, 0.11f);
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

        /// <summary>頭・肩と胴・足を別の幅と厚みにして、体験者の向きで回した実寸を確認する。</summary>
        private static bool VisitorFits(Camera? cam, Vector3 foot, float height, float footExtent,
                                          float yaw, Vector3 corner, Vector3 endA, Vector3 endB, float halfWall,
                                          System.Func<Vector3, float, bool>? canStand = null)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int layer = 0; layer < 4; layer++)
            {
                float y = layer == 0 ? 0f : layer == 1 ? height * 0.45f : layer == 2 ? height * 0.82f : height;
                float halfWidth = layer == 0 ? 0.11f : layer == 3 ? 0.10f : 0.25f;
                float halfDepth = layer == 0 ? FootHalfDepth(height, footExtent) : layer == 3 ? 0.11f : 0.13f;
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

        /// <summary>所定の位置と、互いに向き合った向きが揃ったときだけ写真を撮る。</summary>
        public static bool IsPhotoReady(in Anchor anchor, Vector3 visitorFoot,
                                         float visitorYawDeg, float dollYawDeg, Camera? cam = null)
            => Flat(visitorFoot - anchor.visitorTargetFoot).magnitude <= PhotoPositionToleranceM
               && (anchor.visitorRadiusM <= 0f
                   || VisitorFits(cam, visitorFoot, anchor.visitorHeightM, anchor.visitorRadiusM,
                       visitorYawDeg, anchor.wallCorner, anchor.wallEndA, anchor.wallEndB, anchor.wallHalfThicknessM))
               && Mathf.Abs(Mathf.DeltaAngle(visitorYawDeg, FaceYawDeg(visitorFoot, anchor.foot))) <= PhotoYawToleranceDeg
               && Mathf.Abs(Mathf.DeltaAngle(dollYawDeg, FaceYawDeg(anchor.foot, visitorFoot))) <= PhotoYawToleranceDeg;

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
