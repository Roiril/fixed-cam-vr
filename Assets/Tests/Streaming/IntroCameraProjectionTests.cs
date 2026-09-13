#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class IntroCameraProjectionTests
    {
        private static readonly Vector2 SensorResolution = new(1280f, 960f);
        private static readonly Vector2 FocalLength = new(800f, 800f);
        private static readonly Vector2 PrincipalPoint = new(640f, 480f);
        private static readonly Pose CameraPose = new(
            new Vector3(1.25f, -0.4f, 2.7f),
            Quaternion.Euler(12f, 37f, -8f));

        [Test]
        public void WorldToUv_RoundTripsKnownRaysAtSensorResolution()
        {
            Matrix4x4 worldToUv = BuildWorldToUv(new Vector2Int(1280, 960));

            AssertUv(worldToUv, CameraPose.position + CameraPose.rotation * new Vector3(0f, 0f, 2f),
                new Vector2(0.5f, 0.5f));
            AssertUv(worldToUv, CameraPose.position + CameraPose.rotation * new Vector3(1.6f, 1.2f, 2f),
                new Vector2(1f, 1f));
        }

        [Test]
        public void WorldToUv_RoundTripsKnownRayAfterSquareOutputCrop()
        {
            Matrix4x4 worldToUv = BuildWorldToUv(new Vector2Int(1280, 1280));

            AssertUv(worldToUv, CameraPose.position + CameraPose.rotation * new Vector3(0f, 0f, 3f),
                new Vector2(0.5f, 0.5f));
            // 1280x1280 出力は 1280x960 sensor の左右 160px ずつを crop する。
            // crop 右端 x=1120、縦 1/4 の y=240 を通る既知光線。
            AssertUv(worldToUv, CameraPose.position + CameraPose.rotation * new Vector3(1.8f, -0.9f, 3f),
                new Vector2(1f, 0.25f));
        }

        [Test]
        public void DuplicateTimestamp_DoesNotRefreshImageAgeOrReplaceCapturePose()
        {
            Type bridgeType = GetBridgeType();
            Type sampleType = bridgeType.GetNestedType("FrameSample", BindingFlags.NonPublic)!;
            MethodInfo record = bridgeType.GetMethod(
                "RecordSample", BindingFlags.NonPublic | BindingFlags.Static)!;
            FieldInfo timestampField = sampleType.GetField("timestamp", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo observedAtField = sampleType.GetField("observedAt", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo matrixField = sampleType.GetField("worldToUv", BindingFlags.Public | BindingFlags.Instance)!;

            object latest = Activator.CreateInstance(sampleType)!;
            DateTime firstTimestamp = DateTime.UnixEpoch.AddMilliseconds(1000);
            DateTime secondTimestamp = DateTime.UnixEpoch.AddMilliseconds(1016);
            var firstMatrix = Matrix4x4.identity;
            firstMatrix.m03 = 1f;
            var secondMatrix = Matrix4x4.identity;
            secondMatrix.m03 = 2f;

            latest = Record(record, latest, firstMatrix, firstTimestamp, 10f);
            latest = Record(record, latest, secondMatrix, firstTimestamp, 99f);
            Assert.That((DateTime)timestampField.GetValue(latest)!, Is.EqualTo(firstTimestamp));
            Assert.That(((Matrix4x4)matrixField.GetValue(latest)!).m03, Is.EqualTo(1f));
            Assert.That((float)observedAtField.GetValue(latest)!, Is.EqualTo(10f),
                "同じ Timestamp の通知で画像の鮮度を延長しない");

            latest = Record(record, latest, secondMatrix, secondTimestamp, 11f);
            Assert.That((DateTime)timestampField.GetValue(latest)!, Is.EqualTo(secondTimestamp));
            Assert.That(((Matrix4x4)matrixField.GetValue(latest)!).m03, Is.EqualTo(2f));
            Assert.That((float)observedAtField.GetValue(latest)!, Is.EqualTo(11f));
        }

        private static Matrix4x4 BuildWorldToUv(Vector2Int currentResolution)
        {
            Type bridgeType = GetBridgeType();
            MethodInfo? method = bridgeType!.GetMethod(
                "BuildWorldToUv", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "BuildWorldToUv を解決できない");

            Type? intrinsicsType = Type.GetType(
                "Meta.XR.PassthroughCameraAccess+CameraIntrinsics, meta.xr.mrutilitykit");
            Assert.That(intrinsicsType, Is.Not.Null, "MRUK CameraIntrinsics を解決できない");
            object intrinsics = Activator.CreateInstance(intrinsicsType!)!;
            intrinsicsType!.GetField("FocalLength")!.SetValue(intrinsics, FocalLength);
            intrinsicsType.GetField("PrincipalPoint")!.SetValue(intrinsics, PrincipalPoint);
            intrinsicsType.GetField("SensorResolution")!.SetValue(
                intrinsics, new Vector2Int((int)SensorResolution.x, (int)SensorResolution.y));

            object? result = method!.Invoke(null, new[] { (object)CameraPose, intrinsics, currentResolution });
            Assert.That(result, Is.TypeOf<Matrix4x4>());
            return (Matrix4x4)result!;
        }

        private static Type GetBridgeType()
        {
            Type? bridgeType = Type.GetType(
                "FixedCamVr.OvrBridge.IntroPassthroughCapture, Assembly-CSharp");
            Assert.That(bridgeType, Is.Not.Null, "IntroPassthroughCapture を解決できない");
            return bridgeType!;
        }

        private static object Record(
            MethodInfo method, object sample, Matrix4x4 matrix,
            DateTime timestamp, float observedAt)
        {
            object?[] args = { sample, null, matrix, timestamp, observedAt };
            method.Invoke(null, args);
            return args[0]!;
        }

        private static void AssertUv(Matrix4x4 worldToUv, Vector3 worldPoint, Vector2 expected)
        {
            Vector4 q = worldToUv * new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, 1f);
            Assert.That(q.w, Is.GreaterThan(0f));
            Assert.That(q.x / q.w, Is.EqualTo(expected.x).Within(0.00001f));
            Assert.That(q.y / q.w, Is.EqualTo(expected.y).Within(0.00001f));
        }
    }
}
