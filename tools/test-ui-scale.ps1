# Reports how the UI scales at a given window size.
#
# Run it at the split-screen shape and at the normal one and compare: the difference is what makes
# text unreadable in a tiled window, and it is worth measuring rather than inferring, because the
# obvious lever (matchWidthOrHeight) and the real one are not always the same.
param(
    [int]    $Width  = 1280,
    [int]    $Height = 1440,
    [string] $Label  = "narrow",
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int]    $Wait = 75
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $traceDir) { Remove-Item -Recurse -Force $traceDir -ErrorAction SilentlyContinue }

# Hosting is what walks the game to the character-select screen without anyone clicking.
Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList @(
    "-screen-fullscreen","0","-screen-width","$Width","-screen-height","$Height","-monitor","1",
    "-logFile","$env:TEMP\sr-uiscale-$Label.log",
    "-srplayer",$Label,"-srhost","-srmode","campaign","-srdumpui"
) | Out-Null

Write-Output ("running at " + $Width + "x" + $Height + " (" + $Label + "), waiting " + $Wait + "s")
Start-Sleep -Seconds $Wait

foreach ($f in Get-ChildItem $traceDir -Filter "instance-$Label-*.log" -ErrorAction SilentlyContinue) {
    Get-Content $f.FullName |
        Where-Object { $_ -match "UIDUMP|SCALER|GRID|SESSION OK" } |
        ForEach-Object { Write-Output $_ }
}

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
