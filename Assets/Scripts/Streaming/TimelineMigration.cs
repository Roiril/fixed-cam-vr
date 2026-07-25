#nullable enable
using System;
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json timeline v2（<c>segments[].cues[]</c> / <c>insert</c>）→ v3（<c>segments[].takes[]</c>）の
    /// **決定的変換**。v3 で書き出す時に v2 キーは出さないが、読む時は必ず変換する
    /// （既存 APK の端末キャッシュ・焼き込み show.json が v2 のままのため）。
    ///
    /// Web 側の同名変換（<c>tools/web-compositor/timeline-model.js</c> の <c>migrateV2Segment</c>）と
    /// **同じ結果**を返すこと。共有 fixture で両側を突き合わせる。
    /// 契約の正本は <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> §6.5。
    ///
    /// 純粋（UnityEngine 非依存）。
    /// </summary>
    public static class TimelineMigration
    {
        /// <summary>
        /// タイムライン全体を v3 相当へ正規化する。既に <c>takes</c> を持つ区間はそのまま、
        /// 持たない区間は v2 キーから変換して <c>takes</c> を埋める。
        /// v2 キー自体は消さない（Web が v3 で書き出せば自然に消える）。
        /// </summary>
        public static void EnsureTakes(ShowTimelineDef? timeline)
        {
            if (timeline?.segments == null) return;
            foreach (ShowTimelineSegmentDef? seg in timeline.segments)
            {
                if (seg == null) continue;
                if (seg.takes != null && seg.takes.Length > 0) continue;
                seg.takes = FromV2(seg);
            }
        }

        /// <summary>
        /// 区間 1 つの v2 キーを takes へ変換する。並びは <c>cues[]</c> の順 → 最後に <c>insert</c>
        /// （v2 は 1 区間 1 insert なので決定的）。
        /// </summary>
        public static ShowTakeDef[] FromV2(ShowTimelineSegmentDef seg)
        {
            var list = new List<ShowTakeDef>();

            if (seg.cues != null)
            {
                for (int i = 0; i < seg.cues.Length; i++)
                {
                    ShowSegmentCueDef? c = seg.cues[i];
                    if (c == null || string.IsNullOrEmpty(c.cueId)) continue;
                    list.Add(FromV2Cue(seg, c, i));
                }
            }

            if (seg.hasInsert && seg.insert != null)
                list.Add(FromV2Insert(seg, seg.insert, list.Count));

            return list.ToArray();
        }

        // v2 cue = 「今映っているものを保ったままオーバーレイを重ね、素材が終わったら戻る」1 カットの演出。
        private static ShowTakeDef FromV2Cue(ShowTimelineSegmentDef seg, ShowSegmentCueDef c, int index)
        {
            ShowCueOverrideDef? ov = c.hasOverride ? c.@override : null;
            return new ShowTakeDef
            {
                id = MakeId(seg, index),
                name = c.cueId ?? "",
                at = TakeSchema.AtEnter,
                offsetSec = c.delaySec,
                // v2 の実挙動は「区間を出た後に別の区間で遅れて誤爆」だったが、それはバグであって仕様ではない。
                // 移行後は離脱の瞬間に発火する（設計 §8.1 に挙動変更として明記）。
                ifMissed = TakeSchema.MissedFireOnExit,
                policy = TakeSchema.PolicyHold,
                once = c.once,
                steps = new[]
                {
                    new ShowStepDef
                    {
                        source = TakeSchema.SourceInherit,
                        camera = -1,
                        cueId = c.cueId ?? "",
                        strength = ov != null ? ov.strength : -1f,
                        fadeInSec = ov != null ? ov.fadeIn : -1f,
                        fadeOutSec = ov != null ? ov.fadeOut : -1f,
                        trimStartSec = ov != null ? ov.trimStart : -1f,
                        trimEndSec = ov != null ? ov.trimEnd : -1f,
                        durKind = TakeSchema.DurUntilClipEnd,
                        durSec = 0f,
                        transition = TakeSchema.TransFade,
                        transitionMs = 0f,
                        post = null,
                        hasPost = false,
                    },
                },
            };
        }

        // v2 insert = 「別カメラを N 秒差し込んで戻る」1 カットの演出。
        private static ShowTakeDef FromV2Insert(ShowTimelineSegmentDef seg, ShowInsertDef ins, int index)
        {
            bool exit = ins.IsExit;
            return new ShowTakeDef
            {
                id = MakeId(seg, index),
                name = string.IsNullOrEmpty(ins.cueId) ? "insert" : ins.cueId,
                at = exit ? TakeSchema.AtExit : TakeSchema.AtEnter,
                offsetSec = exit ? 0f : ins.delaySec,
                ifMissed = TakeSchema.MissedFireOnExit,
                policy = TakeSchema.PolicyHold,
                once = ins.once,
                steps = new[]
                {
                    new ShowStepDef
                    {
                        source = TakeSchema.SourceLive,
                        camera = ins.camera,
                        cueId = ins.cueId ?? "",
                        durKind = TakeSchema.DurSec,
                        durSec = ins.durationSec,
                        transition = TakeSchema.TransDip,
                        transitionMs = 0f,
                        post = ins.hasPost ? ins.post : null,
                        hasPost = ins.hasPost && ins.post != null,
                    },
                },
            };
        }

        /// <summary>id 未指定の演出に与える決定的な既定 id（区間内で一意）。</summary>
        public static string MakeId(ShowTimelineSegmentDef seg, int index)
            => $"L{seg.lap}C{seg.camera}#{index}";
    }
}
