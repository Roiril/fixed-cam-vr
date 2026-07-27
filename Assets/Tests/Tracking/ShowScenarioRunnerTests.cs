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
        private static string LineTracePath => Path.Combine(FixtureDir, "scenario_line.trace.json");
        private static string LineScenarioPath => Path.Combine(FixtureDir, "scenario_line.json");

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
        public void StandingStill_ProducesOnlyTheStartSegmentSeed()
        {
            var still = new[]
            {
                new ShowScenarioRunner.Sample { tMs = 0, x = -0.6f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 5000, x = -0.6f, z = 0f },
            };
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeConfig(), still);
            // 起動時のシード（実機の LapCounter.SeedCurrentZone 相当）で「1 周目 / スタートカメラ」に
            // 居ることだけは記録される。動かない限りそれ以外は起きない。
            Assert.That(tr.Count, Is.EqualTo(1), "シードの seg 1 件のみ");
            Assert.That(tr[0].kind, Is.EqualTo("seg"));
            Assert.That(tr[0].t, Is.EqualTo(0));
            Assert.That(tr[0].a, Is.EqualTo(1), "1 周目");
            Assert.That(tr[0].b, Is.EqualTo(0), "スタートカメラ");
        }

        [Test]
        public void StartSegmentTake_IsArmed_WithoutLeavingAndComingBack()
        {
            // 実害の再現（2026-07-27）: 1 周目スタート領域の演出が、シードが無いと永久に武装されず
            // 「実機では出るのに卓だけ沈黙する」。スタート区間 (1, カメラ0) の enter 演出が出ることを固定する。
            ShowScenarioRunner.Config cfg = MakeConfig();
            cfg.Takes = new[] { Take(1, 0, onExit: false, offset: 0.5f, skipWhenMissed: true, 1.0f) };
            cfg.TakeIds = new[] { "t_start" };
            cfg.StepCameras = new[] { new[] { -1 } };

            var still = new[]
            {
                new ShowScenarioRunner.Sample { tMs = 0, x = -0.6f, z = 0f },
                new ShowScenarioRunner.Sample { tMs = 3000, x = -0.6f, z = 0f },
            };
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(cfg, still);
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_start"), Is.True,
                "スタート領域に居るだけで 1 周目の演出が発火する（一度出て戻る必要はない）");
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

        // ---- 通過ライン（at=line・2026-07-27）----
        //   契約: .claude/plans/2026-07-27_position-trigger.md

        private static TakeRunnerLogic.Def LineTake(int lap, int cam, int slot,
            bool skipWhenMissed, params float[] steps) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = skipWhenMissed, once = true, maxDurationSec = 0f,
            yieldOnZoneChange = false, stepDurSec = steps,
            onLine = true, lineIndex = slot,
        };

        // 通過ライン版のシナリオ。ラインは 2 本（どちらもゾーンの内側を x 方向に横切る形で置く）:
        //   slot0 line_C  … C ゾーン（x=0.6 付近）を南北に横切る線。担当カメラ 2
        //   slot1 line_B  … B ゾーン（x=0.0 付近）を南北に横切る線。担当カメラ 1
        private static ShowScenarioRunner.Config MakeLineConfig()
        {
            var takes = new[]
            {
                // 1 周目 C: その区間で line_C を通過したら出す
                LineTake(1, 2, 0, skipWhenMissed: true, 1.0f),
                // 1 周目 B: line_B を通過したら出す
                LineTake(1, 1, 1, skipWhenMissed: true, 0.6f),
                // 1 周目 B に **C のライン**を指した演出（著作ミス）→ 踏んでも出ない
                LineTake(1, 1, 0, skipWhenMissed: true, 0.6f),
                // 2 周目 C: 通らないまま離脱 → fireOnExit で離脱の瞬間に出る
                LineTake(2, 2, 0, skipWhenMissed: false, 0.4f),
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
                TakeIds = new[] { "t_C_line", "t_B_line", "t_B_wrongline", "t_C_miss" },
                Lines = new[]
                {
                    // C ゾーンの中に南北の線（x=0.6, z=-0.4..0.4）。担当カメラ 2。
                    LineCrossLogic.Line.Between(0.6f, -0.4f, 0.6f, 0.4f, 0, 2),
                    // B ゾーンの中に南北の線（x=0.0, z=-0.4..0.4）。担当カメラ 1。
                    LineCrossLogic.Line.Between(0.0f, -0.4f, 0.0f, 0.4f, 0, 1),
                },
                StepCameras = new[] { new[] { -1 }, new[] { -1 }, new[] { -1 }, new[] { -1 } },
                StartCamera = 0,
                TickMs = ShowScenarioRunner.DefaultTickMs,
            };
        }

        // A →（B の線を通過して）B →（C の線を通過して）C → A（2 周目）→ B → C → A。
        // 2 周目は C ゾーンの東寄り（線の向こう側）へ直接入らず、線を踏まないまま離脱する。
        private static ShowScenarioRunner.Sample[] LineWalk()
        {
            var wp = new[]
            {
                new Vector2(-0.6f, 0.0f),   // A 開始
                new Vector2(0.2f, 0.0f),    // B（x=0 の線を東へ通過）
                new Vector2(0.8f, 0.0f),    // C（x=0.6 の線を東へ通過）
                new Vector2(-0.6f, 0.7f),   // A へ（2 周目・線の端の外＝z=0.7 を回って戻る）
                new Vector2(0.0f, 0.7f),    // B（線の端の外なので踏まない）
                new Vector2(0.8f, 0.7f),    // C（同じく踏まない）
                new Vector2(-0.6f, 0.7f),   // A へ（3 周目・ここで 2 周目 C の離脱）
            };
            var list = new List<ShowScenarioRunner.Sample>
            {
                new() { tMs = 0, x = wp[0].x, z = wp[0].y },
            };
            int t = 0;
            foreach (Vector2 pt in wp)
            {
                t += 800;  list.Add(new ShowScenarioRunner.Sample { tMs = t, x = pt.x, z = pt.y }); // 移動
                t += 2000; list.Add(new ShowScenarioRunner.Sample { tMs = t, x = pt.x, z = pt.y }); // 滞在
            }
            return list.ToArray();
        }

        [Test]
        public void Line_FiresWhenCrossedInsideItsOwnSegment()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeLineConfig(), LineWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_B_line"), Is.True, "B の線を通過して出る");
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_C_line"), Is.True, "C の線を通過して出る");
        }

        [Test]
        public void Line_OfAnotherCamera_DoesNotFire_EvenWhenSteppedOn()
        {
            // ユーザー要求の核: 手違いで別領域のラインを指した演出は、そのラインを踏んでも出ない。
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeLineConfig(), LineWalk());
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_B_wrongline"), Is.False);
        }

        [Test]
        public void Line_WalkingAroundTheEnd_DoesNotFire()
        {
            // 2 周目は線の端の外（z=0.7）を通るので横断していない → fireOnExit の 1 本だけが離脱時に出る。
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeLineConfig(), LineWalk());
            var ids = tr.Where(e => e.kind == "take").Select(e => e.id).ToList();
            Assert.That(ids.Count(x => x == "t_C_line"), Is.EqualTo(1), "1 周目の 1 回だけ");
            Assert.That(tr.Any(e => e.kind == "take" && e.id == "t_C_miss"), Is.True,
                "2 周目は踏まなかったので離脱の瞬間に出る（ifMissed=fireOnExit）");
        }

        [Test]
        public void Line_WithoutLines_NothingButFireOnExitHappens()
        {
            // ラインが 1 本も無い（layout.lines 未著作）show.json ではライントリガーは沈黙する。
            ShowScenarioRunner.Config cfg = MakeLineConfig();
            cfg.Lines = System.Array.Empty<LineCrossLogic.Line>();
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(cfg, LineWalk());
            Assert.That(tr.Where(e => e.kind == "take").Select(e => e.id), Is.EqualTo(new[] { "t_C_miss" }));
        }

        [Test]
        public void Line_EveryTakeThatStarts_AlsoEnds()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeLineConfig(), LineWalk());
            var started = tr.Where(e => e.kind == "take").Select(e => e.id).ToList();
            var ended = tr.Where(e => e.kind == "end").Select(e => e.id).ToList();
            CollectionAssert.AreEquivalent(started, ended, "ライントリガーでも必ず終わる（不変条件 2）");
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
        public void GoldenLineTrace_MatchesFixture_OrIsGenerated()
        {
            List<ShowScenarioRunner.Event> tr = ShowScenarioRunner.Run(MakeLineConfig(), LineWalk());
            string json = ShowScenarioRunner.ToJson(tr);

            if (!File.Exists(LineTracePath))
            {
                Directory.CreateDirectory(FixtureDir);
                File.WriteAllText(LineTracePath, json);
                File.WriteAllText(LineScenarioPath, ScenarioJson(MakeLineConfig(), LineWalk()));
                Assert.Pass($"通過ラインの golden fixture を生成した: {LineTracePath}");
            }

            string expected = File.ReadAllText(LineTracePath).Replace("\r\n", "\n");
            Assert.That(json.Replace("\r\n", "\n"), Is.EqualTo(expected),
                "通過ラインのトレースが golden と違う（Web 側 scenario-engine.js のテストも通ること）");
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

            if (c.Lines.Length > 0)
            {
                sb.Append(",\n  \"lines\": [\n");
                for (int i = 0; i < c.Lines.Length; i++)
                {
                    LineCrossLogic.Line l = c.Lines[i];
                    sb.Append("    {\"x1\":").Append(F(l.ax)).Append(",\"z1\":").Append(F(l.az))
                      .Append(",\"x2\":").Append(F(l.bx)).Append(",\"z2\":").Append(F(l.bz))
                      .Append(",\"dir\":").Append(l.dir)
                      .Append(",\"camera\":").Append(l.camera)
                      .Append(",\"defined\":").Append(l.defined ? "true" : "false").Append('}')
                      .Append(i < c.Lines.Length - 1 ? ",\n" : "\n");
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
                  .Append(",\"onLine\":").Append(d.onLine ? "true" : "false")
                  .Append(",\"lineIndex\":").Append(d.lineIndex)
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
