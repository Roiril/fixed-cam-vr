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
    /// <see cref="CourseRegistrationController"/> の状態機械（Idle/Review/Capture/Verify・ホールド平均・
    /// 残差ゲート・キャンセル/確定/やり直し）と C1（プレビューのトランザクション化）end-to-end を
    /// reflection 駆動で固定する（SwitchWiringTests 流の EditMode + 手動 Feed）。
    ///
    /// 点キャプチャは rightHand.position を置いて Feed(mark) → held を dt 0.1 で複数回進めて 0.5s ホールド平均を
    /// 到達させる（HoldAverageSampler は dt 注入なので Time.deltaTime 非依存で完了できる）。
    /// registration.json は一意 temp 名を reflection 注入し TearDown で削除する（実ファイルを汚さない）。
    /// </summary>
    public sealed class CourseRegistrationControllerTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();
        private readonly List<string> _tempFiles = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
            // SetActive(true) が生成する可視化ルート（別ルート GO）の取りこぼしを掃除する。
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                if (go != null && go.name == "[CourseRegViz]") UnityEngine.Object.DestroyImmediate(go);
            foreach (var p in _tempFiles)
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            _tempFiles.Clear();
        }

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            f!.SetValue(target, value);
        }

        private static string Phase(CourseRegistrationController c)
            => typeof(CourseRegistrationController).GetField("_phase", BF)!.GetValue(c)!.ToString()!;

        private static int PointIndex(CourseRegistrationController c)
            => (int)typeof(CourseRegistrationController).GetField("_pointIndex", BF)!.GetValue(c)!;

        private static void RefreshGuidance(CourseRegistrationController c)
            => typeof(CourseRegistrationController).GetMethod("UpdateGuidanceText", BF)!.Invoke(c, null);

        private static CourseRegistrationController.RegInput RI(bool mark, bool held, bool confirm, float dt)
            => new() { mark = mark, markHeld = held, confirm = confirm, deltaTime = dt };

        // 1 点のホールド平均キャプチャ。Down で開始 → held を dt 0.1 で 6 フレーム（0.6s>0.5s の余裕）。
        // 手先を固定するので平均は world にほぼ一致する。
        private static void Cap(CourseRegistrationController c, Transform hand, Vector3 world)
        {
            hand.position = world;
            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f));
            for (int i = 0; i < 6; i++) c.Feed(RI(mark: false, held: true, confirm: false, dt: 0.1f));
        }

        private (CourseRegistrationController c, CourseFrame frame, Transform hand, string regPath) Setup()
        {
            var go = new GameObject("reg");
            _spawned.Add(go);
            var frame = go.AddComponent<CourseFrame>();
            string fileName = "test_reg_" + Guid.NewGuid().ToString("N") + ".json";
            SetField(frame, "registrationFileName", fileName);
            string regPath = Path.Combine(Application.persistentDataPath, fileName);
            _tempFiles.Add(regPath);

            var handGo = new GameObject("hand");
            _spawned.Add(handGo);

            var c = go.AddComponent<CourseRegistrationController>();
            SetField(c, "courseFrame", frame);
            SetField(c, "rightHandTransform", handGo.transform);
            // showControl は null のまま → ResolvePoints は既定 2 点 (-0.5,0.5)/(0.5,0.5)（距離 1.0）にフォールバック。
            return (c, frame, handGo.transform, regPath);
        }

        [Test]
        public void CaptureTwoPoints_ResidualOk_TransitionsToVerify()
        {
            var (c, frame, hand, _) = Setup();
            bool fitAccepted = false;
            c.FitAccepted += () => fitAccepted = true;

            c.Toggle(); // 未登録 → Capture 着地
            Assert.That(Phase(c), Is.EqualTo("Capture"));

            // authored (-0.5,0.5)/(0.5,0.5) と一致する world → identity フィット。
            Cap(c, hand, new Vector3(-0.5f, 1f, 0.5f));
            Cap(c, hand, new Vector3(0.5f, 1f, 0.5f));

            Assert.That(Phase(c), Is.EqualTo("Verify"), "2 点そろい残差ガード通過で Verify");
            Assert.That(fitAccepted, Is.True, "FitAccepted 発火");
            Assert.That(frame.OriginXZ.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(frame.OriginXZ.y, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(frame.YawDeg, Is.EqualTo(0f).Within(0.5f));
        }

        [Test]
        public void ResidualGate_Rejects_ReturnsToCapture()
        {
            var (c, _, hand, _) = Setup();
            bool fitRejected = false;
            c.FitRejected += () => fitRejected = true;

            c.Toggle();
            // world 間距離 1.4（authored 1.0）→ 剛体フィット残差 0.2m/点 > 0.12 許容。
            Cap(c, hand, new Vector3(-0.7f, 1f, 0.5f));
            Cap(c, hand, new Vector3(0.7f, 1f, 0.5f));

            Assert.That(fitRejected, Is.True, "残差過大で FitRejected");
            Assert.That(Phase(c), Is.EqualTo("Capture"), "やり直しで Capture へ戻る");
            Assert.That(PointIndex(c), Is.EqualTo(0), "点 index は 0 へリセット");

            RefreshGuidance(c);
            Assert.That(c.GuidanceText, Does.Contain("打ち直してください"), "失敗理由は次の操作まで残る");

            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f));
            RefreshGuidance(c);

            Assert.That(c.GuidanceText, Does.Contain("計測中"), "次の A 押下で失敗文が消え、進捗表示へ戻る");
            Assert.That(c.GuidanceIsAlert, Is.False);
        }

        [Test]
        public void VerifyRetryMark_StartsFirstPointSamplingWithoutSecondPress()
        {
            var (c, _, hand, _) = Setup();
            c.Toggle();
            Cap(c, hand, new Vector3(-0.5f, 1f, 0.5f));
            Cap(c, hand, new Vector3(0.5f, 1f, 0.5f));
            Assert.That(Phase(c), Is.EqualTo("Verify"));

            hand.position = new Vector3(-0.5f, 1f, 0.5f);
            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f));
            c.Feed(RI(mark: false, held: true, confirm: false, dt: 0.5f));

            Assert.That(Phase(c), Is.EqualTo("Capture"));
            Assert.That(PointIndex(c), Is.EqualTo(1), "やり直しを選んだ A 押下が点 1 の計測開始にも使われる");
        }

        [Test]
        public void ReviewRetryMark_StartsFirstPointSamplingWithoutSecondPress()
        {
            var (c, frame, hand, _) = Setup();
            frame.SetRegistration(Vector2.zero, 0f, 0.02f, 2, save: true);
            c.Toggle();
            Assert.That(Phase(c), Is.EqualTo("Review"));

            hand.position = new Vector3(-0.5f, 1f, 0.5f);
            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f));
            c.Feed(RI(mark: false, held: true, confirm: false, dt: 0.5f));

            Assert.That(Phase(c), Is.EqualTo("Capture"));
            Assert.That(PointIndex(c), Is.EqualTo(1), "確認画面の A 押下が点 1 の計測開始にも使われる");
        }

        [Test]
        public void ReenterRegistration_DoesNotRestorePreviousFailureMessage()
        {
            var (c, _, _, _) = Setup();
            c.Toggle();
            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f));
            c.Feed(RI(mark: false, held: false, confirm: false, dt: 0f));
            RefreshGuidance(c);
            Assert.That(c.GuidanceText, Does.Contain("読み取れませんでした"));

            c.Toggle();
            c.Toggle();
            RefreshGuidance(c);

            Assert.That(c.GuidanceText, Does.Not.Contain("読み取れませんでした"));
            Assert.That(c.GuidanceText, Does.Contain("点 1／2"));
        }

        [Test]
        public void Confirm_CommitsAndSaves_ExitsActive()
        {
            var (c, frame, hand, regPath) = Setup();
            bool confirmed = false;
            c.RegistrationConfirmed += () => confirmed = true;

            c.Toggle();
            Cap(c, hand, new Vector3(-0.5f, 1f, 0.5f));
            Cap(c, hand, new Vector3(0.5f, 1f, 0.5f));
            Assert.That(Phase(c), Is.EqualTo("Verify"));

            c.Feed(RI(mark: false, held: false, confirm: true, dt: 0f)); // B 確定

            Assert.That(c.IsActive, Is.False, "確定で登録モード退場");
            Assert.That(confirmed, Is.True, "RegistrationConfirmed 発火");
            Assert.That(frame.HasRegistration, Is.True, "確定で有効な登録");
            Assert.That(frame.SavedAtIso, Is.Not.Empty, "保存日時が焼き込まれる");
            Assert.That(frame.OriginXZ.x, Is.EqualTo(0f).Within(1e-3f), "変換は preview のまま確定");
            Assert.That(File.Exists(regPath), Is.True, "registration.json 生成");
        }

        [Test]
        public void CancelExit_NoRegistration_RollsBackToIdentity()
        {
            var (c, frame, hand, regPath) = Setup();
            int changed = 0;
            frame.Changed += () => changed++;

            c.Toggle();
            // 原点がずれる非 identity プレビュー（world を +X に 1.0 シフト）。
            Cap(c, hand, new Vector3(0.5f, 1f, 0.5f));
            Cap(c, hand, new Vector3(1.5f, 1f, 0.5f));
            Assert.That(Phase(c), Is.EqualTo("Verify"));
            Assert.That(frame.OriginXZ.x, Is.EqualTo(1f).Within(1e-3f), "プレビューで原点が (1,0) へ");

            c.Toggle(); // = トリガー長押しキャンセル退場

            Assert.That(c.IsActive, Is.False);
            Assert.That(frame.OriginXZ, Is.EqualTo(Vector2.zero), "キャンセルで identity へロールバック");
            Assert.That(frame.YawDeg, Is.EqualTo(0f));
            Assert.That(frame.HasRegistration, Is.False, "確定していないので登録なし");
            Assert.That(changed, Is.GreaterThan(0), "ロールバックで Changed 発火");
            Assert.That(File.Exists(regPath), Is.False, "registration.json は生成されない");
        }

        [Test]
        public void CancelExit_DuringReRegistration_RestoresSavedRegistration()
        {
            var (c, frame, hand, regPath) = Setup();
            var saved = new Vector2(0.2f, 0.3f);
            frame.SetRegistration(saved, 10f, 0.05f, 2, save: true); // 既存登録を仕込む
            Assert.That(File.Exists(regPath), Is.True);
            byte[] beforeBytes = File.ReadAllBytes(regPath);

            c.Toggle(); // hasReg true → Review 着地
            Assert.That(Phase(c), Is.EqualTo("Review"));

            hand.position = new Vector3(1.2f, 1f, 0.5f);
            c.Feed(RI(mark: true, held: true, confirm: false, dt: 0f)); // A=点1から再登録 + 計測開始
            Assert.That(Phase(c), Is.EqualTo("Capture"));

            // 別変換をプレビュー（world を +X に 1.7 相当シフト → 原点 (1.7,0)）。
            for (int i = 0; i < 6; i++) c.Feed(RI(mark: false, held: true, confirm: false, dt: 0.1f));
            Cap(c, hand, new Vector3(2.2f, 1f, 0.5f));
            Assert.That(Phase(c), Is.EqualTo("Verify"));
            Assert.That(frame.OriginXZ.x, Is.EqualTo(1.7f).Within(1e-3f), "再登録プレビューが適用されている");

            c.Toggle(); // キャンセル退場 → 元の保存値へロールバック

            Assert.That(c.IsActive, Is.False);
            Assert.That(frame.OriginXZ.x, Is.EqualTo(saved.x).Within(1e-4f), "保存済みの原点へ復元");
            Assert.That(frame.OriginXZ.y, Is.EqualTo(saved.y).Within(1e-4f));
            Assert.That(frame.YawDeg, Is.EqualTo(10f).Within(1e-3f), "保存済みの yaw へ復元");
            Assert.That(frame.HasRegistration, Is.True, "既存登録は維持");
            Assert.That(File.ReadAllBytes(regPath), Is.EqualTo(beforeBytes), "registration.json は不変");
        }

        [Test]
        public void ReviewExit_B_NoChange_KeepsRegistration()
        {
            var (c, frame, hand, regPath) = Setup();
            var saved = new Vector2(-0.1f, 0.4f);
            frame.SetRegistration(saved, -5f, 0.06f, 3, save: true);
            byte[] beforeBytes = File.ReadAllBytes(regPath);

            c.Toggle(); // Review
            Assert.That(Phase(c), Is.EqualTo("Review"));

            c.Feed(RI(mark: false, held: false, confirm: true, dt: 0f)); // B=保存せず終了

            Assert.That(c.IsActive, Is.False);
            Assert.That(frame.OriginXZ.x, Is.EqualTo(saved.x).Within(1e-4f), "変換は不変");
            Assert.That(frame.OriginXZ.y, Is.EqualTo(saved.y).Within(1e-4f));
            Assert.That(frame.YawDeg, Is.EqualTo(-5f).Within(1e-3f));
            Assert.That(frame.HasRegistration, Is.True, "登録は継続");
            Assert.That(File.ReadAllBytes(regPath), Is.EqualTo(beforeBytes), "registration.json は不変");
        }
    }
}
