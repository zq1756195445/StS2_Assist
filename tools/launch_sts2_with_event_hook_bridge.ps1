param(
  [string]$GameExe,
  [string]$BridgeAddress = "127.0.0.1:43125",
  [string]$GameLog = "$env:APPDATA\SlayTheSpire2\logs\godot.log",
  [string]$HookLog = "$env:TEMP\sts2_event_hook.log",
  [int]$MinRefreshGapMs = 150,
  [int]$DuplicateWindowMs = 1200
)

$ErrorActionPreference = "Stop"

$hookDll = Join-Path $PSScriptRoot "Sts2EventHook\bin\Debug\net8.0\Sts2EventHook.dll"
if (-not (Test-Path $hookDll)) {
  throw "Missing hook assembly: $hookDll. Run 'dotnet build .\tools\Sts2EventHook\Sts2EventHook.csproj' first."
}

$target = Join-Path $PSScriptRoot "launch_sts2_with_log_bridge.ps1"
if (-not (Test-Path $target)) {
  throw "Missing log bridge launcher: $target"
}

$hookLogDir = Split-Path $HookLog -Parent
if ($hookLogDir -and -not (Test-Path $hookLogDir)) {
  New-Item -ItemType Directory -Force -Path $hookLogDir | Out-Null
}

$previousStartupHooks = $env:DOTNET_STARTUP_HOOKS
$previousHookLog = $env:STS2_HOOK_LOG
$previousBridgeAddr = $env:STS2_HUD_EVENT_BRIDGE_ADDR

try {
  $env:DOTNET_STARTUP_HOOKS = $hookDll
  $env:STS2_HOOK_LOG = $HookLog
  $env:STS2_HUD_EVENT_BRIDGE_ADDR = $BridgeAddress

  Write-Host "Launching with event hook + log bridge"
  Write-Host "Hook:   $hookDll"
  Write-Host "Log:    $HookLog"
  Write-Host "Bridge: $BridgeAddress"

  & $target `
    -GameExe $GameExe `
    -BridgeAddress $BridgeAddress `
    -GameLog $GameLog `
    -MinRefreshGapMs $MinRefreshGapMs `
    -DuplicateWindowMs $DuplicateWindowMs
}
finally {
  $env:DOTNET_STARTUP_HOOKS = $previousStartupHooks
  $env:STS2_HOOK_LOG = $previousHookLog
  $env:STS2_HUD_EVENT_BRIDGE_ADDR = $previousBridgeAddr
}
