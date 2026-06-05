# Chocolatey package

The release workflow (`.github/workflows/release.yml`) substitutes `@@VERSION@@`, `@@URL@@`, and `@@HASH@@` in `menyou.nuspec` + `tools/chocolateyInstall.ps1`, runs `choco pack`, and pushes the resulting `.nupkg` to <https://push.chocolatey.org/> using the `CHOCO_API_KEY` secret.

The Chocolatey community moderation team reviews first-time submissions manually; subsequent versions auto-approve. If you're publishing the first version, expect a 1–7 day delay between the workflow push and `choco install menyou` becoming public.

Local sanity check:

```pwsh
choco pack installer/chocolatey/menyou.nuspec --out .
choco install menyou --source "$PWD;https://community.chocolatey.org/api/v2/"
```
