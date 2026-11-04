#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>体験 1 回ぶんの状態を持つ部品が、体験者の交代で必ず落ちるか</b>をソースから確かめる。
    ///
    /// 体験者が替わるときのリセットを書き忘れても、1 人目の体験は正常に動く。壊れるのは 2 人目からで、
    /// 展示の当日まで気づけない。この型の不具合が 4 回起きた（<c>memory/visitor_sound_reset.md</c> ほか）。
    ///
    /// 下の表が「誰が・どこから落とすか」の正本。実行時コード（Editor を除く）にある
    /// <c>ResetRun</c> / <c>ResetRunLocal</c> / <c>ResetRunKeepEpoch</c> の定義と、
    /// <c>RunRestarted</c> / <c>LapCountReset</c> の購読をすべて拾い、表と突き合わせる。
    ///
    /// 新しい部品で落ちたら: 体験者の交代でどこから落とすかを決め、呼び出しを書いてから表に 1 行足す。
    /// 落とす経路は 3 つ（<c>ShowControlClient.ResetForNewVisitor</c>（ShowControlClient.VisitorRun.cs）/ <c>ShowRunDirector.BeginRun</c> /
    /// <c>ShowRunDirector.RunRestarted</c> の購読）。
    /// </summary>
    public sealed class VisitorRunResetCoverageTests
    {
        /// <summary>(定義のあるファイル:メソッド名 / 呼び出しのあるファイル / 呼び出しの文字列 / 経路の説明)</summary>
        private static readonly (string Key, string CallerFile, string Call, string Route)[] Table =
        {
            // ---- ShowControlClient.ResetForNewVisitor が直接落とす
            ("Streaming/CueScheduler.cs:ResetRun", "Streaming/ShowControlClient.VisitorRun.cs", "cueScheduler?.ResetRun()", "ResetForNewVisitor"),
            ("Streaming/TimelineDirector.cs:ResetRun", "Streaming/ShowControlClient.VisitorRun.cs", "timelineDirector?.ResetRun()", "ResetForNewVisitor（BeginRun でもう一度）"),
            ("Streaming/BgmDirector.cs:ResetRun", "Streaming/ShowControlClient.VisitorRun.cs", "ResolveBgmDirector()?.ResetRun()", "ResetForNewVisitor"),
            ("Streaming/Sound/ShowSoundDirector.cs:ResetRun", "Streaming/ShowControlClient.VisitorRun.cs", "ResolveSoundDirector()?.ResetRun()", "ResetForNewVisitor"),
            ("Streaming/Recording/SegmentRecorder.cs:ResetRun", "Streaming/ShowControlClient.VisitorRun.cs", "ResolveRecorder()?.ResetRun(_knownRunEpoch)", "卓の新しい体験者"),
            ("Streaming/Recording/SegmentRecorder.cs:ResetRunLocal", "Streaming/ShowControlClient.VisitorRun.cs", "ResolveRecorder()?.ResetRunLocal()", "現地の新しい体験者"),

            // ---- ShowRunDirector.BeginRun が落とす（導入を挟まない設定でも必ず通る）
            ("Streaming/Recording/SegmentRecorder.cs:ResetRunKeepEpoch", "Streaming/ShowRunDirector.cs", "_recorder?.ResetRunKeepEpoch()", "BeginRun（ゲートを閉じた後）"),
            ("Streaming/SwitchAudioCue.cs:ResetRun", "Streaming/ShowRunDirector.cs", "_switchCue?.ResetRun()", "BeginRun"),
            ("Streaming/Sound/LangSwitchAudioCue.cs:ResetRun", "Streaming/ShowRunDirector.cs", "_langSwitchCue?.ResetRun()", "BeginRun"),
            ("Streaming/EndingCueLogic.cs:ResetRun", "Streaming/ShowRunDirector.cs", "_ending.ResetRun()", "BeginRun"),
            ("Streaming/EndingDecisionLogic.cs:ResetRun", "Streaming/ShowRunDirector.cs", "_endingDecision.ResetRun()", "BeginRun"),

            // ---- 上の部品が中で落とす（持ち主 → 中身）
            ("Streaming/AlertEscalationLogic.cs:ResetRun", "Streaming/SwitchAudioCue.cs", "_esc.ResetRun()", "BeginRun → SwitchAudioCue.ResetRun"),
            ("Streaming/GlitchEscalationLogic.cs:ResetRun", "Streaming/GlitchFx.cs", "_esc.ResetRun()", "BeginRun → GlitchFx.ResetAll"),
            ("Streaming/Sound/SoundCueLogic.cs:ResetRun", "Streaming/Sound/ShowSoundDirector.cs", "_cues.ResetRun()", "ShowSoundDirector.ResetRun"),
            ("Streaming/TakeRunner.cs:ResetRun", "Streaming/TimelineDirector.cs", "takeRunner?.ResetRun()", "TimelineDirector.ResetRun"),
            ("Streaming/TakeRunnerLogic.cs:ResetRun", "Streaming/TakeRunner.cs", "_logic.ResetRun()", "TakeRunner.ResetRun"),
            ("Streaming/EndingFarewellDoll.cs:ResetRun", "Streaming/TakeRunner.cs", "_farewell?.ResetRun()", "TakeRunner.ResetRun"),
            ("Streaming/CommsCueLogic.cs:ResetRun", "Diagnostics/CommsPanel.cs", "_cue.ResetRun()", "RunRestarted → CommsPanel"),
            ("Streaming/WalkGuide.cs:ResetRun", "Streaming/IntroDirector.cs", "walkGuide?.ResetRun()", "RunRestarted → IntroDirector"),
            ("Tracking/LapCounter.cs:ResetRun", "Tracking/LapCounter.cs", "OnLapCountReset() => ResetRun()", "LapCountReset → LapCounter"),

            // ---- 合図を購読して自分で落とす
            ("Tracking/LapCounter.cs:LapCountReset", "Tracking/LapCounter.cs", "LapCountReset += OnLapCountReset", "周の数え直し（導入から本編へ移るときも）"),
            ("Diagnostics/CommsPanel.cs:RunRestarted", "Diagnostics/CommsPanel.cs", "RunRestarted += OnRunRestarted", "BeginRun の最後"),
            ("Diagnostics/StatusHud.cs:RunRestarted", "Diagnostics/StatusHud.cs", "RunRestarted += OnRunRestarted", "BeginRun の最後"),
            ("Streaming/CameraFeelFx.cs:RunRestarted", "Streaming/CameraFeelFx.cs", "RunRestarted += OnHeartbeatRunRestarted", "BeginRun の最後"),
            ("Streaming/EndingFrameCapture.cs:RunRestarted", "Streaming/EndingFrameCapture.cs", "RunRestarted += OnRunRestarted", "BeginRun の最後"),
            ("Streaming/IntroDirector.cs:RunRestarted", "Streaming/IntroDirector.cs", "RunRestarted += OnRunRestarted", "BeginRun の最後"),
            ("Streaming/TitleScreen.cs:RunRestarted", "Streaming/TitleScreen.cs", "RunRestarted += OnRunRestarted", "BeginRun の最後"),
        };

        private static string ScriptsRoot => Path.Combine(Application.dataPath, "Scripts");

        private static string Rel(string path)
            => path.Substring(ScriptsRoot.Length + 1).Replace('\\', '/');

        private static IEnumerable<string> RuntimeSources()
            => Directory.GetFiles(ScriptsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/Editor/"));

        /// <summary>ソースにある「体験 1 回ぶんを落とす口」をすべて拾う（ファイル:名前）。</summary>
        private static SortedSet<string> FoundInSource()
        {
            var found = new SortedSet<string>();
            var def = new Regex(@"\bvoid\s+(ResetRun|ResetRunLocal|ResetRunKeepEpoch)\s*\(");
            var sub = new Regex(@"\.(RunRestarted|LapCountReset)\s*\+=");
            foreach (string path in RuntimeSources())
            {
                string text = File.ReadAllText(path);
                foreach (Match m in def.Matches(text)) found.Add($"{Rel(path)}:{m.Groups[1].Value}");
                foreach (Match m in sub.Matches(text)) found.Add($"{Rel(path)}:{m.Groups[1].Value}");
            }
            return found;
        }

        [Test]
        public void EveryRunResetPoint_IsListedWithItsCaller()
        {
            SortedSet<string> found = FoundInSource();
            var listed = new SortedSet<string>(Table.Select(t => t.Key));
            var missing = found.Except(listed).ToList();
            var stale = listed.Except(found).ToList();
            Assert.That(missing, Is.Empty,
                "体験 1 回ぶんを落とす口が増えたのに、誰が呼ぶかが表に無い。体験者の交代でどこから落とすかを決め、"
                + "呼び出しを書いてから VisitorRunResetCoverageTests.Table に足す: " + string.Join(", ", missing));
            Assert.That(stale, Is.Empty,
                "表にあるのにソースから消えた口。表から外す: " + string.Join(", ", stale));
        }

        [Test]
        public void EveryListedCall_ExistsInItsCallerFile()
        {
            var broken = new List<string>();
            foreach (var t in Table)
            {
                string path = Path.Combine(ScriptsRoot, t.CallerFile);
                if (!File.Exists(path) || !File.ReadAllText(path).Contains(t.Call))
                    broken.Add($"{t.Key}: {t.CallerFile} に「{t.Call}」が無い（経路: {t.Route}）");
            }
            Assert.That(broken, Is.Empty, string.Join("\n", broken));
        }

        /// <summary>
        /// 新しい体験者の号令元は 2 つで、どちらも同じ並びを通る。並びが別の場所へ書き写されると、
        /// 片方だけ直して食い違う（音のリセットが片方だけ抜けていた 2026-09-04 の型）。
        /// </summary>
        [Test]
        public void BothNewVisitorEntries_GoThroughTheSameSequence()
        {
            string scc = File.ReadAllText(Path.Combine(ScriptsRoot, "Streaming/ShowControlClient.VisitorRun.cs"));
            Assert.That(Regex.Matches(scc, @"ResetForNewVisitor\(\(\)").Count, Is.EqualTo(2),
                "TriggerRunReset と BeginNewVisitorRunLocal の 2 か所から呼ぶ");
            Assert.That(Regex.Matches(scc, @"Step\(""sound"", \(\) => ResolveSoundDirector\(\)\?\.ResetRun\(\)\)").Count,
                Is.EqualTo(1), "音のリセットの並びは ResetForNewVisitor の 1 か所だけに書く");
        }

        /// <summary>
        /// ソースに NUL 文字を入れない。grep がファイルをバイナリ扱いし、検索の結果に何も出さなくなる
        /// （2026-11-03、IntroDirector.cs の NUL のせいで WalkGuide.ResetRun の呼び出し元が見えなかった）。
        /// 文字列に NUL が要るなら <c>"\0"</c> と書く。
        /// </summary>
        [Test]
        public void NoScriptSource_ContainsARawNulByte()
        {
            var offenders = Directory.GetFiles(ScriptsRoot, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(Application.dataPath, "Tests"), "*.cs", SearchOption.AllDirectories))
                .Where(p => File.ReadAllBytes(p).Contains((byte)0))
                .Select(p => p.Substring(Application.dataPath.Length + 1))
                .ToList();
            Assert.That(offenders, Is.Empty, "NUL 文字の入ったソース: " + string.Join(", ", offenders));
        }
    }
}
