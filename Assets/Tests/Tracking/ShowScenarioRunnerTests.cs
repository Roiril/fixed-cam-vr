#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// ShowScenarioRunner（実機なし検証の正本）の挙動固定 + **golden トレース fixture の生成／照合**。
    ///
    /// fixture:
    ///   Assets/Tests/Fixtures/scenario_walk.json        … 入力（レイアウト + 歩き + タイムライン）
    ///   Assets/Tests/Fixtures/scenario_walk.trace.json  … 期待トレース（この実装が正）
    /// Web の <c>scenario-engine.js</c> が同じ入力から同じトレースを出すことを node 側テストで固定する
    /// （計画 2026-07-25_show-simulator.md §1）。**fixture が無ければ生成し、あれば照合する。**
    /// </summary>
    public sealed class ShowScenarioRunnerTests
    {
        private static string FixtureDir => Path.Combine(Application.dataPath, "Tests/Fixtures");
        private static string TracePath => Path.Combine(FixtureDir, "scenario_walk.trace.json");
        private static string ScenarioPath => Path.Combine(FixtureDir, "scenario_walk.json");
        private static string SpotTracePath => Path.Combine(FixtureDir, "scenario_spot.trace.json");
        private static string SpotScenarioPath => Path.Combine(FixtureDir, "scenario_spot.json");

        // 3 ゾーン（A/B/C）を x 方向に並べ、0.08m ずつ重ねた 1.8m 四方相当のコース。
        private static ZonePickLogic.Box[] Zones() => new[]
        {
            ZonePickLogic.Box.Aabb(-0.6f, 1f, 0f, 0.34f, 2f, 0.9f, 0),
            ZonePickLogic.Box.Aabb(0.0f, 1f, 0f, 0.34f, 2f, 0.9f, 1),
            ZonePickLogic.Box.Aabb(0.6f, 1f, 0f, 0.34f, 2f, 0.9f, 2),
        };

        private static TakeRunnerLogic.Def Take(int lap, int cam, bool onExit, float offset,
            bool skipWhenMissed, params float[] steps) => new()
        {
            lap = lap, camera = cam, onExit = onExit, offsetSec = offset,
            skipWhenMissed = skipWhenMissed, once = true, maxDurationSec = 0f,
            yieldOnZoneChange = false, stepDurSec = steps,
        };

        private static ShowScenarioRunner.Config MakeConfig()
        {
            // 演出 2 本:
            //   t_B_late  … 2 周目 B に「進入 +20s」（速く歩くと取り逃す → fireOnExit で離脱時に出る）
            //   t_C_exit  … 1 周目 C の離脱時に カメラ0 を 1.0s 差し込む
            var takes = new[]
            {
                Take(2, 1, onExit: false, offset: 20f, skipWhenMissed: false, 1.0f, 0.6f),
                Take(1, 2, onExit: true, offset: 0f, skipWhenMissed: false, 1.0f),
            };
            return new ShowScenarioRunner.Config
            {
                Zones = Zones(),
                HysteresisShrink = 0.04f,
                KeepLastWhenOutside = true,
                HeadY = 1f,
                DwellSec = 0.5f,
                CooldownSec = 0.5f,
                CourseOrder = new[] { 0, 1, 2 },
                Takes = takes,
                TakeIds = new[] { "t_B_late", "t_C_exit" },
                StepCameras = new[] { new[] { -1, -1 }, new[] { 0 } },
                StartCamera = 0,
                TickMs = ShowScenarioRunner.DefaultTickMs,
            };
        }

        // A → B → C → A → B → C と 2 周歩く（各ゾーンに 2 秒ずつ滞在）。
        private static ShowScenarioRunner.Sample[] Walk()
        {
            var list = new List<ShowScenarioRunner.Sample>();
            float[] xs = { -0.6f, 0.0f, 0.6f, -0.6f, 0.0f, 0.6f };
            int t = 0;
            list.Add(new ShowScenarioRunner.Sample { tMs = 0, x = xs[0], z = 0f });
            foreach (float x in xs)
            {
                t += 800;  list.Add(new ShowScenarioRunner.Sample { tMs = t, x = x, z = 0f }); // 移動
                t += 2000; list.Add(new ShowScenarioRunner.Sample { tMs = t, x = x, z = 0f }); // 滞在
            }
            return list.ToArray();
        }

        // ---- セマンティクスの単体固定 ----

        [Test]
        public void Walk_ProducesLapAndSegmentProgression()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), Walk());

            int[] zones = tr.Where(e => e.kind == "zone").Select(e => e.a).ToArray();
            Assert.That(zones, Is.EqualTo(new[] { 1, 2, 0, 1, 2 }),
                "A から歩き出すので確定は B,C,A,B,C の順（開始ゾーン A は確定イベントを出さない）");

            int[] laps = tr.Where(e => e.kind == "lap").Select(e => e.a).ToArray();
            Assert.That(laps, Is.EqualTo(new[] { 2 }), "C→A の復帰で 2 周目になる（1 回だけ）");
        }

        [Test]
        public void FireOnExit_LateTakeStillFires_WhenWalkedThroughFast()
        {
            // 2 周目 B の滞在は 2 秒。offset は 20 秒なので通常なら出ない。
            // ifMissed=fireOnExit により離脱の瞬間に発火するのが期待挙動（設計の最重要リスク対策）。
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), Walk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_B_late"), Is.True,
                "20 秒待てない歩き方でも山場が出る（fireOnExit）");
        }

        [Test]
        public void SkipPolicy_DropsTheTake_WhenWalkedThroughFast()
        {
            ShowScenarioRunner.Config cfg = MakeConfig();
            cfg.Takes[0].skipWhenMissed = true;
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(cfg, Walk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_B_late"), Is.False,
                "skip なら通り過ぎたら出ない");
        }

        [Test]
        public void ExitTake_FiresOnLeavingSegment_AndEndsOnItsOwn()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), Walk());
            ShowScenarioRunner.Event begin = tr.First(e => e.kind == "take" && e.id == "t_C_exit");
            ShowScenarioRunner.Event end = tr.First(e => e.kind == "end" && e.id == "t_C_exit");
            Assert.That(end.t - begin.t, Is.EqualTo(1000).Within(ShowScenarioRunner.DefaultTickMs),
                "尺 1.0s ぶん表示してから終わる");
            Assert.That(end.flag, Is.False, "watchdog ではなく尺どおりの終了");
        }

        [Test]
        public void EveryTakeThatStarts_AlsoEnds()
        {
            // 不変条件 2「演出は必ず終わる」をトレース全体に対して検査する。
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), Walk());
            var started = tr.Where(e => e.kind == "take").Select(e => e.id).ToList();
            var ended = tr.Where(e => e.kind == "end").Select(e => e.id).ToList();
            CollectionAssert.AreEquivalent(started, ended, "始まった演出は必ず終わる");
        }

        [Test]
        public void StandingStill_ProducesNoEvents()
        {
            var still = new[]
            {
                new ShowScenarioRunner.Sample { tMs = 0, x = -0.6f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 5000, x = -0.6f, z = 0f },
            };
            Assert.That(ShowScenarioRunner.Run(MakeConfig(), still), Is.Empty,
                "動かなければ何も起きない（開始ゾーンに居るだけ）");
        }

        [Test]
        public void BriefStepIntoNeighbour_DoesNotCommit()
        {
            // dwell 0.5s 未満の踏み込みは確定しない（境界のうろつきで切り替わらない）。
            var wobble = new[]
            {
                new ShowScenarioRunner.Sample { tMs = 0, x = -0.6f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 400, x = 0.0f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 700, x = -0.6f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 2000, x = -0.6f, z = 0f },
            };
            Assert.That(ShowScenarioRunner.Run(MakeConfig(), wobble).Any(e => e.kind == "zone"), Is.False);
        }

        // ---- 位置トリガー（at=spot・2026-07-27）----
        //   契約: .claude/plans/2026-07-27_position-trigger.md

        private static TakeRunnerLogic.Def SpotTake(int lap, int cam, int slot, float holdSec,
            bool skipWhenMissed, params float[] steps) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = skipWhenMissed, once = true, maxDurationSec = 0f,
            yieldOnZoneChange = false, stepDurSec = steps,
            onSpot = true, spotIndex = slot, holdSec = holdSec,
        };

        // 位置トリガー版のシナリオ。円は 2 つ:
        //   slot0 spot_stand … C ゾーンの中（0.6, +0.5）— 立ち止まりを要求する演出に使う
        //   slot1 spot_pass  … B ゾーンの中（0.0, -0.5）— 踏んだ瞬間に出す演出に使う
        private static ShowScenarioRunner.Config MakeSpotConfig()
        {
            var takes = new[]
            {
                // 1 周目 C: 円の中に 0.5s 立ち止まったら出す（通り抜けでは出ない）
                SpotTake(1, 2, 0, 0.5f, skipWhenMissed: true, 1.0f),
                // 1 周目 C: 3s 立ち止まりを要求 → この歩き方では満たさない = 出ない
                SpotTake(1, 2, 0, 3.0f, skipWhenMissed: true, 0.5f),
                // 1 周目 B: 踏んだ瞬間（hold 0）に出す
                SpotTake(1, 1, 1, 0f, skipWhenMissed: true, 0.6f),
                // 2 周目 C: 円を踏まないまま離脱 → fireOnExit で離脱の瞬間に出る
                SpotTake(2, 2, 0, 0f, skipWhenMissed: false, 0.4f),
            };
            return new ShowScenarioRunner.Config
            {
                Zones = Zones(),
                HysteresisShrink = 0.04f,
                KeepLastWhenOutside = true,
                HeadY = 1f,
                DwellSec = 0.5f,
                CooldownSec = 0.5f,
                CourseOrder = new[] { 0, 1, 2 },
                Takes = takes,
                TakeIds = new[] { "t_C_stand", "t_C_long", "t_B_pass", "t_C_miss" },
                Spots = new[]
                {
                    SpotTriggerLogic.Spot.At(0.6f, 0.5f, 0.25f),
                    SpotTriggerLogic.Spot.At(0.0f, -0.5f, 0.25f),
                },
                StepCameras = new[] { new[] { -1 }, new[] { -1 }, new[] { -1 }, new[] { -1 } },
                StartCamera = 0,
                TickMs = ShowScenarioRunner.DefaultTickMs,
            };
        }

        // A →（円を踏んで）B → C の円で立ち止まる → A（2 周目）→ B → C（円を踏まない）→ A。
        private static ShowScenarioRunner.Sample[] SpotWalk()
        {
            var wp = new[]
            {
                new Vector2(-0.6f, 0.0f),   // A 開始
                new Vector2(0.0f, -0.5f),   // B・spot_pass の上
                new Vector2(0.6f, 0.5f),    // C・spot_stand の上（立ち止まる）
                new Vector2(-0.6f, 0.5f),   // A へ（2 周目）
                new Vector2(0.0f, 0.5f),    // B（spot_pass は踏まない）
                new Vector2(0.6f, -0.5f),   // C（spot_stand は踏まない）
                new Vector2(-0.6f, -0.5f),  // A へ（3 周目・ここで 2 周目 C の離脱）
            };
            var list = new List<ShowScenarioRunner.Sample>
            {
                new() { tMs = 0, x = wp[0].x, z = wp[0].y },
            };
            int t = 0;
            foreach (Vector2 p in wp)
            {
                t += 800;  list.Add(new ShowScenarioRunner.Sample { tMs = t, x = p.x, z = p.y }); // 移動
                t += 2000; list.Add(new ShowScenarioRunner.Sample { tMs = t, x = p.x, z = p.y }); // 滞在
            }
            return list.ToArray();
        }

        [Test]
        public void Spot_FiresWhenViewerStandsInTheCircle_AndNotWhenHoldIsTooLong()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeSpotConfig(), SpotWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_C_stand"), Is.True,
                "0.5s 立ち止まったので出る");
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_C_long"), Is.False,
                "3s の立ち止まりは満たさないので出ない（skip）");
        }

        [Test]
        public void Spot_FiresImmediatelyWhenSteppedOn_WhenHoldIsZero()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeSpotConfig(), SpotWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_B_pass"), Is.True);
        }

        [Test]
        public void Spot_NotVisited_FiresAtExit_WhenFireOnExit()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeSpotConfig(), SpotWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_C_miss"), Is.True,
                "2 周目は円を踏まなかったが、離脱の瞬間に出る（ifMissed=fireOnExit）");
        }

        [Test]
        public void Spot_WithoutSpots_NothingFires()
        {
            // 円が 1 つも無い（layout.spots 未著作）show.json では位置トリガーは沈黙する。
            ShowScenarioRunner.Config cfg = MakeSpotConfig();
            cfg.Spots = System.Array.Empty<SpotTriggerLogic.Spot>();
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(cfg, SpotWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id != "t_C_miss"), Is.False,
                "円を参照する演出は出ない（fireOnExit の t_C_miss だけは離脱時に出る）");
        }

        [Test]
        public void Spot_EveryTakeThatStarts_AlsoEnds()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeSpotConfig(), SpotWalk());
            var started = tr.Where(e => e.kind == "take").Select(e => e.id).ToList();
            var ended = tr.Where(e => e.kind == "end").Select(e => e.id).ToList();
            CollectionAssert.AreEquivalent(started, ended, "位置トリガーでも必ず終わる（不変条件 2）");
        }

        // ---- golden fixture（Web ミラーとの照合用） ----

        [Test]
        public void GoldenTrace_MatchesFixture_OrIsGenerated()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), Walk());
            string json = ShowScenarioRunner.ToJson(tr);

            if (!File.Exists(TracePath))
            {
                Directory.CreateDirectory(FixtureDir);
                File.WriteAllText(TracePath, json);
                File.WriteAllText(ScenarioPath, ScenarioJson(MakeConfig(), Walk()));
                Assert.Pass($"golden fixture を生成した: {TracePath}（次回から照合される）");
            }

            string expected = File.ReadAllText(TracePath).Replace("\r\n", "\n");
            Assert.That(json.Replace("\r\n", "\n"), Is.EqualTo(expected),
                "トレースが golden と違う。セマンティクスを意図して変えたなら fixture を消して再生成し、"
                + "Web 側 scenario-engine.js のテストも通ることを確認すること");
        }

        [Test]
        public void GoldenSpotTrace_MatchesFixture_OrIsGenerated()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeSpotConfig(), SpotWalk());
            string json = ShowScenarioRunner.ToJson(tr);

            if (!File.Exists(SpotTracePath))
            {
                Directory.CreateDirectory(FixtureDir);
                File.WriteAllText(SpotTracePath, json);
                File.WriteAllText(SpotScenarioPath, ScenarioJson(MakeSpotConfig(), SpotWalk()));
                Assert.Pass($"位置トリガーの golden fixture を生成した: {SpotTracePath}");
            }

            string expected = File.ReadAllText(SpotTracePath).Replace("\r\n", "\n");
            Assert.That(json.Replace("\r\n", "\n"), Is.EqualTo(expected),
                "位置トリガーのトレースが golden と違う（Web 側 scenario-engine.js のテストも通ること）");
        }

        // Web 側が同じ入力を組めるよう、シナリオを JSON で書き出す（人間が読める形）。
        private static string ScenarioJson(ShowScenarioRunner.Config c, ShowScenarioRunner.Sample[] w)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"tickMs\": ").Append(c.TickMs)
              .Append(",\n  \"dwellSec\": ").Append(c.DwellSec)
              .Append(",\n  \"cooldownSec\": ").Append(c.CooldownSec)
              .Append(",\n  \"hysteresisShrink\": ").Append(c.HysteresisShrink)
              .Append(",\n  \"headY\": ").Append(c.HeadY)
              .Append(",\n  \"startCamera\": ").Append(c.StartCamera)
              .Append(",\n  \"courseOrder\": [").Append(string.Join(",", c.CourseOrder)).Append("]");

            sb.Append(",\n  \"zones\": [\n");
            for (int i = 0; i < c.Zones.Length; i++)
            {
                ZonePickLogic.Box b = c.Zones[i];
                sb.Append("    {\"cx\":").Append(F(b.CenterX)).Append(",\"cy\":").Append(F(b.CenterY))
                  .Append(",\"cz\":").Append(F(b.CenterZ)).Append(",\"hx\":").Append(F(b.HalfX))
                  .Append(",\"hy\":").Append(F(b.HalfY)).Append(",\"hz\":").Append(F(b.HalfZ))
                  .Append(",\"yawDeg\":0,\"camera\":").Append(b.CameraIndex)
                  .Append(",\"priority\":").Append(b.Priority).Append('}')
                  .Append(i < c.Zones.Length - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]");

            if (c.Spots.Length > 0)
            {
                sb.Append(",\n  \"spots\": [\n");
                for (int i = 0; i < c.Spots.Length; i++)
                {
                    SpotTriggerLogic.Spot s = c.Spots[i];
                    sb.Append("    {\"x\":").Append(F(s.x)).Append(",\"z\":").Append(F(s.z))
                      .Append(",\"rM\":").Append(F(s.rM))
                      .Append(",\"defined\":").Append(s.defined ? "true" : "false").Append('}')
                      .Append(i < c.Spots.Length - 1 ? ",\n" : "\n");
                }
                sb.Append("  ]");
            }

            sb.Append(",\n  \"takes\": [\n");
            for (int i = 0; i < c.Takes.Length; i++)
            {
                TakeRunnerLogic.Def d = c.Takes[i];
                sb.Append("    {\"id\":\"").Append(c.TakeIds[i]).Append("\",\"lap\":").Append(d.lap)
                  .Append(",\"camera\":").Append(d.camera)
                  .Append(",\"onExit\":").Append(d.onExit ? "true" : "false")
                  .Append(",\"offsetSec\":").Append(F(d.offsetSec))
                  .Append(",\"skipWhenMissed\":").Append(d.skipWhenMissed ? "true" : "false")
                  .Append(",\"once\":").Append(d.once ? "true" : "false")
                  .Append(",\"maxDurationSec\":").Append(F(d.maxDurationSec))
                  .Append(",\"yieldOnZoneChange\":").Append(d.yieldOnZoneChange ? "true" : "false")
                  .Append(",\"onSpot\":").Append(d.onSpot ? "true" : "false")
                  .Append(",\"spotIndex\":").Append(d.spotIndex)
                  .Append(",\"holdSec\":").Append(F(d.holdSec))
                  .Append(",\"stepDurSec\":[").Append(string.Join(",", d.stepDurSec.Select(F)))
                  .Append("],\"stepCameras\":[").Append(string.Join(",", c.StepCameras![i]))
                  .Append("]}").Append(i < c.Takes.Length - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]");

            sb.Append(",\n  \"samples\": [\n");
            for (int i = 0; i < w.Length; i++)
                sb.Append("    {\"tMs\":").Append(w[i].tMs).Append(",\"x\":").Append(F(w[i].x))
                  .Append(",\"z\":").Append(F(w[i].z)).Append('}')
                  .Append(i < w.Length - 1 ? ",\n" : "\n");
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }
}
