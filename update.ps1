# Updates the server on pc-server: gets the latest code from GitHub, checks that it builds, then restarts the server.
# If the new code doesn't build, it is put back and the old version keeps running. Server data is never touched.
param(
    # Restart even when there is nothing new (also starts a server that isn't running).
    [switch]$Force
)

# Not 'Stop': Windows PowerShell 5.1 would treat git's normal progress messages (written to stderr) as errors.
# Every git/dotnet/npm call checks $LASTEXITCODE instead, and cmdlets that must succeed use -ErrorAction Stop.
$ErrorActionPreference = 'Continue'
Set-Location -LiteralPath $PSScriptRoot -ErrorAction Stop
$project = Join-Path $PSScriptRoot 'LabGuard.Server'
$startScript = Join-Path $PSScriptRoot 'windows\start-server.ps1'
$taskName = 'Lykantra Server'
$ports = 5000, 5001

function Get-ServerProcessIds {
    Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
}

$before = (git rev-parse HEAD).Trim()
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'git pull failed; the server was not changed.' }
$after = (git rev-parse HEAD).Trim()

if ($before -eq $after -and (Get-ServerProcessIds) -and -not $Force) {
    Write-Host 'Already up to date. The server keeps running.'
    return
}

if ($before -ne $after) {
    Write-Host 'Checking that the new version builds...'
    # Built into a separate folder, so the running server isn't disturbed.
    dotnet build $project --artifacts-path (Join-Path ([IO.Path]::GetTempPath()) 'lykantra-server-build-check') -v quiet -nologo
    if ($LASTEXITCODE -ne 0) {
        git reset --keep $before
        throw 'The new version does not build, so it was not installed. The server keeps running the previous version.'
    }
}

Write-Host 'Stopping the server...'
foreach ($id in Get-ServerProcessIds) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
}
$deadline = (Get-Date).AddSeconds(30)
while ((Get-ServerProcessIds) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
if (Get-ServerProcessIds) { throw 'The server did not stop. Try again in PowerShell as administrator.' }

# The startup task ends once its server has stopped; it must not be running when it is started again.
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
}

Write-Host 'Starting the server...'
$started = $false
if ($task) {
    try {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        $started = $true
    } catch {
        Write-Host 'Could not start the startup task, so the server is started directly instead.'
    }
}
if (-not $started) {
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$startScript`""
}

# dotnet run builds first, so the first start after an update takes a little while.
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-WebRequest 'http://localhost:5000/api/health' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop | Out-Null
        Write-Host 'Server updated and running. Admin console: http://localhost:5173'
        return
    } catch {
        Start-Sleep -Seconds 3
    }
}
throw "The server didn't answer within 3 minutes. See $(Join-Path $PSScriptRoot 'logs\server.log') and server-errors.log."
