#nullable enable
using System;
using System.IO;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class ShowCompositePreview
    {
        [Serializable] private sealed class EndingCueFile
        {
            public EndingCue[] cues = Array.Empty<EndingCue>();
        }
        [Serializable] private sealed class EndingCue
        {
            public string id = "", sourceUrl = "", maskUrl = "";
        }

        // This is a design sample, not a visitor recording. Runtime only uses EndingFrameCapture.
        public static void CreateEndingSamples(out Texture2D released, out Texture2D trapped)
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string showPath = Path.Combine(root, "tools/web-compositor/show.json");
            ShowJson show = LoadShow(showPath, out _) ?? throw new InvalidOperationException("Missing show.json");
            var cues = JsonUtility.FromJson<EndingCueFile>(File.ReadAllText(showPath));
            EndingCue plateCue = Array.Find(cues.cues, c => c.id == "plate_A")
                ?? throw new InvalidOperationException("Missing plate_A");
            EndingCue crowdCue = Array.Find(cues.cues, c => c.id == "gen_dolls_A_left")
                ?? throw new InvalidOperationException("Missing closing crowd cue");
            Texture2D plateTexture = LoadEndingAsset(root, plateCue.sourceUrl);
            Texture2D crowd = LoadEndingAsset(root, crowdCue.sourceUrl);
            Texture2D mask = LoadEndingAsset(root, crowdCue.maskUrl);
            string dir = Path.Combine(root, "Logs/ending-roll-preview");
            Directory.CreateDirectory(dir);
            using var sample = Stage.Create(dir);
            try
            {
                sample.ApplyLayout(show.layout);
                sample.SetFeel(show.feel);
                var plate = new Plate { texture = plateTexture, width = plateTexture.width,
                    height = plateTexture.height, label = plateCue.sourceUrl };
                var camera = show.CameraAt(0);
                Geometry geometry = sample.AimVirtualCamera(camera, plate.width, plate.height);
                if (!geometry.usable) throw new InvalidOperationException("Closing camera has no calibration");
                sample.HideActor();
                sample.Composite(plate, false, show.CameraPost(0) ?? show.post ?? new PostParams(), "");
                released = sample.ReadEndingTexture();
                ShowCompositePreviewPlan.Shot? closing = ShowCompositePreviewPlan.CollectShots(show.timeline)
                    .Find(s => s.takeId == "L4C0#0" && s.step.durKind == "untilMark");
                if (closing == null) throw new InvalidOperationException("Missing closing untilMark step");
                var actor = show.FindActor(closing.step.cg)
                    ?? throw new InvalidOperationException("Missing closing actor");
                if (!TryFollowStand(show, closing, camera, out Vector2 stand, out float yaw))
                    throw new InvalidOperationException("No closing visitor position for design sample");
                sample.SetPlateLuma(plate);
                sample.PlaceActor(actor, stand, yaw, geometry);
                sample.SetAura(closing.step.aura);
                sample.DecayProgress = 1;
                sample.Composite(plate, true,
                    ShowCompositePreviewPlan.ResolvePost(closing.step, show.CameraPost(0), show.post), "");
                trapped = sample.ReadEndingTexture(crowd, mask);
                File.WriteAllBytes(Path.Combine(dir, "sample-return.png"), released.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(dir, "sample-doll.png"), trapped.EncodeToPNG());
                File.WriteAllText(Path.Combine(dir, "sample-source.txt"),
                    "Design sample. Not a visitor recording.\nReturn: " + plateCue.sourceUrl +
                    "\nDoll crowd: " + crowdCue.sourceUrl + "\nDoll placement: closing zone centroid.\n");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(plateTexture);
                UnityEngine.Object.DestroyImmediate(crowd);
                UnityEngine.Object.DestroyImmediate(mask);
            }
        }

        private static Texture2D LoadEndingAsset(string root, string relative)
        {
            string path = Path.Combine(root, "tools/web-compositor", relative.TrimStart('/'));
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
                throw new InvalidOperationException("Cannot load ending sample: " + path);
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }
    }
}
