# MenYou — automation

How a change travels from a pull request to a published release: what runs, what gates on what, and what you configure once. This is the single source of truth for CI/CD, code signing, and distribution.

## The flow

```
open a PR
   ├─ build.yml        restore → build → publish → dry-compile the Inno installer
   ├─ codeql.yml       C# static analysis
   ├─ security.yml     gitleaks · dependency-review · trivy
   └─ CodeRabbit       automated review (APPROVED / CHANGES_REQUESTED)
        │
   merge, by author:
   ├─ trusted author (you / Dependabot / GitHub Actions)
   │     └─ trusted-author-auto-merge.yml → native auto-merge the moment required checks pass
   └─ everyone else
         └─ auto-merge.yml → merges 7 days AFTER an eligible approval, once checks are green
        │
   main moves → release-please.yml
        └─ opens/updates "chore(main): release X.Y.Z" (version bump + CHANGELOG),
           queues it for auto-merge; on merge pushes tag vX.Y.Z
              │
        tag vX.Y.Z → release.yml
              ├─ publish (win-x64, self-contained) → Inno installer
              ├─ GitHub Release (installer asset + SHA-256, unsigned)
              └─ fan-out: winget · Scoop · Chocolatey  (each continue-on-error)
```

## Workflows

### `build.yml`
Push to `main`, PR to `main`, `workflow_dispatch`; skips docs-only changes. Restore → `dotnet build` → `dotnet publish` (win-x64, self-contained) → Inno Setup `iscc` **dry-compile**. The artifact isn't uploaded — the release pipeline rebuilds at tag time with the real version. The `build` job name is the required status check on `main`.

### `codeql.yml`
Push/PR to `main` + a weekly cron. `security-and-quality` queries over the managed C# tree (the native bridge has no managed surface). Results land in the **Security** tab; CodeQL is also a required gate via the ruleset's code-scanning rule.

### `security.yml`
Push/PR to `main`. Three independent jobs:
- **gitleaks** — secret scan (config in `.gitleaks.toml`, which allowlists `.axaml` markup so XAML resource keys don't false-positive).
- **dependency-review** (PR-only) — fails on HIGH/CRITICAL CVEs introduced by manifest changes. Requires the repo's **Dependency graph** to be on.
- **trivy** — filesystem scan, SARIF upload, `exit-code: 0` (findings are triaged in the Security tab, they don't block).

### `trusted-author-auto-merge.yml`
`pull_request_target` on `main`. For PRs authored by `Alpaq92`, `dependabot[bot]`, or `github-actions[bot]`, enables GitHub native auto-merge (`gh pr merge --auto --squash`) — they merge as soon as the required checks pass, with **no approval and no soak**. (release-please's release PR and the monthly refresh PR enable their own auto-merge too.)

### `auto-merge.yml`
The 7-day path for **everyone else**. Triggers: cron every 6h, `pull_request_review`, `workflow_run` (build/CodeQL/Security), and `workflow_dispatch`. Merges a PR only when **all** hold:
1. not a draft, and not a trusted author (those are skipped — handled above);
2. no open `CHANGES_REQUESTED` reviews;
3. at least one `APPROVED` review from a **CODEOWNER**, a **collaborator**, or **`coderabbitai[bot]`**;
4. that approval is **≥ 7 days old** — the clock starts at *approval time*, not PR creation;
5. no failing/pending checks on the head SHA; and
6. the PR is mergeable.

It squash-merges via the REST API using `RELEASE_PLEASE_PAT` so the merge commit triggers downstream workflows.

### `auto-approve-chore.yml`
`pull_request_target` on `main`. Posts an `APPROVED` review (as `github-actions[bot]`) for non-draft PRs whose title is `chore`/`ci`/`docs`/ `refactor`/`build`/`i18n` **and** whose author is `Alpaq92` or `dependabot[bot]`. `github-actions[bot]` is excluded — a token can't approve its own PR. Feature/fix PRs are left for real review.

### `release-please.yml`
Push to `main` + `workflow_dispatch`. Reads Conventional Commits since the last release, opens/updates the release PR (version bump + `.release-please-manifest.json` + CHANGELOG), and queues it for auto-merge. On merge it pushes the `vX.Y.Z` tag, which triggers `release.yml`.

### `monthly-maintenance.yml`
Cron `0 6 1 * *` (1st of each month) + `workflow_dispatch`. Upgrades NuGet packages to their latest **minor/patch** (`dotnet outdated --upgrade --version-lock Major`; majors are left to deliberate review), opens a `fix(deps): monthly dependency refresh` PR, and enables auto-merge. The `fix(deps):` commit then drives release-please to cut a patch release — a maintained, re-released build every month. It also runs the **Crowdin two-way sync** (pull translations into the same PR, push `en.json` sources at the end — see Translations below). Needs `RELEASE_PLEASE_PAT` to run the PR's CI and the merge/release unattended (a `GITHUB_TOKEN`-created PR doesn't trigger workflows); the Crowdin steps additionally need the `CROWDIN_*` secrets and skip without them.

### `release.yml`
Tag push `v*.*.*` + `workflow_dispatch`. Publishes the self-contained x64 build **with ReadyToRun** (`-p:PublishReadyToRun=true`) — crossgen2 AOT-compiles the app + Avalonia to native images so startup skips JITing those paths (measured ~halved framework startup; ~16 MB larger). The JIT stays as a fallback, so the runtime-XAML custom-theme feature is unaffected (unlike NativeAOT, which would break it). Then it compiles the Inno installer, creates the GitHub Release (with the installer's SHA-256 in the body), and fans out to winget / Scoop / Chocolatey (each `continue-on-error`, so one channel's flake doesn't block the rest). Builds ship **unsigned** — see Code signing below.

### Translations (Crowdin)
Community translation runs on [Crowdin](https://crowdin.com/project/menyou). There's **no standalone workflow** — the sync is folded into `monthly-maintenance.yml`, keyed off the repo-root `crowdin.yml` (source `src/MenYou/Languages/en.json`, targets `%two_letters_code%.json`). Each month it:
- **pulls** completed translations from Crowdin into the monthly refresh PR, so they ship in that month's release; and
- **pushes** the current `en.json` source strings back to Crowdin at the end, so newly added strings become translatable.

Both steps self-skip when the `CROWDIN_*` secrets are absent. A one-off seed of the locale files already in the repo is available via [`tools/crowdin-upload.ps1`](../tools/crowdin-upload.ps1) (run once after adding the target languages in Crowdin).

## Conventional Commits

Version bump + CHANGELOG key off the merged-commit title (= the PR title under squash-merge):

| Prefix | CHANGELOG | Bump |
|---|---|---|
| `feat:` | Features | minor |
| `fix:` / `perf:` / `deps:` / `revert:` | Bug Fixes / Performance / Dependencies / Reverts | patch |
| `docs:` / `chore:` / `refactor:` / `test:` / `build:` / `ci:` | hidden | none |

Append `!` for breaking changes. Pre-1.0, breaking changes bump the **minor** version (`bump-minor-pre-major: true` in `release-please-config.json`).

## Code signing

MenYou ships **unsigned**. No free Authenticode path clears the Windows SmartScreen "unrecognized app" warning for an app like this — EV certificates stopped bypassing SmartScreen in 2024, and the only $0 routes (a sponsored OSS signer, or the Microsoft Store) don't fit a low-level shell-integration app. So the release notes publish the installer's **SHA-256** for integrity instead, and the SmartScreen prompt is documented for users.

## Distribution (all free)

- **GitHub Releases** — the anchor; the Inno installer is uploaded per tag and the in-app updater reads the same `releases/latest` feed.
- **winget** — `winget install Alpaq.MenYou` (publisher identity `Alpaq`).
- **Scoop** — `scoop bucket add menyou https://github.com/Alpaq92/scoop-menyou; scoop install menyou`.
- **Chocolatey** — `choco install menyou`.

## Required secrets

| Secret | Purpose | Required? |
|---|---|---|
| `RELEASE_PLEASE_PAT` | PAT (`repo` + `workflow`). Lets merged PRs / pushed tags trigger downstream workflows that `GITHUB_TOKEN` can't. Powers release-please, auto-merge, and the monthly cron. | **Strongly** |
| `WINGET_PAT` | `public_repo` PAT for `winget-releaser`. | for winget |
| `SCOOP_PAT` | PAT for the `Alpaq92/scoop-menyou` bucket repo. | for Scoop |
| `CHOCO_API_KEY` | chocolatey.org API key. | for Chocolatey |
| `CROWDIN_PROJECT_ID` / `CROWDIN_PERSONAL_TOKEN` | Crowdin numeric project ID + personal token (Projects scope). Enable the monthly translation sync. | for translations |

## Required GitHub settings

All of these are currently **on**; they have to stay on for the chain to work end to end:

- **Allow auto-merge** (Settings → General → Pull Requests) — every auto-merge path uses `gh pr merge --auto` / `merge_method: squash`.
- **Allow GitHub Actions to create and approve pull requests** (Settings → Actions → General) — release-please, auto-approve, and the auto-merge workflows all need it.
- **Branch ruleset on `main`** (Settings → Rules → Rulesets; versioned at `.github/rulesets/main-branch-protection.json`): block deletion + force-push, linear history, PRs only (squash, thread resolution, last-push approval, **0 required approvals**), required status check **`build`**, CodeQL code-scanning (high+), with the **repository admin** role as bypass actor.
- **Dependency graph** (Settings → Code security and analysis) — needed by the `dependency-review` job; enable it to clear that check.

## Releasing

The default path is automatic: merge Conventional-Commit PRs to `main` → release-please opens a release PR → it auto-merges when CI is green → the `vX.Y.Z` tag triggers `release.yml`. To cut one out of band:

```powershell
git tag -a v0.2.0 -m "v0.2.0"
git push origin v0.2.0
```

`release.yml` handles both entry points identically.

## One-time setup

1. Set the secrets above (at minimum `RELEASE_PLEASE_PAT`).
2. Create the `Alpaq92/scoop-menyou` bucket repo (a `bucket/` folder; the workflow seeds it).
3. Register at chocolatey.org and copy the API key into `CHOCO_API_KEY` (first publish is manually moderated, 1–7 days).
4. Keep build identity stable across releases (for winget + reproducible builds): `AssemblyName` / product / company / copyright in `MenYou.csproj`, the `app.manifest` compatibility + DPI declarations, deterministic-build flags, and the `Alpaq` publisher identity.
