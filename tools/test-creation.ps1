# Opens character creation and reports what it shows - the preview character's stats and where the
# attribute rows and their + buttons actually are - with screenshots from inside the game.
#
#   -Mode split   host and joiner side by side at 1280x1440, as the launcher runs them
#   -Mode native  one instance at the full screen size, for comparison
#
# Stops at character creation; nothing is created and no save is written.
param(
    [ValidateSet("split", "native")] [string] $Mode = "split",
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int] $Wait = 150,
    [string] $ShotDir = "$env:TEMP\sr-creation-shots"
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
foreach ($d in @($traceDir, (Join-Path $ShotDir $Mode))) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
}

function Start-Instance([string] $label, [int] $w, [int] $h, [string[]] $role) {
    $args = @(
        "-screen-fullscreen", "0", "-screen-width", "$w", "-screen-height", "$h", "-popupwindow",
        "-logFile", "$env:TEMP\sr-creation-$label.log",
        "-srplayer", $label, "-srmode", "campaign", "-srcontroller", "keyboard",
        "-srautoplay", "creation", "-srshots", (Join-Path $ShotDir "$Mode\$label")
    ) + $role
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $args | Out-Null
}

if ($Mode -eq "split") {
    Start-Instance "p1" 1280 1440 @("-srhost")
    Start-Sleep -Seconds 30
    Start-Instance "p2" 1280 1440 @("-srjoin", "127.0.0.1")
} else {
    Start-Instance "native" 2560 1440 @("-srhost")
}

Start-Sleep -Seconds $Wait

foreach ($f in Get-ChildItem $traceDir -Filter "instance-*.log" -ErrorAction SilentlyContinue | Sort-Object Name) {
    "== $($f.Name)"
    Get-Content $f.FullName | Where-Object { $_ -match "CREATION|SESSION OK|creation|autoplay|canvas" } |
        ForEach-Object { "  " + $_.Trim() }
}

"== screenshots"
Get-ChildItem (Join-Path $ShotDir $Mode) -Recurse -Filter *.png -ErrorAction SilentlyContinue |
    ForEach-Object { "  " + $_.FullName }

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
