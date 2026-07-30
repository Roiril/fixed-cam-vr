#nullable enable
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// **スクリーン枠のアスペクトを、実機と卓の両側で固定する。**
    ///
    /// マスク PNG は枠空間で焼かれる。実機シェーダ（<c>ScreenComposite</c>）は live / overlay / CG を
    /// それぞれ <c>_LiveScale</c> / <c>_OverlayScale</c> / <c>_CgScale</c> で contain-fit するのに対し、
    /// **マスクだけは生 uv で読む**（<c>_MaskScale</c> は存在しない）。したがってマスクを作る側は
    /// 「枠が何 : 何か」を知っていなければならず、卓（ブラウザ / CLI）はそれを定数で持つしかない
    /// — 卓は Quad の localScale を読めないため。
    ///
    /// 片側だけ変えると**沈黙して食い違う**（実機だけマスクがずれ、卓では原理的に見えない）。
    /// 2026-07-30 に実際に食い違っていて、現地の実素材 2 件が水平 1.33 倍でずれて合成されていた。
    /// このプロジェクトが色温度・CG 投影・導入の尺で使っているのと同じ「両側にハードコードして
    /// 突き合わせる」流儀をここにも適用する。
    /// </summary>
    public sealed class ScreenFrameAspectTests
    {
        private const string StagePrefabPath = "Assets/Prefabs/Stage/MjpegScreenStage.prefab";

        /// <summary>枠のアスペクト（W/H）。卓の common.js の MW/MH と make-diff-mask.py の FRAME_W/H に対応。</summary>
        private const float ExpectedAspect = 16f / 9f;

        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ReadToolFile(string relative)
        {
            string path = Path.Combine(RepoRoot, "tools", "web-compositor", relative);
            Assert.That(File.Exists(path), Is.True, $"卓のファイルが見つからない: {path}");
            return File.ReadAllText(path);
        }

        private static float ParseFloat(string s) =>
            float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        [Test]
        public void StageQuad_Aspect_Is16By9()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(StagePrefabPath);
            Assert.That(root, Is.Not.Null, $"prefab をロードできない: {StagePrefabPath}");

            var screen = root!.GetComponentInChildren<MjpegScreen>(true);
            Assert.That(screen, Is.Not.Null, "MjpegScreen が prefab に見つからない");

            Vector3 s = screen!.transform.localScale;
            Assert.That(s.y, Is.GreaterThan(1e-5f), "枠の高さが 0（アスペクトを算出できない）");
            float aspect = Mathf.Abs(s.x / s.y);
            Assert.That(aspect, Is.EqualTo(ExpectedAspect).Within(1e-3f),
                $"スクリーン枠のアスペクトが 16:9 でない（実測 {aspect:F4}）。" +
                "変えるなら tools/web-compositor/common.js の MW/MH と make-diff-mask.py の " +
                "FRAME_W/FRAME_H も同時に直すこと（マスクが枠空間で焼かれるため）。");
        }

        [Test]
        public void ScreenAspectOverride_IsUnset_SoQuadDecidesTheFrame()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(StagePrefabPath);
            var screen = root!.GetComponentInChildren<MjpegScreen>(true);
            var so = new SerializedObject(screen);
            SerializedProperty? p = so.FindProperty("screenAspectOverride");
            Assert.That(p, Is.Not.Null, "screenAspectOverride プロパティが無い");
            Assert.That(p!.floatValue, Is.EqualTo(0f).Within(1e-6f),
                "screenAspectOverride が設定されている。0 以外を入れると枠のアスペクトが Quad ではなく" +
                "この値で決まるので、上のテストが実機の真実を見なくなる。");
        }

        [Test]
        public void WebConsole_MaskSize_MatchesFrameAspect()
        {
            string js = ReadToolFile("common.js");
            Match m = Regex.Match(js, @"export\s+const\s+MW\s*=\s*(\d+)\s*,\s*MH\s*=\s*(\d+)");
            Assert.That(m.Success, Is.True, "common.js の MW / MH を読み取れない（宣言の形が変わった？）");
            float mw = ParseFloat(m.Groups[1].Value), mh = ParseFloat(m.Groups[2].Value);
            Assert.That(mw / mh, Is.EqualTo(ExpectedAspect).Within(1e-3f),
                $"卓のマスク寸法 {mw}x{mh} が枠のアスペクト 16:9 と違う。" +
                "実機はマスクを contain-fit せず生 uv で読むので、このずれはそのまま合成のずれになる。");
        }

        [Test]
        public void MaskCli_FrameSize_MatchesFrameAspect()
        {
            string py = ReadToolFile("make-diff-mask.py");
            Match m = Regex.Match(py, @"FRAME_W\s*,\s*FRAME_H\s*=\s*(\d+)\s*,\s*(\d+)");
            Assert.That(m.Success, Is.True, "make-diff-mask.py の FRAME_W / FRAME_H を読み取れない");
            float fw = ParseFloat(m.Groups[1].Value), fh = ParseFloat(m.Groups[2].Value);
            Assert.That(fw / fh, Is.EqualTo(ExpectedAspect).Within(1e-3f),
                $"CLI のマスク寸法 {fw}x{fh} が枠のアスペクト 16:9 と違う。");
        }

        [Test]
        public void MaskShader_DoesNotContainFitTheMask_SoAuthoringMustBeInFrameSpace()
        {
            // この前提が崩れた（= シェーダに _MaskScale が入った）なら、卓を枠空間へ寄せている
            // 上の 2 つのテストは不要になる。そのときはこのテストごと設計を見直すこと。
            string shader = File.ReadAllText(Path.Combine(
                RepoRoot, "Assets", "Art", "Shaders", "Streaming", "ScreenComposite.shader"));
            Assert.That(shader.Contains("_MaskScale"), Is.False,
                "シェーダに _MaskScale が入った。マスクが contain-fit を通るようになったなら、" +
                "卓（common.js / make-diff-mask.py）が枠空間へ寄せている前提が変わる。");
            Assert.That(Regex.IsMatch(shader, @"SAMPLE_TEXTURE2D\(\s*_MaskTex\s*,\s*sampler_MaskTex\s*,\s*uv\s*\)"),
                Is.True, "マスクが生 uv でサンプルされていない（合成モデルが変わった）。");
        }
    }
}
