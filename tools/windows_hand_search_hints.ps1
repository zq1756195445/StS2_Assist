param(
  [string]$SavePath = "$env:APPDATA\SlayTheSpire2\steam\76561198818693118\profile1\saves\current_run.save"
)

$ErrorActionPreference = "Stop"

$save = Get-Content $SavePath -Raw | ConvertFrom-Json
$player = $save.players[0]
$cards = @($player.deck | ForEach-Object { $_.id })

$priority = $cards |
  Where-Object {
    $_ -notmatch 'STRIKE' -and $_ -notmatch 'DEFEND'
  } |
  Group-Object |
  Sort-Object Count, Name -Descending |
  Select-Object -ExpandProperty Name

Write-Host "Character: $($player.character_id)"
Write-Host "Deck count: $($cards.Count)"
Write-Host ""
Write-Host "High-signal search candidates:"
$priority | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "Fallback full deck ids:"
$cards | ForEach-Object { Write-Host "  $_" }
