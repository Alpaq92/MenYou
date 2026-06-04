# Chocolatey install script for MenYou.
#
# The release workflow substitutes @@URL@@, @@HASH@@, and @@VERSION@@
# before running `choco pack` so the published nupkg points at the
# correct GH-Releases asset and carries the SHA256 guard for the
# user-side download.

$ErrorActionPreference = 'Stop'

$packageName = 'menyou'
$installerType = 'exe'
$url64 = '@@URL@@'
$checksum64 = '@@HASH@@'
$checksumType64 = 'sha256'

$packageArgs = @{
    packageName    = $packageName
    fileType       = $installerType
    url64bit       = $url64
    checksum64     = $checksum64
    checksumType64 = $checksumType64
    # MenYou ships an Inno Setup installer. /VERYSILENT runs it with no UI,
    # /SUPPRESSMSGBOXES auto-answers any prompts, /NORESTART blocks an
    # unattended reboot. Inno installs per-machine under Choco's elevated
    # context, registering an Add/Remove entry so Choco's auto-uninstaller
    # can remove it.
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS'
    # Inno exit codes: 0 (success), 3010 (success + reboot required).
    validExitCodes = @(0, 3010)
}

Install-ChocolateyPackage @packageArgs
