#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// <see cref="CourseFrame"/> の 4 DOF 化（床の高さ）を固定する。
    ///
    /// 2026-08-02 に <c>CourseToWorld</c> の第 2 引数の意味が変わった（旧: ワールド y の直指定 →
    /// 新: <b>床からの高さ</b>）。呼び出し側が渡していた定数（ワイヤー 0.03 / タイル 0.015 /
    /// ゾーン中心 1.0 / CG 人形の 0）はすべて「床からの高さ」のつもりの値なので、意味の変更で
    /// 全部が正しく持ち上がる。ここが崩れると**ワイヤーだけ / 人形だけが沈む**という気づきにくい壊れ方をする。
    /// </summary>
    public sealed class CourseFrameFloorTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();
        private readonly List<string> _tempFiles = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
            foreach (var p in _tempFiles)
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            _tempFiles.Clear();
        }

        private CourseFrame MakeFrame(out string regPath)
        {
            var go = new GameObject("frame");
            _spawned.Add(go);
            var frame = go.AddComponent<CourseFrame>();
            string fileName = "test_floor_" + Guid.NewGuid().ToString("N") + ".json";
            typeof(CourseFrame).GetField("registrationFileName", BF)!.SetValue(frame, fileName);
            regPath = Path.Combine(Application.persistentDataPath, fileName);
            _tempFiles.Add(regPath);
            return frame;
        }

        [Test]
        public void CourseToWorld_AddsFloorToTheHeightArgument()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetFloorY(0.42f, 0.01f, save: false);

            Vector3 p = frame.CourseToWorld(new Vector2(1f, -2f), 0.03f);
            Assert.That(p.y, Is.EqualTo(0.45f).Within(1e-4f), "y 引数は床からの高さ");
            Assert.That(p.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(p.z, Is.EqualTo(-2f).Within(1e-4f));
        }

        [Test]
        public void FloorDoesNotDisturbTheHorizontalTransform()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetRegistration(new Vector2(0.7f, -0.3f), 25f, save: false);
            Vector3 before = frame.CourseToWorld(new Vector2(0.5f, 0.5f), 0f);

            frame.SetFloorY(0.42f, 0.01f, save: false);
            Vector3 after = frame.CourseToWorld(new Vector2(0.5f, 0.5f), 0f);

            Assert.That(after.x, Is.EqualTo(before.x).Within(1e-5f), "床を上げても XZ は動かない");
            Assert.That(after.z, Is.EqualTo(before.z).Within(1e-5f));
            Assert.That(after.y - before.y, Is.EqualTo(0.42f).Within(1e-5f));
        }

        [Test]
        public void HeightAboveFloor_IsTheInverse()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetFloorY(-0.18f, 0f, save: false);
            Assert.That(frame.HeightAboveFloor(1.42f), Is.EqualTo(1.60f).Within(1e-4f));
        }

        [Test]
        public void WorldToCourse_RoundTripsRegardlessOfFloor()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetRegistration(new Vector2(-0.4f, 1.1f), -35f, save: false);
            frame.SetFloorY(0.42f, 0f, save: false);

            var course = new Vector2(0.9f, -0.6f);
            Vector3 world = frame.CourseToWorld(course, 1.6f);
            Vector2 back = frame.WorldToCourse(world);
            Assert.That(back.x, Is.EqualTo(course.x).Within(1e-3f));
            Assert.That(back.y, Is.EqualTo(course.y).Within(1e-3f));
        }

        [Test]
        public void SetFloorY_MarksSchemaAndSurvivesSaveLoad()
        {
            CourseFrame frame = MakeFrame(out string path);
            frame.SetRegistration(new Vector2(0.2f, 0.3f), 12f, 0.05f, 3, save: false);
            frame.SetFloorY(0.42f, 0.03f, save: false);
            frame.SaveRegistration();
            Assert.That(File.Exists(path), Is.True);

            CourseFrame reloaded = MakeFrame(out string path2);
            File.Copy(path, path2, overwrite: true);
            reloaded.LoadRegistration();

            Assert.That(reloaded.FloorY, Is.EqualTo(0.42f).Within(1e-4f));
            Assert.That(reloaded.FloorSpreadM, Is.EqualTo(0.03f).Within(1e-4f));
            Assert.That(reloaded.HasFloorY, Is.True);
            Assert.That(reloaded.RegSchema, Is.EqualTo(CourseFrame.FloorSchema));
        }

        [Test]
        public void OldRegistrationFileLoadsWithoutFloor()
        {
            // 床の高さを測っていない旧ファイル。originY=0 を「床が一致している」と読むと、
            // ずれたまま「合っている」と表示することになる。regSchema で区別する。
            CourseFrame frame = MakeFrame(out string path);
            File.WriteAllText(path,
                "{\"originX\":0.5,\"originZ\":-0.5,\"yawDeg\":10.0,\"maxResidualM\":0.05,\"pointCount\":2}");
            frame.LoadRegistration();

            Assert.That(frame.HasRegistration, Is.True, "旧ファイルでも位置合わせは効く");
            Assert.That(frame.OriginXZ.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(frame.RegSchema, Is.EqualTo(0));
            Assert.That(frame.HasFloorY, Is.False, "床の高さは未測定");
            Assert.That(frame.FloorY, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void RollbackRestoresTheFloorToo()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetRegistration(new Vector2(0.1f, 0.1f), 5f, 0.04f, 2, save: true);
            frame.SetFloorY(0.42f, 0.02f, save: true);

            frame.BeginPreviewSession();
            frame.SetFloorY(-0.9f, 0.30f, save: false);      // 失敗した再登録のプレビュー
            frame.SetRegistration(new Vector2(2f, 2f), 90f, save: false);
            frame.RollbackPreviewSession();

            Assert.That(frame.FloorY, Is.EqualTo(0.42f).Within(1e-4f), "確定済みの床へ戻る");
            Assert.That(frame.FloorSpreadM, Is.EqualTo(0.02f).Within(1e-4f));
            Assert.That(frame.OriginXZ.x, Is.EqualTo(0.1f).Within(1e-4f));
        }

        [Test]
        public void ResetClearsTheFloor()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.SetFloorY(0.42f, 0.02f, save: false);
            frame.ResetRegistration(deleteFile: false);

            Assert.That(frame.FloorY, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(frame.HasFloorY, Is.False);
            Assert.That(frame.RegSchema, Is.EqualTo(0));
        }
    }
}
