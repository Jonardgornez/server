# Gets the latest server from GitHub and rebuilds/restarts its container. Run on pc-server from this folder.
# The data volume (database, installers, PC key) is kept; database migrations run when the new server starts.
param(
    # The WSL distro that runs Docker Engine.
    [string]$Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'git pull failed; nothing was rebuilt.' }

# Built first, so a failed build leaves the running server untouched.
wsl.exe -d $Distro --exec docker compose build
if ($LASTEXITCODE -ne 0) { throw 'Build failed; the running server was not changed.' }

wsl.exe -d $Distro --exec docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed. Check: wsl -d $Distro --exec docker compose logs server" }

# Removes only untagged images left over from earlier builds; the data volume is never touched.
wsl.exe -d $Distro --exec docker image prune -f | Out-Null
Write-Host 'Server updated. Logs: wsl -d' $Distro '--exec docker compose logs -f server'
