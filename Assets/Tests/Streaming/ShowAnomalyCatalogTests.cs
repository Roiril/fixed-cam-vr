#nullable enable
using System;
using System.IO;
using System.Linq;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class ShowAnomalyCatalogTests
    {
        [Serializable]
        private sealed class ShowFile
        {
            public ShowTimelineDef timeline = new();
        }

        private static ShowTimelineSegmentDef[] LoadCurrentSegments()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../tools/web-compositor/show.json"));
            Assert.That(File.Exists(path), Is.True, $"現行 show.json が見つからない: {path}");
            ShowFile show = JsonUtility.FromJson<ShowFile>(File.ReadAllText(path));
            Assert.That(show.timeline, Is.Not.Null);
            Assert.That(show.timeline.segments, Is.Not.Null);
            return show.timeline.segments;
        }

        [Test]
        public void CurrentShow_HasNineTakesAndEightAnomalies()
        {
            ShowTimelineSegmentDef[] segments = LoadCurrentSegments();
            Assert.That(segments.Sum(s => s.takes.Length), Is.EqualTo(9));
            Assert.That(ShowAnomalyCatalog.Count(segments), Is.EqualTo(8));
            Assert.That(ShowAnomalyCatalog.CountUnknown(segments), Is.Zero);
        }

        [Test]
        public void TwoCPreparationAndApproach_AreOneAnomaly()
        {
            var segment = Segment("L2C2#0", "L2C2#1");
            Assert.That(ShowAnomalyCatalog.Count(new[] { segment }), Is.EqualTo(1));
            Assert.That(ShowAnomalyCatalog.CountUnknown(new[] { segment }), Is.Zero);
        }

        [Test]
        public void RemovingATake_ChangesTheTotal()
        {
            ShowTimelineSegmentDef[] segments = LoadCurrentSegments();
            ShowTimelineSegmentDef last = segments.Single(s => s.lap == 4 && s.camera == 0);
            last.takes = Array.Empty<ShowTakeDef>();
            Assert.That(ShowAnomalyCatalog.Count(segments), Is.EqualTo(7));
        }

        [Test]
        public void UnknownTake_IsNotCountedAsAnomaly()
        {
            var segments = new[] { Segment("L2C2#0", "L9C9#0", "") };
            Assert.That(ShowAnomalyCatalog.Count(segments), Is.EqualTo(1));
            Assert.That(ShowAnomalyCatalog.CountUnknown(segments), Is.EqualTo(2));
            Assert.That(ShowAnomalyCatalog.Count(Array.Empty<ShowTimelineSegmentDef>()), Is.Zero);
            Assert.That(ShowAnomalyCatalog.CountUnknown(Array.Empty<ShowTimelineSegmentDef>()), Is.Zero);
        }

        private static ShowTimelineSegmentDef Segment(params string[] ids)
            => new() { takes = ids.Select(id => new ShowTakeDef { id = id }).ToArray() };
    }
}
