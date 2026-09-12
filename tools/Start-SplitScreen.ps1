<#
.SYNOPSIS
    Starts a local split-screen session of Stolen Realm.

.DESCRIPTION
    Runs one copy of the game per player, joins them into one session over the loopback address,
    and lays the windows out across the screen.

    There is no screen-splitting trick here. Each player gets a real, whole game client - its own
    camera, its own UI, its own turn - and the windows are simply placed side by side. That is why
    this works at all: Stolen Realm's interface is built around exactly one local view (one
    selected character, one skill bar, 34 window types that each exist once), so the only way to
    give two people two views is to run two of it.

    The session itself is the game's own direct-IP multiplayer, which sets Root.UsingSteam = false
    and identifies peers by a host-assigned NetworkId. Nothing here emulates or bypasses Steam.

.PARAMETER Players
    How many instances to start. 2 is side by side; 3 and 4 use a 2x2 grid.

.PARAMETER Controllers
    One entry per player, in window order: a joystick index, or "keyboard" for the
    mouse-and-keyboard seat. Defaults to keyboard for player 1 and joysticks 0, 1, 2 after it.

.PARAMETER Layout
    Auto, SideBySide, Stacked or Grid.

.PARAMETER Roguelike
    Start a roguelike run instead of the campaign.

.EXAMPLE
    .\Start-SplitScreen.ps1 -Players 2
    .\Start-SplitScreen.ps1 -Players 2 -Controllers keyboard,0
    .\Start-SplitScreen.ps1 -Players 4 -Controllers 0,1,2,3 -Roguelike
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 4)]
    [int] $Players = 2,

    [string[]] $Controllers,

    [ValidateSet("Auto", "SideBySide", "Stacked", "Grid")]
    [string] $Layout = "Auto",

    [switch] $Roguelike,

    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",

    [string] $LogDir = "$env:TEMP\stolen-realm-splitscreen",

    [int] $HostLeadSeconds = 30,

    [switch] $NoPosition
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $GameDir "Stolen Realm.exe"
if (-not (Test-Path $exe)) { throw "Stolen Realm.exe not found at $exe" }

$plugin = Join-Path $GameDir "BepInEx\plugins\SplitCoopMod\SplitCoopMod.dll"
if (-not (Test-Path $plugin)) {
    throw "SplitCoopMod is not installed. Build SplitCoopMod\SplitCoopMod.csproj first - without it the instances start but never host or join."
}

# ---------------------------------------------------------------- window placement via Win32
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class SplitWin {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
Add-Type -AssemblyName System.Windows.Forms

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host ("Screen: {0}x{1}" -f $screen.Width, $screen.Height)

if ($Layout -eq "Auto") { $Layout = if ($Players -le 2) { "SideBySide" } else { "Grid" } }

function Get-Tiles([int] $n, [string] $layout, $bounds) {
    # Each tile is an object rather than a nested array: PowerShell's comma operator binds ahead
    # of the arithmetic inside "@(a + b * c, ...)" and silently turns the operands into arrays.
    $tiles = New-Object System.Collections.Generic.List[object]

    switch ($layout) {
        "SideBySide" {
            $w = [int]($bounds.Width / $n)
            for ($i = 0; $i -lt $n; $i++) {
                $x = $bounds.X + ($i * $w)
                $tiles.Add([pscustomobject]@{ X = $x; Y = $bounds.Y; W = $w; H = $bounds.Height })
            }
        }
        "Stacked" {
            $h = [int]($bounds.Height / $n)
            for ($i = 0; $i -lt $n; $i++) {
                $y = $bounds.Y + ($i * $h)
                $tiles.Add([pscustomobject]@{ X = $bounds.X; Y = $y; W = $bounds.Width; H = $h })
            }
        }
        "Grid" {
            $cols = 2
            $rows = [int][math]::Ceiling($n / $cols)
            $w = [int]($bounds.Width / $cols)
            $h = [int]($bounds.Height / $rows)
            for ($i = 0; $i -lt $n; $i++) {
                $c = $i % $cols
                $r = [int][math]::Floor($i / $cols)
                $x = $bounds.X + ($c * $w)
                $y = $bounds.Y + ($r * $h)
                $tiles.Add([pscustomobject]@{ X = $x; Y = $y; W = $w; H = $h })
            }
        }
    }

    return $tiles
}

$tiles = Get-Tiles $Players $Layout $screen

# ---------------------------------------------------------------- controller defaults
if (-not $Controllers -or $Controllers.Count -eq 0) {
    # Player 1 on keyboard and mouse is the friendliest default: it is the seat that can drive
    # menus comfortably, and it needs no controller to be plugged in to try this at all.
    $Controllers = @("keyboard")
    for ($i = 0; $i -lt ($Players - 1); $i++) { $Controllers += "$i" }
}
# Run through "pwsh -File", -Controllers keyboard,0 arrives as the single string "keyboard,0"
# rather than as two elements, so commas are split here as well as accepted as an array.
$Controllers = @($Controllers | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })

if ($Controllers.Count -lt $Players) {
    throw "Got $($Controllers.Count) -Controllers entries for $Players players: $($Controllers -join ', ')"
}

# ---------------------------------------------------------------- launch
Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

if (Test-Path $LogDir) { Remove-Item -Recurse -Force $LogDir }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"
if (Test-Path $traceDir) { Remove-Item -Recurse -Force $traceDir -ErrorAction SilentlyContinue }

$mode = if ($Roguelike) { "roguelike" } else { "campaign" }
$started = @()

for ($i = 0; $i -lt $Players; $i++) {
    $tile = $tiles[$i]
    $isHost = ($i -eq 0)
    $label = "p$($i + 1)"

    $a = @(
        "-screen-fullscreen", "0",
        "-screen-width",  "$($tile.W)",
        "-screen-height", "$($tile.H)",
        "-popupwindow",                      # borderless, so the tiles meet without chrome
        "-monitor", "1",
        "-logFile", (Join-Path $LogDir "$label.log"),
        "-srplayer", $label,
        "-srmode", $mode,
        "-srcontroller", "$($Controllers[$i])"
    )

    $a += if ($isHost) { @("-srhost") } else { @("-srjoin", "127.0.0.1") }

    Write-Host ("[{0}/{1}] {2} - {3}, controller '{4}', {5}x{6} at {7},{8}" -f `
        ($i + 1), $Players, $label, $(if ($isHost) { "HOST" } else { "join" }),
        $Controllers[$i], $tile.W, $tile.H, $tile.X, $tile.Y)

    $p = Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $a -PassThru
    $started += [pscustomobject]@{ Player = $label; Process = $p; Tile = $tile }

    if ($isHost) {
        # The host must be listening before anyone dials it. Its own socket opens only once it
        # has reached the menu, which is most of this wait.
        Write-Host ("      waiting {0}s for the host to open its socket" -f $HostLeadSeconds)
        Start-Sleep -Seconds $HostLeadSeconds
    } else {
        Start-Sleep -Seconds 6
    }
}

# ---------------------------------------------------------------- place the windows
if (-not $NoPosition) {
    Write-Host "Placing windows..."
    Start-Sleep -Seconds 10

    $SWP_SHOWWINDOW = 0x0040
    foreach ($s in $started) {
        $s.Process.Refresh()
        $h = $s.Process.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { Write-Warning "$($s.Player): no window yet"; continue }

        [SplitWin]::ShowWindow($h, 1) | Out-Null   # SW_SHOWNORMAL, in case it came up minimised
        [SplitWin]::SetWindowPos($h, [IntPtr]::Zero,
            $s.Tile.X, $s.Tile.Y, $s.Tile.W, $s.Tile.H, $SWP_SHOWWINDOW) | Out-Null
    }
}

# ---------------------------------------------------------------- report
Write-Host ""
Write-Host "Waiting for the session to form..."
Start-Sleep -Seconds 25

Write-Host ""
if (Test-Path $traceDir) {
    foreach ($f in Get-ChildItem $traceDir -Filter *.log | Sort-Object Name) {
        $lines = Get-Content $f.FullName
        $ok   = $lines | Where-Object { $_ -match "SESSION OK" }
        $fail = $lines | Where-Object { $_ -match "FAILED" }
        $iso  = $lines | Where-Object { $_ -match "input isolated" }

        $verdict = if ($fail) { "FAILED" } elseif ($ok) { "connected" } else { "still working" }
        Write-Host ("{0,-28} {1}" -f $f.BaseName, $verdict)
        foreach ($l in @($ok) + @($fail) + @($iso)) { if ($l) { Write-Host ("    " + $l.Trim()) } }
    }
} else {
    Write-Warning "No mod traces written - is SplitCoopMod installed and enabled?"
}

Write-Host ""
Write-Host "Each window is a full game client. In the party-select screen every player picks their"
Write-Host "own character with their own controller, exactly as the game's couch co-op already works."
Write-Host ("Unity logs: {0}" -f $LogDir)
