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
    /// 体験前の注意書きの<b>3 枚の積み方</b>（2026-09-05・<c>canon/LEDGER.md</c> 0155）。
    /// 上から <b>言語の並び（1.8°）/ 操作の説明（1.5°）/ 安全の掲示（1.8°）</b>。
    /// 2026-09-04（0147）から 09-05 までは 2 枚（掲示 ＋ 案内）だった。
    ///
    /// ⚠⚠ <b>これは <c>menu text-audit</c> の絵では判定できない。</b> あの絵は面の原点を
    /// 必ず画面中心へ運んでから撮るので、何枚をどう積んだかは 1 枚も写らない
    /// （測っているのは大きさとはみ出しだけ）。実機を被る以外に見る手が無いので機械が持つ。
    ///
    /// ⚠ 測るのは <c>textInfo.characterInfo</c>（実際に組まれた字の四隅）。
    /// <c>textBounds</c> は<b>枠を返すことがある</b>ので、積み方の検証には使わない
    /// （<c>memory/hmd_text_style.md</c>「textBounds も lineInfo.width も枠の幅を返す」）。
    /// </summary>
    public sealed class TitleNoticeLayoutTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [SetUp]
        public void ResetState() => HorrorRelief.Reset();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
            ShowLanguage.Select(ShowLang.Ja);
            HorrorRelief.Reset();
        }

        private static readonly BindingFlags Priv =
            BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>上から下へ並べた 3 枚（実装の <c>Labels</c> と同じ順）。</summary>
        private (TitleNotice notice, TMP_Text[] labels) Spawn()
        {
            var go = new GameObject("[Test] TitleNotice");
            _spawned.Add(go);
            var notice = go.AddComponent<TitleNotice>();
            // ⚠ Edit モードでは Awake が走らない。起こさないと面が組まれず、下の Ignore へ落ちて
            //   **検証していないのにテストが緑に見える**。
            typeof(TitleNotice).GetMethod("Awake", Priv)?.Invoke(notice, null);

            var labels = new List<TMP_Text>();
            foreach (string field in new[] { "_chooser", "_footer", "_text" })
            {
                var t = typeof(TitleNotice).GetField(field, Priv)?.GetValue(notice) as TMP_Text;
                if (t == null)
                    Assert.Ignore("日本語フォントを解決できないので面が組まれていない（実機と同じ挙動）");
                labels.Add(t!);
            }
            return (notice, labels.ToArray());
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

        /// <summary>操作の説明は<b>上下の 2 枚より小さい</b>（ユーザー指定「文字を小さくして」）。</summary>
        [Test]
        public void TheGuide_IsSmallerThanTheOtherTwo()
        {
            var (_, l) = Spawn();
            Assert.Less(l[1].transform.localScale.y, l[0].transform.localScale.y, "並びより大きい");
            Assert.Less(l[1].transform.localScale.y, l[2].transform.localScale.y, "掲示より大きい");
        }

        /// <summary>
        /// ⚠⚠ <b>上から 言語 → 操作 → 掲示 の順に積む</b>（2026-09-05・0155 のユーザー指定）。
        /// 重なると読めず、順が違うと体験者は掲示から読み始める。
        /// </summary>
        [Test]
        public void TheThreeLabels_AreStackedInTheOrderTheUserAsked()
        {
            var (_, l) = Spawn();
            var ink = new[] { Ink(l[0]), Ink(l[1]), Ink(l[2]) };
            float want = Const("FooterGapM");
            for (int i = 0; i + 1 < ink.Length; i++)
            {
                Assert.Less(ink[i + 1].top, ink[i].bottom, $"{i} 枚目と {i + 1} 枚目が重なっている");
                float gap = ink[i].bottom - ink[i + 1].top;
                // ⚠⚠ **狙いとほぼ一致すること**（0151）。ここが緩いと、積み方が行送りの高さで
                //    計算されていた頃の 2.6 倍（狙い 0.075m が画では 0.194m）を見逃す。
                Assert.That(gap, Is.EqualTo(want).Within(0.006f),
                            $"{i} 枚目の下の空きが {gap:0.000}m（狙い {want:0.000}m）");
            }
        }

        /// <summary>
        /// <b>3 枚を合わせた塊</b>が視線の据わりに来る（1 枚だけを中央に置くと、
        /// 残りのぶん全体が上下へずれる）。
        /// </summary>
        [Test]
        public void TheWholeStack_IsCenteredOnTheGaze()
        {
            var (notice, l) = Spawn();
            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);
            float baseY = -Mathf.Sin(Field(notice, "pitchOffsetDeg") * Mathf.Deg2Rad) * d;
            float center = (Ink(l[0]).top + Ink(l[2]).bottom) * 0.5f;
            Assert.That(center, Is.EqualTo(baseY).Within(0.08f),
                        $"塊の中心が {center:0.000}m（据わりは {baseY:0.000}m）");
        }

        /// <summary>
        /// <b>塊ぜんたいが視界に収まる。</b> 面は 2.6m 先に立つので、縦に伸びるほど
        /// 端を読むのに首を振ることになる。⚠ <b>文言を足すたびにここが効く</b> —
        /// 行が増えても機械は何も言わないので（枠は溢れを許す設定）、上限はここが持つ。
        /// ⚠ <b>いちばん行数の多い言語で測る</b>（Français ＝ 掲示が 7 行）。
        /// </summary>
        [Test]
        public void TheWholeStack_FitsInTheView()
        {
            var (notice, l) = Spawn();
            MethodInfo? late = typeof(TitleNotice).GetMethod("LateUpdate", Priv);
            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);

            foreach (ShowLang lang in ShowLanguage.All)
            {
                ShowLanguage.Select(lang);
                late!.Invoke(notice, null);
                float h = Ink(l[0]).top - Ink(l[2]).bottom;
                float deg = 2f * Mathf.Atan2(h * 0.5f, d) * Mathf.Rad2Deg;
                TestContext.WriteLine($"[{ShowLanguage.Code(lang)}] 塊の縦 = {h:0.000}m ＝ {deg:0.0}°");
                Assert.Less(deg, 42f, $"[{lang}] 塊が縦 {deg:0.0}° ＝ 端を読むのに首を振る");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>言語を変えたら 3 枚とも書き直して積み直す。</b> 行数が変わる
        /// （掲示が 日本語 6 行 / Français 7 行）ので、積み直さないと塊がずれる。
        /// </summary>
        [Test]
        public void ChangingLanguage_RewritesAndRestacksEveryLabel()
        {
            var (notice, l) = Spawn();
            MethodInfo? late = typeof(TitleNotice).GetMethod("LateUpdate", Priv);
            Assert.That(late, Is.Not.Null);

            float d = Mathf.Max(Field(notice, "distanceM"), 0.5f);
            float baseY = -Mathf.Sin(Field(notice, "pitchOffsetDeg") * Mathf.Deg2Rad) * d;
            float CenterNow() => (Ink(l[0]).top + Ink(l[2]).bottom) * 0.5f;
            Assert.That(CenterNow(), Is.EqualTo(baseY).Within(0.08f), "日本語");

            ShowLanguage.Select(ShowLang.Fr);   // いちばん行数の多い言語
            late!.Invoke(notice, null);
            Assert.AreEqual(TitleNotice.ChooserFor(ShowLang.Fr), l[0].text, "並びが替わっていない");
            Assert.AreEqual(TitleNotice.GuideFor(ShowLang.Fr, false, 0f), l[1].text,
                            "操作の説明が替わっていない");
            Assert.AreEqual(TitleNotice.NoticeFor(ShowLang.Fr), l[2].text, "掲示が替わっていない");
            Assert.That(CenterNow(), Is.EqualTo(baseY).Within(0.08f), "Français で積み直していない");
        }

        /// <summary>
        /// ⚠⚠ <b>長押しのゲージが伸びても塊は 1 ミリも動かない</b>（2026-09-05・0155）。
        /// 全角 2 種なので字送りが変わらず、行数も変わらない ＝ 積み直す理由が無い。
        /// 動くなら、押しているあいだ面が上下に揺れて読めなくなる。
        /// </summary>
        [Test]
        public void TheGauge_DoesNotMoveTheStack_WhileItFills()
        {
            var (notice, l) = Spawn();
            MethodInfo? late = typeof(TitleNotice).GetMethod("LateUpdate", Priv);

            HorrorRelief.SetHoldProgress(0f);
            late!.Invoke(notice, null);
            var before = (top: Ink(l[0]).top, bottom: Ink(l[2]).bottom, guide: Ink(l[1]).top);

            for (int i = 1; i <= TitleNotice.GaugeCells; i++)
            {
                HorrorRelief.SetHoldProgress(i / (float)TitleNotice.GaugeCells);
                late.Invoke(notice, null);
                Assert.That(Ink(l[0]).top, Is.EqualTo(before.top).Within(1e-4f), $"{i} マスで上端が動いた");
                Assert.That(Ink(l[2]).bottom, Is.EqualTo(before.bottom).Within(1e-4f),
                            $"{i} マスで下端が動いた");
                Assert.That(Ink(l[1]).top, Is.EqualTo(before.guide).Within(1e-4f),
                            $"{i} マスで操作の説明が動いた");
            }
            // ⚠ 満杯まで来たら、画に出ている字も満杯であること（書き替えを忘れていない）。
            StringAssert.Contains(TitleNotice.HoldGauge(1f), l[1].text, "ゲージが画に出ていない");
        }
    }
}
