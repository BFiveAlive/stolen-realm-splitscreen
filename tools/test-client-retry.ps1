# Exercises the client's connection retry by starting the client BEFORE the host exists.
#
# In a normal launch both instances take about the same time to reach the menu, so the host is
# always listening by the time anyone dials it and the retry path never runs. Reversing the order
# is the only way to actually test it - and untested error handling is what fails when it is needed.
param(
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm",
    [int]    $HostDelay = 45
)

$exe = Join-Path $GameDir "Stolen Realm.exe"
$traceDir = Join-Path $GameDir "BepInEx\splitcoop-logs"
$logDir = "$env:TEMP\sr-retry-test"

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $traceDir) { Remove-Item -Recurse -Force $traceDir -ErrorAction SilentlyContinue }
if (Test-Path $logDir)   { Remove-Item -Recurse -Force $logDir }
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$common = @("-screen-fullscreen","0","-screen-width","900","-screen-height","560","-monitor","1","-srmode","campaign")

Write-Output "[1/2] starting CLIENT first - nothing is listening yet"
Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (
    $common + @("-logFile", "$logDir\client.log", "-srjoin", "127.0.0.1", "-srplayer", "client")) | Out-Null

Write-Output "      waiting ${HostDelay}s so the client's first attempts must fail"
Start-Sleep -Seconds $HostDelay

Write-Output "[2/2] starting HOST"
Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (
    $common + @("-logFile", "$logDir\host.log", "-srhost", "-srplayer", "host")) | Out-Null

Write-Output "      waiting 90s for the client to retry into the session"
Start-Sleep -Seconds 90

Write-Output ""
foreach ($f in Get-ChildItem $traceDir -Filter *.log | Sort-Object Name) {
    Write-Output ("[" + $f.BaseName + "]")
    Get-Content $f.FullName |
        Where-Object { $_ -match "connecting|retry|SESSION OK|FAILED|hosting" } |
        ForEach-Object { Write-Output ("  " + $_.Trim()) }
}

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
