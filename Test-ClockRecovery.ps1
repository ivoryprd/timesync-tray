<#
    Test-ClockRecovery.ps1

    Proves TimeSync.exe recovers from a badly wrong clock - the case that
    actually matters after an RTC power loss.

    An earlier attempt at this problem passed its verification only because the
    clock happened to be correct at the time, which hid a bug that refused every
    large correction. So this test deliberately sets the clock years off before
    starting the app.

    If the app fails, the script restores the clock itself from a monotonic
    Stopwatch, so a failed test never leaves you stranded with a wrong clock.

    Needs an elevated prompt. TimeSync.exe inherits this elevation, so no UAC
    prompt appears during the test.
#>

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This test changes the system clock and needs an elevated prompt.'
}

$exe = Join-Path $PSScriptRoot 'TimeSync.exe'
if (-not (Test-Path $exe)) { throw "TimeSync.exe not found at $exe" }

$bogus     = '2019-03-07 04:11:00'
$waitLimit = 90   # seconds to allow for recovery (retry interval is 10s)

# Start clean so the log only shows this test.
Get-Process -Name 'TimeSync' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host ''
Write-Host '=== 1. baseline ===' -ForegroundColor Cyan
Start-Process -FilePath $exe -ArgumentList '--check' -Wait -WindowStyle Hidden
Get-Content (Join-Path $PSScriptRoot 'timesync.log') -Tail 6

# Stopwatch is monotonic, so it keeps counting correctly across the clock jumps
# below. Get-Date arithmetic would not.
$watch  = [Diagnostics.Stopwatch]::StartNew()
$before = Get-Date

Write-Host ''
Write-Host "=== 2. breaking the clock (setting it to $bogus) ===" -ForegroundColor Cyan
Set-Date $bogus | Out-Null
Write-Host "clock now reads: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow

Write-Host ''
Write-Host '=== 3. starting TimeSync and waiting for it to recover ===' -ForegroundColor Cyan
$proc = Start-Process -FilePath $exe -PassThru

$recovered = $false
while ($watch.Elapsed.TotalSeconds -lt $waitLimit) {
    Start-Sleep -Seconds 3
    # The clock jumping forward is the signal we are waiting for.
    if ((Get-Date).Year -ge 2026) { $recovered = $true; break }
}

$expected = $before.Add($watch.Elapsed)
$actual   = Get-Date
$driftSec = [Math]::Abs(($actual - $expected).TotalSeconds)

Write-Host ''
Write-Host '=== 4. result ===' -ForegroundColor Cyan
Write-Host "  clock reads  : $($actual.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "  expected ~   : $($expected.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "  difference   : $([Math]::Round($driftSec,1)) s"
Write-Host "  elapsed      : $([Math]::Round($watch.Elapsed.TotalSeconds,1)) s"

Write-Host ''
Write-Host '--- app log ---' -ForegroundColor DarkGray
Get-Content (Join-Path $PSScriptRoot 'timesync.log') -Tail 20

# 60s of slack: the baseline was already a few seconds off and the sync takes a
# moment. Still nowhere near the ~7 years a failure would leave behind.
if ($recovered -and $driftSec -lt 60) {
    Write-Host ''
    Write-Host 'PASS - the app recovered the clock from a 7-year error.' -ForegroundColor Green
    Write-Host 'The tray icon is running now; quit it from the tray, or leave it.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host 'FAIL - the app did not recover the clock. Restoring it manually.' -ForegroundColor Red
if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
Set-Date $before.Add($watch.Elapsed) | Out-Null
Write-Host "clock restored to: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow
exit 1
