# Is the process we start the process that ends up running?
#
# A Steam game launched straight from its exe can be terminated and relaunched by Steam. That
# looks, from inside a BepInEx plugin, exactly like "Awake ran and then everything was destroyed"
# - which is the symptom being chased here.
$exe = "C:\Program Files (x86)\Steam\steamapps\common\Stolen Realm\Stolen Realm.exe"
$dir = Split-Path -Parent $exe
$trace = Join-Path $dir "BepInEx\splitcoop-logs"

Get-Process "Stolen Realm" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $trace) { Remove-Item -Recurse -Force $trace }

$p = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru `
     -ArgumentList @("-screen-fullscreen","0","-screen-width","900","-screen-height","560","-srhost","-srplayer","diag")
Write-Output ("started pid " + $p.Id)

foreach ($t in 10,25,45) {
    Start-Sleep -Seconds ($t - ([int]$last))
    $last = $t
    $all = @(Get-Process "Stolen Realm" -ErrorAction SilentlyContinue)
    $ids = ($all | ForEach-Object { $_.Id }) -join ","
    Write-Output ("t+" + $t + "s  running pids: [" + $ids + "]  original alive: " + ($all.Id -contains $p.Id))
}

Write-Output ""
Write-Output "trace files written:"
if (Test-Path $trace) { Get-ChildItem $trace | ForEach-Object { Write-Output ("  " + $_.Name) } }
else { Write-Output "  none" }
