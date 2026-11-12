# One-time move of the server's data from the Windows install into the Docker volume "lykantra-data".
# Run once on pc-server, from any folder, after the Windows server has been stopped.
# The Windows files are only read, never changed or deleted, so they remain as a backup.
param(
    # The WSL distro that runs Docker Engine.
    [string]$Distro = 'Ubuntu',
    # The folder that holds lykantra.db, client-updates and pc-channel.dat (where the Windows server ran from).
    [string]$DataFolder = (Join-Path (Split-Path $PSScriptRoot -Parent) 'LabGuard.Server'),
    # Continue even though there is no pc-channel.dat. The server then creates a new PC key and every PC must be set up again.
    [switch]$AllowNewPcKey
)

$ErrorActionPreference = 'Stop'
$serverRoot = Split-Path $PSScriptRoot -Parent
$projectFolder = Join-Path $serverRoot 'LabGuard.Server'
$DataFolder = (Resolve-Path -LiteralPath $DataFolder).Path

function Invoke-Docker {
    wsl.exe -d $Distro --exec docker @args
    if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') failed." }
}

$listening = Get-NetTCPConnection -State Listen -LocalPort 5000, 5001 -ErrorAction SilentlyContinue
if ($listening) {
    throw 'Something is still listening on port 5000 or 5001. Stop the Windows server (and any Docker server) first.'
}

$database = Join-Path $DataFolder 'lykantra.db'
if (-not (Test-Path -LiteralPath $database)) {
    throw "No database at $database. Pass -DataFolder with the folder that holds lykantra.db."
}

$channelFile = Join-Path $DataFolder 'pc-channel.dat'
$hasChannelFile = Test-Path -LiteralPath $channelFile
if (-not $hasChannelFile -and -not $AllowNewPcKey) {
    throw "No pc-channel.dat in $DataFolder. Without it every PC must be set up again. Re-run with -AllowNewPcKey only if that is intended."
}

Push-Location -LiteralPath $serverRoot
try {
    Write-Host 'Building the server image...'
    Invoke-Docker compose build
} finally {
    Pop-Location
}

Invoke-Docker volume create lykantra-data | Out-Null
wsl.exe -d $Distro --exec docker run --rm --user 0 --entrypoint sh -v lykantra-data:/data lykantra-server -c 'test -e /data/lykantra.db'
if ($LASTEXITCODE -eq 0) {
    throw 'The lykantra-data volume already has a database, so nothing was copied. The data has already been moved.'
}

# The staging folder briefly holds the PC key unencrypted, so it is always removed at the end.
$staging = Join-Path ([IO.Path]::GetTempPath()) ('lykantra-import-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path -LiteralPath "$database$suffix") {
            Copy-Item -LiteralPath "$database$suffix" -Destination $staging
        }
    }

    $updates = Join-Path $DataFolder 'client-updates'
    if (Test-Path -LiteralPath $updates) {
        Copy-Item -LiteralPath $updates -Destination $staging -Recurse
    }

    if ($hasChannelFile) {
        Write-Host 'Exporting the PC key (keeps every PC paired)...'
        dotnet run --project $projectFolder -- --export-pc-channel (Join-Path $staging 'pc-channel.json') "--Server:PcChannelFile=$channelFile"
        if ($LASTEXITCODE -ne 0) { throw 'Exporting the PC key failed. Nothing was copied into the volume.' }
    }

    $wslStaging = (wsl.exe -d $Distro --exec wslpath -a $staging).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $wslStaging) { throw "Could not translate $staging to a WSL path." }

    Write-Host 'Copying data into the lykantra-data volume...'
    Invoke-Docker run --rm --user 0 --entrypoint sh -v lykantra-data:/data -v "${wslStaging}:/import:ro" lykantra-server `
        -c 'cp -a /import/. /data/ && chown -R "$APP_UID" /data && chmod -R go-rwx /data'
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Done. The Windows files were left untouched as a backup.'
Write-Host "Start the server:  cd `"$serverRoot`"; wsl -d $Distro --exec docker compose up -d"
