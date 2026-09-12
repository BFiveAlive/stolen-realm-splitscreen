<#
.SYNOPSIS
    Lists the gamepads Rewired sees, with the index -srcontroller expects.

.DESCRIPTION
    Starts the game once, asks the mod to report what Rewired enumerated, and shuts it down again.

    Worth doing rather than guessing: the number Windows shows in "Set up USB game controllers" is
    not necessarily Rewired's index, and assigning the wrong one silently gives two players the
    same pad.
#>
[CmdletBinding()]
param(
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int]    $TimeoutSeconds = 70
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $GameDir "Stolen Realm.exe"
if (-not (Test-Path $exe)) { throw "Stolen Realm.exe not found at $exe" }

$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"
if (Test-Path $traceDir) { Remove-Item -Recurse -Force $traceDir -ErrorAction SilentlyContinue }

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "Starting the game to enumerate controllers (this takes about a minute)..."

$p = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -ArgumentList @(
    "-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "400",
    "-logFile", "$env:TEMP\sr-controller-list.log",
    "-srplayer", "list", "-srlistcontrollers"
)

$found = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3

    if (Test-Path $traceDir) {
        foreach ($f in Get-ChildItem $traceDir -Filter "instance-list-*.log" -ErrorAction SilentlyContinue) {
            $text = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
            if ($text -and $text -match "CONTROLLERS") { $found = $f.FullName; break }
        }
    }
    if ($found) { break }
}

if ($p -and -not $p.HasExited) { $p | Stop-Process -Force }

Write-Host ""
if (-not $found) {
    Write-Warning "No controller report appeared within ${TimeoutSeconds}s."
    Write-Warning "Check that SplitCoopMod is installed in BepInEx\plugins\SplitCoopMod\."
    exit 1
}

Get-Content $found | Where-Object { $_ -match "CONTROLLERS|index " } | ForEach-Object {
    Write-Host ("  " + $_.Trim())
}

Write-Host ""
Write-Host 'Use the index with -Controllers, e.g.  -Controllers keyboard,0,1'
