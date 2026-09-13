#nullable enable

#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

namespace FixedCamVr.EditorTools
{
    /// <summary>廻リ視の生成 manifest だけへパススルーカメラ権限を加える。</summary>
    internal sealed class FixedCamCameraManifest : IPostGenerateGradleAndroidProject
    {
        private const string PackageId = "com.roiril.mawarimi";
        private const string Permission = "horizonos.permission.HEADSET_CAMERA";
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        // Meta XR の OVRGradleGeneration (99999) が manifest を更新した後に実行する。
        public int callbackOrder => 100000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (PlayerSettings.applicationIdentifier != PackageId) return;

            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("生成された AndroidManifest.xml が見つかりません", manifestPath);

            var document = new XmlDocument { PreserveWhitespace = true };
            document.Load(manifestPath);
            XmlElement? manifest = document.DocumentElement;
            if (manifest == null || manifest.Name != "manifest")
                throw new InvalidDataException($"AndroidManifest.xml のルート要素が不正です: {manifestPath}");

            foreach (XmlNode child in manifest.ChildNodes)
            {
                if (child is XmlElement element && element.Name == "uses-permission"
                    && element.GetAttribute("name", AndroidNamespace) == Permission)
                    return;
            }

            XmlElement permission = document.CreateElement("uses-permission");
            permission.SetAttribute("name", AndroidNamespace, Permission);
            XmlNode? application = manifest.SelectSingleNode("application");
            if (application != null) manifest.InsertBefore(permission, application);
            else manifest.AppendChild(permission);
            document.Save(manifestPath);
            Debug.Log($"[FixedCamCameraManifest] {Permission} を生成 manifest へ追加しました");
        }
    }
}
#endif
