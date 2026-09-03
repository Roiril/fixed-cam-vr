#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 音の定位（<c>canon/LEDGER.md</c> 0130 — スクリーンから / 周囲から / 後ろから）。
    ///
    /// ⚠⚠ <b>定位の失敗は画にも動画にも出ない。</b> ステレオのクリップを 3D に置いても
    /// 音は鳴る（頭の中で鳴る）し、方角がでたらめでも録画は同じ絵になる。
    /// 機械で捕まえられるのはここと、走行ログの <c>snd3d</c> / <c>sndAz</c> / <c>sndCall</c>
    /// （<c>tools/analyze-xp-log.py</c>）だけ。
    /// </summary>
    public sealed class SpatialAudioTests
    {
        /// <summary>
        /// <b>名簿に重複が無い。</b> 重複すると <c>CountStereo</c> の分母が水増しされ、
        /// 「何本を見たか」が実態とずれる。
        /// </summary>
        [Test]
        public void MonoRequired_HasNoDuplicates()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (string s in SpatialAudio.MonoRequired)
                Assert.IsTrue(seen.Add(s), $"名簿に {s} が 2 回入っている");
        }

        /// <summary>
        /// <b>鳴らしている音がすべて名簿に居る。</b> ⚠ ここが落ちるのは
        /// 「3D にしたはずの音を名簿へ入れ忘れた」＝ **音は鳴るが定位しない**という
        /// いちばん気づけない壊れ方。
        /// </summary>
        [Test]
        public void MonoRequired_CoversEveryFamilyWePlaceIn3d()
        {
            var set = new System.Collections.Generic.HashSet<string>(SpatialAudio.MonoRequired);
            for (int i = 1; i <= SwitchAudioCue.DefaultVariantCount; i++)
            {
                Assert.IsTrue(set.Contains($"sfx_switch_{i}"), $"sfx_switch_{i} が名簿に無い");
                Assert.IsTrue(set.Contains($"sfx_switch_alert_{i}"),
                              $"sfx_switch_alert_{i} が名簿に無い");
            }
            for (int i = 1; i <= TypeAudioCue.VariantCount; i++)
                Assert.IsTrue(set.Contains($"sfx_type_{i}"), $"sfx_type_{i} が名簿に無い");
            foreach (string res in new[] { "bed_device", "bed_device_worn", "bed_static",
                                           "bed_dolls_laugh", "bed_doll_one",
                                           "bed_dolls_grow_a", "bed_dolls_grow_b" })
                Assert.IsTrue(set.Contains(res), $"{res} が名簿に無い");
            foreach (var c in new[] { SoundCue.Glitch, SoundCue.ScreenOn,
                                      SoundCue.PowerOff, SoundCue.DollCall })
            {
                string baseName = SoundCueLogic.ResourceName(c);
                int n = SoundCueLogic.VariantCount(c);
                for (int i = 0; i < n; i++)
                {
                    string res = n == 1 ? baseName : $"{baseName}_{i + 1}";
                    Assert.IsTrue(set.Contains(res), $"{res}（{c}）が名簿に無い");
                }
            }
        }

        /// <summary>
        /// <b>鳴らさなくなった音は名簿に入れない。</b> 鳴らないものを 3D にしても
        /// 確かめる手が無く、名簿だけが太る（<c>CountStereo</c> の分母も狂う）。
        /// </summary>
        [Test]
        public void MonoRequired_ExcludesSilencedCues()
        {
            var set = new System.Collections.Generic.HashSet<string>(SpatialAudio.MonoRequired);
            foreach (string res in new[] { "sfx_swap", "sfx_seal_close",
                                           "sfx_screen_noise", "sfx_shell_open" })
                Assert.IsFalse(set.Contains(res), $"{res} は鳴らない音なので名簿に入れない");
        }

        /// <summary>
        /// ⚠⚠ <b>3D にするのは向きだけで、大きさは変えない。</b>
        /// 減衰が始まる距離は現実にありえない値まで押し上げてあるので、
        /// 対数減衰の内側 ＝ 倍率 1.0 のままになる（<c>rules/sound-design.md</c> §3 の
        /// 高さの設計を、置いた場所で黙って書き換えないため）。
        /// </summary>
        [Test]
        public void Configure_DoesNotAttenuateWithDistance()
        {
            var go = new GameObject("[t]");
            try
            {
                var src = go.AddComponent<AudioSource>();
                SpatialAudio.Configure(src);
                Assert.That(src.spatialBlend, Is.EqualTo(1f));
                Assert.IsTrue(src.spatialize);
                Assert.That(src.rolloffMode, Is.EqualTo(AudioRolloffMode.Logarithmic));
                Assert.That(src.dopplerLevel, Is.EqualTo(0f));
                // 体験で実際に使う距離（スクリーン 2.0m / 面 1.5m / 笑いの輪 2.5m）が
                // すべて減衰の内側に入っていること。
                Assert.Less(SpatialAudio.LaughRadiusM, src.minDistance);
                Assert.Less(SpatialAudio.CallRadiusM, src.minDistance);
                Assert.Less(3.0f, src.minDistance);
                Assert.Less(src.minDistance, src.maxDistance);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>掴めなかったときの逃げ道（2D）は定位も切る。</summary>
        [Test]
        public void MakeFlat_TurnsOffTheSpatializer()
        {
            var go = new GameObject("[t]");
            try
            {
                var src = go.AddComponent<AudioSource>();
                SpatialAudio.Configure(src);
                SpatialAudio.MakeFlat(src);
                Assert.That(src.spatialBlend, Is.EqualTo(0f));
                Assert.IsFalse(src.spatialize);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// 笑いの輪は<b>頭を中心に、頭の向きに依らない方角</b>へ置く。
        /// ⚠ 頭の向きに付いていくと、振り向いても笑いが後ろへ回り込む ＝ 装置の音になる。
        /// </summary>
        [Test]
        public void Ring_IsRelativeToHeadPositionButNotItsRotation()
        {
            var go = new GameObject("[head]");
            try
            {
                var head = go.transform;
                head.position = new Vector3(3f, 1.6f, -2f);
                head.rotation = Quaternion.Euler(0f, 137f, 0f);
                var a = SpatialAudio.Ring(head, 90f, SpatialAudio.LaughRadiusM,
                                          SpatialAudio.LaughDropM);
                head.rotation = Quaternion.Euler(0f, -41f, 0f);
                var b = SpatialAudio.Ring(head, 90f, SpatialAudio.LaughRadiusM,
                                          SpatialAudio.LaughDropM);
                Assert.That((a - b).magnitude, Is.LessThan(1e-4f), "頭の向きで場所が動いている");

                var flat = new Vector2(a.x - head.position.x, a.z - head.position.z);
                Assert.That(flat.magnitude, Is.EqualTo(SpatialAudio.LaughRadiusM).Within(1e-3f));
                Assert.That(a.y, Is.EqualTo(head.position.y - SpatialAudio.LaughDropM)
                                   .Within(1e-4f), "人形は頭より下に居る");

                head.position += new Vector3(5f, 0f, 5f);
                var c = SpatialAudio.Ring(head, 90f, SpatialAudio.LaughRadiusM,
                                          SpatialAudio.LaughDropM);
                Assert.That((c - a - new Vector3(5f, 0f, 5f)).magnitude, Is.LessThan(1e-4f),
                            "歩いても周囲に居続けること（頭の位置には付いていく）");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// 4 声は<b>90° の区画へ 1 つずつ</b>入るので、いちばん近い 2 つでも
        /// <c>90 - 2×35 = 20°</c> は空く。⚠ 一様乱数にすると重なる回ができ、
        /// その回だけ「周囲の」が成立しない（走行ログの <c>sndAz</c> と同じ判定）。
        /// </summary>
        [Test]
        public void PickLaughBearings_NeverPutsTwoDollsInTheSamePlace()
        {
            var got = new float[4];
            for (int trial = 0; trial < 400; trial++)
            {
                SpatialAudio.PickLaughBearings(got);
                for (int i = 0; i < got.Length; i++)
                {
                    Assert.That(got[i], Is.InRange(0f, 360f));
                    for (int j = i + 1; j < got.Length; j++)
                    {
                        float d = Mathf.Abs(Mathf.DeltaAngle(got[i], got[j]));
                        Assert.GreaterOrEqual(d, 20f - 1e-3f,
                                              $"{got[i]:F1}° と {got[j]:F1}° が近すぎる");
                    }
                }
            }
        }

        /// <summary>
        /// 呼びかけは<b>真後ろちょうどに置かない</b>。正中面は左右差が消えるので、
        /// 0°（正面）と 180°（真後ろ）は聞き分けの手掛かりがいちばん少ない。
        /// </summary>
        [Test]
        public void Behind_IsBehindButNotExactlyOnTheMidline()
        {
            var go = new GameObject("[head]");
            try
            {
                var head = go.transform;
                head.position = new Vector3(-1f, 1.55f, 4f);
                for (int trial = 0; trial < 200; trial++)
                {
                    head.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                    float off = SpatialAudio.PickCallOffAxisDeg();
                    Assert.That(Mathf.Abs(off), Is.InRange(SpatialAudio.CallOffAxisMinDeg,
                                                           SpatialAudio.CallOffAxisMaxDeg));
                    var at = SpatialAudio.Behind(head, off, out float az);
                    // 走行ログの判定（145〜215）と同じ窓に入っていること。
                    Assert.That(az, Is.InRange(145f, 215f));
                    // 実際に頭の後ろ側（前方ベクトルとの内積が負）。
                    var dir = (at - head.position).normalized;
                    Assert.Less(Vector3.Dot(dir, head.forward), 0f, "後ろに置けていない");
                    var flat = new Vector2(at.x - head.position.x, at.z - head.position.z);
                    Assert.That(flat.magnitude, Is.EqualTo(SpatialAudio.CallRadiusM)
                                                  .Within(1e-3f));
                }
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
