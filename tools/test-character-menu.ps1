# Opens the in-game character menu in a tall split-screen window and reports whether the attribute
# rows line up with their + buttons, with a screenshot taken from inside the game.
#
# Starts a REAL game (-srautoplay picks a character, preferring one with unspent attribute points,
# and accepts the party), so back saves up first with tools\Backup-Saves.ps1.
param(
    [int] $Width = 1280,
    [int] $Height = 1440,
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int] $Wait = 150,
    [string] $ShotDir = "$env:TEMP\sr-character-menu-shots"
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"
$label = "menu"

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $ShotDir) { Remove-Item -Recurse -Force $ShotDir -ErrorAction SilentlyContinue }

$startedAt = Get-Date
$process = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -ArgumentList @(
    "-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height", "-popupwindow",
    "-logFile", "$env:TEMP\sr-character-menu.log",
    "-srplayer", $label, "-srhost", "-srmode", "campaign", "-srcontroller", "keyboard",
    "-srautoplay", "-srshots", $ShotDir
)

Start-Sleep -Seconds $Wait

# Only this run's trace: older files are left alone rather than deleted under Program Files.
foreach ($f in Get-ChildItem $traceDir -Filter "instance-$label-*.log" -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt $startedAt }) {
    "== $($f.Name)"
    Get-Content $f.FullName |
        Where-Object { $_ -match "autoplay|Attributes panel|attribute rows|layout fitting failed|canvas 'GUI Manager'|SESSION OK" } |
        ForEach-Object { "  " + $_.Trim() }
}

$text = Get-Content "$env:TEMP\sr-character-menu.log" -Raw -ErrorAction SilentlyContinue
"== game errors: {0} NullReferenceExceptions, {1} crash reports" -f `
    ([regex]::Matches($text, "NullReferenceException")).Count, ([regex]::Matches($text, "Uploading Crash Report")).Count

"== screenshots"
Get-ChildItem $ShotDir -Filter *.png -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "  " + $_.FullName }

if ($process -and -not $process.HasExited) { $process | Stop-Process -Force }
