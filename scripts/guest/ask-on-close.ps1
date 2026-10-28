# A window that asks before it closes, as an app with unsaved changes does: for testing what
# Expanse's close button does with one (it dives in so the question can be answered). With
# -Dialog it also opens a modeless dialog it owns (like a Find box), over its top-right corner.
# -Left and -Top place it (screen pixels).
#   desk.ps1 -Exe conhost.exe -Arguments "--headless powershell -File ...\ask-on-close.ps1 [-Dialog] [-Left X -Top Y]"
param([switch]$Dialog, [int]$Left = 200, [int]$Top = 150)
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Unsaved - Asker'
$form.Width = 640
$form.Height = 420
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($Left, $Top)
$form.Add_FormClosing({
    param($sender, $e)
    $answer = [System.Windows.Forms.MessageBox]::Show($form, 'Save changes?', 'Asker', 'YesNoCancel')
    if ($answer -eq 'Cancel') { $e.Cancel = $true }
})
if ($Dialog) {
    $form.Add_Shown({
        $find = New-Object System.Windows.Forms.Form
        $find.Text = 'Find'
        $find.Width = 320
        $find.Height = 160
        $find.FormBorderStyle = 'FixedDialog'
        $find.ShowInTaskbar = $false
        $find.StartPosition = 'Manual'
        $find.Location = New-Object System.Drawing.Point(($form.Right - 260), ($form.Top + 60))
        $find.Show($form)
    })
}
[System.Windows.Forms.Application]::Run($form)
