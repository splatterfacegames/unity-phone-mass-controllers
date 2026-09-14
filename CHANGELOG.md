# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning; release notes are generated from the matching `## [x.y.z]`
section by `.github/workflows/release.yml`.

## [Unreleased]

## [0.1.0]

### Added

- Initial Unity port of godot-phone-mass-controllers — same wire protocol, same
  browser SDK (`pmc.js`/`pmc.d.ts`).
- One-port HTTP/1.1 + WebSocket host (`PmcHostCore`, engine-free) with player
  identity, rejoin tokens, grace period + tombstones, kick/ban, admin PIN budgets,
  per-address limits, Origin checks, and binary pass-through.
- `PmcHost` MonoBehaviour (Add Component → Splatter → Phone Mass Controllers),
  `PmcLiveHosts` registry, QR join texture (`QrTexture`), `PmcQueue`/`PmcVote`/
  `PmcRotation` lobby helpers.
- Cloudflare tunnels (quick + named) with cloudflared download/verify, DNS check,
  QUIC→http2 fallback, auto-restart and rolling restart.
- Editor tooling: Window → Phone Controllers dock (live host status, QR, players,
  tunnel controls, cloudflared download, test tunnel) and a build preprocessor
  that stages served dirs into `StreamingAssets/pmc/` for player builds.
- `phone-mass-controllers.unitypackage` release artifact
  (`tools/make_unitypackage.py`) and CI (dotnet + node interop + QR corpus).
