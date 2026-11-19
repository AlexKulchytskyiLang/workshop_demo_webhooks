<#
  Starts the whole platform with the AppHost, waits until it is ready, runs the browser tests against it,
  then shuts everything down gracefully (containers are removed; data volumes are kept).

    powershell -File demo/tests/run-e2e.ps1                 # all browser tests
    powershell -File demo/tests/run-e2e.ps1 -Grep "viewer"   # only matching tests
    powershell -File demo/tests/run-e2e.ps1 -Shots -Grep "screens"   # also writes docs/screenshots/*.png
#>
param([string]$Grep = "", [switch]$Shots, [int]$StartupTimeoutSeconds = 420)

$ErrorActionPreference = "Stop"
$demo = Split-Path -Parent $PSScriptRoot
$stopFile = Join-Path $env:TEMP ("invoicehub-stop-" + [guid]::NewGuid().ToString("N"))
$log = Join-Path $demo "docs\apphost-e2e.log"

Write-Host "Starting the platform (log: $log)"
$apphost = Start-Process -FilePath "dotnet" -PassThru -NoNewWindow `
    -ArgumentList @("run", "--project", "`"$demo\src\AppHost\InvoiceHub.AppHost`"", "--launch-profile", "http", "--", "--Demo:StopFile=`"$stopFile`"", "--Demo:PersistData=false") `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"

$exit = 1
try {
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Seconds 4
        if ($apphost.HasExited) { throw "The AppHost stopped unexpectedly. See $log" }
        $api = $false; $portal = $false
        try { $api = (Invoke-WebRequest "http://localhost:5100/health/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
        try { $portal = (Invoke-WebRequest "http://localhost:5173/" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
    } until (($api -and $portal) -or (Get-Date) -gt $deadline)
    if (-not ($api -and $portal)) { throw "The platform was not ready within $StartupTimeoutSeconds s (api=$api, portal=$portal)." }
    Write-Host "Platform is ready. Running browser tests."

    if ($Shots) { $env:SHOTS = "1"; New-Item -ItemType Directory -Force "$demo\docs\screenshots" | Out-Null }
    Push-Location "$demo\web"
    $args = @("node_modules/@playwright/test/cli.js", "test", "-c", "e2e/playwright.config.ts")
    if ($Grep) { $args += @("-g", $Grep) }
    & node @args
    $exit = $LASTEXITCODE
    Pop-Location
}
finally {
    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    if (-not $apphost.WaitForExit(120000)) { Write-Warning "Graceful stop timed out; killing the AppHost."; Stop-Process -Id $apphost.Id -Force }
    Remove-Item $stopFile -ErrorAction SilentlyContinue
}
exit $exit

