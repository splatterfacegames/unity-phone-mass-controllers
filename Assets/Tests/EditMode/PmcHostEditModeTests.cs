using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Pmc.Tests
{
    /// <summary>Glue tests that don't need play mode: settings mirroring, ControllerDir
    /// resolution, the live-host registry, and the edit-mode pump path.</summary>
    public class PmcHostEditModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _objects.Clear();
        }

        private PmcHost NewHost(string name = "test-host")
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.AddComponent<PmcHost>();
        }

        private static string Pascal(string camel)
        {
            return char.ToUpperInvariant(camel[0]) + camel.Substring(1);
        }

        [Test]
        public void SerializedFieldsMirrorEveryCoreSetting()
        {
            PmcHost host = NewHost();
            // Fields handled separately below — either component-level or needing special values.
            var skip = new HashSet<string> { "controllerDir", "autostart", "autoPoll", "allowedOrigins", "tunnelExtraArgs" };
            var pushed = new List<string>();

            var so = new SerializedObject(host);
            SerializedProperty it = so.GetIterator();
            it.NextVisible(true); // m_Script
            while (it.NextVisible(false))
            {
                if (skip.Contains(it.name))
                    continue;
                switch (it.propertyType)
                {
                    case SerializedPropertyType.Integer: it.longValue = 43210; break;
                    case SerializedPropertyType.Float: it.doubleValue = 3.75; break;
                    case SerializedPropertyType.Boolean: it.boolValue = !it.boolValue; break;
                    case SerializedPropertyType.String: it.stringValue = "xyz-" + it.name; break;
                    default: continue;
                }
                pushed.Add(it.name);
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            host.ApplySettings();

            Type core = typeof(PmcHostCore);
            foreach (string field in pushed)
            {
                PropertyInfo p = core.GetProperty(Pascal(field));
                Assert.NotNull(p, "PmcHostCore has no property for serialized field '" + field + "'");
                object want;
                SerializedProperty sp = so.FindProperty(field);
                switch (sp.propertyType)
                {
                    case SerializedPropertyType.Integer: want = Convert.ChangeType(sp.longValue, p.PropertyType); break;
                    case SerializedPropertyType.Float: want = Convert.ChangeType(sp.doubleValue, p.PropertyType); break;
                    case SerializedPropertyType.Boolean: want = sp.boolValue; break;
                    default: want = sp.stringValue; break;
                }
                Assert.AreEqual(want, p.GetValue(host.Host), "field '" + field + "' was not pushed to Host." + p.Name);
            }
            // The point of the test: every mirror field got pushed. Guard against a typo'd skip list.
            Assert.GreaterOrEqual(pushed.Count, 28, "expected ~30 serialized settings fields, found " + pushed.Count);
        }

        [Test]
        public void ArrayAndListSettingsPush()
        {
            PmcHost host = NewHost();
            var so = new SerializedObject(host);

            SerializedProperty origins = so.FindProperty("allowedOrigins");
            origins.arraySize = 2;
            origins.GetArrayElementAtIndex(0).stringValue = "http://a.example:1";
            origins.GetArrayElementAtIndex(1).stringValue = "https://b.example";

            SerializedProperty extra = so.FindProperty("tunnelExtraArgs");
            extra.arraySize = 2;
            extra.GetArrayElementAtIndex(0).stringValue = "--protocol";
            extra.GetArrayElementAtIndex(1).stringValue = "http2";

            so.ApplyModifiedPropertiesWithoutUndo();
            host.ApplySettings();

            CollectionAssert.AreEqual(new[] { "http://a.example:1", "https://b.example" }, host.Host.AllowedOrigins);
            CollectionAssert.AreEqual(new[] { "--protocol", "http2" }, host.Host.TunnelExtraArgs);
        }

        [Test]
        public void ControllerDirPushesResolvedPath()
        {
            PmcHost host = NewHost();
            var so = new SerializedObject(host);
            so.FindProperty("controllerDir").stringValue = "Assets/Demo/Controller";
            so.ApplyModifiedPropertiesWithoutUndo();
            host.ApplySettings();

            string want = PmcHost.ResolveControllerDir("Assets/Demo/Controller");
            Assert.AreEqual(want, host.Host.ControllerDir);
            Assert.AreEqual(want, host.ResolvedControllerDir);
        }

        [Test]
        public void ComponentLevelSettingsStayOnComponent()
        {
            PmcHost host = NewHost();
            Assert.IsFalse(host.Autostart);
            Assert.IsTrue(host.AutoPoll);
            host.Autostart = true;
            host.AutoPoll = false;
            Assert.IsTrue(host.Autostart);
            Assert.IsFalse(host.AutoPoll);
        }

        [Test]
        public void CoreDefaultsMatchContract()
        {
            PmcHost host = NewHost();
            PmcHostCore h = host.Host;
            Assert.AreEqual(8080, h.Port);
            Assert.AreEqual(20, h.PortSearch);
            Assert.AreEqual("*", h.BindAddress);
            Assert.AreEqual("", h.JoinCode);
            Assert.AreEqual(0, h.MaxPlayers);
            Assert.AreEqual(30f, h.GraceSeconds);
            Assert.AreEqual(3600f, h.RememberSeconds);
            Assert.AreEqual(15f, h.HeartbeatSeconds);
            Assert.AreEqual(1 << 20, h.MaxMessageBytes);
            Assert.AreEqual("", h.AdminPin);
            Assert.AreEqual("", h.AdvertiseUrl);
            Assert.AreEqual(0f, h.NoJoinsHintSeconds);
            Assert.AreEqual(8f, h.IoBudgetMsec);
            Assert.AreEqual(0, h.MaxConnections);
            Assert.AreEqual(8, h.MaxConnectionsPerAddress);
            Assert.AreEqual(10, h.JoinCodeMaxFailures);
            Assert.AreEqual(20, h.AdminPinMaxFailures);
            Assert.IsFalse(h.CheckOrigin);
            Assert.AreEqual("quick", h.TunnelMode);
            Assert.IsTrue(h.TunnelVerifyDns);
            Assert.AreEqual(60f, h.TunnelReadyTimeoutSec);
            Assert.IsTrue(h.TunnelAutoRestart);
            Assert.AreEqual(2f, h.TunnelRestartDelaySec);
        }
    }

    public class PmcResolveControllerDirTests
    {
        private const string SA = "/fake/StreamingAssets";

        private static string R(string dir, bool editor)
        {
            return PmcHost.ResolveControllerDir(dir, SA, editor);
        }

        [Test]
        public void EmptyStaysEmpty()
        {
            Assert.AreEqual("", R(null, true));
            Assert.AreEqual("", R("", true));
            Assert.AreEqual("", R("   ", true).Trim());
        }

        [Test]
        public void AbsolutePassesThrough()
        {
            string abs = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/').TrimEnd('/');
            Assert.AreEqual(abs, R(abs, true));
            Assert.AreEqual(abs, R(abs, false));
        }

        [Test]
        public void UrlLikePassesThrough()
        {
            Assert.AreEqual("https://example.com/ctl", R("https://example.com/ctl", true));
        }

        [Test]
        public void StreamingAssetsPrefixMapsUnderStreamingAssetsPath()
        {
            Assert.AreEqual(SA, R("StreamingAssets", true));
            Assert.AreEqual(SA, R("StreamingAssets", false));
            Assert.AreEqual(SA + "/ctl", R("StreamingAssets/ctl", true));
            Assert.AreEqual(SA + "/ctl", R("StreamingAssets\\ctl", false));
        }

        [Test]
        public void ProjectRelativeResolvesInEditor()
        {
            Assert.AreEqual(Path.GetFullPath("Assets/Demo/Controller"), R("Assets/Demo/Controller", true));
            Assert.AreEqual(Path.GetFullPath("Packages/x/ctl"), R("Packages/x/ctl", true));
        }

        [Test]
        public void ProjectRelativeResolvesToCopiedBasenameInPlayer()
        {
            Assert.AreEqual(SA + "/pmc/Controller", R("Assets/Demo/Controller", false));
            Assert.AreEqual(SA + "/pmc/ctl", R("Packages/some.pkg/ctl/", false));
            Assert.AreEqual(SA + "/pmc/Controller", R("Assets\\Demo\\Controller", false));
        }
    }

    public class PmcLiveHostsTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _objects.Clear();
        }

        private PmcHost NewHost()
        {
            var go = new GameObject("live-host");
            _objects.Add(go);
            return go.AddComponent<PmcHost>();
        }

        [Test]
        public void EnableRegistersDisableUnregisters()
        {
            int before = PmcLiveHosts.All.Count;
            PmcHost h = NewHost();
            Assert.IsTrue(PmcLiveHosts.All.Contains(h));
            Assert.AreEqual(before + 1, PmcLiveHosts.All.Count);

            h.gameObject.SetActive(false);
            Assert.IsFalse(PmcLiveHosts.All.Contains(h));

            h.gameObject.SetActive(true);
            Assert.IsTrue(PmcLiveHosts.All.Contains(h));

            Object.DestroyImmediate(h.gameObject);
            _objects.Remove(h.gameObject);
            Assert.IsFalse(PmcLiveHosts.All.Contains(h));
            Assert.AreEqual(before, PmcLiveHosts.All.Count);
        }

        [Test]
        public void ChangedEventFiresOnRegisterAndUnregister()
        {
            int changes = 0;
            Action bump = () => changes++;
            PmcLiveHosts.Changed += bump;
            try
            {
                PmcHost h = NewHost();
                Assert.AreEqual(1, changes);
                Object.DestroyImmediate(h.gameObject);
                _objects.Remove(h.gameObject);
                Assert.AreEqual(2, changes);
            }
            finally
            {
                PmcLiveHosts.Changed -= bump;
            }
        }
    }

    public class PmcHostEditModePumpTests
    {
        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        [Test]
        public void EditModeHostStartsPollsAndServesQr()
        {
            _go = new GameObject("editmode-host");
            var host = _go.AddComponent<PmcHost>();
            Assert.NotNull(host.Host, "Host must exist right after AddComponent (Awake)");
            Assert.DoesNotThrow(() => host.Poll(), "Poll on a stopped host must be safe");

            host.Port = 0; // ephemeral
            Texture2D qr = null;
            try
            {
                Assert.AreEqual(0, host.StartHost());
                Assert.IsTrue(host.Running);
                Assert.IsTrue(PmcLiveHosts.All.Contains(host), "edit-mode host must appear in PmcLiveHosts.All");

                host.Poll(); // what the EditorApplication.update pump calls
                StringAssert.StartsWith("http", host.JoinUrl());
                Assert.Greater(host.BoundPort, 0);

                var stopped = new GameObject("never-started");
                Assert.IsNull(stopped.AddComponent<PmcHost>().QrTexture(),
                    "QrTexture must be null when not running");
                Object.DestroyImmediate(stopped);

                qr = host.QrTexture(4);
                Assert.NotNull(qr);
                Assert.AreEqual(qr.height, qr.width);
                Assert.AreEqual(0, qr.width % 4, "width must be a whole number of modules");
                Assert.AreEqual(FilterMode.Point, qr.filterMode);
            }
            finally
            {
                if (qr != null)
                    Object.DestroyImmediate(qr);
                host.StopHost();
            }
        }
    }
}
