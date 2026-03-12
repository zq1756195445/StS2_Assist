$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$npmCommand = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source

if (-not $npmCommand) {
  $defaultNpm = "C:\Program Files\nodejs\npm.cmd"
  if (Test-Path $defaultNpm) {
    $npmCommand = $defaultNpm
  }
}

if (-not $npmCommand) {
  throw "npm.cmd was not found. Install Node.js or add npm.cmd to PATH."
}

$nodeBinDir = Split-Path -Parent $npmCommand
if ($env:PATH -notlike "*$nodeBinDir*") {
  $env:PATH = "$nodeBinDir;$env:PATH"
}

$runningHud = Get-Process spire-guide -ErrorAction SilentlyContinue
if ($runningHud) {
  Write-Host "[hud] Stopping existing spire-guide process..."
  $runningHud | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

Set-Location $repoRoot
Write-Host "[hud] Starting Tauri dev server..."
& $npmCommand run start
