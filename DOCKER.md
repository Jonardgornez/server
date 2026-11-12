# Running the server in Docker on pc-server

The server and the admin console run as two containers under **Docker Engine inside WSL2**, using WSL's
*mirrored* networking and Docker's host networking. That keeps everything working the way it did on Windows:

- Lab PCs still connect to pc-server's own IP on port **5001**, and the server still sees each PC's real IP address.
- The admin console is still only reachable on pc-server itself: **http://localhost:5173**.
- All server data lives in the Docker volume **`lykantra-data`** (database, client installers, PC key, login keys).
  Rebuilding or updating the containers never touches it.

> Do not use Docker Desktop for this. Its networking hides the PCs' real IP addresses, which breaks PC
> reconnects, registration and the "don't shut down the server PC" protection.

The client (`client/`) is not part of this; lab PCs keep using the installer.

## 1. One-time setup on pc-server

Needs Windows 11 22H2 or later.

1. **Install WSL with Ubuntu** (PowerShell as administrator), then restart Windows:
   ```powershell
   wsl --install -d Ubuntu
   ```
2. **Turn on mirrored networking.** Create or edit `C:\Users\<you>\.wslconfig`:
   ```ini
   [wsl2]
   networkingMode=mirrored
   ```
3. **Let lab PCs reach port 5001 inside WSL** (PowerShell as administrator). Keep the existing Windows
   firewall rule for port 5001 too.
   ```powershell
   New-NetFirewallHyperVRule -Name LykantraPcPort -DisplayName "Lykantra PC port (WSL)" -Direction Inbound `
     -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Protocol TCP -LocalPorts 5001
   ```
4. **Install Docker Engine in Ubuntu** (open the Ubuntu terminal):
   ```bash
   # Lets Docker start as a service whenever WSL starts.
   printf '[boot]\nsystemd=true\n' | sudo tee /etc/wsl.conf
   curl -fsSL https://get.docker.com | sudo sh
   sudo usermod -aG docker $USER
   ```
   Then in PowerShell run `wsl --shutdown`, open Ubuntu again, and check: `docker run --rm hello-world`.
5. **Keep WSL running from boot.** WSL only runs while something uses it, so add a startup task
   (PowerShell as administrator, replace `<you>` with the Windows account that installed Ubuntu):
   ```powershell
   $action  = New-ScheduledTaskAction -Execute 'wsl.exe' -Argument '-d Ubuntu --exec sleep infinity'
   $trigger = New-ScheduledTaskTrigger -AtStartup
   $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
   Register-ScheduledTask -TaskName 'Lykantra WSL' -Action $action -Trigger $trigger -Settings $settings `
     -User '<you>' -Password (Read-Host 'Windows password for <you>') -RunLevel Highest
   ```
   Docker starts with WSL, and both containers start with Docker (`restart: unless-stopped`).
   If the containers are not up after a reboot without anyone signing in, use automatic sign-in for that
   account and change the trigger to `New-ScheduledTaskTrigger -AtLogOn -User '<you>'`.

## 2. Move the existing data into Docker (once)

1. Stop the Windows server (close `dotnet run`, or stop the task/service that starts it) and the old admin
   console (`npm run dev` / `npm run preview`). Remove whatever started them at boot.
2. Get this version: `git pull` in `server` and in `admin`.
3. Run the migration from the `server` folder (normal PowerShell, the account the server ran as):
   ```powershell
   .\docker\migrate-from-windows.ps1
   ```
   It refuses to run while anything listens on port 5000/5001, and never copies into a volume that already
   holds data. If a run fails partway and the server has **never** been started in Docker, the volume only holds
   that partial copy: remove it with `wsl -d Ubuntu --exec docker volume rm lykantra-data` and run the script again.
   It copies `lykantra.db`, `client-updates\` and the PC key (exported from `pc-channel.dat`, so every PC stays
   paired) into the volume. The Windows files are not changed; keep them as a backup.
   If the server ran from another folder, add `-DataFolder 'C:\path\to\that\folder'`.
4. Start both containers:
   ```powershell
   cd server; wsl -d Ubuntu --exec docker compose up -d
   cd ..\admin; wsl -d Ubuntu --exec docker compose up -d
   ```
5. Check: open http://localhost:5173, sign in with the usual admin account, and confirm the PCs come online.
   Server log: `wsl -d Ubuntu --exec docker compose logs -f server` (from the `server` folder).

## 3. Updating

After changes are pushed to GitHub, on pc-server run, in each folder:

```powershell
cd server; .\deploy.ps1
cd ..\admin; .\deploy.ps1
```

Each script does `git pull`, builds the new image, and only then replaces the running container, so a failed
build leaves the old version running. Database migrations run when the new server starts.

## 4. Backup and restore

A backup holds the PC key and the login keys unencrypted, so it goes in a folder outside the repo that only
administrators can read. Create that folder once (PowerShell as administrator):

```powershell
New-Item -ItemType Directory C:\LykantraBackups
icacls C:\LykantraBackups /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
```

Back up (from the `server` folder, PowerShell as administrator; stops the server for a few seconds so the
database copy is consistent):

```powershell
wsl -d Ubuntu --exec docker compose stop server
wsl -d Ubuntu --exec docker run --rm --user 0 --entrypoint tar -v lykantra-data:/data:ro -v /mnt/c/LykantraBackups:/backup lykantra-server czf /backup/lykantra-backup.tgz -C /data .
wsl -d Ubuntu --exec docker compose start server
```

Run one test backup after setup. If it fails with "Permission denied", also give the Windows account that runs
WSL access: `icacls C:\LykantraBackups /grant:r "<you>:(OI)(CI)F"`.

Restore (replaces everything in the volume; the archive is checked first, so a bad file changes nothing):

```powershell
wsl -d Ubuntu --exec docker compose stop server
wsl -d Ubuntu --exec docker run --rm --user 0 --entrypoint sh -v lykantra-data:/data -v /mnt/c/LykantraBackups:/backup:ro lykantra-server -c 'tar tzf /backup/lykantra-backup.tgz | grep -qx ./lykantra.db && find /data -mindepth 1 -delete && tar xzf /backup/lykantra-backup.tgz -C /data && chown -R "$APP_UID" /data && chmod -R go-rwx /data'
wsl -d Ubuntu --exec docker compose start server
```

## 5. Going back to an earlier version

```powershell
git log --oneline            # pick the commit
git checkout <commit>
wsl -d Ubuntu --exec docker compose up -d --build
git checkout main            # when the fix is pushed, then .\deploy.ps1
```

A newer server may have already changed the database (migrations only go forward). Restore a backup taken
before the update if the older server can't start.

## Security notes

- In Docker the PC key (`pc-channel.json`) and the admin login keys (`keys/`) are stored unencrypted in the volume
  (on Windows the PC key was DPAPI-encrypted). Only the container's user can read them, but anyone in the WSL
  `docker` group or with Windows administrator rights can. Keep pc-server's admin accounts to the people who run it.
- If the admin account still has the old development password (`admin123`), change it in the console now.
- A fresh install (no migrated database) runs in Production and creates no admin account.

## Never run

- `docker compose down -v` or `docker volume rm lykantra-data`: these delete all server data.
