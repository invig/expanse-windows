# A window that asks before it closes, as an app with unsaved changes does: for testing what
# Expanse's close button does with one (it dives in so the question can be answered). With
# -Dialog it also opens a modeless dialog it owns (like a Find box), over its top-right corner.
#   desk.ps1 -Exe conhost.exe -Arguments "--headless powershell -File ...\ask-on-close.ps1 [-Dialog]"
param([switch]$Dialog)
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Unsaved - Asker'
$form.Width = 640
$form.Height = 420
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(200, 150)
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
