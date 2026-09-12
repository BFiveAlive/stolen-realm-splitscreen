<#
.SYNOPSIS
    Copies Stolen Realm's save folder aside before a split-screen session.

.DESCRIPTION
    Several instances share one save folder, because Unity's persistentDataPath is fixed per
    product and nothing here redirects it. In the ordinary case each player picks a different
    character and the writes land in different files - but that is an expectation, not a guarantee
    that has been stress-tested, and characters here represent a lot of play.

    Cheap insurance: one timestamped copy, kept out of the game's own folder.
#>
[CmdletBinding()]
param(
    [string] $SaveDir = "$env:USERPROFILE\AppData\LocalLow\Burst2Flame Entertainment\Stolen Realm",
    [string] $Destination = "$env:USERPROFILE\Documents\StolenRealm-SaveBackups",
    [int]    $Keep = 10
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SaveDir)) { throw "Save folder not found: $SaveDir" }

$stamp  = Get-Date -Format "yyyy-MM-dd_HHmmss"
$target = Join-Path $Destination $stamp

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $SaveDir "*") -Destination $target -Recurse -Force

$files = @(Get-ChildItem -Recurse -File $target)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("Backed up {0} files ({1:N1} MB) to {2}" -f $files.Count, ($bytes / 1MB), $target)

# Oldest first, so trimming keeps the most recent $Keep.
$all = @(Get-ChildItem -Directory $Destination | Sort-Object Name)
if ($all.Count -gt $Keep) {
    $drop = $all[0..($all.Count - $Keep - 1)]
    foreach ($d in $drop) {
        Remove-Item -Recurse -Force $d.FullName
        Write-Host ("  removed old backup " + $d.Name)
    }
}
