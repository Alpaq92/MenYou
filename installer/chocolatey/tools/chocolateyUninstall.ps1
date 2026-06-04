# Chocolatey uninstall script for MenYou.
#
# MenYou ships an Inno Setup installer. The Chocolatey package installs it
# per-machine (/ALLUSERS), so Inno records its uninstaller under the
# machine Uninstall hive as "{AppId}_is1" with a QuietUninstallString
# (e.g. "...\unins000.exe" /VERYSILENT). We invoke that. HKCU is checked
# too in case the package was ever installed per-user.

$ErrorActionPreference = 'Stop'

# Must match installer/inno/menyou.iss [Setup] AppId.
$subKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A9F2C7E4-3B6D-4F8A-9C1E-5D7B2A4F6E83}_is1'

$ran = $false
foreach ($hive in @('HKLM:', 'HKCU:')) {
    $path = Join-Path $hive $subKey
    if (Test-Path $path) {
        $props = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
        $cmd = $props.QuietUninstallString
        if (-not $cmd) { $cmd = $props.UninstallString }
        if ($cmd) {
            # QuietUninstallString already includes the silent flag; for a
            # bare UninstallString append one.
            if ($cmd -notmatch '(?i)/VERYSILENT|/SILENT') { $cmd = "$cmd /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" }
            Write-Host "Uninstalling MenYou: $cmd"
            & cmd.exe /c $cmd
            if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) {
                Write-Warning "MenYou uninstaller exited with code $LASTEXITCODE."
            }
            $ran = $true
            break
        }
    }
}

if (-not $ran) {
    Write-Warning 'MenYou Inno uninstall entry not found; nothing to remove.'
}
