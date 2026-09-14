@echo off
rem Fake cloudflared for PmcTunnel tests (Windows). Mode comes from PMC_FAKE_CLOUDFLARED_MODE:
rem ok | slow | no_url | error_exit | error_429_once | exit_after_ready | quic_fail |
rem unregister | unregister_recover | named_ok | named_local | rotating_url | needs_config.
rem Replays logs from logs\ to stderr.
rem error_429_once / rotating_url write a marker at %PMC_FAKE_STATE_FILE% to vary per run.
setlocal EnableDelayedExpansion
set "LOGS=%~dp0logs"
set "ARGS=%*"
rem %* keeps any quoting from the caller's cmd line; strip it so the findstr checks below
rem match the same substrings the Godot fixture saw (e.g. --config <path>, --url http://...).
set "ARGS=%ARGS:"=%"
if /i "%~1"=="--version" (
  echo cloudflared version 2025.8.1 ^(built 2025-08-01-0000 UTC^)
  exit /b 0
)
set "MODE=%PMC_FAKE_CLOUDFLARED_MODE%"
if "%MODE%"=="" set "MODE=ok"
set "HTTP2="
set "HASCFG="
set "NAMED="
echo %ARGS%| findstr /c:"--protocol http2" >nul && set "HTTP2=1"
echo %ARGS%| findstr /c:"--config " >nul && set "HASCFG=1"
echo %ARGS%| findstr /c:"run --token" >nul && set "NAMED=1"
echo %ARGS%| findstr /c:"run " >nul && set "HASRUN=1"
echo %ARGS%| findstr /c:"--no-autoupdate" >nul || goto badargs
if not defined HASRUN (
  echo %ARGS%| findstr /c:"--url http://127.0.0.1:" >nul || goto badargs
)
if "%MODE%"=="needs_config" if not defined HASCFG (
  echo ERR this fixture requires --config to be passed 1>&2
  exit /b 1
)
if "%MODE%"=="named_ok" (
  if not defined NAMED (
    echo ERR expected 'run --token' in args 1>&2
    exit /b 2
  )
  type "%LOGS%\named_registered.log" 1>&2
  goto idle
)
if "%MODE%"=="named_local" (
  if not defined HASRUN (
    echo ERR expected 'run' in args 1>&2
    exit /b 2
  )
  type "%LOGS%\named_registered.log" 1>&2
  goto idle
)
if "%MODE%"=="error_429_once" (
  if not exist "%PMC_FAKE_STATE_FILE%" (
    echo x>"%PMC_FAKE_STATE_FILE%"
    type "%LOGS%\error_429.log" 1>&2
    exit /b 1
  )
)
if "%MODE%"=="error_exit" (
  type "%LOGS%\error_429.log" 1>&2
  exit /b 1
)
if "%MODE%"=="no_url" (
  type "%LOGS%\no_url.log" 1>&2
  goto idle
)
if "%MODE%"=="quic_fail" if not defined HTTP2 (
  type "%LOGS%\quic_fail.log" 1>&2
  goto idle
)
if "%MODE%"=="rotating_url" (
  if exist "%PMC_FAKE_STATE_FILE%" (set "U=second-words-here") else (echo x>"%PMC_FAKE_STATE_FILE%" & set "U=first-words-here")
  echo 2026-09-14T09:00:00Z INF Your quick Tunnel: https://!U!.trycloudflare.com 1>&2
  type "%LOGS%\registered.log" 1>&2
  goto idle
)
if "%MODE%"=="slow" ping -n 4 127.0.0.1 >nul
type "%LOGS%\banner.log" 1>&2
if "%MODE%"=="slow" ping -n 2 127.0.0.1 >nul
type "%LOGS%\registered.log" 1>&2
if "%MODE%"=="exit_after_ready" (
  ping -n 2 127.0.0.1 >nul
  exit /b 0
)
if "%MODE%"=="unregister" (
  ping -n 3 127.0.0.1 >nul
  type "%LOGS%\unregistered.log" 1>&2
)
if "%MODE%"=="unregister_recover" (
  ping -n 3 127.0.0.1 >nul
  type "%LOGS%\unregistered.log" 1>&2
  ping -n 4 127.0.0.1 >nul
  type "%LOGS%\registered.log" 1>&2
)
:idle
for /l %%i in (1,1,120) do ping -n 2 127.0.0.1 >nul
exit /b 0
:badargs
echo ERR unexpected arguments: %* 1>&2
exit /b 2
