#nullable enable
using System;
using System.Collections.Generic;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 「<c>Tools/FixedCamVr/Diagnostics/Preview Show Composite</c>」の**決め事**だけを Unity から切り離した純ロジック。
    ///
    /// レンダは Editor 側（<c>ShowCompositePreview</c>）が本番と同じ機構でやる。ここに置くのは
    /// 「どのカットを撮るか / どのカメラで構えるか / どのプレートを当てるか / どの post を掛けるか /
    /// どの名前で保存するか」— **PNG を見ても正しさを確認できない部分**で、これらは目で確かめる手段が無い
    /// （撮れた絵が 1 枚足りないことに人は気づけない）。だからテストで固定する。
    ///
    /// ⚠ ここの規則は本番（<see cref="ShowCgLayer"/> / <c>TakeRunner</c> / <c>ShowControlClient</c>）の
    ///    振る舞いの**写し**。プレビューが本番と違う判断をしたら、それは証拠として無価値になる。
    ///    本番側を変えたら必ずここも変えること（対応関係は各メソッドのコメントに書いてある）。
    /// </summary>
    public static class ShowCompositePreviewPlan
    {
        /// <summary>プレートとして受け付ける拡張子（卓の保存物は jpg、手置きの png も許す）。</summary>
        private static readonly string[] PlateExtensions = { ".jpg", ".jpeg", ".png" };

        /// <summary>
        /// 撮影対象の 1 カット。<see cref="camera"/> は「この映像を撮った実カメラ」＝人形を構える視点で、
        /// 区間のカメラ（<see cref="segmentCamera"/>）とは別物になりうる。
        /// </summary>
        public sealed class Shot
        {
            public int lap;
            public int segmentCamera;
            public string takeId = "";
            public int takeIndex;
            public int stepIndex;
            public int camera;
            public ShowStepDef step = new ShowStepDef();
        }

        /// <summary>
        /// タイムラインから「CG 人形が出るカット」だけを拾う。
        ///
        /// **CG が空のカットは撮らない** — このツールの目的は人形と映像の像空間の一致を見ることなので、
        /// 人形の居ない絵を混ぜると出力が薄まって「どれを見ればいいか」が分からなくなる。
        /// 順序は show.json の並び順そのまま（区間 → 演出 → カット）。並べ替えると差分比較が効かなくなる。
        /// </summary>
        public static List<Shot> CollectShots(ShowTimelineDef? timeline)
        {
            var shots = new List<Shot>();
            if (timeline?.segments == null) return shots;

            foreach (ShowTimelineSegmentDef? seg in timeline.segments)
            {
                if (seg?.takes == null) continue;
                for (int ti = 0; ti < seg.takes.Length; ti++)
                {
                    ShowTakeDef? take = seg.takes[ti];
                    if (take?.steps == null) continue;
                    for (int si = 0; si < take.steps.Length; si++)
                    {
                        ShowStepDef? step = take.steps[si];
                        if (step == null || !step.HasCg) continue;
                        shots.Add(new Shot
                        {
                            lap = seg.lap,
                            segmentCamera = seg.camera,
                            takeId = ResolveTakeId(take, seg, ti),
                            takeIndex = ti,
                            stepIndex = si,
                            camera = ResolveCamera(step, seg.camera),
                            step = step,
                        });
                    }
                }
            }
            return shots;
        }

        /// <summary>
        /// 人形を構える実カメラ。<c>TakeRunner</c> が <see cref="ShowCgLayer.Apply"/> へ渡す式の写しで、
        /// <c>step.camera</c> 未指定（-1）なら「いま映しているゾーンのカメラ」＝区間のカメラへ落ちる。
        ///
        /// **素材カット（clip / still）でも同じ**。人形は「その構図の中に立つ」ものなので、
        /// 映しているのが録画でも静止画でも、姿勢を決めるのは元の実カメラになる。
        /// </summary>
        public static int ResolveCamera(ShowStepDef step, int segmentCamera)
            => step.camera >= 0 ? step.camera : segmentCamera;

        /// <summary>
        /// 演出 id。空なら本番と同じ既定 id（<see cref="TimelineMigration.MakeId"/>）を使う。
        /// 卓のリボンに出ている名前と PNG のファイル名を一致させるのが目的なので、自前で採番しない。
        /// </summary>
        public static string ResolveTakeId(ShowTakeDef take, ShowTimelineSegmentDef seg, int takeIndex)
            => string.IsNullOrEmpty(take.id) ? TimelineMigration.MakeId(seg, takeIndex) : take.id;

        /// <summary>
        /// カット 1 枚の出力ファイル名。<c>周_カメラ_演出_カット</c> の順にしてあるので、
        /// ファイル名の辞書順 ≒ 体験の時間順になる（差分を見るとき人が並べ替えなくて済む）。
        /// </summary>
        public static string ShotFileName(Shot shot)
            => $"{shot.lap}_{shot.camera}_{SanitizeToken(shot.takeId)}_{shot.stepIndex}.png";

        /// <summary>較正確認（プレート + ワイヤーのみ）の出力ファイル名。</summary>
        public static string CalibCheckFileName(int camera) => $"calibcheck_{camera}.png";

        /// <summary>
        /// ファイル名に使えない文字を潰す。演出 id の既定は <c>L3C2#0</c> のように <c>#</c> を含み、
        /// そのままだと URL やシェルで扱いに困る（Windows のファイル名としては通ってしまうので気づきにくい）。
        /// </summary>
        public static string SanitizeToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return "noid";
            var sb = new System.Text.StringBuilder(token!.Length);
            foreach (char c in token)
                sb.Append((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || c == '-' || c == '_' ? c : '-');
            return sb.ToString();
        }

        /// <summary>
        /// カメラ id に対応する実写プレートを選ぶ。
        ///
        /// 卓（capture-server.py）の保存名が <c>cam&lt;ID&gt;_&lt;YYYYmmdd&gt;_&lt;HHMMSS&gt;_&lt;ms&gt;.&lt;ext&gt;</c> なので、
        /// **<c>cam&lt;ID&gt;_</c> で始まるものだけ**を候補にする（<c>cap_*</c> はカメラが分からないので使わない
        /// — 別カメラの絵を当てて「ずれている」と誤診するのが一番まずい）。
        /// 名前の時刻部分は固定幅なので、**辞書順の最大 = 最新**。いちばん新しい 1 枚を返す。
        /// </summary>
        /// <param name="fileNames">ディレクトリ内のファイル名（パスではなく basename）。</param>
        /// <param name="cameraId">show.json の <c>cameras[i].id</c>（A / B / C / D）。</param>
        public static string? PickPlate(IEnumerable<string>? fileNames, string? cameraId)
        {
            if (fileNames == null || string.IsNullOrEmpty(cameraId)) return null;
            string prefix = "cam" + cameraId + "_";
            string? best = null;
            foreach (string? name in fileNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!HasPlateExtension(name)) continue;
                if (best == null || string.CompareOrdinal(name, best) > 0) best = name;
            }
            return best;
        }

        private static bool HasPlateExtension(string name)
        {
            foreach (string ext in PlateExtensions)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// カット表示中に映像へ掛かる post を解決する。
        ///
        /// **区間 post は入らない**のが要点。本番の <c>ShowControlClient.ApplyPostForActive</c> は
        /// 「インサート層（＝カットの post）」と「区間層」を**排他**にしていて、演出が走っているあいだは
        /// カット層が区間層を素通りさせずに解決する。ここを取り違えると、プレビューだけ区間の色が乗って
        /// 実機と違う絵が出る（しかも「それらしく」見えるので気づけない）。
        /// 段階（上ほど優先）: カット post → カメラ個別 post → global post。
        /// </summary>
        public static PostParams ResolvePost(ShowStepDef? step, PostParams? cameraPost, PostParams? globalPost)
        {
            PostParams basePost = cameraPost ?? globalPost ?? new PostParams();
            if (step != null && step.hasPost && step.post != null) return step.post;
            return basePost;
        }
    }
}
