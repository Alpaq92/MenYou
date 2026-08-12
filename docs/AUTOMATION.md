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
              ├─ publish → 3 Inno installers: win-x64 (self-contained),
              ├─ GitHub Release (all installer assets + SHA-256, unsigned)
              └─ fan-out: winget · Chocolatey  (each continue-on-error;
                          self-contained x64 only — arm64 + FD are direct-download)
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
Tag push `v*.*.*` + `workflow_dispatch`. Publishes two builds — self-contained x64 and native win-arm64 — both **with composite ReadyToRun** (`-p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true`), which crossgen2 AOT-compiles the app + Avalonia to native images so startup skips JITing those paths (measured ~halved framework startup; ~16 MB larger). *Composite* emits one native image for the whole self-contained closure rather than one per assembly, so cross-assembly calls resolve directly instead of through indirection stubs — slower build, larger output, both taken against startup. The JIT stays as a fallback, so the runtime-XAML custom-theme feature is unaffected (unlike NativeAOT, which would break it). Then it compiles the two Inno installers (`MenYou-Setup`, `MenYou-arm64-Setup`), creates the GitHub Release (with each installer's SHA-256 in the body), and fans out to winget / Chocolatey (each `continue-on-error`, so one channel's flake doesn't block the rest). A third **framework-dependent** installer (`MenYou-fd-Setup`, direct-download only) shipped through 0.9.19 and was retired in 0.9.20 — see [`OPTIMIZATION.md`](OPTIMIZATION.md) §3. Builds ship **unsigned** — see Code signing below. (ReadyToRun, the pdb exclusion, and the rest of the startup/packaging tuning are written up in [`OPTIMIZATION.md`](OPTIMIZATION.md).)

### `crowdin-badge.yml`
Daily cron + `workflow_dispatch`. Publishes the README's **dynamic localization-percentage badge** straight from the live Crowdin project. Crowdin's own badge (`badges.crowdin.net/menyou/localized.svg`) only renders for a **public** project with *Settings → General → Badges → "Display badges"* enabled — otherwise it 403s. This workflow is the visibility-agnostic alternative: it calls the Crowdin API (`GET /projects/{id}/languages/progress`) with the existing `CROWDIN_*` secrets, computes the **words-weighted overall translated %** (the same metric Crowdin's "localized" badge uses), and force-pushes a [shields.io endpoint](https://shields.io/badges/endpoint-badge) JSON to an **orphan `badges` branch** (force-pushed each run, so it never accumulates history). The README badge then points at `img.shields.io/endpoint?url=…/badges/crowdin-localization.json`. Self-skips when the `CROWDIN_*` secrets are absent. To use it instead of the native badge, swap the one commented line at the top of `README.md`.

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

MenYou ships **unsigned**, and the release notes publish each installer's **SHA-256** for integrity instead.

The original reasoning was about SmartScreen: no free Authenticode path clears the "unrecognized app" warning, EV certificates stopped bypassing SmartScreen in 2024, and the $0 routes (a sponsored OSS signer, the Microsoft Store) don't fit a low-level shell-integration app. That is still true — **but it is the wrong frame for the problem that actually bites.**

Windows Defender has repeatedly flagged MenYou installers as `Trojan:Win32/Wacatac.B!ml`: 0.9.15 and 0.9.19 (`MenYou-fd-Setup`), then 0.9.20 (`MenYou-Setup`, self-contained — the first release with no FD variant, which is what disproved the theory that the FD build was somehow special). This is a **different mechanism from SmartScreen**. It is a cloud ML verdict issued at download time, and its two dominant inputs are *unsigned* and *zero prevalence*:

- It is not reproducible offline. The payload files, a locally built installer, and that installer carrying a GitHub Mark-of-the-Web all scan clean under `MpCmdRun`. Nothing in the bytes is objectionable; no change to `menyou.iss` can cause or cure it. (A 0.9.16 revert made on the theory that the installer's WMI `Terminate()` call triggered it was simply wrong.)
- Every release mints a fresh hash for every asset, so each release is an independent roll of the dice.
- Package-manager prevalence is not sufficient on its own — the self-contained installer ships via winget and Chocolatey and was flagged anyway on a direct download.

And "unsigned + low prevalence" is only half of it — plenty of unsigned apps are never flagged. MenYou's own feature set supplies the rest: three `WH_KEYBOARD_LL` hooks (reads as a keylogger), four `WH_MOUSE_LL` hooks, a `WH_GETMESSAGE` hook that maps `MenYou.Bridge.dll` into `explorer.exe` (injection into a system process), `AdjustTokenPrivileges` for `SE_SHUTDOWN_NAME`, and persistence via both `...CurrentVersion\Run` and `schtasks`. That is the canonical profile of a keylogger with persistence and injection, and every item is required by a shipped feature. The injection is already done the clean, OS-mediated way (`SetWindowsHookEx`, as Open-Shell does it — not `CreateRemoteThread`), so there is no less-suspicious technique left to adopt. **Expect a flag on any release until the binaries are signed; a clean release is luck, not a fix.**

**A valid Authenticode signature is a strong negative signal for the ML classifier even though it no longer buys a SmartScreen bypass.** The two are worth deciding separately. Options that did not exist (or were not considered) when the section above was written:

| Route | Notes |
|---|---|
| **Azure Trusted Signing** | Microsoft-operated CA, subscription-priced rather than per-certificate, and open to individual developers subject to an identity-history requirement. Has a first-party GitHub Action. Verify current pricing and eligibility before committing. |
| **SignPath Foundation** | Free for qualifying open-source projects; application and approval required. |
| **Certum Open Source** | Long-standing low-cost option for OSS developers; hardware token or cloud signing. |

The installer script is **already wired for this**: pass `/DMySignTool="<signtool cmd $f>"` and Inno signs both the installer and the uninstaller (`SignedUninstaller=yes`). What is missing is a signing step in `release.yml` and a certificate. Until one exists, the only per-file remedy is submitting the flagged hash to [Microsoft WDSI](https://www.microsoft.com/en-us/wdsi/filesubmission), which clears that one build and nothing after it.

## Distribution (all free)

- **GitHub Releases** — the anchor; the Inno installer is uploaded per tag and the in-app updater reads the same `releases/latest` feed.
- **winget** — `winget install Alpaq.MenYou` (publisher identity `Alpaq`).
- **Chocolatey** — `choco install menyou`.

## Required secrets

| Secret | Purpose | Required? |
|---|---|---|
| `RELEASE_PLEASE_PAT` | PAT (`repo` + `workflow`). Lets merged PRs / pushed tags trigger downstream workflows that `GITHUB_TOKEN` can't. Powers release-please, auto-merge, and the monthly cron. | **Strongly** |
| `WINGET_PAT` | `public_repo` PAT for `winget-releaser`. | for winget |
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
3. Register at chocolatey.org and copy the API key into `CHOCO_API_KEY` (first publish is manually moderated, 1–7 days).
4. Keep build identity stable across releases (for winget + reproducible builds): `AssemblyName` / product / company / copyright in `MenYou.csproj`, the `app.manifest` compatibility + DPI declarations, deterministic-build flags, and the `Alpaq` publisher identity.
