#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// DiscoveryLogic（発見表・TTL・roam・conflict・切替判定の純ロジック）の検証。
    /// 時刻は明示的に渡すので決定的。ネットワーク・Unity ライフサイクルは対象外
    /// （pinned / discoveryEnabled のゲートは <see cref="DiscoveryLogic.ShouldSwitch"/> で真理値表を固定）。
    /// </summary>
    public sealed class DiscoveryLogicTests
    {
        private static DiscoveryLogic.Entry Cam(string id, string uuid, string ip, int port = 8080)
            => new() { id = id, uuid = uuid, ip = ip, port = port, role = DiscoveryLogic.RoleCamera };

        private static DiscoveryLogic.Entry Server(string uuid, string ip, int port = 8099)
            => new() { id = "", uuid = uuid, ip = ip, port = port, role = DiscoveryLogic.RoleShowServer };

        private static DiscoveryLogic Make(float ttl = 12f)
        {
            var l = new DiscoveryLogic();
            l.SetTtl(ttl);
            return l;
        }

        [Test]
        public void Upsert_AddsEntry_ResolvesById()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            Assert.That(l.Count, Is.EqualTo(1));
            Assert.That(l.TryGetCamera("A", 1f, out var e, out bool conflict), Is.True);
            Assert.That(conflict, Is.False);
            Assert.That(e.ip, Is.EqualTo("192.168.1.10"));
            Assert.That(e.uuid, Is.EqualTo("uuid-1"));
        }

        [Test]
        public void Upsert_MissingUuid_Ignored()
        {
            var l = Make();
            l.Upsert(Cam("A", "", "192.168.1.10"), now: 0f);
            Assert.That(l.Count, Is.EqualTo(0));
        }

        [Test]
        public void Prune_RemovesExpiredEntries()
        {
            var l = Make(ttl: 10f);
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            // now=15 で TTL(10) 超過 → prune で消える。
            l.Prune(now: 15f);
            Assert.That(l.Count, Is.EqualTo(0));
            Assert.That(l.TryGetCamera("A", 15f, out _, out _), Is.False);
        }

        [Test]
        public void TryGetCamera_ExpiredEntry_NotReturnedEvenBeforePrune()
        {
            var l = Make(ttl: 10f);
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            // prune していなくても now-lastSeen>TTL は解決対象外。
            Assert.That(l.TryGetCamera("A", 11f, out _, out _), Is.False);
        }

        [Test]
        public void Roam_SameUuidNewIp_UpdatesEndpoint_NoConflict()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            // 同一機（同 uuid）が DHCP で IP を変えた。
            l.Upsert(Cam("A", "uuid-1", "192.168.1.55"), now: 5f);
            Assert.That(l.Count, Is.EqualTo(1)); // uuid キーなので増えない
            Assert.That(l.HasConflict("A", 5f), Is.False);
            Assert.That(l.TryGetCamera("A", 5f, out var e, out _), Is.True);
            Assert.That(e.ip, Is.EqualTo("192.168.1.55")); // 新 IP へ追従
        }

        [Test]
        public void Conflict_SameIdDifferentUuid_StopsResolution()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            l.Upsert(Cam("A", "uuid-2", "192.168.1.11"), now: 1f); // 別機が同 id を名乗る
            Assert.That(l.HasConflict("A", 1f), Is.True);
            Assert.That(l.TryGetCamera("A", 1f, out _, out bool conflict), Is.False);
            Assert.That(conflict, Is.True);
            // 切替候補も出さない（フラッピング禁止）。
            Assert.That(l.FindSwitchCandidate("A", "192.168.1.10", 8080, 1f).hasCandidate, Is.False);
        }

        [Test]
        public void Conflict_ClearsAfterOneUuidExpires()
        {
            var l = Make(ttl: 10f);
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            l.Upsert(Cam("A", "uuid-2", "192.168.1.11"), now: 0f);
            Assert.That(l.HasConflict("A", 0f), Is.True);
            // uuid-1 だけ更新し続け、uuid-2 は TTL 超過 → conflict 解消。
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 11f);
            Assert.That(l.HasConflict("A", 11f), Is.False);
            Assert.That(l.TryGetCamera("A", 11f, out var e, out _), Is.True);
            Assert.That(e.uuid, Is.EqualTo("uuid-1"));
        }

        [Test]
        public void FindSwitchCandidate_DifferentEndpoint_ReturnsCandidate()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.55", port: 8080), now: 0f);
            var c = l.FindSwitchCandidate("A", currentIp: "192.168.1.10", currentPort: 8080, now: 0f);
            Assert.That(c.hasCandidate, Is.True);
            Assert.That(c.ip, Is.EqualTo("192.168.1.55"));
            Assert.That(c.port, Is.EqualTo(8080));
        }

        [Test]
        public void FindSwitchCandidate_SameEndpoint_NoCandidate()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10", port: 8080), now: 0f);
            var c = l.FindSwitchCandidate("A", currentIp: "192.168.1.10", currentPort: 8080, now: 0f);
            Assert.That(c.hasCandidate, Is.False);
        }

        [Test]
        public void FindSwitchCandidate_UnknownId_NoCandidate()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            Assert.That(l.FindSwitchCandidate("B", "0.0.0.0", 8080, 0f).hasCandidate, Is.False);
        }

        [Test]
        public void TryGetShowServer_ReturnsLatestLiveServer()
        {
            var l = Make();
            l.Upsert(Server("srv-1", "192.168.1.2"), now: 0f);
            l.Upsert(Server("srv-1", "192.168.1.9"), now: 3f); // 同 PC が IP 変えた
            Assert.That(l.TryGetShowServer(3f, out string ip, out int port), Is.True);
            Assert.That(ip, Is.EqualTo("192.168.1.9"));
            Assert.That(port, Is.EqualTo(8099));
            // カメラ解決には出てこない。
            Assert.That(l.TryGetCamera("", 3f, out _, out _), Is.False);
        }

        [Test]
        public void TryGetLatestCamera_ReturnsEntryEvenOnConflict()
        {
            var l = Make();
            l.Upsert(Cam("A", "uuid-1", "192.168.1.10"), now: 0f);
            l.Upsert(Cam("A", "uuid-2", "192.168.1.11"), now: 2f);
            // HUD 用: conflict でも最新 lastSeen のエントリは取れる。
            Assert.That(l.TryGetLatestCamera("A", 2f, out var e), Is.True);
            Assert.That(e.uuid, Is.EqualTo("uuid-2"));
        }

        // ---- 切替ゲートの真理値表（断+候補+照合済み+非pin+有効 で発火）----

        [Test]
        public void ShouldSwitch_AllConditionsMet_Fires()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(
                discoveryEnabled: true, pinned: false, frameBroken: true,
                hasCandidate: true, infoVerified: true), Is.True);
        }

        [Test]
        public void ShouldSwitch_Pinned_DoesNotFire()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(true, pinned: true, true, true, true), Is.False);
        }

        [Test]
        public void ShouldSwitch_DiscoveryDisabled_DoesNotFire()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(discoveryEnabled: false, false, true, true, true), Is.False);
        }

        [Test]
        public void ShouldSwitch_NotBroken_DoesNotFire()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(true, false, frameBroken: false, true, true), Is.False);
        }

        [Test]
        public void ShouldSwitch_NoCandidate_DoesNotFire()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(true, false, true, hasCandidate: false, true), Is.False);
        }

        [Test]
        public void ShouldSwitch_InfoNotVerified_DoesNotFire()
        {
            Assert.That(DiscoveryLogic.ShouldSwitch(true, false, true, true, infoVerified: false), Is.False);
        }
    }
}
