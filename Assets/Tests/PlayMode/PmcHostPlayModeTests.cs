using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatter.Pmc.Tests
{
    /// <summary>Play-mode glue tests: autostart, per-frame polling, QR output, registry.</summary>
    public class PmcHostPlayModeTests
    {
        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.Destroy(_go);
        }

        private PmcHost NewHost()
        {
            _go = new GameObject("playmode-host");
            return _go.AddComponent<PmcHost>();
        }

        [UnityTest]
        public IEnumerator AutostartStartsTheHost()
        {
            PmcHost host = NewHost();
            host.Autostart = true;
            host.Port = 0;
            yield return null; // Start() ran on the first frame
            Assert.IsTrue(host.Running, "Autostart must call Host.Start() in Start()");
            Assert.Greater(host.BoundPort, 0);
            StringAssert.StartsWith("http", host.JoinUrl());
        }

        [UnityTest]
        public IEnumerator RegistryTracksPlayModeHost()
        {
            PmcHost host = NewHost();
            yield return null;
            Assert.IsTrue(PmcLiveHosts.All.Contains(host));
            Object.Destroy(_go);
            _go = null;
            yield return null; // destruction completes next frame
            Assert.IsFalse(PmcLiveHosts.All.Contains(host));
        }

        [UnityTest]
        public IEnumerator QrTextureDimensionsAndContents()
        {
            PmcHost host = NewHost();
            host.Port = 0;
            Assert.AreEqual(0, host.StartHost());
            Texture2D qr = null;
            try
            {
                yield return null;
                const int modulePx = 8;
                qr = host.QrTexture(modulePx);
                Assert.NotNull(qr);
                Assert.AreEqual(qr.height, qr.width, "QR is square");
                Assert.AreEqual(0, qr.width % modulePx, "size is a whole number of modules");
                Assert.GreaterOrEqual(qr.width, (21 + 8) * modulePx, "at least a v1 code + quiet zone");
                Assert.AreEqual(FilterMode.Point, qr.filterMode);
                // Quiet zone corners must be white.
                Color32[] px = qr.GetPixels32();
                Assert.AreEqual(255, px[0].r);
                // ...and there must be at least one dark module somewhere.
                bool anyDark = false;
                for (int y = 0; y < qr.height && !anyDark; y += 7)
                {
                    for (int x = 0; x < qr.width && !anyDark; x += 5)
                        anyDark = px[y * qr.width + x].r < 128;
                }
                Assert.IsTrue(anyDark, "QR has no dark modules");
            }
            finally
            {
                if (qr != null)
                    Object.Destroy(qr);
            }
        }

        [Test]
        public void QrTextureNullWhenNotRunning()
        {
            PmcHost host = NewHost();
            Assert.IsNull(host.QrTexture());
        }
    }

    public class PmcQrTextureTests
    {
        [Test]
        public void FromMatrixDimensionsAndOrientation()
        {
            // 2x2 matrix, dark modules on the diagonal.
            var m = new bool[2, 2];
            m[0, 0] = true;
            m[1, 1] = true;
            Texture2D tex = PmcQrTexture.FromMatrix(m, modulePx: 3, quietModules: 1);
            try
            {
                // total modules = 2 + 2*1 = 4 → 12 px
                Assert.AreEqual(12, tex.width);
                Assert.AreEqual(12, tex.height);
                Color32[] px = tex.GetPixels32();
                // Matrix row 0 is the TOP scanline. Module (0,0) → texture rows 6..8, cols 3..5.
                Assert.Less(px[7 * tex.width + 4].r, 128, "top-left module should be dark");
                // Module (1,1) → rows 3..5, cols 6..8.
                Assert.Less(px[4 * tex.width + 7].r, 128, "bottom-right module should be dark");
                // Light modules and quiet zone stay white.
                Assert.AreEqual(255, px[7 * tex.width + 7].r, "top-right module should be light");
                Assert.AreEqual(255, px[0].r, "quiet zone should be light");
                Assert.AreEqual(255, px[11 * tex.width + 11].r, "quiet zone should be light");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void FromMatrixRejectsBadInput()
        {
            Assert.IsNull(PmcQrTexture.FromMatrix((bool[,])null));
            Assert.IsNull(PmcQrTexture.FromMatrix(new bool[2, 2], 0));
            Assert.IsNull(PmcQrTexture.FromMatrix(new bool[0, 0]));
            Assert.IsNull(PmcQrTexture.FromMatrix(new bool[2, 3], 1, 0), "non-square matrix");
        }
    }
}
