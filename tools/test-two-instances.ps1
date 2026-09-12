# Proves the load-bearing claim: two instances of Stolen Realm on one PC, under one Steam login,
# can form a direct-IP session.
#
# Each instance gets its own Unity log via -logFile. Without that they all write to the same
# Player.log and the answer to "which one failed" is unreadable.
#
# The game's own harness reports the outcome as "MPRUN HOST READY" / "MPRUN JOIN READY", so this
# script does not judge success itself - it reads the game's verdict.
param(
    [string] $OutDir = "$env:TEMP\sr-splitcoop-test",
    [int]    $HostLead = 30,
    [int]    $Wait = 120,
    [switch] $Roguelike
)

$exe = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm\Stolen Realm.exe"
$dir = Split-Path -Parent $exe
$mode = if ($Roguelike) { "roguelike" } else { "campaign" }

if (-not (Test-Path $exe)) { Write-Output "game not found at $exe"; exit 1 }

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Also clear the mod's per-instance traces so this run's are the only ones present.
$traceDir = Join-Path $dir "BepInEx\splitcoop-logs"
if (Test-Path $traceDir) { Remove-Item -Recurse -Force $traceDir }

$common = @("-screen-fullscreen","0","-screen-width","900","-screen-height","560","-monitor","1","-srmode",$mode)

$p1Log = Join-Path $OutDir "player1.log"
$p2Log = Join-Path $OutDir "player2.log"

Write-Output "[1/4] starting HOST..."
$a1 = $common + @("-logFile",$p1Log,"-srhost","-srplayer","1")
$p1 = Start-Process -FilePath $exe -WorkingDirectory $dir -ArgumentList $a1 -PassThru

Write-Output "      waiting ${HostLead}s for the host to reach the menu and open its socket"
Start-Sleep -Seconds $HostLead

Write-Output "[2/4] starting CLIENT -> 127.0.0.1 ..."
$a2 = $common + @("-logFile",$p2Log,"-srjoin","127.0.0.1","-srplayer","2")
$p2 = Start-Process -FilePath $exe -WorkingDirectory $dir -ArgumentList $a2 -PassThru

Write-Output "[3/4] waiting ${Wait}s for the session to form"
Start-Sleep -Seconds $Wait

Write-Output "[4/4] results"
Write-Output ""

foreach ($pair in @(@("HOST",$p1Log), @("CLIENT",$p2Log))) {
    $who = $pair[0]; $log = $pair[1]
    Write-Output ("--- " + $who + " ---")
    if (-not (Test-Path $log)) { Write-Output "  no log written"; continue }

    # Copied first: Unity holds the file open, and Select-String on the live file can fail.
    $copy = "$log.copy"
    Copy-Item $log $copy -Force -ErrorAction SilentlyContinue
    $hits = Select-String -LiteralPath $copy -Pattern "MPRUN|\[AutoClient\]|Split Co-op|Connect Steam|About to create steam relay" -ErrorAction SilentlyContinue
    if ($hits) { $hits | ForEach-Object { Write-Output ("  " + $_.Line.Trim()) } }
    else { Write-Output "  (nothing matched)" }
    Write-Output ""
}

Write-Output "--- mod traces ---"
if (Test-Path $traceDir) {
    Get-ChildItem $traceDir -Filter *.log | ForEach-Object {
        Write-Output ("  [" + $_.Name + "]")
        Get-Content $_.FullName | ForEach-Object { Write-Output ("    " + $_) }
    }
} else { Write-Output "  none written" }

Write-Output ""
$alive = @(Get-Process "Stolen Realm" -ErrorAction SilentlyContinue)
Write-Output ("instances still alive: " + $alive.Count)
