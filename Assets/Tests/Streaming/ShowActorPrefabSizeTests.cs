#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形プレハブの**実寸**を固定する。
    ///
    /// なぜテストにするか: 実寸は show.json の `heightM` へ合わせる縮尺の分母なので、
    /// ここが狂うと**人形の大きさが黙って変わる**。実測（2026-08-05）で市松人形が
    /// 目標 1.6m に対し 2.27m の巨人になっていた（ボーンが頭頂まで無く実寸を 3 割小さく
    /// 見積もっていた）。直したうえで、Remy 側が巻き添えで変わらないことも押さえる。
    ///
    /// 期待値の根拠:
    ///   Remy       … Mixamo の人体リグ。HeadTop_End まであるのでボーンで測れる
    ///   Ichimatsu  … 実物の市松人形から起こしたモデル。全高 0.40m（tools/doll-model/README.md）
    /// </summary>
    public sealed class ShowActorPrefabSizeTests
    {
        [TestCase("ShowActors/Remy", 3.718f, 0.02f)]
        [TestCase("ShowActors/Ichimatsu", 0.410f, 0.02f)]
        public void 実寸は実物の寸法と一致する(string resource, float expectedM, float toleranceM)
        {
            var prefab = Resources.Load<GameObject>(resource);
            Assert.IsNotNull(prefab, $"Resources/{resource} が読めません");

            GameObject? go = null;
            try
            {
                go = Object.Instantiate(prefab);
                var rig = go.GetComponent<ShowActorRig>();
                Assert.IsNotNull(rig, $"{resource} に ShowActorRig がありません");
                rig!.Prepare();
                Assert.That(rig.MeasuredHeightM, Is.EqualTo(expectedM).Within(toleranceM),
                            $"{resource} の実寸が変わった＝人形の大きさが変わる");
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        [TestCase("ShowActors/Remy")]
        [TestCase("ShowActors/Ichimatsu")]
        public void 腕のボーンが解決できる(string resource)
        {
            var prefab = Resources.Load<GameObject>(resource);
            Assert.IsNotNull(prefab);
            GameObject? go = null;
            try
            {
                go = Object.Instantiate(prefab);
                var rig = go!.GetComponent<ShowActorRig>();
                rig!.Prepare();
                // ここが false だと Drive が即 return して**腕が一切動かない**（黙って静止する）。
                Assert.IsTrue(rig.HasRig, $"{resource} の肩〜手のボーンが揃っていません");
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }
    }
}
