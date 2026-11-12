# Updates the server on pc-server: gets the latest code from GitHub, checks that it builds, then restarts the server.
# If the new code doesn't build, it is put back and the old version keeps running. Server data is never touched.
param(
    # Restart even when there is nothing new (also starts a server that isn't running).
    [switch]$Force
)

# Not 'Stop': Windows PowerShell 5.1 would treat git's normal progress messages (written to stderr) as errors.
# Every git/dotnet call checks $LASTEXITCODE instead, and cmdlets that must succeed use -ErrorAction Stop.
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

# Only processes started from this folder are stopped; anything else on the ports is reported, never killed.
function Stop-Server {
    foreach ($id in Get-ServerProcessIds) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
        if ("$($process.ExecutablePath) $($process.CommandLine)" -notlike "*$PSScriptRoot*") {
            throw "Port 5000/5001 is used by another program ($($process.Name), PID $id), not by this server. Close it, then run update.ps1 again."
        }
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-ServerProcessIds) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (Get-ServerProcessIds) { throw 'The server did not stop. Try again in PowerShell as administrator.' }
}

function Undo-Pull([string]$commit) {
    git reset --keep $commit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not put the previous code back. Run 'git status' in $PSScriptRoot and send the output to the developer."
    }
}

$before = (git rev-parse HEAD).Trim()
git fetch
if ($LASTEXITCODE -ne 0) { throw 'git fetch failed (no internet?); the server was not changed.' }
$target = git rev-parse '@{u}'
if ($LASTEXITCODE -ne 0 -or -not $target) {
    throw "This folder isn't on the main branch. Run: git checkout main  then run update.ps1 again."
}
$target = $target.Trim()

# The database and PC key are ignored by git. An update must never write over them.
if ($before -ne $target) {
    # Names are passed as arguments, not piped: Windows PowerShell 5.1 adds a CR to piped lines, so nothing would match.
    $changed = @(git diff --name-only $before $target)
    $touchesData = @()
    for ($i = 0; $i -lt $changed.Count; $i += 100) {
        $batch = $changed[$i..([Math]::Min($i + 99, $changed.Count - 1))]
        $touchesData += @(git check-ignore --no-index -- $batch)
    }
    if ($touchesData) {
        throw "The update would overwrite server data ($($touchesData -join ', ')), so it was not installed. Tell the developer."
    }
}

git merge --ff-only $target
if ($LASTEXITCODE -ne 0) { throw 'git could not update the code (local changes?); the server was not changed. Run git status.' }
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
        Undo-Pull $before
        throw 'The new version does not build, so it was not installed. The server keeps running the previous version.'
    }
}

Write-Host 'Stopping the server...'
Stop-Server

# The startup task ends once its server has stopped; it must not be running when it is started again.
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        throw "The '$taskName' task is still running. Run: Stop-ScheduledTask '$taskName', then update.ps1 -Force."
    }
}

Write-Host 'Starting the server...'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')
$started = $false
if ($task) {
    try {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        $started = $true
    } catch {
        # Started directly only from a normal window, so the server never runs with administrator rights.
        if ($isAdmin) { throw "Could not start the '$taskName' task: $($_.Exception.Message)" }
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
        $reply = Invoke-WebRequest 'http://localhost:5000/api/health' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        if ($reply.Content -match '"status"\s*:\s*"ok"') {
            Write-Host 'Server updated and running. Admin console: http://localhost:5173'
            return
        }
    } catch {
        # Not up yet.
    }
    Start-Sleep -Seconds 3
}
throw "The server didn't answer within 3 minutes. See $(Join-Path $PSScriptRoot 'logs\server-errors.log') and server.log."
