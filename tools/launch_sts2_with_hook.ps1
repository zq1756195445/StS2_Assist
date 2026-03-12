param(
  [string]$GameExe,
  [string]$BridgeAddress = "127.0.0.1:43125",
  [string]$GameLog = "$env:APPDATA\SlayTheSpire2\logs\godot.log",
  [string]$HookLog = "$env:TEMP\sts2_event_hook.log",
  [int]$MinRefreshGapMs = 150,
  [int]$DuplicateWindowMs = 1200
)

$target = Join-Path $PSScriptRoot "launch_sts2_with_event_hook_bridge.ps1"
if (-not (Test-Path $target)) {
  throw "Missing event hook launcher: $target"
}

Write-Host "launch_sts2_with_hook.ps1 forwards to launch_sts2_with_event_hook_bridge.ps1"

& $target `
  -GameExe $GameExe `
  -BridgeAddress $BridgeAddress `
  -GameLog $GameLog `
  -HookLog $HookLog `
  -MinRefreshGapMs $MinRefreshGapMs `
  -DuplicateWindowMs $DuplicateWindowMs
