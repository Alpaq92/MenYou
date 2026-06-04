# Installer assets

Templates rendered by `.github/workflows/release.yml` on each tag push.

| Path | What |
|---|---|
| `inno/menyou.iss`            | Inno Setup script — the actual installer wizard. Compiled by `iscc` in CI into `dist/MenYou-Setup-<ver>.exe`. |
| `scoop/menyou.json.template` | Scoop manifest — pushed to the `Alpaq92/scoop-menyou` bucket repo. Runs the Inno installer `/VERYSILENT`. |
| `chocolatey/menyou.nuspec`   | Chocolatey package metadata — packed + pushed to push.chocolatey.org. |
| `chocolatey/tools/chocolateyInstall.ps1`  | Per-install entry point. Pulls the signed Setup.exe from GH Releases and runs it `/VERYSILENT /ALLUSERS`. |
| `chocolatey/tools/chocolateyUninstall.ps1`| Runs the Inno uninstaller via the `{AppId}_is1` registry `QuietUninstallString`. |

Winget intentionally has no in-repo template — the release workflow uses
[winget-releaser](https://github.com/vedantmgoyal9/winget-releaser),
which derives the manifest from the GitHub Release asset automatically.

See [`../docs/AUTOMATION.md`](../docs/AUTOMATION.md) for the broader
deployment story (signing, channel selection, free options).
