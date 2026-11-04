#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// 保存した無人写真から解いた固定カメラの配置と部屋の寸法。正本は <c>tools/twin/rig.json</c>（<c>plate_fit.py</c> が書く）。
    /// A は三脚、B は床置き、C は棚に留めたアーム。デジタルツインの見た目の機材だけがこれを使う。
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
            public float hfovDeg;
        }

        [Serializable]
        private sealed class Shelf
        {
            public float[] min = Array.Empty<float>();
            public float[] max = Array.Empty<float>();
        }

        [Serializable]
        private sealed class Room
        {
            public float xMin;
            public float zMax;
        }

        [Serializable]
        private sealed class File_
        {
            public Cam[] cameras = Array.Empty<Cam>();
            public Shelf? shelf;
            public Room? room;
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

        /// <summary>id のカメラの位置・向き・縦画角（4:3）。無ければ false。</summary>
        public static bool TryPose(string id, out Vector3 position, out Quaternion rotation, out float verticalFovDeg)
        {
            foreach (Cam c in Data.cameras)
            {
                if (c.id != id) continue;
                position = new Vector3(c.pos[0], c.pos[1], c.pos[2]);
                Vector3 target = new Vector3(c.lookAt[0], c.lookAt[1], c.lookAt[2]);
                rotation = Quaternion.LookRotation(target - position, Vector3.up) * Quaternion.Euler(0f, 0f, c.rollDeg);
                verticalFovDeg = 2f * Mathf.Atan(Mathf.Tan(c.hfovDeg * 0.5f * Mathf.Deg2Rad) * 0.75f) * Mathf.Rad2Deg;
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            verticalFovDeg = 0f;
            return false;
        }

        public static string Mount(string id)
        {
            foreach (Cam c in Data.cameras)
                if (c.id == id) return c.mount;
            return "tripod";
        }

        /// <summary>実物の部屋の西の壁（x）と北の壁（z）。</summary>
        public static Vector2 RoomBack
        {
            get
            {
                Room r = Data.room ?? throw new InvalidOperationException("[TwinRig] room が無い");
                return new Vector2(r.xMin, r.zMax);
            }
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
