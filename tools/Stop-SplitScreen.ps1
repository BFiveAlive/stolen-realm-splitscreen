<#
.SYNOPSIS
    Closes every Stolen Realm instance.

.DESCRIPTION
    Split-screen leaves several windows open and only one of them has focus, so quitting them by
    hand is fiddly. This closes the lot.

    Windows are asked to close first so the game can save and shut its socket down; anything still
    alive after the grace period is killed.
#>
[CmdletBinding()]
param([int] $GraceSeconds = 10)

$procs = @(Get-Process "Stolen Realm" -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Host "No instances running."; return }

Write-Host ("Closing {0} instance(s)..." -f $procs.Count)
foreach ($p in $procs) { $p.CloseMainWindow() | Out-Null }

$deadline = (Get-Date).AddSeconds($GraceSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if (@(Get-Process "Stolen Realm" -ErrorAction SilentlyContinue).Count -eq 0) {
        Write-Host "All instances closed."
        return
    }
}

$left = @(Get-Process "Stolen Realm" -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) {
    Write-Warning ("{0} did not close in {1}s; killing." -f $left.Count, $GraceSeconds)
    $left | Stop-Process -Force
}
Write-Host "Done."
