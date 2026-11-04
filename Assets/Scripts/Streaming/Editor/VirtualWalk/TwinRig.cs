#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// 企画書（paper/dcexpo 2 節と図 1・図 4）の固定カメラの配置。正本は <c>tools/twin/rig.json</c>。
    /// A は三脚、B は床置きの台、C は棚に留めたアーム。デジタルツインの見た目の機材だけがこれを使う。
    /// 本番の合成の姿勢（show.json の calib）には触らない。
    /// </summary>
    internal static class TwinRig
    {
        [Serializable]
        private sealed class Cam
        {
            public string id = "";
            public string mount = "";
            public float[] pos = Array.Empty<float>();
            public float[] lookAt = Array.Empty<float>();
            public float rollDeg;
        }

        [Serializable]
        private sealed class Shelf
        {
            public float[] min = Array.Empty<float>();
            public float[] max = Array.Empty<float>();
        }

        [Serializable]
        private sealed class File_
        {
            public Cam[] cameras = Array.Empty<Cam>();
            public Shelf? shelf;
        }

        private static File_? _data;

        private static File_ Data
        {
            get
            {
                if (_data != null) return _data;
                string path = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "tools", "twin", "rig.json");
                _data = JsonUtility.FromJson<File_>(File.ReadAllText(path))
                        ?? throw new InvalidOperationException($"[TwinRig] 読めない: {path}");
                return _data;
            }
        }

        /// <summary>id のカメラの位置と向き。無ければ false。</summary>
        public static bool TryPose(string id, out Vector3 position, out Quaternion rotation)
        {
            foreach (Cam c in Data.cameras)
            {
                if (c.id != id) continue;
                position = new Vector3(c.pos[0], c.pos[1], c.pos[2]);
                Vector3 target = new Vector3(c.lookAt[0], c.lookAt[1], c.lookAt[2]);
                rotation = Quaternion.LookRotation(target - position, Vector3.up) * Quaternion.Euler(0f, 0f, c.rollDeg);
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        public static string Mount(string id)
        {
            foreach (Cam c in Data.cameras)
                if (c.id == id) return c.mount;
            return "tripod";
        }

        /// <summary>C のアームを留める棚の範囲（m）。</summary>
        public static Bounds ShelfBounds
        {
            get
            {
                Shelf s = Data.shelf ?? throw new InvalidOperationException("[TwinRig] shelf が無い");
                var b = new Bounds();
                b.SetMinMax(new Vector3(s.min[0], s.min[1], s.min[2]), new Vector3(s.max[0], s.max[1], s.max[2]));
                return b;
            }
        }
    }
}
