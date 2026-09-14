#!/bin/sh
# Fake cloudflared for PmcTunnel tests (Linux/macOS). Mode comes from PMC_FAKE_CLOUDFLARED_MODE:
# ok | slow | no_url | error_exit | error_429_once | exit_after_ready | quic_fail |
# unregister | unregister_recover | named_ok | named_local | rotating_url | needs_config.
# Replays logs/ to stderr. error_429_once / rotating_url use a marker at $PMC_FAKE_STATE_FILE.
LOGS="$(dirname "$0")/logs"
if [ "$1" = "--version" ]; then
  echo "cloudflared version 2025.8.1 (built 2025-08-01-0000 UTC)"
  exit 0
fi
ARGS="$*"
MODE="${PMC_FAKE_CLOUDFLARED_MODE:-ok}"
HAS_HTTP2=""; HAS_CFG=""; NAMED=""; HASRUN=""
case "$ARGS" in *"--protocol http2"*) HAS_HTTP2=1 ;; esac
case "$ARGS" in *"--config "*) HAS_CFG=1 ;; esac
case "$ARGS" in *"run --token"*) NAMED=1 ;; esac
case "$ARGS" in *"run "*) HASRUN=1 ;; esac
case "$ARGS" in *--no-autoupdate*) ;; *) echo "ERR unexpected arguments: $ARGS" >&2; exit 2 ;; esac
if [ -z "$HASRUN" ]; then
  case "$ARGS" in *"--url http://127.0.0.1:"*) ;; *) echo "ERR unexpected arguments: $ARGS" >&2; exit 2 ;; esac
fi
idle() { i=0; while [ $i -lt 120 ]; do sleep 1; i=$((i + 1)); done; exit 0; }
if [ "$MODE" = "needs_config" ] && [ -z "$HAS_CFG" ]; then
  echo "ERR this fixture requires --config to be passed" >&2; exit 1
fi
if [ "$MODE" = "named_ok" ]; then
  [ -z "$NAMED" ] && { echo "ERR expected 'run --token' in args" >&2; exit 2; }
  cat "$LOGS/named_registered.log" >&2; idle
fi
if [ "$MODE" = "named_local" ]; then
  [ -z "$HASRUN" ] && { echo "ERR expected 'run' in args" >&2; exit 2; }
  cat "$LOGS/named_registered.log" >&2; idle
fi
if [ "$MODE" = "error_429_once" ] && [ ! -f "$PMC_FAKE_STATE_FILE" ]; then
  echo x > "$PMC_FAKE_STATE_FILE"; cat "$LOGS/error_429.log" >&2; exit 1
fi
case "$MODE" in
  error_exit) cat "$LOGS/error_429.log" >&2; exit 1 ;;
  no_url) cat "$LOGS/no_url.log" >&2; idle ;;
esac
if [ "$MODE" = "quic_fail" ] && [ -z "$HAS_HTTP2" ]; then
  cat "$LOGS/quic_fail.log" >&2; idle
fi
if [ "$MODE" = "rotating_url" ]; then
  if [ -f "$PMC_FAKE_STATE_FILE" ]; then U=second-words-here; else echo x > "$PMC_FAKE_STATE_FILE"; U=first-words-here; fi
  echo "2026-09-14T09:00:00Z INF Your quick Tunnel: https://$U.trycloudflare.com" >&2
  cat "$LOGS/registered.log" >&2
  idle
fi
[ "$MODE" = "slow" ] && sleep 3
cat "$LOGS/banner.log" >&2
[ "$MODE" = "slow" ] && sleep 1
cat "$LOGS/registered.log" >&2
if [ "$MODE" = "exit_after_ready" ]; then sleep 1; exit 0; fi
if [ "$MODE" = "unregister" ]; then sleep 2; cat "$LOGS/unregistered.log" >&2; fi
if [ "$MODE" = "unregister_recover" ]; then sleep 2; cat "$LOGS/unregistered.log" >&2; sleep 3; cat "$LOGS/registered.log" >&2; fi
idle
