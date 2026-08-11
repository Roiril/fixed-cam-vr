#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// タイトルのシェーダが<b>ビルドから剥がれていない</b>ことを機械で固定する。
    ///
    /// Unity は「どのマテリアルからも参照されていないシェーダ」をビルドから外す。
    /// <see cref="TitleScreen"/> は実行時 <c>Shader.Find</c> で掴むので、
    /// <c>ProjectSettings/GraphicsSettings.asset</c> の <c>m_AlwaysIncludedShaders</c> に
    /// 入っていないと **Editor では動くのに実機でだけタイトルが出ない**。
    ///
    /// 2026-07-31 に `FixedCamVr/IntroVeil` がこれで剥がれ、導入の枠が実機で 1 度も描かれなかった
    /// （気づいたのは実機録画をフル解像度で見たとき）。<b>同じ穴を人の注意で塞がない。</b>
    /// </summary>
    public sealed class TitleShaderInclusionTests
    {
        private const string SettingsPath = "ProjectSettings/GraphicsSettings.asset";

        [TestCase(TitleScreen.VeilShaderName)]
        [TestCase(TitleScreen.GlyphShaderName)]
        public void Shader_IsAlwaysIncluded(string shaderName)
        {
            HashSet<string>? included = LoadAlwaysIncluded();
            if (included == null)
            {
                Assert.Inconclusive($"{SettingsPath} を読めませんでした（Unity の版で構造が変わった可能性）。");
                return;
            }
            Assert.IsTrue(included.Contains(shaderName),
                $"{shaderName} が m_AlwaysIncludedShaders に入っていません。" +
                "実行時 Shader.Find で掴むシェーダはビルドから剥がれるので、" +
                $"{SettingsPath} に追加すること（実機でだけタイトルが出なくなる）。");
        }

        [Test]
        public void SdfTexture_IsLoadable()
        {
            // 距離場が Resources に無ければ、シェーダが揃っていてもタイトルは組めない。
            var tex = Resources.Load<Texture2D>(TitleScreen.SdfResourcePath);
            Assert.IsNotNull(tex,
                $"Resources/{TitleScreen.SdfResourcePath} が読めません。" +
                "焼き直しは py -3.11 tools/make-title-sdf.py");
        }

        private static HashSet<string>? LoadAlwaysIncluded()
        {
            Object[] objs = AssetDatabase.LoadAllAssetsAtPath(SettingsPath);
            if (objs == null || objs.Length == 0) return null;
            var so = new SerializedObject(objs[0]);
            SerializedProperty? list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray) return null;

            var names = new HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var s = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null) names.Add(s.name);
            }
            return names;
        }
    }
}
