# A window that asks before it closes, as an app with unsaved changes does: for testing what
# Expanse's close button does with one (it dives in so the question can be answered).
#   desk.ps1 -Exe powershell.exe -Arguments "-WindowStyle Hidden -File ...\ask-on-close.ps1"
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Unsaved - Asker'
$form.Width = 640
$form.Height = 420
$form.Add_FormClosing({
    param($sender, $e)
    $answer = [System.Windows.Forms.MessageBox]::Show($form, 'Save changes?', 'Asker', 'YesNoCancel')
    if ($answer -eq 'Cancel') { $e.Cancel = $true }
})
[System.Windows.Forms.Application]::Run($form)
