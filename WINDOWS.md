# Running Lykantra on pc-server (Windows)

The server and the admin console run directly on Windows, the same as with `dotnet run` and `npm run dev`. The
scripts here do the starting and updating for you:

| Script | What it does | When |
|---|---|---|
| `windows\install-autostart.ps1` | Starts it automatically when Windows boots | once |
| `update.ps1` | Gets the latest code, checks it builds, restarts | every time changes are pushed |

Each repo has its own copies: `server` and `admin` each have an `update.ps1` and a `windows\install-autostart.ps1`.

Your data (database, PC key, uploaded installers) stays where it always was, in `server\LabGuard.Server`.
The scripts never touch it.

> `C:\...\server` and `C:\...\admin` below mean the folders where you `git pull` today. Use your real paths.

---

## Setup (once)

### 1. Stop the Docker containers

If you started Lykantra in Docker, stop it, so it doesn't hold the ports. In **PowerShell**:

```powershell
cd C:\...\server; docker compose down
cd C:\...\admin;  docker compose down
```

(Don't add `-v`. Red error text just means there was nothing to stop.)

Also close any window still running `dotnet run` or `npm run dev`.

### 2. Get the latest code

```powershell
cd C:\...\server; git pull
cd C:\...\admin;  git pull
```

### 3. Turn on automatic start

Open **PowerShell as administrator** (Start → type `PowerShell` → right-click → *Run as administrator*).
When Windows asks, use the **same Windows account** you normally run the server with.

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
cd C:\...\server; .\windows\install-autostart.ps1
cd C:\...\admin;  .\windows\install-autostart.ps1
```

Each one ends with **`Done.`**

### 4. Start everything now

In **PowerShell** (normal is fine):

```powershell
cd C:\...\server; .\update.ps1 -Force
cd C:\...\admin;  .\update.ps1 -Force
```

They end with **`Server updated and running`** and **`Admin console updated and running`**.

### 5. Check

1. Open **http://localhost:5173** on pc-server and sign in.
2. Within a minute, the lab PCs show **Online**.
3. Restart pc-server, wait 2 minutes, then open http://localhost:5173 again. It should open without you
   starting anything.

Setup is finished.

---

## Installing an update

When new changes are on GitHub, in **PowerShell**:

```powershell
cd C:\...\server; .\update.ps1
cd C:\...\admin;  .\update.ps1
```

- **Nothing new:** it says *Already up to date* and leaves everything running.
- **New version:** it checks that the new code builds, then restarts. This takes about a minute.
- **The new code doesn't build:** it puts the code back and keeps the old version running. Tell the developer.

---

## If something goes wrong

| What you see | What to do |
|---|---|
| *running scripts is disabled on this system* | Do step 3's `Set-ExecutionPolicy` line. |
| *did not stop. Try again in PowerShell as administrator* | Run the same `update.ps1` in PowerShell as administrator. |
| *didn't answer within 3 minutes* | Open `C:\...\server\logs\server-errors.log` and `server.log`. For the console: `C:\...\admin\logs\admin-errors.log`. |
| *git pull failed* | Someone changed files on pc-server. Run `git status` in that folder and send the output to the developer. |
| http://localhost:5173 doesn't open after a reboot | In PowerShell: `cd C:\...\server; .\update.ps1 -Force` and the same in `admin`. If that works but the next reboot fails again, redo step 3. |

**To start by hand** (for example, after you stopped it): `.\update.ps1 -Force` in `server`, then in `admin`.

**Logs:** `server\logs\` and `admin\logs\`. The previous run's logs are kept as `*.previous.log`.

**To turn off automatic start:**
`Unregister-ScheduledTask -TaskName 'Lykantra Server','Lykantra Admin' -Confirm:$false` (PowerShell as administrator).
