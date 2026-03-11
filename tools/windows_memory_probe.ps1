param(
  [string]$ProcessName = "SlayTheSpire2",
  [int]$Top = 40
)

$ErrorActionPreference = "Stop"

$process = Get-Process -Name $ProcessName -ErrorAction Stop
$appData = $env:APPDATA
$steamRoot = Join-Path $appData "SlayTheSpire2\\steam"

Write-Host "Process"
$process | Select-Object ProcessName, Id, Path, MainWindowTitle | Format-Table -AutoSize

Write-Host ""
Write-Host "Interesting modules"
$process.Modules |
  Where-Object {
    $_.ModuleName -match "SlayTheSpire2|sts2|Godot|coreclr|hostfxr|hostpolicy|System\\.Private\\.CoreLib|0Harmony"
  } |
  Select-Object ModuleName, FileName, BaseAddress, ModuleMemorySize |
  Sort-Object ModuleName |
  Format-Table -AutoSize

Write-Host ""
Write-Host "Top modules"
$process.Modules |
  Select-Object -First $Top ModuleName, BaseAddress, ModuleMemorySize |
  Format-Table -AutoSize

Write-Host ""
Write-Host "Save / replay files"
Get-ChildItem $steamRoot -Recurse -Depth 3 -ErrorAction SilentlyContinue |
  Where-Object {
    $_.Name -in @("current_run.save", "latest.mcr", "progress.save", "prefs.save")
  } |
  Select-Object FullName, Length, LastWriteTime |
  Sort-Object FullName |
  Format-Table -AutoSize

Write-Host ""
Write-Host "Suggested memory-reader.json module"
if ($process.Path) {
  [pscustomobject]@{
    processNames = @("$($process.ProcessName).exe", $process.ProcessName)
    moduleName = [System.IO.Path]::GetFileName($process.Path)
  } | ConvertTo-Json -Depth 3
}
