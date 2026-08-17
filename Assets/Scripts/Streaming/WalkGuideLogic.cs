#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 歩行誘導の段（<c>canon/LEDGER.md</c> 0079）。<b><see cref="IntroStage"/> は増やさない</b> —
    /// これは段 0（開始待ち）の<b>内側</b>で回る別の層で、相もゲートも増やさない。
    /// </summary>
    public enum WalkGuideStage
    {
        /// <summary>出していない（タイトルが立っている / 本編 / 終幕 / 幾何が解けない）。</summary>
        Off,
        /// <summary>円が現れている途中。<b>矢印より先に目的地を見せる</b>（行き先が無い矢印は方角にしかならない）。</summary>
        SpotIn,
        /// <summary>山形が起点から円へ向かって順に点いていく途中。</summary>
        Trail,
        /// <summary>出し切って、流れだけが回っている。<b>ここが体験者の持ち時間</b>。</summary>
        Hold,
        /// <summary>円へ着いた。弧が閉じて、矢印が先に引く。</summary>
        Arrive,
        /// <summary>消えている途中。</summary>
        Out,
        /// <summary>終わった（この体験ではもう出さない）。</summary>
        Done,
    }

    /// <summary>誘導の各層へ配る値（すべて 0..1）。<b>見え方の判断はここに集約する</b>。</summary>
    public struct WalkGuideWeights
    {
        /// <summary>円ぜんたいの濃さ。</summary>
        public float spot;

        /// <summary>弧が描かれている量（0 = まだ 1 本も無い / 1 = 全部の弧が出ている）。</summary>
        public float ring;

        /// <summary>矢印ぜんたいの濃さ。</summary>
        public float arrow;

        /// <summary>山形が起点から先端まで出た割合（0 = 1 つも点いていない / 1 = 全部点いた）。</summary>
        public float reveal;

        /// <summary>流れの位相（0..1 を繰り返す）。山形の上を明るさが走る。</summary>
        public float flow;

        /// <summary>
        /// 着いた量（0 = まだ / 1 = 閉じ切った）。弧の切れ目が埋まって 1 本の輪になる。
        /// <b>ここだけは戻らない</b> — 着いたことは取り消せない。
        /// </summary>
        public float arrive;

        /// <summary>何も出していない。</summary>
        public static WalkGuideWeights Hidden => new WalkGuideWeights();
    }

    /// <summary>1 フレーム分の観測。<b>シーン API を触らない・dt 注入</b>。</summary>
    public struct WalkGuideInput
    {
        /// <summary>
        /// 誘導を出してよいか。呼び出し側が
        /// 「相が導入 ＆ 段 0 ＆ タイトルが閉じた ＆ 被っている ＆ 位置合わせ済み ＆ 道筋が解けた」を
        /// 全部 AND して渡す。
        /// </summary>
        public bool wanted;

        /// <summary>体験者の居場所が信用できるか（位置合わせ済みで頭のポーズが来ている）。</summary>
        public bool posValid;

        /// <summary>円の中心から体験者までの距離 (m)。</summary>
        public float distM;

        /// <summary>円の半径 (m)。<b>描いている輪と同じ値でなければならない</b>。</summary>
        public float radiusM;

        /// <summary>経過 (秒)。</summary>
        public float dt;
    }

    /// <summary>
    /// <b>タイトルの直後、体験者を所定の位置まで歩かせる誘導</b>の状態機械
    /// （<c>canon/LEDGER.md</c> 0079・シーン API を触らない dt 注入ロジック）。
    ///
    /// 描くのは <see cref="WalkGuide"/>、道筋を解くのは <see cref="WalkGuidePath"/>。
    ///
    /// ⚠⚠ <b>円を描いたら、開始の判定もその円でなければならない。</b>
    /// 導入の既定の開始は体験エリアへの<b>接近</b>（境界の 1.0m 外）なので、円を出したまま
    /// 接近で始めると<b>指示より手前で演出が走る</b> ＝ 装置が出した指示が嘘になる。
    /// だから誘導が出ているあいだ、<see cref="IntroLogic"/> の接近・安全網・救済は止まる
    /// （<c>IntroInput.guidingToSpot</c>）。
    ///
    /// ⚠ <b>止めたぶんの出口をここが持つ。</b> <see cref="HoldMaxSec"/> を超えても着かなければ
    /// 誘導を畳んで従来の判定へ戻す（<see cref="TimedOut"/>）。装置が諦めるのは正直な形で、
    /// 黙って「立っても始まらない」を作らないための唯一の逃げ道。
    /// </summary>
    public sealed class WalkGuideLogic
    {
        /// <summary>円が現れるまで (秒)。弧が 1 本ずつ描かれる。</summary>
        public const float SpotInSec = 0.9f;

        /// <summary>山形が起点から円まで点き切るまで (秒)。</summary>
        public const float TrailSec = 1.1f;

        /// <summary>着いてから引き始めるまで (秒)。弧が閉じて、矢印が先に消える。</summary>
        public const float ArriveSec = 0.7f;

        /// <summary>引き切るまで (秒)。</summary>
        public const float OutSec = 0.6f;

        /// <summary>流れが 1 周する速さ（毎秒何周）。<b>速いと急かし、遅いと止まって見える</b>。</summary>
        public const float FlowPerSec = 0.55f;

        /// <summary>
        /// 着かないまま待てる上限 (秒)。超えたら誘導を畳んで従来の開始判定へ戻す。
        ///
        /// ⚠ <b>短くしない。</b> 体験者は説明を読み、周りを見てから歩き出す。
        /// ⚠ <b>長くもしない。</b> ここが伸びるほど「立っても始まらない」時間が伸びる。
        /// </summary>
        public const float HoldMaxSec = 30f;

        /// <summary>円の中に留まって「着いた」とみなすまで (秒)。通りすがりで始めない。</summary>
        public const float ArriveHoldSec = 0.5f;

        /// <summary>円から出たとみなす余白 (m)。縁で震えても数えが 0 へ戻らない。</summary>
        public const float ExitMarginM = 0.10f;

        /// <summary>
        /// これより長い dt は数えない (秒)。HMD の着脱・再開の飛びで
        /// <b>1 フレームで滞在が成立する</b>のを防ぐ（<see cref="LineCrossLogic"/> と同じ流儀）。
        /// </summary>
        public const float MaxContinuousDtSec = 0.5f;

        private WalkGuideStage _stage = WalkGuideStage.Off;
        private float _elapsed;
        private float _holdSec;
        private float _flow;
        private float _arriveFrom;
        private bool _timedOut;
        private float _inSec;
        private bool _inside;
        private bool _arrived;

        /// <summary>いまの段。</summary>
        public WalkGuideStage Stage => _stage;

        /// <summary>出しているか（実行体が描くべきか）。</summary>
        public bool Active => _stage != WalkGuideStage.Off && _stage != WalkGuideStage.Done;

        /// <summary>
        /// 体験者へ「円へ行け」と言い切っているか。
        /// <b><see cref="IntroLogic"/> の接近・安全網・救済を止めてよいのはこれが true のあいだだけ。</b>
        /// 着いた後（<see cref="WalkGuideStage.Arrive"/> 以降）は止めない — もう用が済んでいる。
        /// </summary>
        public bool Directing => _stage == WalkGuideStage.SpotIn
                                 || _stage == WalkGuideStage.Trail
                                 || _stage == WalkGuideStage.Hold;

        /// <summary>着かないまま上限を超えたか（＝従来の開始判定へ戻した）。</summary>
        public bool TimedOut => _timedOut;

        /// <summary>
        /// <b>円へ着いたか。</b> 一度立てば <see cref="Reset"/> まで戻らない
        /// （着いたことは取り消せない ＝ 縁で 1 回だけ導入が始まる）。
        /// </summary>
        public bool Arrived => _arrived;

        /// <summary>いま円の中に居るか（診断・テスト用）。</summary>
        public bool Inside => _inside;

        /// <summary>円の中に留まっている秒数（診断・テスト用）。</summary>
        public float InsideSec => _inSec;

        /// <summary><see cref="WalkGuideStage.Hold"/> に居る秒数（診断・テスト用）。</summary>
        public float HoldSec => _holdSec;

        /// <summary>体験 1 回ぶんの状態を落とす（ラン開始 / 導入のやり直し）。冪等。</summary>
        public void Reset()
        {
            _stage = WalkGuideStage.Off;
            _elapsed = 0f;
            _holdSec = 0f;
            _flow = 0f;
            _arriveFrom = 0f;
            _timedOut = false;
            _inSec = 0f;
            _inside = false;
            _arrived = false;
        }

        /// <summary>時間を進める。<b>段が変わったら true</b>（呼び出し側が縁でログと観測を出す）。</summary>
        public bool Tick(in WalkGuideInput input)
        {
            float dt = input.dt > 0f ? input.dt : 0f;
            WalkGuideStage before = _stage;
            UpdateArrival(input, dt);

            // 流れは出ているあいだだけ回す（畳んだ後も回すと、次に出したとき位相が飛ぶ）。
            if (_stage == WalkGuideStage.Trail || _stage == WalkGuideStage.Hold)
            {
                _flow += dt * FlowPerSec;
                if (_flow >= 1f) _flow -= Mathf.Floor(_flow);
            }

            switch (_stage)
            {
                case WalkGuideStage.Off:
                    // ⚠ **一度終わったら出し直さない**（Done から戻らない）。導入の段 0 は
                    //    位置合わせのやり直し等で行き来するので、戻れる形にすると
                    //    体験者の目の前で誘導が点滅する。出し直すのは Reset だけ。
                    if (input.wanted) Enter(WalkGuideStage.SpotIn);
                    break;

                case WalkGuideStage.SpotIn:
                    if (!input.wanted) { Enter(WalkGuideStage.Out); break; }
                    _elapsed += dt;
                    if (_arrived) { Enter(WalkGuideStage.Arrive); break; }
                    if (_elapsed >= SpotInSec) Enter(WalkGuideStage.Trail);
                    break;

                case WalkGuideStage.Trail:
                    if (!input.wanted) { Enter(WalkGuideStage.Out); break; }
                    _elapsed += dt;
                    if (_arrived) { Enter(WalkGuideStage.Arrive); break; }
                    if (_elapsed >= TrailSec) Enter(WalkGuideStage.Hold);
                    break;

                case WalkGuideStage.Hold:
                    if (!input.wanted) { Enter(WalkGuideStage.Out); break; }
                    _elapsed += dt;
                    _holdSec += dt;
                    if (_arrived) { Enter(WalkGuideStage.Arrive); break; }
                    // ⚠ 諦めるのはここ 1 か所。畳むだけで、開始判定を差し戻すのは呼び出し側。
                    if (_holdSec >= HoldMaxSec)
                    {
                        _timedOut = true;
                        Enter(WalkGuideStage.Out);
                    }
                    break;

                case WalkGuideStage.Arrive:
                    _elapsed += dt;
                    if (_elapsed >= ArriveSec) Enter(WalkGuideStage.Out);
                    break;

                case WalkGuideStage.Out:
                    _elapsed += dt;
                    if (_elapsed >= OutSec) Enter(WalkGuideStage.Done);
                    break;
            }

            return _stage != before;
        }

        /// <summary>
        /// 円に着いたかを数える。<b>誘導が体験者へ「行け」と言っているあいだだけ</b>数える —
        /// 言う前の滞在を持ち越すと、円の上に置いた HMD で勝手に始まる。
        ///
        /// ⚠ <b>ヒステリシス付きの滞在で判定する</b>（<see cref="StartSpotLogic"/> のように
        /// 「一度外に居た」は要求しない）。誘導は<b>スタッフが A を押した後</b>にしか出ないので、
        /// 人の判断という門は既に通っている。逆に「外に居たこと」を要求すると、
        /// たまたま円の上に立たされた体験者が <see cref="HoldMaxSec"/> のあいだ置き去りになる。
        /// </summary>
        private void UpdateArrival(in WalkGuideInput input, float dt)
        {
            if (_arrived) return;
            if (!Directing || !input.wanted || !input.posValid)
            {
                _inSec = 0f;
                _inside = false;
                return;
            }
            // ⚠ 飛んだフレームは数えない（着脱・再開の 1 フレームで滞在を成立させない）。
            if (dt > MaxContinuousDtSec) { _inSec = 0f; return; }

            float r = input.radiusM > 0.01f ? input.radiusM : ShowStartSpotDef.DefaultRadiusM;
            _inside = _inside ? input.distM <= r + ExitMarginM : input.distM <= r;
            _inSec = _inside ? _inSec + dt : 0f;
            if (_inSec >= ArriveHoldSec) _arrived = true;
        }

        /// <summary>段を移る。<b>着いた量は引き継ぐ</b>（Out で 0 から張り直すと輪が一度ほどける）。</summary>
        private void Enter(WalkGuideStage next)
        {
            _arriveFrom = Weights.arrive;
            _stage = next;
            _elapsed = 0f;
        }

        /// <summary>いまの段から各層へ配る値。</summary>
        public WalkGuideWeights Weights
        {
            get
            {
                switch (_stage)
                {
                    case WalkGuideStage.SpotIn:
                    {
                        float k = Smooth(Clamp01(_elapsed / SpotInSec));
                        return new WalkGuideWeights { spot = k, ring = k, flow = _flow };
                    }

                    case WalkGuideStage.Trail:
                    {
                        float k = Clamp01(_elapsed / TrailSec);
                        return new WalkGuideWeights
                        {
                            spot = 1f, ring = 1f,
                            // ⚠ **濃さは先に決まって、点く数だけが増える。**
                            //    両方を同時に動かすと「2 つのことが起きている」に見える
                            //    （連絡の面の `PanelInkAt` と同じ理屈）。
                            arrow = Smooth(Clamp01(_elapsed / (TrailSec * 0.3f))),
                            reveal = k,
                            flow = _flow,
                        };
                    }

                    case WalkGuideStage.Hold:
                        return new WalkGuideWeights
                        { spot = 1f, ring = 1f, arrow = 1f, reveal = 1f, flow = _flow };

                    case WalkGuideStage.Arrive:
                    {
                        float k = Smooth(Clamp01(_elapsed / ArriveSec));
                        return new WalkGuideWeights
                        {
                            spot = 1f,
                            ring = 1f,
                            // ⚠ **矢印が先に消える。** 着いた後も道筋が残っていると
                            //    「まだ歩け」に見える。輪だけが残って閉じる。
                            arrow = 1f - k,
                            reveal = 1f,
                            flow = _flow,
                            arrive = Lerp(_arriveFrom, 1f, k),
                        };
                    }

                    case WalkGuideStage.Out:
                    {
                        float k = Smooth(Clamp01(_elapsed / OutSec));
                        return new WalkGuideWeights
                        {
                            spot = 1f - k,
                            ring = 1f,
                            arrow = (1f - k) * (_arriveFrom > 0.001f ? 0f : 1f),
                            reveal = 1f,
                            flow = _flow,
                            arrive = _arriveFrom,
                        };
                    }

                    default:
                        return WalkGuideWeights.Hidden;
                }
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>
    /// 誘導の道筋（矢印の起点と、円の位置）を <c>layout</c> から解く純関数
    /// （<see cref="IntroStructureWireLogic"/> と同じ分け方）。
    ///
    /// ⚠⚠ <b>捏造しない。</b> 床も部屋も未著作なら道筋は解けない（<see cref="Path.valid"/> = false）。
    /// 既定の 1.8m 四方から矢印を引くと、<b>実物の床に無い所へ歩かせる</b>ことになる。
    ///
    /// ⚠ <b>卓が置いた円（<c>layout.startSpot</c>）が常に優先。</b> 導出は「まだ置いていない現場でも
    /// 誘導が出る」ための既定であって、人の判断を上書きしてよいという意味ではない。
    /// </summary>
    public static class WalkGuidePath
    {
        /// <summary>壁から歩く道までの距離 (m)。<b>円は 2 本の道が交わる所</b>に置く。</summary>
        public const float LaneOffsetM = 0.42f;

        /// <summary>
        /// 矢印の起点を、床（＝ 歩ける範囲）の縁から<b>どれだけ外側まで伸ばしてよいか</b> (m)。
        ///
        /// ⚠⚠ <b>床の内側で止めない。</b> 体験者は床の外に立っていて、矢印は
        /// 「<b>ここから行く</b>」を示すもの（ユーザー指定・0079）。内側で止めると、
        /// 起点が体験者の足元より先にあって「どこから歩き出すのか」が画に出ない。
        /// 実測でも、床の中だけに収めると円のすぐ手前に山形が団子になった。
        ///
        /// ⚠ 伸ばしすぎない。床の外に何があるかは著作されていない（机・柱・別の展示）。
        /// 導入の接近判定が見ている 1.0m より内側に留める。
        /// </summary>
        public const float StartOutsideM = 0.55f;

        /// <summary>これより短い道筋は矢印にしない (m)。数個の山形が団子になって方向が読めない。</summary>
        public const float MinPathM = 0.35f;

        /// <summary>2 本の壁が「同じ角を共有している」とみなす距離 (m)。</summary>
        public const float CornerTolM = 0.12f;

        /// <summary>2 本の壁が平行すぎて交点が解けないとみなす外積の下限。</summary>
        public const float MinCrossForCorner = 0.2f;

        /// <summary>
        /// 導出した円の半径 (m)。<b>描く輪と判定の円はこれ 1 つ</b>。
        ///
        /// ⚠⚠ <b>0.35 → 0.245（0.7 倍）</b>（2026-08-17・ユーザー赤入れ
        /// 「到達ポイント表示はもう少し小さめにお願い。今の 0.7 倍くらい」）。
        /// <b>見た目だけ縮めない</b> — 判定の円を大きいままにすると、輪の外で導入が始まって
        /// 「そこへ立て」という指示と食い違う（<c>canon/LEDGER.md</c> 0079 の要点そのもの）。
        ///
        /// ⚠ 卓が置いた円（<c>layout.startSpot</c>）はその半径を使う。人の判断を上書きしない。
        /// ⚠ これ以上小さくしない — 立ち位置がシビアになり、着けない体験者が
        /// <see cref="WalkGuideLogic.HoldMaxSec"/> 待つことになる。
        /// </summary>
        public const float SpotRadiusM = 0.245f;

        /// <summary>解けた道筋。</summary>
        public struct Path
        {
            /// <summary>円を出せるか。false なら誘導ごと出さない。</summary>
            public bool valid;

            /// <summary>矢印を出せるか（円は出せるが道筋が短すぎる、はありうる）。</summary>
            public bool hasArrow;

            /// <summary>矢印の起点（course XZ）。<b>東の側</b>。</summary>
            public Vector2 from;

            /// <summary>円の中心（course XZ）。<b>導入の開始判定もこの円で行う</b>。</summary>
            public Vector2 spot;

            /// <summary>円の半径 (m)。<b>描く輪と判定の円は同じ</b>（別にすると指示が嘘になる）。</summary>
            public float radiusM;

            /// <summary>床の高さ（course y）。</summary>
            public float floorY;

            /// <summary>円が卓で著作されたものか（false なら壁の角から導出した）。</summary>
            public bool spotAuthored;
        }

        /// <summary>
        /// 道筋を解く。<b>円 → 矢印</b>の順に決める（行き先が決まらないと矢印は引けない）。
        /// </summary>
        public static Path Solve(ShowLayoutDef? layout, ShowRoomDef? room)
        {
            var path = new Path { radiusM = SpotRadiusM };

            // 床が無ければ何も出さない（捏造しない）。矢印の起点を床の縁から決めるので、
            // 床の実寸はどの経路でも要る。
            if (!IntroStructureWireLogic.TryFloorExtents(layout, room, out float w, out float d))
                return path;
            path.floorY = room != null ? room.floorY : 0f;

            bool hasCorner = TryWallCorner(room, out Vector2 corner,
                                           out Vector2 dirEast, out Vector2 nEast, out Vector2 nOther);

            ShowStartSpotDef? authored = layout?.ResolveStartSpot();
            if (authored != null)
            {
                path.spot = new Vector2(authored.x, authored.z);
                path.radiusM = authored.ResolveRadiusM();
                path.spotAuthored = true;
            }
            else if (hasCorner)
            {
                // 2 本の道（壁からそれぞれ LaneOffsetM 離した線）の交点 ＝ 角に立てる場所。
                path.spot = corner + (nEast + nOther) * LaneOffsetM;
            }
            else
            {
                return path;   // 円の置き場が決まらない
            }
            path.valid = true;

            // 矢印は「東から壁沿いに」。壁の角が解ければその腕の向き、解けなければ真東から。
            Vector2 dir = hasCorner ? dirEast : new Vector2(1f, 0f);
            float len = RayToFloorEdge(path.spot, dir, w, d);
            if (len >= MinPathM)
            {
                path.hasArrow = true;
                path.from = path.spot + dir * len;
            }
            return path;
        }

        /// <summary>
        /// 部屋の壁から L の角を探し、<b>東の側の腕</b>を返す。
        ///
        /// <paramref name="dirEast"/> は角から東の腕の先へ向かう単位ベクトル
        /// （＝ 体験者が歩いてくる向きの逆）、<paramref name="nEast"/> / <paramref name="nOther"/> は
        /// 各腕から<b>床の内側へ</b>向かう単位法線。
        /// </summary>
        public static bool TryWallCorner(ShowRoomDef? room, out Vector2 corner,
                                         out Vector2 dirEast, out Vector2 nEast, out Vector2 nOther)
        {
            corner = Vector2.zero;
            dirEast = new Vector2(1f, 0f);
            nEast = new Vector2(0f, -1f);
            nOther = new Vector2(1f, 0f);
            if (room?.walls == null) return false;

            for (int i = 0; i < room.walls.Length; i++)
            {
                ShowRoomWallDef? a = room.walls[i];
                if (a == null || !a.IsUsable()) continue;
                for (int j = i + 1; j < room.walls.Length; j++)
                {
                    ShowRoomWallDef? b = room.walls[j];
                    if (b == null || !b.IsUsable()) continue;
                    if (!TrySharedEnd(a, b, out Vector2 c, out Vector2 farA, out Vector2 farB)) continue;

                    Vector2 dA = (farA - c).normalized;
                    Vector2 dB = (farB - c).normalized;
                    // ほぼ一直線の 2 枚は「角」ではない（交点も解けない）。
                    if (Mathf.Abs(dA.x * dB.y - dA.y * dB.x) < MinCrossForCorner) continue;

                    corner = c;
                    // ⚠ **東 ＝ course の +x**（`layout.grid` の col0 が西端）。
                    //    先が東にある方の腕へ矢印を置く（ユーザー指定「東から壁沿いに」）。
                    bool aIsEast = farA.x >= farB.x;
                    dirEast = aIsEast ? dA : dB;
                    nEast = InwardNormal(c, aIsEast ? farA : farB);
                    nOther = InwardNormal(c, aIsEast ? farB : farA);
                    return true;
                }
            }
            return false;
        }

        /// <summary>2 枚の壁が端点を共有していれば、その角と、それぞれの反対側の端を返す。</summary>
        private static bool TrySharedEnd(ShowRoomWallDef a, ShowRoomWallDef b,
                                         out Vector2 corner, out Vector2 farA, out Vector2 farB)
        {
            var a1 = new Vector2(a.x1, a.z1);
            var a2 = new Vector2(a.x2, a.z2);
            var b1 = new Vector2(b.x1, b.z1);
            var b2 = new Vector2(b.x2, b.z2);
            float t2 = CornerTolM * CornerTolM;

            if ((a1 - b1).sqrMagnitude <= t2) { corner = a1; farA = a2; farB = b2; return true; }
            if ((a1 - b2).sqrMagnitude <= t2) { corner = a1; farA = a2; farB = b1; return true; }
            if ((a2 - b1).sqrMagnitude <= t2) { corner = a2; farA = a1; farB = b2; return true; }
            if ((a2 - b2).sqrMagnitude <= t2) { corner = a2; farA = a1; farB = b1; return true; }

            corner = Vector2.zero; farA = Vector2.zero; farB = Vector2.zero;
            return false;
        }

        /// <summary>
        /// 壁（角 → 先）から<b>床の内側</b>へ向かう単位法線。
        /// 内側は course 原点（＝ 床の中心）の側とみなす。
        /// </summary>
        public static Vector2 InwardNormal(Vector2 corner, Vector2 far)
        {
            Vector2 dir = (far - corner).normalized;
            var n = new Vector2(-dir.y, dir.x);
            Vector2 mid = (corner + far) * 0.5f;
            // 原点（床の中心）へ向く側を採る。壁の上に原点が乗っている病的な場合は符号を変えない。
            if (Vector2.Dot(n, -mid) < 0f) n = -n;
            return n;
        }

        /// <summary>
        /// <paramref name="from"/> から <paramref name="dir"/> へ進んで、床の縁の
        /// <see cref="StartOutsideM"/> だけ外側にぶつかるまでの距離 (m)。
        /// </summary>
        public static float RayToFloorEdge(Vector2 from, Vector2 dir, float floorW, float floorD)
        {
            float hx = floorW * 0.5f + StartOutsideM;
            float hz = floorD * 0.5f + StartOutsideM;
            float t = float.MaxValue;
            t = Mathf.Min(t, AxisHit(from.x, dir.x, hx));
            t = Mathf.Min(t, AxisHit(from.y, dir.y, hz));
            if (t == float.MaxValue || t < 0f) return 0f;
            return t;
        }

        private static float AxisHit(float p, float v, float half)
        {
            if (Mathf.Abs(v) < 1e-5f) return float.MaxValue;
            float target = v > 0f ? half : -half;
            float t = (target - p) / v;
            return t >= 0f ? t : float.MaxValue;
        }
    }
}
