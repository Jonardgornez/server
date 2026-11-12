# Lykantra in Docker on pc-server

This guide moves the **server** and the **admin console** on pc-server from Windows into Docker.
Lab PCs don't change: they keep their client and stay paired.

| Part | Time | How often |
|---|---|---|
| A. Prepare pc-server | ~30 min | once |
| B. Move your data into Docker | ~10 min | once |
| C. Install an update | ~2 min | every time changes are pushed |

**Where to type.** Each step tells you where to type it:

- 🟦 **PowerShell (admin)**: Start menu → type `PowerShell` → right-click → *Run as administrator*
- 🟧 **Ubuntu**: Start menu → `Ubuntu`

**Folders.** `C:\...\server` and `C:\...\admin` below mean the folders where you `git pull` today. Use your real paths.

---

## A. Prepare pc-server (once)

### A1. Keep Docker Desktop out of the way

Lykantra **cannot** run in Docker Desktop. Docker Desktop hides each lab PC's real IP address, so all PCs would
look like one address, and registration, reconnecting and other PC features would break. Lykantra runs in its own
Docker, inside Ubuntu, instead.

You may keep Docker Desktop for other things:

1. Open Docker Desktop → **Settings** → **Resources** → **WSL integration** → switch **Ubuntu** **off** → *Apply*.
2. Don't run anything in Docker Desktop on ports **5000**, **5001** or **5173**.
3. If you don't use Docker Desktop for anything else: Settings → General → untick
   *Start Docker Desktop when you sign in* (or uninstall it).

### A2. Install Ubuntu

🟦 PowerShell (admin):

```powershell
wsl -l -v
```

- If the list has **`Ubuntu`**, go to A3. (`docker-desktop` doesn't count; that one belongs to Docker Desktop.)
- If not, install it, then **restart Windows**:
  ```powershell
  wsl --install -d Ubuntu
  ```
  After the restart, open 🟧 Ubuntu once and create a Linux username and password when it asks.

### A3. Turn on mirrored networking

Mirrored networking lets Ubuntu use pc-server's real network address.

🟦 PowerShell (admin):

```powershell
notepad "$env:USERPROFILE\.wslconfig"
```

Click *Yes* if Notepad offers to create the file. Make sure it contains these two lines, then save:

```ini
[wsl2]
networkingMode=mirrored
```

Then restart WSL:

```powershell
wsl --shutdown
```

✅ **Check:** open 🟧 Ubuntu and run `hostname -I`. It should show pc-server's own IP, the same one lab PCs use
to connect.

### A4. Open port 5001 for the lab PCs

🟦 PowerShell (admin), as one command:

```powershell
New-NetFirewallHyperVRule -Name LykantraPcPort -DisplayName "Lykantra PC port (WSL)" -Direction Inbound -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Protocol TCP -LocalPorts 5001
```

Keep your existing Windows firewall rule for port 5001 as well.

### A5. Install Docker inside Ubuntu

🟧 Ubuntu, one line at a time:

```bash
printf '[boot]\nsystemd=true\n' | sudo tee /etc/wsl.conf
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```

🟦 PowerShell (admin):

```powershell
wsl --shutdown
```

✅ **Check** in 🟦 PowerShell:

```powershell
wsl -d Ubuntu --exec docker info --format '{{.OperatingSystem}}'
```

It should print something like `Ubuntu 24.04 LTS`.
If it prints **`Docker Desktop`**, step A1.1 isn't done: switch Ubuntu off in Docker Desktop's WSL integration.

### A6. Start Lykantra automatically when pc-server boots

WSL only runs while something is using it. This task keeps it running from boot, so Docker and both
containers start on their own.

🟦 PowerShell (admin). Replace **`YOUR-WINDOWS-USER`** with the account you're signed in with
(run `whoami` if unsure):

```powershell
$user = 'YOUR-WINDOWS-USER'
$action   = New-ScheduledTaskAction -Execute 'wsl.exe' -Argument '-d Ubuntu --exec sleep infinity'
$trigger  = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'Lykantra WSL' -Action $action -Trigger $trigger -Settings $settings -User $user -Password (Read-Host "Windows password for $user") -RunLevel Highest
```

You check this one in **B6**, after everything is moved.

### A7. Allow the update scripts to run

🟦 PowerShell (admin):

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

---

## B. Move your data into Docker (once)

The Windows files are only **copied**, never changed or deleted. They stay as your backup.

### B1. Stop the Windows versions

- Close the window running the server (`dotnet run`) and the one running the admin console (`npm run dev` /
  `npm run preview`).
- If something starts them automatically at boot (a scheduled task, a shortcut in Startup, a service), remove it.

✅ **Check:** this prints nothing:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5000,5001,5173 -ErrorAction SilentlyContinue
```

### B2. Get the latest code

🟦 PowerShell:

```powershell
cd C:\...\server; git pull
cd C:\...\admin;  git pull
```

### B3. Copy the data in

🟦 PowerShell (admin), signed in as the **same Windows user** that installed Ubuntu:

```powershell
cd C:\...\server
.\docker\migrate-from-windows.ps1
```

This takes a few minutes the first time (it builds the server). It copies these into Docker:

- the database (`lykantra.db`)
- the uploaded client installers (`client-updates`)
- the PC key (`pc-channel.dat`), so every PC stays paired

It ends with **`Done.`**

> If the server used to run from a different folder than `server\LabGuard.Server`, add
> `-DataFolder 'C:\that\folder'` (the folder that has `lykantra.db` in it).

### B4. Start the server and the admin console

🟦 PowerShell:

```powershell
cd C:\...\server; wsl -d Ubuntu --exec docker compose up -d
cd C:\...\admin;  wsl -d Ubuntu --exec docker compose up -d --build
```

### B5. Check that it works

1. Open **http://localhost:5173** on pc-server and sign in as usual.
2. Within a minute, the lab PCs show **Online** on the dashboard.
3. If your admin password is still the old test one (`admin123`), change it now.

### B6. Check the automatic start

Restart pc-server, wait 2 minutes **without signing in**, then sign in and open http://localhost:5173.

- **Opens?** Setup is finished.
- **Doesn't open?** Set Windows to sign in automatically for that user, then in 🟦 PowerShell (admin):
  ```powershell
  Set-ScheduledTask -TaskName 'Lykantra WSL' -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME)
  ```

---

## C. Install an update

When new changes are on GitHub, run this in 🟦 PowerShell:

```powershell
cd C:\...\server; .\deploy.ps1
cd C:\...\admin;  .\deploy.ps1
```

Each script gets the new code, builds it, and only then swaps it in. If anything fails, the old version keeps
running. Your data is never touched.

> `git pull` on its own is no longer enough. The containers only change when `deploy.ps1` rebuilds them.

---

## Backups

A backup contains the PC key unencrypted, so keep it in a folder only administrators can open.

**Create the folder (once)**, 🟦 PowerShell (admin):

```powershell
New-Item -ItemType Directory C:\LykantraBackups
icacls C:\LykantraBackups /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "$($env:USERNAME):(OI)(CI)F"
```

**Make a backup**, 🟦 PowerShell (admin), from `C:\...\server`. The server pauses for a few seconds:

```powershell
wsl -d Ubuntu --exec docker compose stop server
wsl -d Ubuntu --exec docker run --rm --user 0 --entrypoint tar -v lykantra-data:/data:ro -v /mnt/c/LykantraBackups:/backup lykantra-server czf /backup/lykantra-backup.tgz -C /data .
wsl -d Ubuntu --exec docker compose start server
```

✅ `C:\LykantraBackups\lykantra-backup.tgz` now exists. Copy it somewhere safe; the next backup overwrites it.

**Restore a backup** (replaces all current server data). The file is checked first, so a broken backup changes nothing:

```powershell
wsl -d Ubuntu --exec docker compose stop server
wsl -d Ubuntu --exec docker run --rm --user 0 --entrypoint sh -v lykantra-data:/data -v /mnt/c/LykantraBackups:/backup:ro lykantra-server -c 'tar tzf /backup/lykantra-backup.tgz | grep -qx ./lykantra.db && find /data -mindepth 1 -delete && tar xzf /backup/lykantra-backup.tgz -C /data && chown -R "$APP_UID" /data && chmod -R go-rwx /data'
wsl -d Ubuntu --exec docker compose start server
```

---

## If something goes wrong

| What you see | What to do |
|---|---|
| `docker info` prints **Docker Desktop** | A1.1: switch Ubuntu off in Docker Desktop → Settings → Resources → WSL integration, then `wsl --shutdown`. |
| `There is no distribution with the supplied name` | Ubuntu isn't installed for this Windows user. Do A2 while signed in as that user. |
| `permission denied ... docker.sock` | A5 isn't finished: run `wsl --shutdown`, then try again. |
| Migration: *Something is still listening on port 5000 or 5001* | B1: the Windows server is still running. |
| Migration: *The lykantra-data volume already has data* | The data was already moved, so nothing needs doing. If an earlier run **failed** and the server never started in Docker, run `wsl -d Ubuntu --exec docker volume rm lykantra-data` and run B3 again. |
| Migration: *No pc-channel.dat* | Wrong folder: use `-DataFolder` (B3). |
| Lab PCs stay **Offline** | `hostname -I` in Ubuntu must show pc-server's IP (A3), and the A4 firewall rule must exist. |
| *running scripts is disabled on this system* | Do A7. |
| http://localhost:5173 doesn't open | Run `wsl -d Ubuntu --exec docker ps` and check that two containers are listed. Logs: from `C:\...\server`, run `wsl -d Ubuntu --exec docker compose logs --tail 50 server`. |

**Going back to an older version:** from `C:\...\server` (or `admin`):

```powershell
git log --oneline
git checkout <commit>
wsl -d Ubuntu --exec docker compose up -d --build
```

Later, `git checkout main` and `.\deploy.ps1` bring you back to the latest version. A newer server may already
have upgraded the database, so if the older one won't start, restore a backup taken before the update.

---

## Good to know

- **Never run** `docker compose down -v` or `docker volume rm lykantra-data` once the server is in use. They delete all server data.
- In Docker the PC key and the admin login keys are stored unencrypted inside the `lykantra-data` volume (on
  Windows the PC key was encrypted). Only Windows administrators and the Ubuntu user can reach them, so keep
  pc-server's admin accounts to the people who run it.
- The admin console only opens on pc-server itself (http://localhost:5173), the same as before.
