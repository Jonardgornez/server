# Shows why the Docker setup isn't reachable. Only reads information; changes nothing.
# Run on pc-server in normal PowerShell: .\docker\diagnose.ps1   (the result is also copied to the clipboard)
param(
    # The WSL distro that runs Docker Engine.
    [string]$Distro = 'Ubuntu'
)

$report = & {
    '=== 1. Which Docker (must say Ubuntu..., not Docker Desktop) ==='
    wsl.exe -d $Distro --exec docker info --format '{{.OperatingSystem}}'

    '=== 2. Containers (both must say Up) ==='
    wsl.exe -d $Distro --exec docker ps -a --format '{{.Names}} | {{.Status}}'

    '=== 3. Console from inside Ubuntu (should print {"status":"ok"}) ==='
    # No double quotes inside sh scripts: Windows PowerShell 5.1 breaks them when passing arguments to wsl.exe.
    wsl.exe -d $Distro --exec sh -c 'curl -s -m 5 http://127.0.0.1:5173/api/health || echo no-answer'

    '=== 4. Console from Windows (should print {"status":"ok"}) ==='
    try { (Invoke-WebRequest http://localhost:5173/api/health -UseBasicParsing -TimeoutSec 5).Content }
    catch { "no answer: $($_.Exception.Message)" }

    '=== 5. Windows and WSL versions ==='
    (Get-CimInstance Win32_OperatingSystem).Caption + ' build ' + [Environment]::OSVersion.Version.Build
    wsl.exe --version
    wsl.exe -l -v

    '=== 6. .wslconfig (must contain networkingMode=mirrored) ==='
    $wslConfig = Join-Path $env:USERPROFILE '.wslconfig'
    if (Test-Path -LiteralPath $wslConfig) { Get-Content -LiteralPath $wslConfig } else { 'missing' }

    '=== 7. IP addresses (Ubuntu should show the same IP as Windows) ==='
    'Ubuntu:  ' + (wsl.exe -d $Distro --exec hostname -I)
    ipconfig | Select-String 'IPv4'

    '=== 8. Ports listening inside Ubuntu (expect 5000, 5001, 5173) ==='
    wsl.exe -d $Distro --exec sh -c "ss -ltn | grep -E ':(5000|5001|5173) ' || echo none"

    '=== 9. Ports listening on Windows ==='
    Get-NetTCPConnection -State Listen -LocalPort 5000, 5001, 5173 -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize | Out-String

    '=== 10. Container logs (last 20 lines each) ==='
    wsl.exe -d $Distro --exec sh -c 'for c in $(docker ps -a --format {{.Names}}); do echo --- $c; docker logs --tail 20 $c 2>&1; done'
} 2>&1 | Out-String

# wsl.exe prints some messages in UTF-16, which shows up as spaced-out letters; the NUL characters are removed.
$report = $report -replace "`0", ''
$report
try {
    Set-Clipboard -Value $report
    'The result above is copied to the clipboard: paste it in the chat.'
} catch {
    'Could not copy to the clipboard: select the text above and copy it.'
}
