#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 体験者の歩行を合成して、体験 1 回（導入 → 3 周 → 終了）を **HMD を被らずに**走らせる
    /// デバッグ起動フック。<see cref="RegVizDebugDriver"/> と同じ流儀で、Development ビルド +
    /// 起動フラグの時だけ動く。
    ///
    /// <b>既存コードは 1 行も変更していない。</b> 動かすのは <c>OVRCameraRig</c> の Transform だけで、
    /// 結果として <c>CenterEyeAnchor</c> のワールド位置が動く ＝ ゾーン判定・通過ライン・周回・
    /// CG の follow・開始位置の判定が、実際に歩いたときと同じ経路を通る。
    ///
    /// <b>なぜ course 座標で歩くか</b>: 位置合わせ（registration）が現地とズレていても、
    /// course 空間では一貫している。物理的にどこに立っているかはズレても、**体験の論理**
    /// （どの区間に居るか・何周目か・どの演出が出るか）の検証はそのまま成立する。
    ///
    /// <b>経路は grid から機械的に作る</b>（手書きの座標を焼かない）。カメラ間の移動は
    /// 「出発カメラと到着カメラのタイルだけを通る」BFS で解くので、**途中で第三のカメラの領域を
    /// 横切らない**。手書きの直線だと、部屋の形によっては意図しない区間へ一瞬入って
    /// 予定外の演出が武装する（実際にこのレイアウトの B→C 直線は A を横切る）。
    ///
    /// 起動:
    ///   <c>adb shell am start -e xpwalk 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity</c>
    /// ログは全て <c>[XPWalk]</c> タグ。体験そのものの観測は <c>[XP]</c>（ShowTelemetryHost）が出す。
    ///
    /// ⚠⚠ <b><c>-e relief 1</c> を足すと、注意書きの中でホラー軽減モードへ入った走行になる</b>
    /// （2026-09-05・<c>canon/LEDGER.md</c> 0154）。あのモードは<b>左コントローラの長押しでしか
    /// 入れない</b>ので、素の走行では音の経路が 1 度も通らない ＝
    /// <b>実機で効いているかを確かめる手段が無い</b>。
    /// ⚠ <b>校正は両側を流す</b>（`~/.claude/rules/work-style.md` §2-3）— 付けない走行で
    /// <c>relief=0/0/1.00/…</c>、付けた走行で <c>relief=1/1/0.50/…</c> が出て初めて、
    /// 対象と計器のどちらが正しいかが決まる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowWalkDebugDriver : MonoBehaviour
    {
        /// <summary>起動してから歩き始めるまでの待ち（show.json 受信・ゾーン生成・登録ロードを待つ）。</summary>
        private const float StartupDelaySec = 8f;

        /// <summary>歩く速さ (m/s)。実際の体験者はもっと速いが、区間を飛ばさない速度にしてある。</summary>
        private const float WalkSpeed = 0.5f;

        /// <summary>カメラの代表点に着いてから次へ向かうまでの滞在 (秒)。演出の尺を見せ切るため。</summary>
        private const float DwellSec = 6f;

        /// <summary>到達判定の半径 (m)。</summary>
        private const float ArriveEps = 0.05f;

        /// <summary>何があっても打ち切る上限 (秒)。ドライバが居座って次のテストを邪魔しないため。</summary>
        private const float HardLimitSec = 600f;

        /// <summary>導入が終わるのを待つ上限 (秒)。超えたら諦めて歩き出す（導入の不具合も観測対象）。</summary>
        private const float IntroWaitLimitSec = 90f;

        /// <summary>タイトルが閉じ切るのを待つ上限 (秒)。A から Done まで実測 4.85 秒。</summary>
        private const float TitleWaitLimitSec = 15f;

        /// <summary>
        /// A を押す前に、真っ暗な待ち（注意書きが出ている段）を保つ秒数。
        /// <b>走行の画に注意書きを写すためだけの間</b>で、実機の運用ではここは数十秒ある。
        /// </summary>
        private const float NoticeReadSec = 4f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Debug.isDebugBuild) return;
            if (!FlagPresent()) return;
            var go = new GameObject("[XPWalkDriver]");
            DontDestroyOnLoad(go);
            go.AddComponent<ShowWalkDebugDriver>();
            Debug.Log("[XPWalk] 起動フラグ検出 — 自動走行を予約（8 秒後）");
        }

        private static bool FlagPresent() => ExtraPresent("xpwalk");

        /// <summary>
        /// 起動フラグを 1 つ読む（Android は intent の extra・それ以外はコマンドライン引数）。
        /// ⚠ <b>読めなければ false</b> — 走行の起動そのものを例外で止めない。
        /// </summary>
        private static bool ExtraPresent(string name)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = act.Call<AndroidJavaObject>("getIntent");
                string v = intent.Call<string>("getStringExtra", name);
                return !string.IsNullOrEmpty(v);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[XPWalk] intent extra 読取失敗（{name}）: {e.Message}");
                return false;
            }
#else
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
#endif
        }

        private Transform? _rig;
        private Transform? _head;
        private CourseFrame? _frame;
        private ShowControlClient? _show;
        private ShowRunDirector? _run;
        private TimelineDirector? _timeline;

        private void Start() => StartCoroutine(DriveRoutine());

        private IEnumerator DriveRoutine()
        {
            yield return new WaitForSeconds(StartupDelaySec);

            if (!Resolve()) yield break;

            // ⚠ **位置合わせから抜ける。** 2026-08-13 から起動と同時に位置合わせへ入るようになった
            //   （`canon/LEDGER.md` 0023 の ②。アプリを起動するのはスタッフなので）。
            //   そのままだと導入が凍結され、**走行しても段 0 から 1 歩も進まない**（実測で踏んだ）。
            //   ⚠ **確定はしない。** B 確定は registration.json を書き換えるので、現場で合わせた
            //   値を検証用の走行が壊すことになる。既存の登録をそのまま使って退場するだけ。
            var reg = FindObjectOfType<CourseRegistrationController>();
            if (reg != null && reg.IsActive)
            {
                reg.Toggle();
                Debug.Log("[XPWalk] 位置合わせから退場した（確定はしない）");
                yield return null;
            }

            ShowLayoutDef? layout = _show!.Layout;
            ShowGridDef? grid = layout?.grid;
            if (grid == null || !grid.HasData())
            {
                Debug.LogError("[XPWalk] layout.grid が無い — 経路を作れないので終了");
                yield break;
            }

            int[] order = _show.CourseOrder ?? Array.Empty<int>();
            if (order.Length == 0)
            {
                Debug.LogError("[XPWalk] layout.course.order が空 — 終了");
                yield break;
            }

            var map = BuildCells(grid, out int rows, out int cols);
            int laps = _run != null ? _run.TotalLaps : 3;
            var route = BuildRoute(map, rows, cols, grid.tileM, order, laps, out string routeLog);
            if (route.Count == 0)
            {
                Debug.LogError("[XPWalk] 経路生成に失敗 — 終了");
                yield break;
            }
            Debug.Log($"[XPWalk] 経路: {routeLog}");

            // --- 導入 ---
            // HMD を被っていない前提で走らせるので、被り検知を無効化する（Development ビルド限定）。
            // これが無いと run.intro.startLineId を使う設定では導入が永久に始まらない。
            if (_show != null) _show.UserPresentProvider = () => true;

            // ⚠⚠ **タイトルは実機と同じく A で閉じる**（2026-08-14）。それまで被り検知の provider が
            //    「被っている」と「タイトルが立っていない」を兼ねていたので、ここで全体を true に
            //    上書きした瞬間にタイトルを飛び越して導入が始まり、**題字は走行の画に 1 枚も
            //    写っていなかった**（＝ タイトル画面が一度も自動検証されていなかった）。
            //    いまは開始承認が別 provider なので、A を送らないと段 0 から進まない。
            var title = FindObjectOfType<TitleScreen>();
            if (title != null && title.IsBlocking)
            {
                // ⚠ **注意書きを読む間を置く**（2026-08-14）。実機ではスタッフが位置合わせを終えて
                //    周回リセットし、体験者に被せてから A を押すので、真っ暗な待ち（`TitleStage.Wait`）は
                //    数十秒ある。ここで即座に A を送ると**待ちが 1 フレームも無く、注意書きが
                //    走行の画に 1 枚も写らない**（安全の掲示なのに、実機で読めるか確かめる手段が無い）。
                // ⚠⚠ **読む間より先に入る。** 実機の体験者は注意書きを読みながら長押しして、
                //    そのあとスタッフが A を押すので、**注意書きが出ているあいだ陽気な曲が鳴る**
                //    （`canon/LEDGER.md` 0154 のユーザー指定）。読む間の後に入れると、
                //    走行では入った 0.3 秒後に題字が立つので**注意書きの最中に鳴った証拠が残らない**
                //    （2026-09-05 の走行 20260905_100203 が実際にそうだった）。
                // ⚠ `TitleScreen.BeginTitle` が落とすので、A を押す前でなければ次のリセットで消える。
                //    実機の押下と同じ入口（`Toggle`）を通す。
                if (ExtraPresent("relief"))
                {
                    FixedCamVr.Streaming.HorrorRelief.Toggle();
                    Debug.Log("[XPWalk] ホラー軽減モードへ入った（-e relief 1）— "
                              + "既存の音が半分になり、陽気な曲が流れる走行になる");
                }

                Debug.Log("[XPWalk] 注意書きを読む間（真っ暗な待ち）");
                yield return new WaitForSeconds(NoticeReadSec);

                if (title.RequestAdvance()) Debug.Log("[XPWalk] タイトルを A で閉じた（実機と同じ入り方）");
                else Debug.LogWarning($"[XPWalk] タイトルの A が効かない（{title.DescribeAdvanceBlock()}）");
                float titleWait = 0f;
                while (title.IsBlocking && titleWait < TitleWaitLimitSec)
                {
                    titleWait += Time.deltaTime;
                    yield return null;
                }
                Debug.Log(title.IsBlocking
                    ? $"[XPWalk] タイトルが {TitleWaitLimitSec:F0}s で閉じ切らなかった — そのまま進む"
                    : $"[XPWalk] タイトルが閉じ切った（{titleWait:F1}s）");
            }
            else if (title == null)
            {
                Debug.Log("[XPWalk] タイトルがシーンに居ない（そのまま導入へ）");
            }

            // 開始ラインが著作されていれば、実機と同じ入り方をする ＝ その線を横切ってから中へ入る。
            string startLineId = _run != null ? (_run.IntroDef?.startLineId ?? "") : "";
            Vector2 before = Vector2.zero;
            Vector2 after = Vector2.zero;
            bool hasLine = !string.IsNullOrEmpty(startLineId)
                           && TryLineCrossing(layout, startLineId, out before, out after);

            // ⚠ **体験エリアの外から始める。** 封印の箱（SealedBox）は外に立っている人にしか見えないので、
            //    中から歩き出す走行では**この演出だけ一度も観測できない**（実測で box=0 のまま 29 標本）。
            //    ここで外へ出て少し立つと、自動走行が導入の最初の絵まで通しで踏む。
            yield return StartCoroutine(ApproachFromOutside(layout, hasLine ? before : Vector2.zero));

            // ⚠⚠ **歩行誘導が出る現場では、円へ着かないと導入が始まらない**（canon/LEDGER.md 0079）。
            //    接近では抜けないので、ここを飛ばすと走行は 30 秒の時間切れを待つことになる。
            //    タイトルを A で閉じるのと同じ理屈 — **走行側が実機と同じ入り方をする**。
            yield return StartCoroutine(WalkToGuideSpot(layout));

            // ⚠ **導入は箱の外で流れる**（canon/LEDGER.md 0005）。終わるまで中へ入らない —
            //    入ると黒しか見えないので、演出の画が 1 枚も撮れない。
            float introWait = 0f;
            while (_run != null && _run.Phase == ShowPhase.Intro && introWait < IntroWaitLimitSec)
            {
                introWait += Time.deltaTime;
                yield return null;
            }
            if (_run != null && _run.Phase == ShowPhase.Intro)
                Debug.LogWarning($"[XPWalk] 導入が {IntroWaitLimitSec:F0}s で終わらなかった — そのまま歩き出す");
            else
                Debug.Log($"[XPWalk] 導入が終わった（{introWait:F1}s）— ここから中へ入る");

            if (hasLine)
            {
                Debug.Log($"[XPWalk] 開始ライン '{startLineId}' を横切る " +
                          $"({before.x:F2},{before.y:F2}) → ({after.x:F2},{after.y:F2})");
                yield return StartCoroutine(WalkTo(before));
                yield return StartCoroutine(WalkTo(after));
            }

            Vector2 startCourse = ResolveStartCourse(layout, map, rows, cols, grid.tileM, order[0]);
            Debug.Log($"[XPWalk] 開始位置へ ({startCourse.x:F2},{startCourse.y:F2})");
            yield return StartCoroutine(WalkTo(startCourse));

            // --- 本編: 経路を辿る ---
            float t0 = Time.realtimeSinceStartup;
            foreach (Waypoint wp in route)
            {
                if (Time.realtimeSinceStartup - t0 > HardLimitSec)
                {
                    Debug.LogWarning("[XPWalk] 上限時間に達したので打ち切る");
                    break;
                }
                if (_run != null && _run.Phase == ShowPhase.Finished)
                {
                    Debug.Log("[XPWalk] 体験が終了したので歩行を止める");
                    break;
                }

                yield return StartCoroutine(WalkTo(wp.Course));
                if (wp.HoldSec > 0f)
                {
                    Debug.Log($"[XPWalk] 到着 cam={wp.Camera} ({wp.Course.x:F2},{wp.Course.y:F2}) — {wp.HoldSec:F0}s 滞在");
                    yield return StartCoroutine(HoldAndMaybeReport(wp.HoldSec));
                }
            }

            // 帰りの A で締めを待っているなら、そこでも押す（最後の区間は上のループを抜けた後）。
            yield return StartCoroutine(HoldAndMaybeReport(ReportWaitSec + 2f));

            // 終了の判定は ShowRunDirector が握っている。歩き終わっても終わらないなら、
            // それ自体が観測結果（周回したのに終わらない = 周回検知の不具合）。
            // 帰りの A では前の周の録画が流れ切るのを待つので、run.endHoldMaxSec ぶんの余裕を見る。
            float tailLimit = _run != null ? Mathf.Max(30f, ShowRunDefaults.EndHoldMaxSec + 10f) : 30f;
            float tail = 0f;
            while (_run != null && _run.Phase != ShowPhase.Finished && tail < tailLimit)
            {
                tail += Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[XPWalk] 走行終了 phase={(_run != null ? _run.Phase.ToString() : "?")} " +
                      $"lap={(_run != null ? _run.Lap : -1)} 経過={Time.realtimeSinceStartup - t0:F0}s");
        }

        // ---------------------------------------------------------------- 参照

        private bool Resolve()
        {
            _frame = FindObjectOfType<CourseFrame>();
            _show = FindObjectOfType<ShowControlClient>();
            _run = FindObjectOfType<ShowRunDirector>();
            _timeline = FindObjectOfType<TimelineDirector>();

            var cam = Camera.main;
            _head = cam != null ? cam.transform : null;
            if (_head == null)
            {
                var go = GameObject.Find("OVRCameraRig/TrackingSpace/CenterEyeAnchor");
                _head = go != null ? go.transform : null;
            }
            if (_head != null)
            {
                // CenterEyeAnchor の祖先で OVRCameraRig を探す（親に === Rig === 等が居ても効く）。
                Transform? t = _head;
                while (t != null && t.name != "OVRCameraRig") t = t.parent;
                _rig = t != null ? t : (_head.parent != null ? _head.parent.parent : null);
                if (_rig == null) _rig = _head.root;
            }

            if (_frame == null || _show == null || _head == null || _rig == null)
            {
                Debug.LogError($"[XPWalk] 必要な参照が揃わない: frame={_frame != null} show={_show != null} " +
                               $"head={_head != null} rig={_rig != null} — 終了");
                return false;
            }
            Debug.Log($"[XPWalk] rig='{_rig.name}' head='{_head.name}' " +
                      $"reg={(_frame.HasRegistration ? "あり" : "なし")} run={_run != null}");
            return true;
        }

        // ---------------------------------------------------------------- 歩行

        /// <summary>
        /// 滞在しつつ、<b>「報告するまで」で止まっているカットがあれば 1 回だけ報告を押す</b>。
        ///
        /// <b>なぜ走行側に要るか</b>: 4 周目 A の締め（<c>durKind:"untilMark"</c>）は体験者の
        /// 左 X / Y でしか進まない。走行はコントローラを持たないので、押す真似をしないと
        /// <b>締めのカットは実機で一度も検証されない</b>（タイトルの A を押す真似と同じ理由）。
        ///
        /// ⚠ **押すのは 1 回だけ**。実機の体験者と同じ回数にしないと、終幕の報告数が嘘になる。
        /// ⚠ 走っている演出が待っている時だけ押す（誰も待っていない所で押すと、
        ///   「報告は進行に使わない」を検証している他の判定を汚す）。
        /// ⚠⚠ **すぐには押さない**（<see cref="ReportHesitateSec"/>）。待ちが立った瞬間に押すと、
        ///   AIエージェントからの連絡の③（押さないまま 3 秒で催促・`canon/LEDGER.md` 0054）が
        ///   <b>実機で一度も走らない</b>。人はどのみち一拍おいて押すので、そちらの方が実機に近い。
        /// </summary>
        private IEnumerator HoldAndMaybeReport(float holdSec)
        {
            float t = 0f;
            while (t < holdSec)
            {
                t += Time.deltaTime;
                if (!_reported && _show != null && _timeline != null && _timeline.IsWaitingForVisitorMark)
                {
                    _waitedSec += Time.deltaTime;
                    if (_waitedSec >= ReportHesitateSec)
                    {
                        _reported = true;
                        Debug.Log($"[XPWalk] 異変を報告する（左 X の代わり・{_waitedSec:0.0}s ためらった）");
                        _show.RecordVisitorMark();
                    }
                }
                yield return null;
            }
        }

        // このランで報告を押したか（1 回だけ）。
        private bool _reported;

        // 締めのカットが待ち始めてからの経過（ためらい）。
        private float _waitedSec;

        /// <summary>
        /// 締めのカットが待ち始めてから押すまで (秒)。
        /// ⚠ 連絡③の閾値（<c>CommsCueLogic.PromptAfterWaitSec</c> = 2s）より<b>長く</b>取る。
        /// 縮めると③が走らず、催促は実機で検証されないまま出荷される。
        /// </summary>
        private const float ReportHesitateSec = 4.5f;

        /// <summary>
        /// 締めのカットが報告を待ちうる時間（走行の最後にこれだけ粘る）。
        /// ⚠ <see cref="ReportHesitateSec"/> ぶんためらってから押すので、その余裕を含める。
        /// </summary>
        private const float ReportWaitSec = 12f;

        /// <summary>体験エリアの外に出て少し立つ。封印の箱を見る時間（<see cref="SealBoxHoldSec"/>）。</summary>
        private const float SealBoxHoldSec = 4f;

        /// <summary>武装に必要な距離へさらに足す余裕 (m)。</summary>
        private const float OutsideMarginM = 0.5f;

        /// <summary>導入を起こす立ち位置の「矩形の外までの距離」(m)。中へは入らない。</summary>
        private const float TriggerMarginM = 0.35f;

        /// <summary>
        /// <paramref name="dir"/> の向きへ、<b>矩形の外までの距離</b>が <paramref name="wantM"/> に
        /// なるまで出た点を返す。原点からの半径で決めると、斜め方向で必ず足りなくなる。
        /// </summary>
        private static Vector2 StepOutUntil(Vector2 dir, Vector2 half, float wantM)
        {
            float reach = Mathf.Max(half.x, half.y);
            for (int i = 0; i < 60; i++)
            {
                if (ContainmentShellLogic.DistanceOutsideM(dir * reach, half) >= wantM) break;
                reach += 0.1f;
            }
            return dir * reach;
        }

        /// <summary>
        /// 体験エリアの外へ回り込んでから中へ入る。<b>封印の箱は外からしか見えない</b>ので、
        /// ここを通らないと自動走行では一度も観測できない。
        ///
        /// 出る向きは<b>これから入る側</b>（開始ラインの手前の点）。反対側へ回ると、
        /// 実際の体験者と違う面を見ることになる。
        /// </summary>
        private IEnumerator ApproachFromOutside(ShowLayoutDef? layout, Vector2 entryPoint)
        {
            if (!ContainmentShellLogic.TryFootprint(layout, _show != null ? _show.Room : null,
                                                    out Vector2 half))
            {
                Debug.Log("[XPWalk] 体験エリアの footprint が解けない — 外からの接近は飛ばす");
                yield break;
            }

            Vector2 dir = entryPoint.sqrMagnitude > 1e-4f ? entryPoint.normalized : new Vector2(0f, -1f);
            // ⚠ **原点からの半径で立ち位置を決めない**（2026-08-11 実測）。斜めに出ると
            //    「矩形の外までの距離」は半径よりずっと短く、実測で 1.8m 出したつもりが 1.20m しか
            //    離れておらず、接近の武装（1.0 + 0.35）に届かないまま導入が始まらなかった。
            Vector2 outside = StepOutUntil(dir, half,
                IntroLogic.ApproachNearM + ApproachLogic.ArmMarginM + OutsideMarginM);
            Debug.Log($"[XPWalk] 外から接近 ({outside.x:F2},{outside.y:F2}) — 封印の箱を {SealBoxHoldSec:F0}s 見る");
            yield return StartCoroutine(WalkTo(outside));
            FaceCourseOrigin();
            yield return new WaitForSeconds(SealBoxHoldSec);

            // **導入の合図はここ** — 境界へ近づく（IntroLogic.ApproachNearM 以内へ）。
            // 立ち止まる場所は**境界の外**。導入が終わるまで中へは入らない。
            Vector2 trigger = StepOutUntil(dir, half, TriggerMarginM);
            Debug.Log($"[XPWalk] 近づく ({trigger.x:F2},{trigger.y:F2}) — 導入が始まるはず");
            yield return StartCoroutine(WalkTo(trigger));
        }

        /// <summary>
        /// 歩行誘導の指定ポイントまで歩いて、少し留まる（<c>canon/LEDGER.md</c> 0079）。
        ///
        /// ⚠ <b>円が解けない現場では何もしない</b> — 導入は従来どおり接近で始まるので、
        /// ここで止まると走行が丸ごと成立しなくなる。
        /// </summary>
        private IEnumerator WalkToGuideSpot(ShowLayoutDef? layout)
        {
            WalkGuidePath.Path path = WalkGuidePath.Solve(layout, _show != null ? _show.Room : null);
            if (!path.valid)
            {
                Debug.Log("[XPWalk] 歩行誘導の円が解けない — 従来どおり接近だけで導入を待つ");
                yield break;
            }
            Debug.Log($"[XPWalk] 指定ポイントへ ({path.spot.x:F2},{path.spot.y:F2}) 半径 {path.radiusM:F2}m");
            yield return StartCoroutine(WalkTo(path.spot));
            FaceCourseOrigin();
            // 円の中に留まる（WalkGuideLogic.ArriveHoldSec）。取りこぼさないよう余裕を見る。
            yield return new WaitForSeconds(WalkGuideLogic.ArriveHoldSec * 3f);
        }

        /// <summary>
        /// 体験エリアの中心へ向き直す。<b>位置だけ動かしても画には出ない</b> —
        /// 頭の向きは机に置いた端末のままなので、封印の箱が視界の外にあって
        /// 「描画された（box=1）のに画には無い」になる（2026-08-10 実測で踏んだ）。
        ///
        /// 回すのは<b>頭の位置を軸にした rig の yaw</b>。rig の原点で回すと頭ごと平行移動してしまう。
        /// </summary>
        private void FaceCourseOrigin()
        {
            if (_rig == null || _head == null || _frame == null) return;
            Vector3 headW = _head.position;
            Vector3 to = _frame.CourseToWorld(Vector2.zero, 1.2f) - headW;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-4f) return;
            float want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            _rig.RotateAround(headW, Vector3.up, Mathf.DeltaAngle(_head.eulerAngles.y, want));
            Debug.Log($"[XPWalk] 体験エリアの方を向く（yaw {want:F0}°）");
        }

        private IEnumerator WalkTo(Vector2 courseTarget)
        {
            float guard = 0f;
            while (guard < 60f)
            {
                guard += Time.deltaTime;
                if (_frame == null || _head == null || _rig == null) yield break;

                Vector3 headW = _head.position;
                // 高さは使わない（XZ だけを見て水平に歩かせる）。CourseToWorld の第 2 引数は
                // 2026-08-02 から「床からの高さ」なので、ここに headW.y を渡すと意味が違う。
                Vector3 targetW = _frame.CourseToWorld(courseTarget, 0f);
                var d = new Vector2(targetW.x - headW.x, targetW.z - headW.z);
                float dist = d.magnitude;
                if (dist <= ArriveEps) yield break;

                float step = WalkSpeed * Time.deltaTime;
                Vector2 move = dist <= step ? d : d.normalized * step;
                _rig.position += new Vector3(move.x, 0f, move.y);
                yield return null;
            }
            Debug.LogWarning($"[XPWalk] 到達できないまま 60s ({courseTarget.x:F2},{courseTarget.y:F2})");
        }

        // ---------------------------------------------------------------- 経路生成

        private readonly struct Waypoint
        {
            public readonly Vector2 Course;
            public readonly float HoldSec;
            public readonly int Camera;
            public Waypoint(Vector2 c, float hold, int cam) { Course = c; HoldSec = hold; Camera = cam; }
        }

        /// <summary>cells（rows 本の文字列）を [row, col] → カメラ index（未割当 -1）へ。</summary>
        private static int[,] BuildCells(ShowGridDef g, out int rows, out int cols)
        {
            rows = Mathf.Max(0, g.rows);
            cols = Mathf.Max(0, g.cols);
            var map = new int[rows, cols];
            for (int r = 0; r < rows; r++)
            {
                string line = g.cells != null && r < g.cells.Length ? (g.cells[r] ?? "") : "";
                for (int c = 0; c < cols; c++)
                {
                    char ch = c < line.Length ? line[c] : '.';
                    map[r, c] = ch >= '0' && ch <= '8' ? ch - '0' : -1;
                }
            }
            return map;
        }

        private static Vector2 CellCenter(int r, int c, int rows, int cols, float tileM)
        {
            ZoneLayoutSolver.CellRect(r, c, rows, cols, tileM,
                out float xLo, out float xHi, out float zLo, out float zHi);
            return new Vector2((xLo + xHi) * 0.5f, (zLo + zHi) * 0.5f);
        }

        /// <summary>そのカメラのタイル集合の重心にいちばん近いタイル（＝代表点）。</summary>
        private static bool RepresentativeCell(int[,] map, int rows, int cols, int camera, out int rr, out int cc)
        {
            float sr = 0f, sc = 0f;
            int n = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (map[r, c] == camera) { sr += r; sc += c; n++; }
            rr = cc = -1;
            if (n == 0) return false;
            sr /= n; sc /= n;

            float best = float.MaxValue;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (map[r, c] != camera) continue;
                    float d = (r - sr) * (r - sr) + (c - sc) * (c - sc);
                    if (d >= best) continue;
                    best = d; rr = r; cc = c;
                }
            return rr >= 0;
        }

        /// <summary>
        /// from セルから to セルへ、<paramref name="camA"/> か <paramref name="camB"/> のタイルだけを通る
        /// 最短経路（4 近傍 BFS）。第三のカメラの領域を横切らないことがこの関数の存在理由。
        /// </summary>
        private static List<(int r, int c)>? BfsPath(int[,] map, int rows, int cols,
            (int r, int c) from, (int r, int c) to, int camA, int camB)
        {
            var prev = new int[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) prev[r, c] = -2;

            var q = new Queue<(int r, int c)>();
            q.Enqueue(from);
            prev[from.r, from.c] = -1;
            int[] dr = { 1, -1, 0, 0 };
            int[] dc = { 0, 0, 1, -1 };

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur == to) break;
                for (int k = 0; k < 4; k++)
                {
                    int nr = cur.r + dr[k], nc = cur.c + dc[k];
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;
                    if (prev[nr, nc] != -2) continue;
                    int cam = map[nr, nc];
                    if (cam != camA && cam != camB) continue;
                    prev[nr, nc] = cur.r * cols + cur.c;
                    q.Enqueue((nr, nc));
                }
            }
            if (prev[to.r, to.c] == -2) return null;

            var path = new List<(int r, int c)>();
            var p = to;
            while (true)
            {
                path.Add(p);
                int back = prev[p.r, p.c];
                if (back < 0) break;
                p = (back / cols, back % cols);
            }
            path.Reverse();
            return path;
        }

        /// <summary>
        /// order を <paramref name="laps"/> 周ぶん（+ 最後に order[0] へ戻る＝**帰りの A**）辿る
        /// ウェイポイント列。曲がり角だけを残す（直線区間の中間セルは間引く）。
        ///
        /// 締めの order[0] は「周回を確定させるためのおまけ」ではなく<b>体験の最後の区間</b>で、
        /// ここで前の周の録画が流れる。だから滞在も他と同じだけ取る（録画の再生を観測するため）。
        /// </summary>
        private static List<Waypoint> BuildRoute(int[,] map, int rows, int cols, float tileM,
            int[] order, int laps, out string log)
        {
            var route = new List<Waypoint>();
            log = "";
            var reps = new Dictionary<int, (int r, int c)>();
            foreach (int cam in order)
            {
                if (RepresentativeCell(map, rows, cols, cam, out int r, out int c)) reps[cam] = (r, c);
                else Debug.LogWarning($"[XPWalk] カメラ {cam} のタイルが grid に無い");
            }
            if (reps.Count < 2) return route;

            // laps 周 + 帰りの A（order[0] へ戻ると周回が上がり、そこが最後の区間になる）。
            if (laps < 1) laps = 3;
            var seq = new List<int>();
            for (int lap = 0; lap < laps; lap++)
                foreach (int cam in order) seq.Add(cam);
            seq.Add(order[0]);

            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < seq.Count; i++)
            {
                int from = seq[i - 1], to = seq[i];
                if (!reps.ContainsKey(from) || !reps.ContainsKey(to)) continue;
                List<(int r, int c)>? path = BfsPath(map, rows, cols, reps[from], reps[to], from, to);
                if (path == null)
                {
                    Debug.LogWarning($"[XPWalk] {from}->{to} の経路が無い（タイルが繋がっていない）— 直行する");
                    route.Add(new Waypoint(CellCenter(reps[to].r, reps[to].c, rows, cols, tileM), DwellSec, to));
                    sb.Append(from).Append("~>").Append(to).Append(' ');
                    continue;
                }

                // 曲がり角だけ残す（直線の途中は歩行で自然に通る）。
                for (int k = 1; k < path.Count - 1; k++)
                {
                    var a = path[k - 1]; var b = path[k]; var c2 = path[k + 1];
                    bool straight = (a.r == b.r && b.r == c2.r) || (a.c == b.c && b.c == c2.c);
                    if (straight) continue;
                    route.Add(new Waypoint(CellCenter(b.r, b.c, rows, cols, tileM), 0f, -1));
                }
                var last = path[path.Count - 1];
                route.Add(new Waypoint(CellCenter(last.r, last.c, rows, cols, tileM), DwellSec, to));
                sb.Append(from).Append("->").Append(to).Append(' ');
            }
            log = sb.ToString().TrimEnd();
            return route;
        }

        /// <summary>
        /// 開始ラインを確実に横切る 2 点（線の中点から法線方向へ前後 <c>CrossMarginM</c>）を返す。
        /// 「たまたま経路が横切る」に頼ると、部屋の形や登録のずれで沈黙して検証にならない。
        /// </summary>
        private static bool TryLineCrossing(ShowLayoutDef? layout, string lineId,
            out Vector2 before, out Vector2 after)
        {
            const float CrossMarginM = 0.35f;
            before = after = Vector2.zero;
            ShowLineDef[]? lines = layout?.lines;
            if (lines == null) return false;
            foreach (ShowLineDef? l in lines)
            {
                if (l == null || l.id != lineId) continue;
                var a = new Vector2(l.x1, l.z1);
                var b = new Vector2(l.x2, l.z2);
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-6f) return false;
                Vector2 n = new Vector2(-d.y, d.x).normalized;   // 線の法線
                Vector2 m = (a + b) * 0.5f;
                before = m - n * CrossMarginM;
                after = m + n * CrossMarginM;
                return true;
            }
            return false;
        }

        /// <summary>導入の開始位置。<c>layout.startSpot</c> が著作されていればそれ、無ければ順路先頭の代表点。</summary>
        private static Vector2 ResolveStartCourse(ShowLayoutDef? layout, int[,] map, int rows, int cols,
            float tileM, int firstCamera)
        {
            // hasStartSpot を見る（JsonUtility はキーが無くても実体を作るので startSpot != null は信じない）。
            if (layout != null && layout.hasStartSpot && layout.startSpot != null)
                return new Vector2(layout.startSpot.x, layout.startSpot.z);
            if (RepresentativeCell(map, rows, cols, firstCamera, out int r, out int c))
                return CellCenter(r, c, rows, cols, tileM);
            return Vector2.zero;
        }
    }
}
