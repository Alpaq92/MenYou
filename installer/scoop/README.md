# Scoop manifest

The release workflow (`.github/workflows/release.yml`) renders
`menyou.json.template` into a final `menyou.json` with the tag version,
download URL, and installer SHA256, then pushes it into the bucket repo
configured via the `SCOOP_PAT` secret.

The expected bucket repository name is `Alpaq92/scoop-menyou`. Users then
add it once:

```pwsh
scoop bucket add menyou https://github.com/Alpaq92/scoop-menyou
scoop install menyou
```

To add MenYou to Scoop's official `extras` bucket later, copy the
rendered `menyou.json` from a recent release into a PR against
[ScoopInstaller/Extras](https://github.com/ScoopInstaller/Extras).
