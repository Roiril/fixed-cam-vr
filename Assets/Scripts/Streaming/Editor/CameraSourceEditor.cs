#nullable enable
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using FixedCamVr.Streaming;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// CameraSource Inspector にビルド URL プレビュー / 接続テスト / ブラウザ起動ボタンを追加。
    /// </summary>
    [CustomEditor(typeof(CameraSource))]
    public sealed class CameraSourceEditor : Editor
    {
        private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(2) };
        private string _status = "";
        private MessageType _statusType = MessageType.None;
        private bool _testing;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var src = (CameraSource)target;
            var url = src.BuildUrl();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Built URL", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(url, EditorStyles.textField, GUILayout.Height(18));

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_testing))
                {
                    if (GUILayout.Button(_testing ? "Testing..." : "Test Connection"))
                    {
                        _ = TestConnectionAsync(url);
                    }
                }
                if (GUILayout.Button("Open in Browser"))
                {
                    Application.OpenURL(url);
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, _statusType);
            }
        }

        private async Task TestConnectionAsync(string url)
        {
            _testing = true;
            _status = "Connecting...";
            _statusType = MessageType.Info;
            Repaint();

            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();
                int code = (int)resp.StatusCode;
                bool ok = resp.IsSuccessStatusCode;
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "(no content-type)";
                _status = $"{(ok ? "✅" : "⚠️")} HTTP {code}  ({sw.ElapsedMilliseconds}ms)\nContent-Type: {ct}";
                _statusType = ok ? MessageType.Info : MessageType.Warning;
            }
            catch (System.Exception ex)
            {
                sw.Stop();
                _status = $"❌ {ex.Message}";
                _statusType = MessageType.Error;
            }
            finally
            {
                _testing = false;
                Repaint();
            }
        }
    }
}
