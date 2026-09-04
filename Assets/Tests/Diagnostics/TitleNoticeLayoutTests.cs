#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>
    /// 体験前の注意書きの<b>2 枚の積み方</b>（2026-09-04・<c>canon/LEDGER.md</c> 0147）。
    /// 本文（1.8°）の下に、小さな案内（1.5°・3 言語）が付く。
    ///
    /// ⚠⚠ <b>これは <c>menu text-audit</c> の絵では判定できない。</b> あの絵は面の原点を
    /// 必ず画面中心へ運んでから撮るので、2 枚をどう積んだかは 1 枚も写らない
    /// （測っているのは大きさとはみ出しだけ）。実機を被る以外に見る手が無いので機械が持つ。
    ///
    /// ⚠ 測るのは <c>textInfo.characterInfo</c>（実際に組まれた字の四隅）。
    /// <c>textBounds</c> は<b>枠を返すことがある</b>ので、積み方の検証には使わない
    /// （<c>memory/hmd_text_style.md</c>「textBounds も lineInfo.width も枠の幅を返す」）。
    /// </summary>
    public sealed class TitleNoticeLayoutTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
            ShowLanguage.Select(ShowLang.Ja);
        }

        private static readonly BindingFlags Priv =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private (TitleNotice notice, TMP_Text body, TMP_Text footer) Spawn()
        {
            var go = new GameObject("[Test] TitleNotice");
            _spawned.Add(go);
            var notice = go.AddComponent<TitleNotice>();
            // ⚠ Edit モードでは Awake が走らない。起こさないと面が組まれず、下の Ignore へ落ちて
            //   **検証していないのにテストが緑に見える**。
            typeof(TitleNotice).GetMethod("Awake", Priv)?.Invoke(notice, null);
            var body = typeof(TitleNotice).GetField("_text", Priv)?.GetValue(notice) as TMP_Text;
            var footer = typeof(TitleNotice).GetField("_footer", Priv)?.GetValue(notice) as TMP_Text;
            if (body == null || footer == null)
                Assert.Ignore("日本語フォントを解決できないので面が組まれていない（実機と同じ挙動）");
            return (notice, body!, footer!);
        }

        private static float Field(object o, string name)
        {
            FieldInfo? f = o.GetType().GetField(name, Priv);
            return f?.GetValue(o) is float v ? v : 0f;
        }

        private static float Const(string name)
        {
            FieldInfo? f = typeof(TitleNotice).GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic);
            return f?.GetValue(null) is float v ? v : 0f;
        }

        /// <summary>実際に組まれた字が占める上端・下端（追従根から見た m）。</summary>
        private static (float top, float bottom) Ink(TMP_Text t)
        {
            t.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            TMP_TextInfo info = t.textInfo;
            float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ci = info.characterInfo[i];
                if (!ci.isVisible) continue;
                top = Mathf.Max(top, ci.topLeft.y);
                bottom = Mathf.Min(bottom, ci.bottomRight.y);
            }
            Assert.That(top, Is.GreaterThan(bottom), "字が 1 つも組まれていない");
            float s = t.transform.localScale.y;
            float y = t.transform.localPosition.y;
            return (y + top * s, y + bottom * s);
        }

        /// <summary>案内は<b>本文より小さい</b>（ユーザー指定「文字を小さくして」）。</summary>
        [Test]
        public void Footer_IsSmallerThanTheNotice()
        {
            var (_, body, footer) = Spawn();
            Assert.Less(footer.transform.localScale.y, body.transform.localScale.y,
                        "案内が本文と同じか大きい");
        }

        /// <summary>
        /// 案内は<b>本文の下</b>に、1 行ぶんほど空けて置く。
        /// ⚠ 重なると読めず、空けすぎると別の掲示に見える。
        /// </summary>
        [Test]
        public void Footer_SitsBelowTheNotice_WithOneLineOfAir()
        {
            var (_, body, footer) = Spawn();
            var b = Ink(body);
            var f = Ink(footer);

            Assert.Less(f.top, b.bottom, "案内が本文に重なっている");
            float gap = b.bottom - f.top;
            float want = Const("FooterGapM");
            Assert.That(gap, Is.InRange(want * 0.5f, want * 2.2f),
                        $"空きが {gap:0.000}m（狙い {want:0.000}m）");
        }

        /// <summary>
        /// <b>2 枚を合わせた塊</b>が視線の据わりに来る（本文だけを中央に置くと、
        /// 案内のぶん全体が下へ落ちる）。
        /// </summary>
        [Test]
        public void TheWholeStack_IsCenteredOnTheGaze()
        {
            var (notice, body, footer) = Spawn();
            var b = Ink(body);
            var f = Ink(footer);

            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);
            float baseY = -Mathf.Sin(Field(notice, "pitchOffsetDeg") * Mathf.Deg2Rad) * d;
            float center = (b.top + f.bottom) * 0.5f;
            Assert.That(center, Is.EqualTo(baseY).Within(0.08f),
                        $"塊の中心が {center:0.000}m（据わりは {baseY:0.000}m）");
        }

        /// <summary>
        /// <b>塊ぜんたいが視界に収まる。</b> 面は 2.6m 先に立つので、縦に伸びるほど
        /// 端を読むのに首を振ることになる。⚠ <b>文言を足すたびにここが効く</b> —
        /// 行が増えても機械は何も言わないので（枠は溢れを許す設定）、上限はここが持つ。
        /// </summary>
        [Test]
        public void TheWholeStack_FitsInTheView()
        {
            var (notice, body, footer) = Spawn();
            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);
            float h = Ink(body).top - Ink(footer).bottom;
            float deg = 2f * Mathf.Atan2(h * 0.5f, d) * Mathf.Rad2Deg;
            TestContext.WriteLine($"塊の縦 = {h:0.000}m ＝ {deg:0.0}°（{d:0.0}m 先）");
            Assert.Less(deg, 42f, $"塊が縦 {deg:0.0}° ＝ 端を読むのに首を振る");
        }

        /// <summary>
        /// ⚠⚠ <b>言語を変えたら積み直す。</b> 行数が変わる（日本語 6 行 / Français 7 行）ので、
        /// 積み直さないと行数の少ない言語で塊が下へずれる。
        /// </summary>
        [Test]
        public void ChangingLanguage_RestacksTheLabels()
        {
            var (notice, body, footer) = Spawn();
            MethodInfo? late = typeof(TitleNotice).GetMethod("LateUpdate", Priv);
            Assert.That(late, Is.Not.Null);

            float CenterNow()
            {
                var b = Ink(body);
                var f = Ink(footer);
                return (b.top + f.bottom) * 0.5f;
            }

            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);
            float baseY = -Mathf.Sin(Field(notice, "pitchOffsetDeg") * Mathf.Deg2Rad) * d;
            Assert.That(CenterNow(), Is.EqualTo(baseY).Within(0.08f), "日本語");

            ShowLanguage.Select(ShowLang.Fr);   // いちばん行数の多い言語
            late!.Invoke(notice, null);
            Assert.AreEqual(TitleNotice.ComposeFor(ShowLang.Fr), body.text, "文面が替わっていない");
            Assert.That(CenterNow(), Is.EqualTo(baseY).Within(0.08f), "Français で積み直していない");
        }
    }
}
