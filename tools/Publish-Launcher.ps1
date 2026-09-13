<#
.SYNOPSIS
    Builds the mod and publishes the launcher as one self-contained .exe in dist\.

.DESCRIPTION
    The order matters: the launcher embeds SplitCoopMod.dll at build time and installs it into the
    game on Launch, so the mod has to be built first or the launcher ships without it.

    The result runs on a machine with no .NET installed. It is large (the runtime is inside it)
    because a player double-clicking a launcher should not first be sent off to install a runtime.
#>
[CmdletBinding()]
param(
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"

Write-Host "Building SplitCoopMod..."
dotnet build (Join-Path $repo "SplitCoopMod\SplitCoopMod.csproj") -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "The mod did not build." }

Write-Host "Publishing the launcher..."
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
dotnet publish (Join-Path $repo "Launcher\SplitScreenLauncher.csproj") -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw "The launcher did not publish." }

Get-ChildItem $dist -Filter *.pdb | Remove-Item

$exe = Get-Item (Join-Path $dist "StolenRealmSplitScreen.exe")
Write-Host ("{0}  ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB))
