#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 題字の開始案内（X/Y 短押しの文）を<b>初めて出す瞬間</b>に落ちないことを固定する。
    ///
    /// 2026-09-15 実害: 生成時に <c>SetActive(false)</c> した TextMeshPro は Awake を通っておらず、
    /// 最初の <c>text</c> は null。<c>SetStartGuidance(Ready)</c> が <c>text.Length</c> で
    /// NullReference を投げ、自動走行（<c>ShowWalkDebugDriver</c>）のコルーチンごと死んで
    /// 導入まで 1 歩も進まなかった。被って操作した分だけ進んで見えるので、走行の記録が無いと気づけない。
    /// </summary>
    public sealed class TitleStartPromptTests
    {
        [Test]
        public void SetStartGuidance_OnNeverActivatedPrompt_DoesNotThrow()
        {
            var root = new GameObject("TitleStartPromptTest");
            try
            {
                var title = root.AddComponent<TitleScreen>();
                // 本物の Build は Resources と Always Included シェーダを要る。ここでは案内の TMP だけを
                // 実機と同じ形（生成 → 未起動のまま SetActive(false)）で差し込み、段を Hold にする。
                // テストの asmdef は TMPro を参照しないので、型は TitleScreen が使う TMP_Text の実体
                // （TextMeshPro）を名前で引く。
                Type tmpType = Type.GetType("TMPro.TextMeshPro, Unity.TextMeshPro")!;
                Assume.That(tmpType, Is.Not.Null, "前提: TextMeshPro の型を引ける");
                var promptGo = new GameObject("TitleStartPrompt");
                promptGo.transform.SetParent(root.transform, worldPositionStays: false);
                Component prompt = promptGo.AddComponent(tmpType);
                promptGo.SetActive(false);
                PropertyInfo textProp = tmpType.GetProperty("text")!;
                string? initial = (string?)textProp.GetValue(prompt);
                Assert.That(initial, Is.Null.Or.Empty, "前提: 未起動の TMP の text は空か null");

                typeof(TitleScreen).GetField("_startPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(title, prompt);
                var logic = (TitleLogic)typeof(TitleScreen)
                    .GetField("_logic", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(title)!;
                logic.Begin();
                logic.RequestAdvance();
                for (int i = 0; i < 200 && logic.Stage != TitleStage.Hold; i++)
                    logic.Tick(0.1f, new TitleInput { concealReady = true });
                Assume.That(logic.Stage, Is.EqualTo(TitleStage.Hold), "前提: 段を Hold へ進められる");

                Assert.DoesNotThrow(() => title.SetStartGuidance(TitleStartGuidance.Ready));
                Assert.That(promptGo.activeSelf, Is.True, "案内が出ていない");
                Assert.That((string?)textProp.GetValue(prompt),
                    Is.EqualTo(TitleScreen.StartPromptText(TitleStartGuidance.Ready, ShowLanguage.Current)));

                // 2 回目（既に出ている）は文が変わったときだけ書き換える。
                Assert.DoesNotThrow(() => title.SetStartGuidance(TitleStartGuidance.ShortPress));
                Assert.That((string?)textProp.GetValue(prompt),
                    Is.EqualTo(TitleScreen.StartPromptText(TitleStartGuidance.ShortPress, ShowLanguage.Current)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
