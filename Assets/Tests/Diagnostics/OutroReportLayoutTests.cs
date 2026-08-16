#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Diagnostics;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>
    /// 終幕の報告の<b>置き方</b>を固定する（<c>canon/LEDGER.md</c> 0063）。
    ///
    /// ⚠⚠ <b>この 2 つは絵では確かめられない。</b>
    /// <c>menu text-audit</c> の <c>Shoot</c> は<b>面の原点を必ず画面中心へ運んでから</b>撮るので、
    /// 塊をどこへ置いたかは 1 枚も写らない（あの絵は<b>大きさとはみ出し専用</b>）。
    /// 実機を被る以外に見る手が無いから、ここで機械が持つ。
    ///
    /// ⚠ 日本語フォントを解決できない環境では面そのものが組まれない（実機と同じ挙動）。
    /// そのときは判定できないので <c>Ignore</c> にする — <b>通ったことにはしない</b>。
    /// </summary>
    public sealed class OutroReportLayoutTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private (OutroReport report, TMP_Text text) SpawnReport()
        {
            var go = new GameObject("[Test] OutroReport");
            _spawned.Add(go);
            var report = go.AddComponent<OutroReport>();
            // ⚠ **Edit モードでは Awake が走らない。** 起こさないと面が組まれず、下の
            //   `Assert.Ignore` へ落ちて**検証していないのにテストが緑に見える**
            //   （`HmdTextAudit` が `BuildsItsOwnText` で同じことをしている）。
            typeof(OutroReport)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(report, null);
            var f = typeof(OutroReport).GetField("_text",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tmp = f?.GetValue(report) as TMP_Text;
            if (tmp == null)
                Assert.Ignore("日本語フォントを解決できないので面が組まれていない（実機と同じ挙動）");
            return (report, tmp!);
        }

        private static float Field(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return f?.GetValue(o) is float v ? v : 0f;
        }

        /// <summary>
        /// 揃えは<b>左</b>、縦は<b>上</b>（1 字ずつ出すので、行が増えるたびに縦中央を
        /// 取り直されると打ち終わった行が跳ねる）。
        /// </summary>
        [Test]
        public void Alignment_IsTopLeft()
        {
            var (_, tmp) = SpawnReport();
            Assert.AreEqual(TextAlignmentOptions.TopLeft, tmp.alignment,
                            "左寄せ ＋ 縦は上寄せ（canon/LEDGER.md 0063）");
        }

        /// <summary>
        /// <b>左寄せでも字の塊は視界の中央に居る。</b> 左寄せの意図は行頭が揃うことで、
        /// 塊が視界の左へ寄ることではない（黒の中に単独で立つ面なので、寄せると首を振って読む）。
        ///
        /// 塊の中心が、頭の正面から <c>pitchOffsetDeg</c> だけ下げた <c>distanceM</c> の点に
        /// 一致していることを見る。<b>枠は 1.70m・実際の行は 1.0m ほど</b>なので、
        /// 運ばなければ x は 0.3m 以上ずれる（＝ 許容 1cm はゆるくない）。
        /// </summary>
        [Test]
        public void InkBlock_IsCenteredInView_EvenThoughAlignmentIsLeft()
        {
            var (report, tmp) = SpawnReport();

            float distanceM = Field(report, "distanceM");
            float pitchDeg = Field(report, "pitchOffsetDeg");
            float scale = tmp.transform.localScale.x;
            Assert.That(scale, Is.GreaterThan(0f), "倍率が 0 なら何も測れない");

            // 実際に字が乗っている範囲の中心（面のローカル）を、面の親の座標へ持ち上げる。
            Bounds ink = tmp.textBounds;
            Vector3 center = tmp.transform.localPosition + ink.center * scale;

            float rad = pitchDeg * Mathf.Deg2Rad;
            float d = Mathf.Max(distanceM, 0.5f);
            var want = new Vector3(0f, -Mathf.Sin(rad) * d, Mathf.Cos(rad) * d);

            Assert.That(center.x, Is.EqualTo(want.x).Within(0.01f),
                        "字の塊が視界の中央から横へずれている（左寄せの塊を運んでいない）");
            Assert.That(center.y, Is.EqualTo(want.y).Within(0.01f),
                        "字の塊が視界の中央から縦へずれている（上寄せの塊を運んでいない）");
            Assert.That(center.z, Is.EqualTo(want.z).Within(0.01f));
        }

        /// <summary>
        /// 組み上がった直後は<b>1 文字も出ていない</b>。打鍵が出現の演出なので、
        /// 段 <c>Report</c> へ入る前に全文が見えていると、打つ意味がまるごと消える。
        /// </summary>
        [Test]
        public void StartsWithNothingTyped()
        {
            var (report, tmp) = SpawnReport();
            Assert.AreEqual(0, tmp.maxVisibleCharacters);
            Assert.AreEqual(0, report.VisibleChars);
        }

        /// <summary>
        /// 打鍵の数は<b>改行を除いた字数</b>（<c>maxVisibleCharacters</c> は改行も 1 文字として
        /// 数えるので、鳴らすと「字が出ていないのに 1 発鳴る」が 4 回起きる）。
        /// TMP に測らせた値と、文言側の期待値（<c>OutroReportTextTests</c>）が一致することを見る。
        /// </summary>
        [Test]
        public void ReportChars_ExcludeTheNewlines()
        {
            var (report, _) = SpawnReport();
            string body = OutroReportText.Compose(0);
            int want = 0;
            foreach (char c in body)
                if (c != '\n') want++;
            Assert.AreEqual(want, report.ReportChars,
                            "テレメトリの repChars ＝ 打鍵の数（改行を除く）");
        }
    }
}
