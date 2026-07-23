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
    /// C1 の核: <see cref="CourseFrame"/> のプレビューセッション（Begin/Commit/Rollback）を dt/IO 非依存で固定する。
    /// キャンセル退場で確定前 state（変換 + 要再登録 + 品質メタ）へ戻ること、コミット後は no-op であること、
    /// 未適用（ダーティなし）では Changed を無駄撃ちしないことを検証する。
    ///
    /// registration.json は一意 temp 名を reflection 注入し TearDown で削除する（実機の実ファイルを汚さない）。
    /// EditMode は Awake が自動起動しないため loadOnAwake は発火せず、書込は temp ファイルにのみ起こる。
    /// </summary>
    public sealed class CourseFramePreviewSessionTests
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

        // 一意 temp 名を持つ CourseFrame を返す（Awake 未起動＝loadOnAwake は発火しない）。
        private CourseFrame MakeFrame(out string regPath)
        {
            var go = new GameObject("frame");
            _spawned.Add(go);
            var frame = go.AddComponent<CourseFrame>();
            string fileName = "test_reg_" + Guid.NewGuid().ToString("N") + ".json";
            typeof(CourseFrame).GetField("registrationFileName", BF)!.SetValue(frame, fileName);
            regPath = Path.Combine(Application.persistentDataPath, fileName);
            _tempFiles.Add(regPath);
            return frame;
        }

        [Test]
        public void BeginPreviewRollback_RestoresIdentity_ChangedOnceOnRollback()
        {
            CourseFrame frame = MakeFrame(out _);
            int changed = 0;
            frame.Changed += () => changed++;

            frame.BeginPreviewSession();
            frame.SetRegistration(new Vector2(0.3f, -0.2f), 15f, save: false); // プレビュー適用（Changed +1）
            Assert.That(changed, Is.EqualTo(1), "プレビュー適用で 1 回");

            frame.RollbackPreviewSession();
            Assert.That(changed, Is.EqualTo(2), "Rollback で追加 1 回（ダーティなので復元 + Changed）");
            Assert.That(frame.OriginXZ, Is.EqualTo(Vector2.zero));
            Assert.That(frame.YawDeg, Is.EqualTo(0f));
            Assert.That(frame.HasRegistration, Is.False, "プレビューは hasReg を立てない");
        }

        [Test]
        public void CommitThenRollback_KeepsPreview_NoExtraChanged()
        {
            CourseFrame frame = MakeFrame(out _);
            var preview = new Vector2(0.4f, 0.1f);

            frame.BeginPreviewSession();
            frame.SetRegistration(preview, 20f, save: false);
            frame.CommitPreviewSession();

            int changed = 0;
            frame.Changed += () => changed++;
            frame.RollbackPreviewSession(); // コミット済み → no-op

            Assert.That(changed, Is.EqualTo(0), "コミット後の Rollback は no-op（Changed 追加なし）");
            Assert.That(frame.OriginXZ, Is.EqualTo(preview), "変換は preview のまま保持");
            Assert.That(frame.YawDeg, Is.EqualTo(20f));
        }

        [Test]
        public void Rollback_RestoresNeedsReRegistration()
        {
            CourseFrame frame = MakeFrame(out _);
            frame.MarkNeedsReRegistration();
            Assert.That(frame.NeedsReRegistration, Is.True);

            frame.BeginPreviewSession();
            frame.SetRegistration(new Vector2(0.2f, 0.2f), 5f, save: false); // needsReReg が false 化される
            Assert.That(frame.NeedsReRegistration, Is.False);

            frame.RollbackPreviewSession();
            Assert.That(frame.NeedsReRegistration, Is.True,
                "Rollback で要再登録フラグが復元される（黙って警告が消える現行バグの是正）");
        }

        [Test]
        public void Rollback_RestoresQualityMeta()
        {
            CourseFrame frame = MakeFrame(out _);
            // 前値: 未登録なので 0 / 0。
            Assert.That(frame.MaxResidualM, Is.EqualTo(0f));
            Assert.That(frame.PointCount, Is.EqualTo(0));

            frame.BeginPreviewSession();
            frame.SetRegistration(new Vector2(0.1f, 0.1f), 0f, 0.05f, 3, save: false);
            Assert.That(frame.MaxResidualM, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(frame.PointCount, Is.EqualTo(3));

            frame.RollbackPreviewSession();
            Assert.That(frame.MaxResidualM, Is.EqualTo(0f), "品質メタ（残差）が前値へ");
            Assert.That(frame.PointCount, Is.EqualTo(0), "品質メタ（点数）が前値へ");
        }

        [Test]
        public void BeginRollback_WithoutPreview_NoChangedNoTransformChange()
        {
            CourseFrame frame = MakeFrame(out _);
            int changed = 0;
            frame.Changed += () => changed++;

            frame.BeginPreviewSession();
            frame.RollbackPreviewSession(); // ダーティなし

            Assert.That(changed, Is.EqualTo(0), "未適用 Rollback は Changed を撃たない");
            Assert.That(frame.OriginXZ, Is.EqualTo(Vector2.zero));
            Assert.That(frame.YawDeg, Is.EqualTo(0f));
        }

        [Test]
        public void RollbackOutsideSession_NoOp_NoException()
        {
            CourseFrame frame = MakeFrame(out _);
            int changed = 0;
            frame.Changed += () => changed++;

            Assert.DoesNotThrow(() => frame.RollbackPreviewSession());
            Assert.That(changed, Is.EqualTo(0));
            Assert.That(frame.OriginXZ, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void ExistingRegistration_PreviewRollback_RestoresSavedValues_JsonUnchanged()
        {
            CourseFrame frame = MakeFrame(out string regPath);
            var saved = new Vector2(0.2f, 0.3f);
            // 確定保存で「既存登録」を作る（temp ファイルに書き込み・hasReg / savedAtIso が立つ）。
            frame.SetRegistration(saved, 10f, 0.04f, 2, save: true);
            Assert.That(File.Exists(regPath), Is.True, "確定保存で registration.json が生成される");
            string savedIso = frame.SavedAtIso;
            byte[] beforeBytes = File.ReadAllBytes(regPath);

            frame.BeginPreviewSession();
            frame.SetRegistration(new Vector2(-0.5f, 0.5f), 40f, 0.09f, 4, save: false); // 別変換をプレビュー
            frame.RollbackPreviewSession();

            Assert.That(frame.OriginXZ, Is.EqualTo(saved), "保存済みの原点へ戻る");
            Assert.That(frame.YawDeg, Is.EqualTo(10f), "保存済みの yaw へ戻る");
            Assert.That(frame.HasRegistration, Is.True, "既存登録は維持");
            Assert.That(frame.SavedAtIso, Is.EqualTo(savedIso), "保存日時が復元される");
            Assert.That(frame.MaxResidualM, Is.EqualTo(0.04f).Within(1e-6f));
            Assert.That(File.ReadAllBytes(regPath), Is.EqualTo(beforeBytes), "registration.json は書き換わらない");
        }
    }
}
