#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 端末キャッシュの素材 URL を焼き込み（<c>sa://</c>）へ読み替える判定。
    ///
    /// ⚠⚠ **なぜ要るか**（2026-09-05）。設定の優先順位は 焼き込み &lt; 端末キャッシュ &lt; ライブ で、
    ///   キャッシュは卓から受け取った URL をそのまま保存する。だから**卓に一度でも繋いだ機は
    ///   「卓を指す相対 URL」を持ったまま再起動する**。その機を卓なしで起動すると素材が 1 つも
    ///   解決できない（実機 Quest α のキャッシュを実測: 素材 URL 39 本すべてが相対だった）。
    /// </summary>
    public class ShowAssetRemapTests
    {
        private static Dictionary<string, string> Map() => new()
        {
            ["/masks/cue_A.png"] = "sa://assets/cue_A.png",
            ["/masks/cue_A_1.png"] = "sa://assets/cue_A_1.png",
            ["/recordings/pov_0_t02_20260823_202835.mp4"] = "sa://assets/pov_0_t02_20260823_202835.mp4",
        };

        [Test]
        public void 表にある卓のURLは焼き込みへ読み替わる()
        {
            Assert.IsTrue(ShowAssetResolver.TryRemapToBaked(Map(), "/masks/cue_A.png", out string baked));
            Assert.AreEqual("sa://assets/cue_A.png", baked);
        }

        /// <summary>
        /// ⚠ ファイル名だけで突き合わせると、末尾が似た別素材が同じ行き先へ潰れる。
        /// **完全な URL をキーにする**ので、`cue_A` と `cue_A_1` は最後まで別物のまま。
        /// 潰れても落ちないので、現場では「別の cue のマスクが出ている」としか見えない。
        /// </summary>
        [Test]
        public void 名前の似た別素材が同じ行き先へ潰れない()
        {
            var map = Map();
            ShowAssetResolver.TryRemapToBaked(map, "/masks/cue_A.png", out string a);
            ShowAssetResolver.TryRemapToBaked(map, "/masks/cue_A_1.png", out string a1);
            Assert.AreEqual("sa://assets/cue_A.png", a);
            Assert.AreEqual("sa://assets/cue_A_1.png", a1);
            Assert.AreNotEqual(a, a1);
        }

        /// <summary>
        /// 最後の焼き込みより後に卓で足した素材。**触らない**（従来どおり卓を指したまま）。
        /// 卓が居れば出るし、居なければ `AbortCurrentCue` が畳んでライブ映像へ戻る既知の挙動になる。
        /// </summary>
        [Test]
        public void 焼き込みに無い素材は素通しする()
        {
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(Map(), "/captures/added_today.png", out string baked));
            Assert.AreEqual("/captures/added_today.png", baked, "false のときは元の値のまま返す");
        }

        [Test]
        public void 既に焼き込みを指している値は触らない()
        {
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(Map(), "sa://assets/cue_A.png", out string baked));
            Assert.AreEqual("sa://assets/cue_A.png", baked);
        }

        [Test]
        public void 外部の絶対URLは触らない()
        {
            const string url = "https://example.com/x.png";
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(Map(), url, out string baked));
            Assert.AreEqual(url, baked);
        }

        /// <summary>
        /// 対応表を持たない APK（焼き込みが古い / 無い）。**読み替えず、落ちもしない。**
        /// 呼び出し側（`ShowControlClient.RemapCachedAssetsToBaked`）は、この状態を警告 1 行で言う。
        /// 黙って旧挙動へ落ちるのがいちばん危ないため。
        /// </summary>
        [Test]
        public void 対応表が無いときは読み替えない()
        {
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(null, "/masks/cue_A.png", out string b1));
            Assert.AreEqual("/masks/cue_A.png", b1);
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(new Dictionary<string, string>(), "/masks/cue_A.png", out string b2));
            Assert.AreEqual("/masks/cue_A.png", b2);
        }

        [Test]
        public void 空のURLで落ちない()
        {
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(Map(), "", out string b1));
            Assert.AreEqual("", b1);
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(Map(), null, out string b2));
            Assert.AreEqual("", b2);
        }

        /// <summary>行き先が空の壊れた表。読み替えたことにしない（空 URL を掴ませない）。</summary>
        [Test]
        public void 行き先が空なら読み替えない()
        {
            var map = new Dictionary<string, string> { ["/masks/cue_A.png"] = "" };
            Assert.IsFalse(ShowAssetResolver.TryRemapToBaked(map, "/masks/cue_A.png", out string baked));
            Assert.AreEqual("/masks/cue_A.png", baked);
        }
    }
}
