# Opens character creation and reports what it shows - the preview character's stats and where the
# attribute rows and their + buttons actually are - with screenshots from inside the game.
#
#   -Mode split   host and joiner side by side at 1280x1440, as the launcher runs them
#   -Mode native  one instance at the full screen size, for comparison
#
# -Controllers 0,1 gives each window a real gamepad, which is what rewires Rewired's players and
# is the situation that once got saved to the registry and broke every later launch. The games are
# closed gracefully at the end so the game's quit-time saves really happen, and the saved
# controller assignments are compared before and after.
#
# Stops at character creation; nothing is created and no save is written.
param(
    [ValidateSet("split", "native")] [string] $Mode = "split",
    [string[]] $Controllers = @("keyboard", "keyboard"),
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int] $Wait = 150,
    [string] $ShotDir = "$env:TEMP\sr-creation-shots"
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"
$prefsKey = "HKCU:\Software\Burst2Flame Entertainment\Stolen Realm"
$Controllers = @($Controllers | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })

function Get-Assignments {
    $item = Get-Item $prefsKey -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    $name = $item.GetValueNames() | Where-Object { $_ -like "RewiredSaveData_ControllerAssignments*" }
    if (-not $name) { return $null }
    $v = $item.GetValue($name)
    if ($v -is [byte[]]) { $v = [Text.Encoding]::ASCII.GetString($v).TrimEnd([char]0) }
    return $v
}

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
foreach ($d in @($traceDir, (Join-Path $ShotDir $Mode))) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
}

$assignmentsBefore = Get-Assignments
$started = @()

function Start-Instance([string] $label, [int] $w, [int] $h, [string] $controller, [string[]] $role) {
    $args = @(
        "-screen-fullscreen", "0", "-screen-width", "$w", "-screen-height", "$h", "-popupwindow",
        "-logFile", "$env:TEMP\sr-creation-$label.log",
        "-srplayer", $label, "-srmode", "campaign", "-srcontroller", $controller,
        "-srautoplay", "creation", "-srshots", (Join-Path $ShotDir "$Mode\$label")
    ) + $role
    $script:started += Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $args -PassThru
}

if ($Mode -eq "split") {
    Start-Instance "p1" 1280 1440 $Controllers[0] @("-srhost")
    Start-Sleep -Seconds 30
    Start-Instance "p2" 1280 1440 $Controllers[1] @("-srjoin", "127.0.0.1")
} else {
    Start-Instance "native" 2560 1440 $Controllers[0] @("-srhost")
}

Start-Sleep -Seconds $Wait

foreach ($f in Get-ChildItem $traceDir -Filter "instance-*.log" -ErrorAction SilentlyContinue | Sort-Object Name) {
    "== $($f.Name)"
    Get-Content $f.FullName | Where-Object { $_ -match "CREATION|SESSION OK|creation|autoplay|save guard|controller assignments|reserved|isolated" } |
        ForEach-Object { "  " + $_.Trim() }
}

"== game errors per window"
foreach ($label in $(if ($Mode -eq "split") { "p1", "p2" } else { "native" })) {
    $log = "$env:TEMP\sr-creation-$label.log"
    $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
    "  {0}: {1} NullReferenceExceptions, {2} crash reports" -f $label,
        ([regex]::Matches($text, "NullReferenceException")).Count,
        ([regex]::Matches($text, "Uploading Crash Report")).Count
}

"== screenshots"
Get-ChildItem (Join-Path $ShotDir $Mode) -Recurse -Filter *.png -ErrorAction SilentlyContinue |
    ForEach-Object { "  " + $_.FullName }

# Gracefully, so Unity runs its quit handlers - a forced kill would skip exactly the save being tested.
foreach ($p in $started) { if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null } }
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and ($started | Where-Object { -not $_.HasExited })) { Start-Sleep -Seconds 2 }
$forced = @($started | Where-Object { -not $_.HasExited })
$forced | Stop-Process -Force -ErrorAction SilentlyContinue
"== closed gracefully: {0} of {1}" -f ($started.Count - $forced.Count), $started.Count

$assignmentsAfter = Get-Assignments
"== saved controller assignments unchanged: " + ($assignmentsBefore -eq $assignmentsAfter)
if ($assignmentsBefore -ne $assignmentsAfter) { "  before: $assignmentsBefore"; "  after:  $assignmentsAfter" }
