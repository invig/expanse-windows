# Signs the VM straight in to this account at boot (Windows' auto sign-in), so a restart doesn't
# wait at the lock screen. It asks for the account's password; run it yourself from the Mac:
#
#   ssh -t -i ~/.ssh/windows_vm_ed25519 test@192.168.64.4 powershell -NoProfile -ExecutionPolicy Bypass -File expanse-windows\scripts\guest\autologon.ps1
#
# The password is kept in the registry in plain text (Winlogon\DefaultPassword): fine for a
# throwaway VM. To undo, set AutoAdminLogon to 0 and delete DefaultPassword.
$ErrorActionPreference = 'Stop'
$secure = Read-Host "Windows password for $env:USERNAME" -AsSecureString
$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty $key AutoAdminLogon '1'
Set-ItemProperty $key DefaultUserName $env:USERNAME
Set-ItemProperty $key DefaultDomainName $env:COMPUTERNAME
Set-ItemProperty $key DefaultPassword ([Net.NetworkCredential]::new('', $secure).Password)
'Auto sign-in is on: after a restart the VM goes straight to the desktop.'
