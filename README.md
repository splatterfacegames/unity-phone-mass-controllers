# unity-phone-mass-controllers

A pure-C# Unity package that turns your Unity game into the host for a room full of phone
controllers — Jackbox-style. Players scan a QR code, their phone's browser loads a controller
page served **by the game itself**, and they connect over WebSocket. No app install, no Node,
no external server.

Unity port of [godot-phone-mass-controllers](https://github.com/splatterfacegames/godot-phone-mass-controllers)
— same wire protocol, same browser SDK. Docs site: **https://pmc-unity.jethachan.net**
(Godot version: https://pmc.jethachan.net).

## Features

- One-port HTTP + WebSocket (RFC 6455) host — the game serves the controller page itself
- Player model: ids, rejoin tokens, grace period, tombstones, kick/ban, tab-replacement
- JSON messages + raw binary pass-through (FlatBuffers-friendly)
- QR join URL — LAN auto-detect, or one-click **Cloudflare tunnel** (quick + named)
- Join codes, admin PIN with brute-force budgets, per-IP limits, Origin checks
- RTT measurement + epoch-ms clock sync for timing/fairness games
- Browser SDK `pmc.js`: reconnect, clock sync, `feedback()` haptics, `keepScreenOn()`
- Lobby helpers: queue, vote (majority/veto/timeout), rotation (winner/loser/strict)
- Editor dock with live player list + QR, works in Play mode and edit mode
- Pure C# — no native binaries; core compiles without UnityEngine (`dotnet test` in CI)

## Install

**UPM (recommended):** Package Manager → `+` → *Add package from git URL…*

```
https://github.com/splatterfacegames/unity-phone-mass-controllers.git?path=/Packages/com.splatterfacegames.phone-mass-controllers
```

**unitypackage:** download `phone-mass-controllers.unitypackage` from
[Releases](https://github.com/splatterfacegames/unity-phone-mass-controllers/releases) → Assets → Import Package.

## Quickstart

Add a `PmcHost` to a GameObject (*Add Component → Splatter → Phone Mass Controllers*), set
**Controller Dir** to your controller page folder (e.g. `Assets/MyGame/Controller`), tick
**Autostart** — or do it in code:

```csharp
using Splatter.Pmc;
using UnityEngine;
using UnityEngine.UI;

public class Party : MonoBehaviour {
    [SerializeField] PmcHost host;      // or GetComponent<PmcHost>() — same GameObject
    [SerializeField] RawImage qrImage;  // on your lobby canvas

    void Awake() {
        host.ControllerDir = "Assets/MyGame/Controller";
        // Same events as PmcHostCore — Host exposes the full surface too:
        host.PlayerJoined += p => Debug.Log(p.Name + " joined");
        host.MessageReceived += OnMessage;
    }
    void Start() {
        if (!host.Running) host.StartHost();
        qrImage.texture = host.QrTexture();   // null until the host is Running
    }
    void OnMessage(PmcPlayer p, Newtonsoft.Json.Linq.JToken data) { /* … */ }
}
```

**ControllerDir resolution** (the build preprocessor copies project folders into the player):

- `Assets/...` or `Packages/...` — Editor: resolved against the project; player build:
  `StreamingAssets/pmc/<folder name>` (the preprocessor copies it there before the build).
- `StreamingAssets/...` — resolved under `Application.streamingAssetsPath` as-is.
- absolute path (or `scheme://…`) — used verbatim.

All host events fire on the main thread inside `PmcHost.Poll()` (called from `Update()` — or
`EditorApplication.update` in edit mode, so hosts can run without entering Play). Find running
hosts via `PmcLiveHosts.All`.

Controller pages are plain HTML/JS importing `pmc.js` (served by the host at `/pmc/pmc.js`):

```html
<script type="module">
  import { connect, feedback, keepScreenOn } from '/pmc/pmc.js';
  const pmc = connect({ name: 'Player' });
  pmc.on('message', (d) => { /* game → phone */ });
  buzzer.onclick = () => { pmc.send({ type: 'buzz', at: pmc.timestamp() }); feedback('buzz'); };
</script>
```

See the **Buzzer Party** sample (Package Manager → Samples) and [SPEC.md](SPEC.md).

## Requirements

- Unity **2021.3+** (developed on Unity 6 / 6000.3.24f1)
- `com.unity.nuget.newtonsoft-json` (declared as a UPM dependency)
- Tunnel feature spawns `cloudflared` — desktop platforms only (Windows/macOS/Linux)

## License

MIT — see [LICENSE](LICENSE).
