# Checks whether the keyboard reaches a gamepad player's window.
#
# Starts two windows side by side - player 1 hosting on keyboard and mouse, player 2 joining on the
# gamepad in XInput slot 0 - waits until player 2 has its pad, then presses keys through Windows
# and reads player 2's INPUTPROBE line: how many key presses it heard, and how many reached a game
# action. Heard is expected (the keyboard is a live device); reaching an action is the bug.
#
#   -KeepKeyboard   pass -srkeepkeyboard to player 2, leaving its keyboard and mouse live, to show
#                   what happens without the guard
#
# Stops at party select; nothing is created and no save is written.
param(
    [switch] $KeepKeyboard,
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int] $PadSlot = 0
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KeyLeakInput {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
$startedAt = Get-Date

function Start-Window([string] $label, [int] $x, [string[]] $extra) {
    $args = @(
        "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "1440", "-popupwindow",
        "-logFile", "$env:TEMP\sr-keyleak-$label.log",
        "-srplayer", $label, "-srmode", "campaign"
    ) + $extra
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $args -PassThru
}

function Get-Trace([string] $label) {
    Get-ChildItem $traceDir -Filter "instance-$label-*.log" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $startedAt } | Select-Object -First 1
}

$p1 = Start-Window "p1" 0 @("-srhost", "-srcontroller", "keyboard")
Start-Sleep -Seconds 30

$p2Extra = @("-srjoin", "127.0.0.1", "-srcontroller", "xinput:$PadSlot")
if ($KeepKeyboard) { $p2Extra += "-srkeepkeyboard" }
$p2 = Start-Window "p2" 1280 $p2Extra

# Wait for player 2 to take its pad; controllers are only assigned once the game has started.
$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    Start-Sleep -Seconds 2
    $t = Get-Trace "p2"
    if ($t -and (Get-Content $t.FullName -Raw) -match "input isolated") { $ready = $true }
}
"player 2 took its pad: $ready"
Start-Sleep -Seconds 4   # a guard tick, and the session settling

# Keyboard focus on player 1, as when the keyboard player is playing. Player 2 hears the keys
# anyway: every window reads input while unfocused.
$p1.Refresh()
[KeyLeakInput]::SetForegroundWindow($p1.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500

# W A S D, Space, Tab, 1 2 3 - movement, camera, confirm and skill keys.
$keys = 0x57, 0x41, 0x53, 0x44, 0x20, 0x09, 0x31, 0x32, 0x33
for ($round = 0; $round -lt 3; $round++) {
    foreach ($vk in $keys) {
        [KeyLeakInput]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [KeyLeakInput]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
}
"sent $($keys.Count * 3) key presses"
Start-Sleep -Seconds 5

foreach ($label in "p1", "p2") {
    $t = Get-Trace $label
    "== $label " + $(if ($t) { $t.Name } else { "(no trace)" })
    if ($t) {
        Get-Content $t.FullName | Where-Object { $_ -match "input isolated|SESSION OK|keyboard and mouse switched off|could not switch off|INPUTPROBE" } |
            ForEach-Object { "  " + $_.Trim() }
    }
    $text = Get-Content "$env:TEMP\sr-keyleak-$label.log" -Raw -ErrorAction SilentlyContinue
    "  game errors: {0} NullReferenceExceptions" -f ([regex]::Matches($text, "NullReferenceException")).Count
}

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
