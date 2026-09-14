using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Splatter.Pmc.Editor {

    /// <summary>
    /// Window → Phone Controllers: live status for every running <see cref="PmcHost"/>
    /// (read straight from <see cref="PmcLiveHosts.All"/> — play mode runs in-process,
    /// so no debugger channel is needed), a cloudflared downloader, a throwaway
    /// quick/named test tunnel against a tiny local page, and docs links.
    /// Port of the Godot addon's dock.gd — all IMGUI, no scene dependencies, and
    /// everything is guarded against a null/stopped host.
    /// </summary>
    public sealed class PmcDockWindow : EditorWindow {
        const double RepaintIntervalSec = 0.75;
        const int MaxPlayersListed = 24;
        const float QrSize = 168f;
        static readonly string[] ModeLabels = { "Quick (random URL)", "Named (stable URL)" };

        static readonly KeyValuePair<string, string>[] Links = {
            new KeyValuePair<string, string>("README",
                "https://github.com/splatterfacegames/unity-phone-mass-controllers#readme"),
            new KeyValuePair<string, string>("Spec: outside-LAN join",
                "https://github.com/splatterfacegames/unity-phone-mass-controllers/blob/main/SPEC.md#5-outside-lan-join-cloudflare-tunnels-quick--named"),
            new KeyValuePair<string, string>("Exporting / StreamingAssets",
                "https://github.com/splatterfacegames/unity-phone-mass-controllers/blob/main/docs/exporting.md"),
            new KeyValuePair<string, string>("Known tunnel caveats",
                "https://github.com/splatterfacegames/unity-phone-mass-controllers/issues?q=is%3Aissue+label%3Atunnel"),
            new KeyValuePair<string, string>("Cloudflare Quick Tunnels",
                "https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/"),
        };

        [MenuItem("Window/Phone Controllers")]
        public static void Open() {
            var w = GetWindow<PmcDockWindow>();
            w.titleContent = new GUIContent("Phone Controllers");
            w.minSize = new Vector2(340, 300);
            w.Show();
        }

        PmcTunnel _tunnel;
        PmcTunnelTestResponder _responder;
        GUIStyle _headingStyle;
        GUIStyle _linkStyle;
        Vector2 _scroll;
        double _nextRepaint;
        bool _dirty = true;
        double _nextPrune;

        // cloudflared
        string _binaryStatus = "";
        bool _downloading;

        // test tunnel
        int _testMode;
        string _testNamedToken = "";
        string _testNamedHost = "";
        string _testStatus = "Idle.";
        string _testUrl = "";
        Texture2D _testQr;

        sealed class HostUi {
            public bool Named;
            public string Token = "";
            public string Hostname = "";
        }
        readonly Dictionary<PmcHost, HostUi> _hostUi = new Dictionary<PmcHost, HostUi>();

        void OnEnable() {
            titleContent = new GUIContent("Phone Controllers");
            _responder = new PmcTunnelTestResponder();
            _tunnel = new PmcTunnel { AllowDownload = true };
            _tunnel.StateChanged += OnTunnelState;
            _tunnel.DownloadFinished += OnDownloadFinished;
            EditorApplication.update += Tick;
            RefreshBinaryStatus();
        }

        void OnDisable() {
            EditorApplication.update -= Tick;
            if (_tunnel != null) {
                _tunnel.StateChanged -= OnTunnelState;
                _tunnel.DownloadFinished -= OnDownloadFinished;
                try { _tunnel.Stop(); } catch (Exception) { }
                _tunnel = null;
            }
            if (_responder != null) {
                _responder.Dispose();
                _responder = null;
            }
            DestroyQr(ref _testQr);
        }

        void Tick() {
            var t = _tunnel;
            if (t != null) {
                try { t.Pump(); } catch (Exception) { }
            }
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextPrune) {
                _nextPrune = now + 5.0;
                List<PmcHost> dead = null;
                foreach (var k in _hostUi.Keys)
                    if (k == null) (dead ?? (dead = new List<PmcHost>())).Add(k);
                if (dead != null)
                    foreach (var k in dead) _hostUi.Remove(k);
            }
            if (_dirty || now >= _nextRepaint) {
                _dirty = false;
                _nextRepaint = now + RepaintIntervalSec;
                Repaint();
            }
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI() {
            if (_headingStyle == null) {
                _headingStyle = new GUIStyle(EditorStyles.boldLabel);
                _headingStyle.normal.textColor = new Color(0.56f, 0.94f, 0.6f);
                _linkStyle = new GUIStyle(EditorStyles.label);
                _linkStyle.normal.textColor = new Color(0.4f, 0.7f, 1f);
                _linkStyle.stretchWidth = false;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            CloudflaredSection();
            EditorGUILayout.Space();
            TestTunnelSection();
            EditorGUILayout.Space();
            HostsSection();
            EditorGUILayout.Space();
            DocsSection();
            EditorGUILayout.EndScrollView();
        }

        void Heading(string text) {
            EditorGUILayout.LabelField(text, _headingStyle);
        }

        void CloudflaredSection() {
            Heading("cloudflared (for players outside your network)");
            EditorGUILayout.LabelField(_binaryStatus, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope()) {
                bool unsupported = PmcTunnel.UnsupportedReason() != "";
                using (new EditorGUI.DisabledScope(unsupported || _downloading)) {
                    if (GUILayout.Button("Download cloudflared", GUILayout.Width(150)))
                        StartDownload();
                }
                if (GUILayout.Button("Show folder", GUILayout.Width(90)))
                    ShowInstallFolder();
                if (_downloading) {
                    var r = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight,
                        GUILayout.MinWidth(100));
                    float p = _tunnel != null ? _tunnel.GetDownloadProgress() : -1f;
                    EditorGUI.ProgressBar(r, Mathf.Clamp01(p), p < 0f ? "downloading…" : (p * 100f).ToString("0") + "%");
                }
            }
        }

        void TestTunnelSection() {
            Heading("Test a tunnel");
            bool live = _tunnel != null && (_tunnel.State == "starting" || _tunnel.State == "ready" ||
                _tunnel.State == "downloading" || _tunnel.State == "lost");
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(live ? "Stop" : "Test tunnel",
                        GUILayout.Width(90), GUILayout.ExpandWidth(false)))
                    OnTestPressed(live);
                _testMode = EditorGUILayout.Popup(_testMode, ModeLabels, GUILayout.Width(160));
                EditorGUILayout.LabelField(_testStatus, EditorStyles.wordWrappedMiniLabel);
            }
            if (_testMode == 1) {
                _testNamedToken = EditorGUILayout.PasswordField("Tunnel token", _testNamedToken);
                _testNamedHost = EditorGUILayout.TextField("Public hostname", _testNamedHost);
            }
            UrlRow(_testUrl, "https://<random>.trycloudflare.com");
            QrPreview(_testQr);
        }

        void HostsSection() {
            var hosts = PmcLiveHosts.All;
            Heading("Running game");
            if (hosts == null || hosts.Count == 0) {
                EditorGUILayout.LabelField(
                    "No PmcHost is running. Enter Play mode (or run one in edit mode) to see its status here.",
                    EditorStyles.wordWrappedMiniLabel);
                return;
            }
            // The registry is live (hosts join/leave during play-mode transitions);
            // draw from a snapshot so a mid-OnGUI change can't trip the loop.
            foreach (var host in new List<PmcHost>(hosts)) {
                if (host == null) continue;
                DrawHost(host);
            }
        }

        void DrawHost(PmcHost host) {
            if (!_hostUi.TryGetValue(host, out var ui))
                _hostUi[host] = ui = new HostUi();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField("PmcHost on '" + host.name + "'", EditorStyles.boldLabel);
                PmcHostCore core = null;
                try { core = host.Host; } catch (Exception) { }
                if (core == null || !core.Running) {
                    EditorGUILayout.LabelField("Not listening.", EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                List<PmcPlayer> players;
                try { players = core.Players(); } catch (Exception) { players = new List<PmcPlayer>(); }
                int connected = 0;
                foreach (var p in players) if (p.Connected) connected++;
                var tunnel = core.GetTunnel();
                string tstate = tunnel != null && tunnel.State != null ? tunnel.State : "";
                string status = "port " + core.BoundPort + " · " + connected + " player" + (connected == 1 ? "" : "s");
                if (players.Count > connected)
                    status += " (" + (players.Count - connected) + " reconnecting)";
                if (tstate != "" && tstate != "stopped" && tstate != "idle")
                    status += " · tunnel: " + tstate;
                EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);

                string joinUrl;
                try { joinUrl = core.JoinUrl(); } catch (Exception) { joinUrl = ""; }
                UrlRow(joinUrl, "http://<lan-ip>:<port>/?code=…");

                if (players.Count == 0) {
                    EditorGUILayout.LabelField("No players yet.", EditorStyles.miniLabel);
                } else {
                    int shown = 0;
                    foreach (var p in players) {
                        if (shown >= MaxPlayersListed) {
                            EditorGUILayout.LabelField("…and " + (players.Count - shown) + " more",
                                EditorStyles.miniLabel);
                            break;
                        }
                        shown++;
                        EditorGUILayout.LabelField(
                            p.Name + "  #" + p.Id + "  " + p.RttMs.ToString("0") + " ms  " +
                            (p.Connected ? "connected" : "reconnecting"), EditorStyles.miniLabel);
                    }
                }

                Texture2D qr = null;
                try { qr = host.QrTexture(); } catch (Exception) { }
                QrPreview(qr);

                EditorGUILayout.LabelField(
                    "Tunnel: " + (tstate == "" ? "idle" : tstate) +
                    (tunnel != null && !string.IsNullOrEmpty(tunnel.Url) ? " — " + tunnel.Url : ""),
                    EditorStyles.miniLabel);
                if (tunnel != null && !string.IsNullOrEmpty(tunnel.LastError))
                    EditorGUILayout.LabelField(tunnel.LastError, EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button("Quick test tunnel")) {
                        core.TunnelMode = "quick";
                        core.StartTunnel();
                    }
                    if (GUILayout.Button("Stop tunnel"))
                        core.StopTunnel();
                }
                ui.Named = EditorGUILayout.ToggleLeft("Named tunnel (stable URL)", ui.Named);
                if (ui.Named) {
                    ui.Token = EditorGUILayout.PasswordField("Tunnel token", ui.Token);
                    ui.Hostname = EditorGUILayout.TextField("Public hostname", ui.Hostname);
                    if (GUILayout.Button("Start named tunnel")) {
                        core.TunnelMode = "named";
                        core.NamedTunnelToken = ui.Token.Trim();
                        core.NamedTunnelHostname = ui.Hostname.Trim();
                        core.StartTunnel();
                    }
                }
            }
        }

        void DocsSection() {
            Heading("Docs");
            using (new EditorGUILayout.VerticalScope()) {
                using (new EditorGUILayout.HorizontalScope()) {
                    for (int i = 0; i < 3 && i < Links.Length; i++)
                        LinkButton(Links[i]);
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    for (int i = 3; i < Links.Length; i++)
                        LinkButton(Links[i]);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        void LinkButton(KeyValuePair<string, string> link) {
            if (GUILayout.Button(link.Key, _linkStyle))
                Application.OpenURL(link.Value);
            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
        }

        void UrlRow(string url, string placeholder) {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.SelectableLabel(url == "" ? placeholder : url, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUI.DisabledScope(url == "")) {
                    if (GUILayout.Button("Copy", GUILayout.Width(46)))
                        EditorGUIUtility.systemCopyBuffer = url;
                    if (GUILayout.Button("Open", GUILayout.Width(46)))
                        Application.OpenURL(url);
                }
            }
        }

        void QrPreview(Texture2D tex) {
            if (tex == null) return;
            var r = GUILayoutUtility.GetRect(QrSize, QrSize, QrSize, QrSize);
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit, true);
        }

        // ------------------------------------------------------------- behavior

        void RefreshBinaryStatus() {
            string why = PmcTunnel.UnsupportedReason();
            if (why != "") {
                _binaryStatus = why;
                return;
            }
            string path = PmcTunnel.ResolveBinary(_tunnel != null ? _tunnel.BinaryPath : "");
            _binaryStatus = path == ""
                ? "Not installed. It will be saved to " + PmcTunnel.InstallPath()
                : "Found: " + path;
        }

        void StartDownload() {
            if (_tunnel == null) return;
            _downloading = true;
            _binaryStatus = "Downloading " + PmcTunnel.AssetName() + "...";
            _tunnel.Download();
        }

        void ShowInstallFolder() {
            var dir = Path.GetDirectoryName(PmcTunnel.InstallPath());
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        void OnDownloadFinished(bool ok, string info) {
            _downloading = false;
            _binaryStatus = ok ? "Saved to " + info : "Download failed: " + info;
            _dirty = true;
        }

        void OnTestPressed(bool live) {
            if (_tunnel == null || _responder == null) return;
            if (live) {
                _tunnel.Stop();
                _responder.Close();
                return;
            }
            int port = _responder.Listen(18490);
            if (port == 0) {
                _testStatus = "Could not open a local test port.";
                return;
            }
            if (_testMode == 1) {
                _tunnel.Mode = "named";
                _tunnel.NamedToken = _testNamedToken.Trim();
                _tunnel.NamedHostname = _testNamedHost.Trim();
            } else {
                _tunnel.Mode = "quick";
            }
            int err = _tunnel.Start(port);
            if (err != 0)
                _testStatus = "Failed to start tunnel: " + _tunnel.LastError;
        }

        void OnTunnelState(string state, string detail) {
            switch (state) {
                case "downloading":
                    _downloading = true;
                    _testStatus = "Downloading cloudflared: " + detail;
                    break;
                case "starting":
                    _downloading = false;
                    _testStatus = "Starting cloudflared (the URL usually appears within 5-15 s)...";
                    break;
                case "ready":
                    _downloading = false;
                    _testStatus = "Ready. Open the URL (or scan the QR) from any network.";
                    SetTestUrl(detail);
                    break;
                case "lost":
                    _testStatus = "Tunnel lost: " + detail +
                        " (it may recover on its own, or press Stop then Test to restart)";
                    break;
                case "failed":
                    _downloading = false;
                    _testStatus = "Failed: " + detail;
                    if (_responder != null) _responder.Close();
                    SetTestUrl("");
                    break;
                case "stopped":
                    _downloading = false;
                    _testStatus = "Stopped.";
                    SetTestUrl("");
                    break;
            }
            RefreshBinaryStatus();
            _dirty = true;
        }

        void SetTestUrl(string url) {
            _testUrl = url ?? "";
            DestroyQr(ref _testQr);
            if (_testUrl == "") return;
            byte[] png;
            try { png = PmcQr.EncodePng(_testUrl, PmcQr.Ecc.M, 6, 4); }
            catch (Exception) { return; }
            if (png == null || png.Length == 0) return;
            _testQr = new Texture2D(2, 2);
            _testQr.LoadImage(png);
            _testQr.filterMode = FilterMode.Point;
        }

        static void DestroyQr(ref Texture2D tex) {
            if (tex == null) return;
            DestroyImmediate(tex);
            tex = null;
        }
    }
}
