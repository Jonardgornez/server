# Starts the server in the background, the same as `dotnet run` in the LabGuard.Server folder, with its output in logs\.
# Used by the "Lykantra Server" startup task and by update.ps1. Does nothing if the server is already running.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'LabGuard.Server'
$logs = Join-Path $repo 'logs'

if (Get-NetTCPConnection -State Listen -LocalPort 5000, 5001 -ErrorAction SilentlyContinue) {
    Write-Host 'The server is already running.'
    return
}

# The previous run's logs are kept once, as *.previous.log.
New-Item -ItemType Directory -Force -Path $logs | Out-Null
foreach ($name in 'server', 'server-errors') {
    $log = Join-Path $logs "$name.log"
    if (Test-Path -LiteralPath $log) {
        Move-Item -LiteralPath $log -Destination (Join-Path $logs "$name.previous.log") -Force
    }
}

# Run from the project folder, so the database and PC key are found where they always were.
Start-Process -FilePath 'dotnet' -ArgumentList 'run' -WorkingDirectory $project -NoNewWindow -Wait `
    -RedirectStandardOutput (Join-Path $logs 'server.log') `
    -RedirectStandardError (Join-Path $logs 'server-errors.log')
