# One-time VM setup, run as SYSTEM through UTM's guest agent (scripts/vm.sh setup):
# OpenSSH server with key-only logins for administrators, and the .NET 10 SDK (ARM64).
# Output goes to C:\ProgramData\expanse-setup.log.
param([Parameter(Mandatory)][string]$PublicKeyFile)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$status = 'C:\ProgramData\expanse-setup.status'
Set-Content $status 'running' -Encoding ascii
Start-Transcript -Path 'C:\ProgramData\expanse-setup.log' -Append | Out-Null
try {
    # --- OpenSSH server: Microsoft's Win32-OpenSSH MSI. (Windows' own optional feature installs
    # through Windows Update, which on a fresh VM queues it behind cumulative updates for ages.)
    if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
        $rel = Invoke-RestMethod https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases/latest
        $asset = $rel.assets | Where-Object name -like 'OpenSSH-ARM64-*.msi' | Select-Object -First 1
        Write-Output "installing $($asset.name)"
        $msi = Join-Path $env:TEMP $asset.name
        Invoke-WebRequest $asset.browser_download_url -OutFile $msi -UseBasicParsing
        $p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn ADDLOCAL=Server" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec exited $($p.ExitCode)" }
    }
    Set-Service sshd -StartupType Automatic
    Start-Service sshd   # first start writes the default sshd_config

    $keys = 'C:\ProgramData\ssh\administrators_authorized_keys'
    Copy-Item $PublicKeyFile $keys -Force
    icacls.exe $keys /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null

    $config = 'C:\ProgramData\ssh\sshd_config'
    $text = Get-Content $config -Raw
    $text = $text -replace '(?m)^#?PasswordAuthentication\s+\w+', 'PasswordAuthentication no'
    Set-Content $config $text -Encoding ascii

    $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
    if ($rule) { Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Enabled True -Profile Any }
    else {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True `
            -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Any | Out-Null
    }

    # --- .NET 10 SDK, machine-wide ---
    $dotnet = 'C:\Program Files\dotnet'
    $haveSdk = (Test-Path "$dotnet\dotnet.exe") -and ((& "$dotnet\dotnet.exe" --list-sdks) -match '^10\.')
    if (-not $haveSdk) {
        $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer -UseBasicParsing
        & $installer -Channel 10.0 -InstallDir $dotnet -Architecture arm64
    }
    $path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if ($path -notlike "*$dotnet*") { [Environment]::SetEnvironmentVariable('Path', "$path;$dotnet", 'Machine') }
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Machine')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Machine')

    # sshd hands new sessions the PATH it started with.
    Restart-Service sshd
    # For vm.sh: who's signed in at the desktop (the account SSH logs in as).
    (Get-CimInstance Win32_ComputerSystem).UserName | Set-Content 'C:\ProgramData\expanse-user.txt' -Encoding ascii
    Write-Output 'setup done'
    $result = 'done'
} catch {
    Write-Output "setup failed: $_"
    $result = "failed: $_"
} finally {
    Stop-Transcript | Out-Null
    Set-Content $status $result -Encoding ascii
}
