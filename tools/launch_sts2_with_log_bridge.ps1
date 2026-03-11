param(
  [string]$GameExe,
  [string]$BridgeAddress = "127.0.0.1:43125",
  [string]$GameLog = "$env:APPDATA\SlayTheSpire2\logs\godot.log",
  [int]$MinRefreshGapMs = 150,
  [int]$DuplicateWindowMs = 1200
)

$ErrorActionPreference = "Stop"

function Find-DefaultGameExe {
  $candidates = @(
    "C:\Program Files (x86)\Steam\steamapps\common",
    "C:\Program Files\Steam\steamapps\common",
    (Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common"),
    (Join-Path $env:ProgramFiles "Steam\steamapps\common")
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

  foreach ($root in $candidates) {
    $match = Get-ChildItem $root -Recurse -Filter "SlayTheSpire2.exe" -ErrorAction SilentlyContinue |
      Select-Object -First 1 -ExpandProperty FullName
    if ($match) {
      return $match
    }
  }

  return $null
}

function Send-RefreshEvent {
  param(
    [string]$Addr,
    [string]$Reason,
    [string]$Line
  )

  $parts = $Addr.Split(":", 2)
  if ($parts.Count -ne 2) {
    return
  }

  $client = $null
  $stream = $null
  $writer = $null

  try {
    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Connect($parts[0], [int]$parts[1])
    $stream = $client.GetStream()
    $writer = [System.IO.StreamWriter]::new($stream)
    $writer.AutoFlush = $true

    $payload = @{
      kind = "refresh"
      source = "game-log"
      trigger = @{
        typeName = "godot.log"
        methodName = $Reason
      }
      detail = $Line
    } | ConvertTo-Json -Compress

    $writer.WriteLine($payload)
  }
  catch {
    Write-Warning "Failed to send refresh event to $Addr : $($_.Exception.Message)"
  }
  finally {
    if ($writer) { $writer.Dispose() }
    if ($stream) { $stream.Dispose() }
    if ($client) { $client.Dispose() }
  }
}

function Get-RefreshReason {
  param([string]$Line)

  if ($Line -match "Player \d+ playing card ") { return "play-card" }
  if ($Line -match "Player \d+ chose cards ") { return "choose-cards" }
  if ($Line -match "Monster .+ performing move ") { return "monster-move" }
  if ($Line -match "Creating NCombatRoom with mode=ActiveCombat") { return "combat-room" }
  if ($Line -match "Continuing run with character:") { return "continue-run" }
  if ($Line -match "Quit button pressed") { return "quit" }
  return $null
}

if (-not $GameExe) {
  $GameExe = Find-DefaultGameExe
}

if (-not $GameExe -or -not (Test-Path $GameExe)) {
  throw "Game exe not found. Pass -GameExe <path to SlayTheSpire2.exe>."
}

$logDir = Split-Path $GameLog -Parent
if (-not (Test-Path $logDir)) {
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
}
if (-not (Test-Path $GameLog)) {
  New-Item -ItemType File -Path $GameLog | Out-Null
}

Write-Host "Launching with file event bridge"
Write-Host "Game:  $GameExe"
Write-Host "Log:   $GameLog"
Write-Host "Bridge: $BridgeAddress"
Write-Host "Min refresh gap: $MinRefreshGapMs ms"
Write-Host "Duplicate window: $DuplicateWindowMs ms"

$script:lastRefreshAt = [DateTime]::MinValue
$script:lastEventKey = $null
$script:lastEventAt = [DateTime]::MinValue

Start-Process -FilePath $GameExe -WorkingDirectory (Split-Path $GameExe -Parent)

Get-Content -Path $GameLog -Tail 0 -Wait | ForEach-Object {
  $line = [string]$_
  if ([string]::IsNullOrWhiteSpace($line)) {
    return
  }

  Write-Host $line

  $reason = Get-RefreshReason -Line $line
  if (-not $reason) {
    return
  }

  $now = [DateTime]::UtcNow
  $eventKey = "$reason|$line"
  $duplicateAge = ($now - $script:lastEventAt).TotalMilliseconds
  if ($script:lastEventKey -eq $eventKey -and $duplicateAge -lt $DuplicateWindowMs) {
    Write-Host "HUD refresh skipped -> duplicate $reason"
    return
  }

  $elapsed = ($now - $script:lastRefreshAt).TotalMilliseconds
  if ($elapsed -lt $MinRefreshGapMs) {
    return
  }

  $script:lastEventKey = $eventKey
  $script:lastEventAt = $now
  $script:lastRefreshAt = $now
  Write-Host "HUD refresh -> $reason"
  Send-RefreshEvent -Addr $BridgeAddress -Reason $reason -Line $line
}
