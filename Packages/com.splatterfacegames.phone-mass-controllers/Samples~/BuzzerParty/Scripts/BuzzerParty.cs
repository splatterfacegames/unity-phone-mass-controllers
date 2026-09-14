using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Splatter.Pmc.Demo
{
    /// <summary>
    /// Buzzer Party: the demo host scene for unity-phone-mass-controllers. Port of demo/main.gd.
    ///
    /// Shows the join QR and a live roster, runs rounds (<see cref="BuzzerGame"/>) and talks to
    /// phones through <see cref="PmcHost"/>. The phone side lives in Assets/Demo/Controller/
    /// (message formats are listed at the top of controller.js).
    ///
    /// The whole uGUI lobby is built in code, so the scene file stays a camera plus one GameObject
    /// with <see cref="PmcHost"/> + this component.
    /// </summary>
    [RequireComponent(typeof(PmcHost))]
    public sealed class BuzzerParty : MonoBehaviour
    {
        private static readonly Color BG = Hex("#101226");
        private static readonly Color PANEL = Hex("#1d2044");
        private static readonly Color PANEL_2 = Hex("#171a36");
        private static readonly Color TEXT = Hex("#f4f3ff");
        private static readonly Color DIM = Hex("#9a9cc4");
        private static readonly Color ACCENT = Hex("#ff5a5f");
        private static readonly Color GOOD = Hex("#2ec27e");
        private static readonly Color GOLD = Hex("#ffc53d");

        [Header("Host")]
        [Tooltip("The PmcHost to use. Same GameObject by default.")]
        [SerializeField] private PmcHost host;
        [Tooltip("Controller page directory (project-relative). When empty — or when the configured " +
            "directory is missing in the Editor — the demo serves <this scene's folder>/Controller, " +
            "so the imported Samples~ copy works too.")]
        [SerializeField] private string controllerDir = "";
        [Tooltip("Admin PIN for the phone 'Host controls' card. Empty = a random 4-digit PIN each launch.")]
        [SerializeField] private string adminPin = "";

        [Header("Match rules")]
        [SerializeField] private int targetScore = 5;
        [SerializeField] private int flashMs = 1400;
        [SerializeField] private int leadMs = 1500;
        [SerializeField] private int revealMs = 4000;
        [SerializeField] private int lockoutMs = 1500;
        [SerializeField] private int seed = 0;
        [SerializeField] private bool autoNext = true;

        private BuzzerGame _game;
        private long _revealUntil;
        private bool _rosterDirty = true;
        private bool _pinVisible;
        private string _seenJoinUrl = "";
        private Font _font;

        // UI
        private RawImage _qrRect;
        private Text _urlLabel;
        private Text _codeLabel;
        private Button _tunnelBtn;
        private Text _tunnelLabel;
        private Text _pinLabel;
        private Button _startBtn;
        private Text _stageTitle;
        private Text _stageSub;
        private BuzzerSymbolView _symbol;
        private Text _symbolLabel;
        private Text _rosterTitle;
        private RectTransform _roster;
        private Text _roundLabel;
        private Image _movedBanner;

        // ------------------------------------------------------------------ setup

        private void Awake()
        {
            if (host == null)
                host = GetComponent<PmcHost>();
            _game = new BuzzerGame(seed)
            {
                FlashMs = flashMs,
                LeadMs = leadMs,
                TargetScore = targetScore,
                LockoutMs = lockoutMs,
            };
            _font = LoadDefaultFont();

            PickControllerDir();
            if (string.IsNullOrEmpty(host.AdminPin))
                host.AdminPin = string.IsNullOrEmpty(adminPin) ? RandomPin() : adminPin;

            host.PlayerJoined += OnPlayerJoined;
            host.PlayerRejoined += OnPlayerRejoined;
            host.PlayerDisconnected += delegate { Changed(); };
            host.PlayerLeft += OnPlayerLeft;
            host.PlayerUpdated += delegate { Changed(); };
            host.AdminAuthenticated += delegate { Changed(); };
            host.MessageReceived += OnMessage;
            host.JoinUrlChanged += OnJoinUrlChanged;
            host.TunnelStateChanged += OnTunnelState;

            BuildUi();
        }

        private void Start()
        {
            int err = host.Running ? 0 : host.StartHost();
            if (err != 0)
            {
                Debug.LogError("Buzzer Party: host failed to start (" + err + ")");
                _stageTitle.text = "Could not open a network port";
                return;
            }
            _seenJoinUrl = host.JoinUrl();
            RefreshJoinInfo();
            Debug.Log("BUZZER_READY port=" + host.BoundPort + " url=" + host.JoinUrl() + " pin=" + host.AdminPin);
        }

        /// <summary>
        /// Picks the controller dir: the host's setting, then this component's override, then
        /// &lt;scene folder&gt;/Controller — the first that exists in the Editor, else the first
        /// non-empty one (builds serve the pre-copied StreamingAssets copy).
        /// </summary>
        private void PickControllerDir()
        {
            var candidates = new List<string> { host.ControllerDir, controllerDir, SceneDir() + "/Controller" };
            string chosen = "";
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c))
                    continue;
                if (Application.isEditor && !Directory.Exists(PmcHost.ResolveControllerDir(c)))
                    continue;
                chosen = c;
                break;
            }
            if (chosen == "")
                chosen = SceneDir() + "/Controller";
            host.ControllerDir = chosen;
        }

        private string SceneDir()
        {
            string p = gameObject.scene.path;
            if (string.IsNullOrEmpty(p))
                return "Assets/Demo";
            int i = p.LastIndexOf('/');
            return i > 0 ? p.Substring(0, i) : "Assets";
        }

#if UNITY_EDITOR
        // Runs in the Editor when the scene loads or the component changes. If the host's
        // controller dir is missing on disk — e.g. the UPM sample was imported under
        // Assets/Samples/... — repoint it at <scene folder>/Controller so the build
        // preprocessor copies the folder the game actually serves.
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += HealControllerDirInEditor;
        }

        private void HealControllerDirInEditor()
        {
            if (this == null || Application.isPlaying)
                return;
            PmcHost h = host != null ? host : GetComponent<PmcHost>();
            if (h == null)
                return;
            string fallback = SceneDir() + "/Controller";
            if (!Directory.Exists(fallback) || Directory.Exists(PmcHost.ResolveControllerDir(h.ControllerDir)))
                return;
            var so = new UnityEditor.SerializedObject(h);
            UnityEditor.SerializedProperty prop = so.FindProperty("controllerDir");
            if (prop == null || prop.stringValue == fallback)
                return;
            prop.stringValue = fallback;
            so.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(h);
        }
#endif

        private static string RandomPin()
        {
            return UnityEngine.Random.Range(0, 10000).ToString("D4");
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // ------------------------------------------------------------------ per-frame

        private void Update()
        {
            long now = NowMs();
            foreach (BuzzerGame.TickEvent ev in _game.Tick(now))
            {
                if (ev.Type == "flash")
                {
                    host.Host.Broadcast(new JObject
                    {
                        ["type"] = "flash",
                        ["round"] = _game.RoundNo,
                        ["symbol"] = ev.Symbol,
                        ["seq"] = ev.Seq,
                    });
                }
                else
                {
                    EndRound(now);
                }
            }
            if (autoNext && _game.GamePhase == BuzzerGame.Phase.Reveal && _revealUntil > 0 && now >= _revealUntil)
                StartRound();
            if (_rosterDirty)
            {
                _rosterDirty = false;
                RebuildRoster();
            }
            UpdateStage(now);

            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return))
                StartRound();
            if (Input.GetKeyDown(KeyCode.R))
                ResetMatch();
        }

        // ------------------------------------------------------------------ host events

        private void OnPlayerJoined(PmcPlayer p)
        {
            _game.AddPlayer(p.Id);
            SendSecret(p);
            Changed();
        }

        private void OnPlayerRejoined(PmcPlayer p)
        {
            _game.AddPlayer(p.Id);
            SendSecret(p);
            Changed();
        }

        private void OnPlayerLeft(PmcPlayer p, string reason)
        {
            // Timed-out players keep their score (the host remembers their token); kicked ones don't.
            if (reason == "kicked" || reason == "leave")
                _game.RemovePlayer(p.Id);
            else
                _game.Secrets.Remove(p.Id);
            Changed();
        }

        private void OnMessage(PmcPlayer p, JToken data)
        {
            if (data == null)
                return;
            if (data.Type == JTokenType.Bytes)
            {
                // Binary echo: phones use it as a latency probe.
                host.Host.Send(p, ((JValue)data).Value as byte[]);
                return;
            }
            if (data.Type != JTokenType.Object)
                return;
            var d = (JObject)data;
            switch (d.Value<string>("type"))
            {
                case "buzz":
                    OnBuzz(p, d);
                    break;
                case "admin":
                    if (!p.IsAdmin)
                        return;
                    switch (d.Value<string>("action"))
                    {
                        case "start":
                            if (_game.GamePhase != BuzzerGame.Phase.Round)
                                StartRound();
                            break;
                        case "next":
                            StartRound();
                            break;
                        case "reset":
                            ResetMatch();
                            break;
                        case "kick":
                            int id = d.Value<int?>("id") ?? -1;
                            PmcPlayer target = id != p.Id ? host.Host.GetPlayer(id) : null;
                            if (target != null)
                                host.Host.Kick(target, "Removed by the game admin");
                            break;
                    }
                    break;
            }
        }

        private void OnBuzz(PmcPlayer p, JObject data)
        {
            long now = NowMs();
            // Phones stamp their tap on the host clock (pmc.timestamp()). Credit it, but never further
            // back than the player's round-trip time — fair for remote players, safe against backdating.
            long rtt = RttMs(p);
            long at = now;
            JToken claimed = data["at"];
            if (claimed != null && (claimed.Type == JTokenType.Integer || claimed.Type == JTokenType.Float))
            {
                long t = claimed.Value<long>();
                at = t < now - rtt ? now - rtt : t > now ? now : t;
            }
            BuzzerGame.BuzzOutcome r = _game.Buzz(p.Id, at, now);
            host.Host.Send(p, new JObject
            {
                ["type"] = "buzz",
                ["result"] = ResultName(r.Result),
                ["locked_ms"] = r.LockedMs,
            });
            if (r.Result == BuzzerGame.BuzzResult.Win)
                EndRound(now);
        }

        private static string ResultName(BuzzerGame.BuzzResult r)
        {
            switch (r)
            {
                case BuzzerGame.BuzzResult.Win: return "win";
                case BuzzerGame.BuzzResult.Wrong: return "wrong";
                case BuzzerGame.BuzzResult.Locked: return "locked";
                default: return "idle";
            }
        }

        private void SendSecret(PmcPlayer p)
        {
            int idx = _game.AssignLateSecret(p.Id);
            if (idx < 0)
                return;
            BuzzerGame.SymbolDef s = BuzzerGame.Symbols[idx];
            host.Host.Send(p, new JObject
            {
                ["type"] = "secret",
                ["round"] = _game.RoundNo,
                ["for"] = p.Id,
                ["symbol"] = new JObject
                {
                    ["id"] = s.Id,
                    ["label"] = s.Label,
                    ["color"] = s.Color,
                    ["shape"] = s.Shape,
                },
            });
        }

        private void StartRound()
        {
            var ids = new List<int>();
            foreach (PmcPlayer p in host.Host.Players(false))
                ids.Add(p.Id);
            if (!_game.StartRound(ids, NowMs()))
                return;
            _revealUntil = 0;
            foreach (PmcPlayer p in host.Host.Players(false))
                SendSecret(p);
            Changed();
        }

        private void EndRound(long now)
        {
            _revealUntil = _game.GamePhase == BuzzerGame.Phase.Reveal ? now + revealMs : 0;
            Changed();
        }

        private void ResetMatch()
        {
            _game.ResetMatch();
            _revealUntil = 0;
            Changed();
        }

        private void Changed()
        {
            _rosterDirty = true;
            host.Host.Broadcast(StateJson());
        }

        private JObject StateJson()
        {
            var list = new JArray();
            foreach (PmcPlayer p in host.Host.Players(true))
            {
                list.Add(new JObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["color"] = ProfileString(p, "color", "#888888"),
                    ["emoji"] = ProfileString(p, "emoji", ""),
                    ["score"] = _game.Scores.TryGetValue(p.Id, out int s) ? s : 0,
                    ["connected"] = p.Connected,
                    ["admin"] = p.IsAdmin,
                    ["rtt"] = RttMs(p),
                });
            }
            return new JObject
            {
                ["type"] = "state",
                ["phase"] = _game.PhaseName,
                ["round"] = _game.RoundNo,
                ["target"] = _game.TargetScore,
                ["winner"] = _game.WinnerId,
                ["match_winner"] = _game.MatchWinnerId,
                ["players"] = list,
            };
        }

        /// <summary>Round-trip ms, or 0 when unknown.</summary>
        private static long RttMs(PmcPlayer p)
        {
            return p != null ? (long)Mathf.Max(0f, p.RttMs) : 0;
        }

        private static string ProfileString(PmcPlayer p, string key, string fallback)
        {
            JToken v = p != null && p.Profile != null ? p.Profile[key] : null;
            return v != null && v.Type == JTokenType.String ? v.Value<string>() : fallback;
        }

        private void OnTunnelState(string state, string url)
        {
            switch (state)
            {
                case "downloading":
                    _tunnelLabel.text = "Downloading cloudflared…";
                    break;
                case "starting":
                    _tunnelLabel.text = "Opening tunnel…";
                    break;
                case "ready":
                    _tunnelLabel.text = "Public link ready";
                    Debug.Log("BUZZER_TUNNEL url=" + host.JoinUrl());
                    _tunnelBtn.GetComponentInChildren<Text>().text = "Stop sharing";
                    break;
                case "failed":
                    _tunnelLabel.text = "Tunnel failed: " + url;
                    Debug.Log("BUZZER_TUNNEL_FAILED " + url);
                    _tunnelBtn.GetComponentInChildren<Text>().text = "Share outside LAN";
                    break;
                case "stopped":
                    _tunnelLabel.text = "LAN only";
                    _tunnelBtn.GetComponentInChildren<Text>().text = "Share outside LAN";
                    break;
            }
            _tunnelBtn.interactable = state != "downloading" && state != "starting";
            RefreshJoinInfo();
        }

        private void OnTunnelPressed()
        {
            if (_tunnelBtn.GetComponentInChildren<Text>().text == "Stop sharing")
                host.Host.StopTunnel();
            else
                host.Host.StartTunnel();
        }

        private void RefreshJoinInfo()
        {
            if (!host.Running)
                return;
            Texture2D old = _qrRect.texture as Texture2D;
            _qrRect.texture = host.QrTexture(8);
            if (old != null)
                Destroy(old);
            string url = host.JoinUrl();
            int q = url.IndexOf('?');
            if (q >= 0)
                url = url.Substring(0, q);
            if (url.StartsWith("http://", StringComparison.Ordinal))
                url = url.Substring(7);
            else if (url.StartsWith("https://", StringComparison.Ordinal))
                url = url.Substring(8);
            _urlLabel.text = url;
            string code = host.JoinCode;
            _codeLabel.text = code != "" ? "CODE  " + code : "";
            _codeLabel.enabled = code != "";
        }

        private void OnJoinUrlChanged(string url)
        {
            // The tunnel URL is ephemeral — when it changes, every scanned QR is stale. Say so loudly.
            bool changed = _seenJoinUrl != "" && url != _seenJoinUrl;
            _seenJoinUrl = url;
            RefreshJoinInfo();
            if (changed)
            {
                _movedBanner.gameObject.SetActive(true);
                Debug.Log("BUZZER_MOVED url=" + url);
            }
        }

        // ------------------------------------------------------------------ UI construction

        private void BuildUi()
        {
            EnsureEventSystem();
            var canvasGo = new GameObject("BuzzerCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            var bg = Ui("bg", canvasGo.transform, typeof(Image));
            Stretch((RectTransform)bg.transform);
            bg.GetComponent<Image>().color = BG;

            var content = Ui("content", bg.transform, typeof(HorizontalLayoutGroup));
            Stretch((RectTransform)content.transform);
            var cols = content.GetComponent<HorizontalLayoutGroup>();
            cols.padding = new RectOffset(28, 28, 28, 28);
            cols.spacing = 28;
            cols.childForceExpandHeight = true;
            cols.childForceExpandWidth = true;
            cols.childControlHeight = true;
            cols.childControlWidth = true;

            // ---- left column: title, QR, join info, controls ----
            var left = Ui("left", content.transform, typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var lv = left.GetComponent<VerticalLayoutGroup>();
            lv.spacing = 10;
            lv.childForceExpandWidth = true;
            lv.childControlWidth = true;
            lv.childControlHeight = false;
            left.GetComponent<LayoutElement>().preferredWidth = 330;

            var titleBox = Ui("title", left.transform, typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var tv = titleBox.GetComponent<VerticalLayoutGroup>();
            tv.spacing = -20;
            tv.childControlHeight = false;
            tv.childControlWidth = true;
            tv.childForceExpandHeight = false;
            tv.childForceExpandWidth = true;
            titleBox.GetComponent<LayoutElement>().preferredHeight = 96;
            Label(titleBox.transform, "BUZZER", 50, TEXT, TextAnchor.UpperLeft, FontStyle.Bold, 56);
            Label(titleBox.transform, "PARTY", 50, ACCENT, TextAnchor.UpperLeft, FontStyle.Bold, 56);

            var qrPanel = Ui("qr", left.transform, typeof(Image), typeof(LayoutElement));
            qrPanel.GetComponent<Image>().color = Color.white;
            qrPanel.GetComponent<LayoutElement>().preferredHeight = 330;
            var qrHolder = Ui("qr-img", qrPanel.transform, typeof(RawImage));
            var qrRt = (RectTransform)qrHolder.transform;
            Stretch(qrRt);
            qrRt.offsetMin = new Vector2(20f, 20f);
            qrRt.offsetMax = new Vector2(-20f, -20f);
            _qrRect = qrHolder.GetComponent<RawImage>();

            var info = Ui("info", left.transform, typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var iv = info.GetComponent<VerticalLayoutGroup>();
            iv.spacing = 0;
            iv.childControlHeight = false;
            iv.childControlWidth = true;
            iv.childForceExpandHeight = false;
            info.GetComponent<LayoutElement>().preferredHeight = 110;
            Label(info.transform, "Scan to join, or open", 16, DIM, TextAnchor.UpperLeft, FontStyle.Normal, 24);
            _urlLabel = Label(info.transform, "…", 22, TEXT, TextAnchor.UpperLeft, FontStyle.Bold, 30);
            _codeLabel = Label(info.transform, "", 30, GOLD, TextAnchor.UpperLeft, FontStyle.Bold, 40);

            var spacer = Ui("spacer", left.transform, typeof(LayoutElement));
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            var tunnelRow = Ui("tunnel", left.transform, typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var th = tunnelRow.GetComponent<HorizontalLayoutGroup>();
            th.spacing = 12;
            th.childControlHeight = false;
            th.childForceExpandWidth = true;
            tunnelRow.GetComponent<LayoutElement>().preferredHeight = 60;
            _tunnelBtn = MakeButton(tunnelRow.transform, "Share outside LAN", PANEL, OnTunnelPressed, true);
            _tunnelLabel = Label(tunnelRow.transform, "LAN only", 15, DIM, TextAnchor.MiddleLeft);
            _tunnelLabel.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;

            // ---- right column: stage, roster bar, roster ----
            var right = Ui("right", content.transform, typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var rv = right.GetComponent<VerticalLayoutGroup>();
            rv.spacing = 14;
            rv.childForceExpandWidth = true;
            rv.childControlWidth = true;
            rv.childControlHeight = false;
            right.GetComponent<LayoutElement>().flexibleWidth = 1;

            var stage = Ui("stage", right.transform, typeof(Image), typeof(LayoutElement));
            stage.GetComponent<Image>().color = PANEL_2;
            stage.GetComponent<LayoutElement>().flexibleHeight = 1;
            var stageBox = Ui("stagebox", stage.transform, typeof(VerticalLayoutGroup));
            Stretch((RectTransform)stageBox.transform);
            var sv = stageBox.GetComponent<VerticalLayoutGroup>();
            sv.childAlignment = TextAnchor.MiddleCenter;
            sv.spacing = 4;
            sv.padding = new RectOffset(24, 24, 16, 16);
            sv.childControlWidth = true;
            sv.childControlHeight = false;
            sv.childForceExpandWidth = true;

            _roundLabel = Label(stageBox.transform, "", 16, DIM, TextAnchor.MiddleCenter, FontStyle.Normal, 26);
            _stageTitle = Label(stageBox.transform, "", 38, TEXT, TextAnchor.MiddleCenter, FontStyle.Bold, 56);
            var symbolGo = Ui("symbol", stageBox.transform, typeof(BuzzerSymbolView), typeof(LayoutElement));
            var sle = symbolGo.GetComponent<LayoutElement>();
            sle.minHeight = 170;
            sle.flexibleHeight = 1;
            _symbol = symbolGo.GetComponent<BuzzerSymbolView>();
            _symbolLabel = Label(stageBox.transform, "", 30, TEXT, TextAnchor.MiddleCenter, FontStyle.Bold, 42);
            _stageSub = Label(stageBox.transform, "", 20, DIM, TextAnchor.MiddleCenter, FontStyle.Normal, 30);

            var bar = Ui("bar", right.transform, typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var bh = bar.GetComponent<HorizontalLayoutGroup>();
            bh.spacing = 10;
            bh.childControlHeight = false;
            bh.childForceExpandWidth = true;
            bh.childAlignment = TextAnchor.MiddleCenter;
            bar.GetComponent<LayoutElement>().preferredHeight = 52;
            _rosterTitle = Label(bar.transform, "PLAYERS", 16, DIM, TextAnchor.MiddleLeft);
            _rosterTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var pinBtn = MakeButton(bar.transform, "Admin PIN ••••", PANEL_2, OnPinToggle, false, 15);
            _pinLabel = pinBtn.GetComponentInChildren<Text>();
            MakeButton(bar.transform, "New match", PANEL, delegate { ResetMatch(); }, false);
            _startBtn = MakeButton(bar.transform, "Start round", ACCENT, delegate { StartRound(); }, false);
            var startTxt = _startBtn.GetComponentInChildren<Text>();
            startTxt.color = Hex("#16121a");

            var scrollGo = Ui("scroll", right.transform, typeof(ScrollRect), typeof(LayoutElement), typeof(Image));
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            var sle2 = scrollGo.GetComponent<LayoutElement>();
            sle2.minHeight = 150;
            sle2.preferredHeight = 190;
            var viewport = Ui("viewport", scrollGo.transform, typeof(RectMask2D));
            Stretch((RectTransform)viewport.transform);
            var rosterGo = Ui("roster", viewport.transform, typeof(GridLayoutGroup), typeof(ContentSizeFitter));
            var grid = rosterGo.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(220f, 74f);
            grid.spacing = new Vector2(10f, 10f);
            grid.childAlignment = TextAnchor.UpperLeft;
            rosterGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _roster = (RectTransform)rosterGo.transform;
            var sr = scrollGo.GetComponent<ScrollRect>();
            sr.content = _roster;
            sr.viewport = (RectTransform)viewport.transform;
            sr.horizontal = false;
            sr.vertical = true;
            // The grid needs to track the viewport width so wrapping follows the window.
            _roster.anchorMin = new Vector2(0f, 1f);
            _roster.anchorMax = new Vector2(1f, 1f);
            _roster.pivot = new Vector2(0.5f, 1f);
            _roster.offsetMin = new Vector2(0f, 0f);
            _roster.offsetMax = new Vector2(0f, 0f);

            // Full-width banner when the join URL moves (ephemeral tunnel). Click to dismiss.
            var bannerGo = Ui("moved", canvasGo.transform, typeof(Image), typeof(Button));
            var brt = (RectTransform)bannerGo.transform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(0.5f, 1f);
            brt.sizeDelta = new Vector2(0f, 46f);
            brt.anchoredPosition = Vector2.zero;
            _movedBanner = bannerGo.GetComponent<Image>();
            _movedBanner.color = ACCENT;
            var mbtn = bannerGo.GetComponent<Button>();
            mbtn.targetGraphic = _movedBanner;
            mbtn.onClick.AddListener(delegate { bannerGo.SetActive(false); });
            var mtxt = Label(bannerGo.transform, "JOIN LINK CHANGED — phones: re-scan the QR", 22, Hex("#16121a"), TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(mtxt.rectTransform);
            bannerGo.SetActive(false);
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(transform, false);
        }

        private void OnPinToggle()
        {
            _pinVisible = !_pinVisible;
            _pinLabel.text = _pinVisible ? "Admin PIN " + host.AdminPin : "Admin PIN ••••";
        }

        private void UpdateStage(long now)
        {
            int connected = host.Host.Players(false).Count;
            _startBtn.interactable = connected > 0 && _game.GamePhase != BuzzerGame.Phase.Round;
            switch (_game.GamePhase)
            {
                case BuzzerGame.Phase.Lobby:
                {
                    _roundLabel.text = "LOBBY";
                    _stageTitle.text = "Scan the code to join";
                    BuzzerGame.SymbolDef sym = BuzzerGame.Symbols[(int)(now / 1200 % BuzzerGame.Symbols.Length)];
                    _symbol.ShowSymbol(sym.Shape, Dimmed(Hex(sym.Color), 0.35f));
                    _symbolLabel.text = "";
                    _stageSub.text = connected == 0
                        ? "Every phone gets a secret symbol. Buzz when yours flashes up here!"
                        : connected + " player" + (connected == 1 ? "" : "s") + " in. Press Start (or Space) when everyone has joined.";
                    break;
                }
                case BuzzerGame.Phase.Round:
                {
                    _roundLabel.text = "ROUND " + _game.RoundNo;
                    if (_game.FlashSymbol < 0)
                    {
                        _stageTitle.text = "Get ready…";
                        _symbol.ClearSymbol();
                        _symbolLabel.text = "";
                        _stageSub.text = "Check your phone for your secret symbol";
                    }
                    else
                    {
                        BuzzerGame.SymbolDef sym = BuzzerGame.Symbols[_game.FlashSymbol];
                        _stageTitle.text = "Is this yours?";
                        _symbol.ShowSymbol(sym.Shape, Hex(sym.Color));
                        _symbolLabel.text = sym.Label.ToUpperInvariant();
                        _symbolLabel.color = Hex(sym.Color);
                        _stageSub.text = "Buzz only when YOUR symbol is showing";
                    }
                    break;
                }
                case BuzzerGame.Phase.Reveal:
                case BuzzerGame.Phase.Over:
                {
                    _roundLabel.text = "ROUND " + _game.RoundNo;
                    PmcPlayer w = _game.WinnerId >= 0 ? host.Host.GetPlayer(_game.WinnerId) : null;
                    if (w != null)
                    {
                        int symIdx = _game.Secrets.TryGetValue(w.Id, out int si) ? si : 0;
                        BuzzerGame.SymbolDef sym = BuzzerGame.Symbols[symIdx];
                        _symbol.ShowSymbol(sym.Shape, Hex(sym.Color));
                        _symbolLabel.text = sym.Label.ToUpperInvariant();
                        _symbolLabel.color = Hex(sym.Color);
                        string em = ProfileString(w, "emoji", "");
                        if (_game.GamePhase == BuzzerGame.Phase.Over)
                        {
                            _stageTitle.text = em + " " + w.Name + " WINS THE MATCH!";
                            _stageSub.text = "Press Start for a rematch";
                        }
                        else
                        {
                            _stageTitle.text = em + " " + w.Name + " got it!";
                        }
                    }
                    else
                    {
                        _symbol.ClearSymbol();
                        _symbolLabel.text = "";
                        _stageTitle.text = "Nobody buzzed in time";
                    }
                    if (_game.GamePhase == BuzzerGame.Phase.Reveal)
                    {
                        if (autoNext && _revealUntil > 0)
                            _stageSub.text = "Next round in " + Mathf.CeilToInt(Mathf.Max(0f, (_revealUntil - now) / 1000f)) + "…";
                        else
                            _stageSub.text = "Press Start for the next round";
                    }
                    break;
                }
            }
        }

        private void RebuildRoster()
        {
            for (int i = _roster.childCount - 1; i >= 0; i--)
                Destroy(_roster.GetChild(i).gameObject);
            List<PmcPlayer> list = host.Host.Players(true);
            list.Sort(delegate (PmcPlayer a, PmcPlayer b)
            {
                int sa = _game.Scores.TryGetValue(a.Id, out int xa) ? xa : 0;
                int sb = _game.Scores.TryGetValue(b.Id, out int xb) ? xb : 0;
                if (sa != sb)
                    return sb - sa;
                return a.Id - b.Id;
            });
            int online = 0;
            foreach (PmcPlayer p in list)
            {
                if (p.Connected)
                    online++;
                PlayerCard(p);
            }
            _rosterTitle.text = "PLAYERS  " + online + " online" +
                (list.Count == online ? "" : ", " + (list.Count - online) + " reconnecting");
        }

        private void PlayerCard(PmcPlayer p)
        {
            bool isWinner = p.Id == _game.WinnerId && _game.GamePhase != BuzzerGame.Phase.Round;
            var card = Ui("card", _roster, typeof(Image), typeof(Outline));
            var img = card.GetComponent<Image>();
            img.color = PANEL;
            var outline = card.GetComponent<Outline>();
            outline.effectColor = GOLD;
            outline.effectDistance = new Vector2(3f, -3f);
            outline.enabled = isWinner;
            var cg = card.AddComponent<CanvasGroup>();
            cg.alpha = p.Connected ? 1f : 0.45f;

            var row = Ui("row", card.transform, typeof(HorizontalLayoutGroup));
            Stretch((RectTransform)row.transform);
            var rh = row.GetComponent<HorizontalLayoutGroup>();
            rh.spacing = 10;
            rh.padding = new RectOffset(10, 10, 10, 10);
            rh.childAlignment = TextAnchor.MiddleCenter;
            rh.childControlWidth = true;
            rh.childControlHeight = false;
            rh.childForceExpandWidth = false;
            rh.childForceExpandHeight = false;

            var avatar = Ui("avatar", row.transform, typeof(Image), typeof(LayoutElement));
            avatar.GetComponent<Image>().color = Hex(ProfileString(p, "color", "#888888"));
            var ale = avatar.GetComponent<LayoutElement>();
            ale.preferredWidth = 52;
            ale.preferredHeight = 52;
            string emoji = ProfileString(p, "emoji", "");
            if (emoji == "")
                emoji = p.Name != null && p.Name.Length > 0 ? p.Name.Substring(0, 1).ToUpperInvariant() : "?";
            var avText = Label(avatar.transform, emoji, 26, TEXT, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(avText.rectTransform);

            var textCol = Ui("text", row.transform, typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var tcv = textCol.GetComponent<VerticalLayoutGroup>();
            tcv.spacing = -2;
            tcv.childControlHeight = false;
            tcv.childControlWidth = true;
            tcv.childForceExpandHeight = false;
            tcv.childForceExpandWidth = true;
            tcv.childAlignment = TextAnchor.MiddleLeft;
            textCol.GetComponent<LayoutElement>().flexibleWidth = 1;
            string name = p.Name != null && p.Name != "" ? p.Name : "Player " + p.Id;
            Label(textCol.transform, name, 18, TEXT, TextAnchor.MiddleLeft, FontStyle.Bold, 24);

            string status = p.IsAdmin ? "admin" : "connected";
            long rtt = RttMs(p);
            if (p.Connected && rtt > 0)
                status += " · " + rtt + " ms";
            if (!p.Connected)
                status = "reconnecting…"; // socket lost, grace running — distinct from "left"
            Label(textCol.transform, status, 13, p.Connected ? GOOD : DIM, TextAnchor.MiddleLeft, FontStyle.Normal, 20);

            int score = _game.Scores.TryGetValue(p.Id, out int sc) ? sc : 0;
            var scoreText = Label(row.transform, score.ToString(), 28, isWinner ? GOLD : TEXT, TextAnchor.MiddleRight, FontStyle.Bold);
            scoreText.gameObject.AddComponent<LayoutElement>().preferredWidth = 40;
        }

        // ------------------------------------------------------------------ UI helpers

        private GameObject Ui(string name, Transform parent, params Type[] comps)
        {
            var all = new List<Type> { typeof(RectTransform) };
            all.AddRange(comps);
            var go = new GameObject(name, all.ToArray());
            go.transform.SetParent(parent, false);
            return go;
        }

        private Text Label(Transform parent, string text, int size, Color color, TextAnchor anchor,
            FontStyle style = FontStyle.Normal, float preferredHeight = 0)
        {
            var go = Ui("label", parent, typeof(Text));
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = anchor;
            t.text = text;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            if (preferredHeight > 0)
            {
                var le = go.AddComponent<LayoutElement>();
                le.preferredHeight = preferredHeight;
                le.minHeight = preferredHeight;
            }
            return t;
        }

        private Button MakeButton(Transform parent, string text, Color bg, UnityEngine.Events.UnityAction onClick,
            bool flexibleWidth = false, int fontSize = 18)
        {
            var go = Ui("button-" + text, parent, typeof(Image), typeof(Button), typeof(LayoutElement));
            var img = go.GetComponent<Image>();
            img.color = bg;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1.1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.4f);
            btn.colors = colors;
            btn.onClick.AddListener(onClick);
            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = 48;
            le.minWidth = 96;
            if (flexibleWidth)
                le.flexibleWidth = 1;
            var t = Label(go.transform, text, fontSize, TEXT, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(t.rectTransform);
            return btn;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Color Dimmed(Color c, float mul)
        {
            return new Color(c.r * (1f - mul), c.g * (1f - mul), c.b * (1f - mul), c.a);
        }

        private static Color Hex(string h)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(h, out c) ? c : Color.magenta;
        }

        private static Font LoadDefaultFont()
        {
#if UNITY_2022_1_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }
    }
}
