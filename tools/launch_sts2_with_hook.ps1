param(
  [string]$GameExe,
  [string]$BridgeAddress = "127.0.0.1:43125",
  [string]$GameLog = "$env:APPDATA\SlayTheSpire2\logs\godot.log",
  [int]$MinRefreshGapMs = 150,
  [int]$DuplicateWindowMs = 1200
)

$target = Join-Path $PSScriptRoot "launch_sts2_with_log_bridge.ps1"
if (-not (Test-Path $target)) {
  throw "Missing log bridge launcher: $target"
}

Write-Host "launch_sts2_with_hook.ps1 is deprecated. Forwarding to launch_sts2_with_log_bridge.ps1"

& $target `
  -GameExe $GameExe `
  -BridgeAddress $BridgeAddress `
  -GameLog $GameLog `
  -MinRefreshGapMs $MinRefreshGapMs `
  -DuplicateWindowMs $DuplicateWindowMs
