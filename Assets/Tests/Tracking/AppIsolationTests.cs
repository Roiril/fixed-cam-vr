#nullable enable
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 同居 2 アプリ（廻リ視 = FixedCamVr.* / TableDuo = TableDuoVr.*）の分離を機械で守る。
    ///
    /// この規約は [.claude/rules/parallel-projects.md] §1 に文章で書いてあるが、
    /// **文章は守られない**。片方の asmdef にもう片方を足しても Unity は何も言わないし、
    /// 差分レビューでも 1 行なので見落とす。壊れたことに気づくのは
    /// 「片方をビルドしたら相手のコードが混ざっていた」時になる。
    ///
    /// 移植元: unity-game-studio の `AssemblyIsolationTests`（ゲーム間の分離を同じ形で検査している）。
    /// 向こうでは「規約 → 機械検査」への昇格が、足した当日に実バグを 1 件見つけている。
    /// </summary>
    public sealed class AppIsolationTests
    {
        private const string FixedCamPrefix = "FixedCamVr";
        private const string TableDuoPrefix = "TableDuoVr";

        private static string AssetsRoot => Application.dataPath.Replace('\\', '/');

        [Serializable]
        private sealed class Asmdef
        {
            public string name = "";
            public string[] references = Array.Empty<string>();
        }

        private static string Rel(string abs) =>
            "Assets" + abs.Replace('\\', '/').Substring(AssetsRoot.Length);

        private static (string path, Asmdef def)[] AllAsmdefs()
        {
            return Directory
                .GetFiles(AssetsRoot, "*.asmdef", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                // PackageCache 等の外部は対象外（Assets 配下だけを見ている時点で入らないが明示する）
                .Where(p => !p.Contains("/PackageCache/"))
                .Select(p =>
                {
                    Asmdef? d = JsonUtility.FromJson<Asmdef>(File.ReadAllText(p));
                    Assert.IsNotNull(d, $"asmdef を読めない: {Rel(p)}");
                    return (p, d!);
                })
                .ToArray();
        }

        // asmdef の references は GUID 形式（"GUID:xxxx"）でも名前でも書ける。
        // 名前で書かれている場合だけ判定できるので、GUID の場合は参照先の名前へ解決する。
        private static string ResolveName(string reference, (string path, Asmdef def)[] all)
        {
            if (!reference.StartsWith("GUID:", StringComparison.Ordinal)) return reference;
            string guid = reference.Substring("GUID:".Length);
            foreach ((string path, Asmdef def) in all)
            {
                string meta = path + ".meta";
                if (!File.Exists(meta)) continue;
                if (File.ReadAllText(meta).Contains(guid)) return def.name;
            }
            return reference;   // 解決できない = 外部パッケージ。判定対象外
        }

        [Test]
        public void 廻リ視とTableDuoのasmdefが相互参照していない()
        {
            (string path, Asmdef def)[] all = AllAsmdefs();
            Assert.IsNotEmpty(all, "asmdef が 1 つも見つからない（テストの前提が壊れている）");

            foreach ((string path, Asmdef def) in all)
            {
                string owner = def.name;
                bool isFixedCam = owner.StartsWith(FixedCamPrefix, StringComparison.Ordinal);
                bool isTableDuo = owner.StartsWith(TableDuoPrefix, StringComparison.Ordinal);
                if (!isFixedCam && !isTableDuo) continue;   // MyCobotHandVr 等は対象外

                foreach (string raw in def.references)
                {
                    string r = ResolveName(raw, all);
                    if (isFixedCam && r.StartsWith(TableDuoPrefix, StringComparison.Ordinal))
                    {
                        Assert.Fail($"廻リ視の {owner} が TableDuo の {r} を参照している "
                                    + $"({Rel(path)})。2 アプリは完全分離する "
                                    + ".claude/rules/parallel-projects.md §1");
                    }
                    if (isTableDuo && r.StartsWith(FixedCamPrefix, StringComparison.Ordinal))
                    {
                        Assert.Fail($"TableDuo の {owner} が廻リ視の {r} を参照している "
                                    + $"({Rel(path)})。2 アプリは完全分離する "
                                    + ".claude/rules/parallel-projects.md §1");
                    }
                }
            }
        }

        [Test]
        public void 廻リ視のコードがTableDuoのディレクトリに居ない()
        {
            // 置き場所も契約のうち。Assets/TableDuo/ 配下に FixedCamVr の asmdef があると、
            // 「TableDuo だけ触る」作業が廻リ視を巻き込む。
            foreach ((string path, Asmdef def) in AllAsmdefs())
            {
                bool underTableDuo = path.Contains("/TableDuo/");
                if (underTableDuo && def.name.StartsWith(FixedCamPrefix, StringComparison.Ordinal))
                {
                    Assert.Fail($"{Rel(path)} は Assets/TableDuo/ 配下なのに廻リ視の "
                                + $"asmdef ({def.name}) になっている");
                }
            }
        }
    }
}
