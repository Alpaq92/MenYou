# MenYou automation

End-to-end map of how a change gets from a PR to users — what fires
when, what gates on what, what to configure once. Single source of
truth for CI/CD + code signing + distribution.

## TL;DR

```
[ open a PR ]
      │
      ├─ CI build (build.yml)             — restore → build → publish → dry-compile Inno installer
      ├─ CodeQL    (codeql.yml)            — static analysis on C#
      ├─ Security  (security.yml)          — gitleaks + dependency-review + trivy
      └─ CodeRabbit (auto, no workflow)    — review + APPROVED / CHANGES_REQUESTED

[ approval lands from CODEOWNER / collaborator / coderabbitai ]
      │
      └─ 7-day soak (auto-merge.yml)
            │
            └─ all 3 checks green → squash-merge to main

[ main moves ]
      │
      └─ release-please.yml                — opens / updates "chore(main): release X.Y.Z" PR
            │                                  • bumps .release-please-manifest.json
            │                                  • prepends CHANGELOG section
            │                                  • auto-merges itself when CI is green
            │
            └─ release PR merges → tag v<X.Y.Z> pushed
                  │
                  └─ release.yml           — publish + sign + GH Release
                        │
                        ├─ publish_winget       (winget-releaser action)
                        ├─ publish_scoop        (push to Alpaq92/scoop-menyou bucket)
                        └─ publish_chocolatey   (choco push)
```

## Per-workflow detail

### `.github/workflows/build.yml`
**Trigger:** push to `main`, pull_request to `main`, `workflow_dispatch`.
**Skips:** docs-only changes (`**.md`, `docs/**`, `LICENSE`).
**Does:** restore → `dotnet build` → `dotnet publish` (win-x64,
self-contained) → Inno Setup `iscc` dry-compile. Fails on any error.
The artifact is **not** uploaded — the release pipeline rebuilds at tag
time with the actual version.

### `.github/workflows/codeql.yml`
**Trigger:** push to `main`, PR to `main`, cron Mondays 06:17 UTC.
**Does:** `github/codeql-action` with `security-and-quality` queries
against the managed C# tree (`src/MenYou/MenYou.csproj`). Skips the
native bridge — that has no managed surface. Results land in the
**Security** tab.

### `.github/workflows/security.yml`
**Trigger:** push to `main`, PR to `main`.
**Three jobs, all independent:**
- **gitleaks** — full-history secret scan.
- **dependency-review** (PR-only) — fails on HIGH/CRITICAL CVE introduced
  by NuGet manifest changes.
- **trivy** — filesystem scan for HIGH/CRITICAL vulnerable code, SARIF
  upload. `exit-code: 0` so findings don't block merge — they're
  triaged in the Security tab.

### `.github/workflows/auto-merge.yml`
**Trigger:** cron every 6h, `pull_request_review` submit, `workflow_run`
on (build, CodeQL, Security) completion, `workflow_dispatch`.

**Eligibility (all must hold):**
1. Not a draft.
2. No open `CHANGES_REQUESTED` reviews.
3. At least one `APPROVED` review from one of:
   - A user listed in `.github/CODEOWNERS`
   - A repo collaborator
   - `coderabbitai[bot]`
4. The latest eligible approval is **≥ 7 days old**. The clock starts
   at *approval time*, not PR creation — a stale PR that just got
   approved doesn't merge instantly. Dependabot PRs skip this (handled
   by `dependabot-auto-merge.yml`).
5. No failing or pending check runs on the head SHA.
6. PR is mergeable (no conflicts).

When all hold, the PR is squash-merged via the GitHub REST API. The
merge commit uses `RELEASE_PLEASE_PAT` so it triggers downstream
workflows (notably `release-please.yml` on the bumped `main`).

### `.github/workflows/dependabot-auto-merge.yml`
**Trigger:** PR opened.
**Filter:** actor is `dependabot[bot]` AND ecosystem is `nuget` or
`github_actions`. Enables GitHub's **native auto-merge** (`gh pr merge
--auto --squash`) — the PR merges as soon as required checks pass,
without the 7-day soak.

### `.github/workflows/auto-approve-chore.yml`
**Trigger:** `pull_request_target` on opened / reopened /
ready_for_review / synchronize, base = `main`.
**Filter:** non-draft + title prefix in `{chore, ci, docs, refactor,
build}` + author in `{Alpaq92, github-actions[bot], dependabot[bot]}`.
**Does:** posts an `APPROVED` review under `github-actions[bot]` so the
branch ruleset's "Require approval" gate is satisfied without a human
review. Feature / fix PRs are **deliberately excluded** — those still
get reviewed (by you or CodeRabbit).

### `.github/workflows/release-please.yml`
**Trigger:** push to `main`, `workflow_dispatch`.
**Does:** `googleapis/release-please-action@v4` reads Conventional
Commits since the last release, opens / updates the "Release X.Y.Z"
PR (bumps version + appends CHANGELOG section), and queues that PR for
auto-merge via `gh pr merge --auto --squash`. When the release PR
merges, it pushes the tag `vX.Y.Z`. The tag push triggers
`release.yml` (which is the publish pipeline).

### `.github/workflows/release.yml`
**Trigger:** tag push matching `v*.*.*`, `workflow_dispatch`.
**Does:** publish self-contained x64 → Inno Setup `iscc` → optional
SignPath signing → upload to GitHub Release → fan-out to winget / Scoop /
Chocolatey.

Signing is **optional**. When SignPath secrets are absent the
workflow still publishes the release; a `⚠ Unsigned build` notice is
appended to the body and the per-channel publish jobs continue normally.

**Job graph:**

```
on: push tags v*.*.*  (or workflow_dispatch)
 │
 ├── build      (windows-latest)
 │     ├── dotnet publish -c Release -r win-x64 --self-contained
 │     ├── iscc (Inno Setup)   ─→ dist/MenYou-Setup-X.Y.Z.exe
 │     ├── upload unsigned     (only when SignPath configured)
 │     ├── SignPath sign       (skipped when secrets missing)
 │     └── upload signed artifact (canonical: menyou-installer)
 │
 ├── github_release  (ubuntu-latest)
 │     ├── download menyou-installer
 │     ├── build body.md (signed/unsigned banner + install commands)
 │     └── softprops/action-gh-release ─→ GH Release for the tag
 │
 ├── publish_winget       (windows-latest, continue-on-error)
 │     └── vedantmgoyal9/winget-releaser
 │
 ├── publish_scoop        (ubuntu-latest, continue-on-error)
 │     ├── compute SHA256 of Setup.exe
 │     ├── render installer/scoop/menyou.json.template
 │     └── push to Alpaq92/scoop-menyou bucket repo
 │
 └── publish_chocolatey   (windows-latest, continue-on-error)
       ├── render installer/chocolatey/menyou.nuspec
       ├── choco pack
       └── choco push ─→ push.chocolatey.org
```

## Conventional Commits — quick reference

The version-bump and CHANGELOG generation key off the merged-commit
title (which equals the PR title under squash-merge). Use these
prefixes:

| Prefix | CHANGELOG section | Bumps version |
|---|---|---|
| `feat:` | Features | minor (`0.x.0`) |
| `fix:` | Bug Fixes | patch (`0.0.x`) |
| `perf:` | Performance | patch |
| `deps:` | Dependencies | patch |
| `revert:` | Reverts | patch |
| `docs:` | (hidden) | none |
| `chore:` | (hidden) | none |
| `refactor:` | (hidden) | none |
| `test:` | (hidden) | none |
| `build:` | (hidden) | none |
| `ci:` | (hidden) | none |

Append `!` for breaking changes (`feat!:` → major bump). For pre-1.0
releases, breaking changes bump the **minor** version per SemVer
section 4 — release-please knows this via `bump-minor-pre-major: true`
in `release-please-config.json`.

---

## Code signing

### Why sign at all?

Unsigned `.exe` files on Windows hit two friction walls:

1. **SmartScreen / "Windows protected your PC"** — unsigned binaries
   that haven't built reputation get blocked with a full-screen
   warning. Users have to click *More info → Run anyway*, which most
   won't.
2. **Authenticode warnings** — UAC, the Properties dialog, AV
   heuristics, and corporate AppLocker policies all look at the
   Authenticode signature. No signature ≈ "Unknown publisher".

Signing doesn't bypass SmartScreen the *first* time a brand-new cert
goes out — Microsoft's reputation algorithm still needs downloads to
warm up. **EV (Extended Validation)** certs are the exception: they
skip the reputation wait. EV is expensive (~$250–500/yr) and requires
a hardware token, so it's outside scope for a free project.

What signing **does** buy you even on day one:

- A real publisher name in the UAC dialog instead of "Unknown".
- AV engines stop quarantining on heuristics-only.
- Corporate "signed by a trusted CA" policies let your app through.
- Reputation accrues over time — SmartScreen quiets down naturally.

### Free signing options

#### SignPath Foundation (what the pipeline targets)

- **URL**: <https://signpath.org/foundation>
- **Cost**: Free for qualifying open-source projects.
- **Cert type**: OV (Organization Validated) code-signing cert, HSM-
  backed in the Foundation's infrastructure. Issued to "SignPath
  Foundation" with the project name in the description.
- **How it works**: CI uploads the unsigned artifact to SignPath via
  the `signpath/github-action-submit-signing-request` action;
  SignPath signs it with the Foundation's HSM-resident cert and
  returns the signed file. Keys never leave SignPath's HSM.
- **Requirements (typical)**:
  - OSI-approved open-source license. ✓ (MenYou is MIT.)
  - Public source repository. ✓
  - Real maintainer identity (not anonymous).
  - Reproducible build from CI (GitHub Actions). ✓
- **Reputation**: The Foundation cert has been around long enough
  that SmartScreen reputation transfers partially.
- **Caveat**: Review queue. Onboarding takes 1–3 weeks.

#### Azure Trusted Signing

- **URL**: <https://learn.microsoft.com/azure/trusted-signing/>
- **Cost**: $9.99/month at the "Basic" tier.
- **Cert type**: Short-lived (3-day) Microsoft-issued certs.
- **Pros**: Microsoft is the issuer → SmartScreen reputation is
  much faster than third-party OV certs.
- **Cons**: Requires Azure subscription + identity validation (D-U-N-S
  number for organizations, government ID for individuals).

#### Sigstore / cosign

- **URL**: <https://www.sigstore.dev/>
- **Format**: Sigstore signatures are **not** Authenticode. Don't
  satisfy SmartScreen / UAC.
- **Use as**: Supplement to Authenticode, not a substitute.

#### Self-signed certificate

- Self-signed certs aren't trusted by any client machine unless the
  user manually imports them. SmartScreen treats this as worse than
  no signature.
- **Use case**: Internal tools where you control the deployment
  machines. Not for public distribution.

### Cheap paid options (fallback)

| Option | Annual cost | Cert type | Notes |
|---|---|---|---|
| **Certum Open Source** | ~€25/yr | OV | Polish CA. Issued only to open-source maintainers. Cheapest legit code-signing cert in existence. [Shop link](https://shop.certum.eu/data-safety/code-signing-certificates/certum-opensource-code-signing.html) |
| **SSL.com** | ~$129/yr | OV (eSigner cloud) | Cloud signing, no HSM needed. |
| **DigiCert / Sectigo EV** | ~$250–500/yr | EV | Hardware token. Bypasses SmartScreen reputation wait. |

**Certum's open-source cert is the realistic fallback** if SignPath's
Foundation queue is too slow.

---

## Distribution channels (all free)

- **GitHub Releases** — anchor. The Inno Setup installer
  (`MenYou-Setup-X.Y.Z.exe`) is uploaded for every tag; the in-app
  updater reads this same `releases/latest` feed.
- **winget** — auto-published. Users: `winget install Alpaq.MenYou`.
- **Scoop** — auto-published to the `Alpaq92/scoop-menyou` bucket.
  Users: `scoop bucket add menyou https://github.com/Alpaq92/scoop-menyou; scoop install menyou`.
- **Chocolatey** — auto-published. Users: `choco install menyou`.
- **Microsoft Store** — would require MSIX + a developer account
  ($19 one-time individual). Outside the free path.

---

## Required GitHub secrets

| Secret | Purpose | Required? |
|---|---|---|
| `RELEASE_PLEASE_PAT` | PAT with `repo` scope. Lets release-please's merged release PR push a tag that *triggers* `release.yml`. Without it, the tag pushes but no workflow fires — you'd have to re-tag manually. Also used by auto-merge for the same reason. | Strongly recommended |
| `WINGET_PAT` | PAT with `public_repo` scope. Used by `winget-releaser` to open a PR against its winget-pkgs fork. | Required for winget |
| `SCOOP_PAT` | PAT for the `Alpaq92/scoop-menyou` bucket repo. | Required for Scoop |
| `CHOCO_API_KEY` | chocolatey.org API key. | Required for Chocolatey |
| `SIGNPATH_API_TOKEN` | SignPath personal API token. Triggers Authenticode signing when present. | Optional |
| `SIGNPATH_ORG_ID` | SignPath organization ID. | Optional |
| `SIGNPATH_PROJECT_SLUG` | SignPath project slug. | Optional |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Defaults to `release-signing`. | Optional |

When any optional group is missing, the workflow notices at runtime
(per-job gate steps) and the corresponding step self-skips. The
release still ships.

## Required GitHub settings

The settings below have to be on for the automation chain to work end
to end. Configure under **Settings → General → Pull Requests** unless
noted.

- **Allow auto-merge** ✓ — required by `gh pr merge --auto`.
- **Allow GitHub Actions to create and approve pull requests** ✓ —
  required by `auto-approve-chore.yml`.
- **Squash merging** ✓ (and ideally the only enabled merge style) —
  every auto-merge path uses `merge_method: 'squash'`.
- Branch rules on `main` (**Settings → Rules → Rulesets**):
  - Require status checks: `build`, `Analyze (csharp)`, `Secret scan
    (gitleaks)`. Add `Dependency review` and `Trivy filesystem scan`
    once you've watched them stay reliable.
  - Require approval of most recent reviewable push: ✓.
  - Bypass actors: **Repository admin role** (so you can override a
    flaky check via the UI), **Deploy keys** (not applicable here).
- **Settings → Branches → Default branch**: `main`.
- **Settings → Code security and analysis**: enable Dependabot alerts
  + Dependabot security updates.

## Releasing

The default path is **automatic**:

1. Merge PRs to `main` using Conventional Commits in their titles
   (`feat:`, `fix:`, etc).
2. release-please opens a "chore(main): release X.Y.Z" PR that bumps
   the version and writes the next CHANGELOG section. It auto-merges
   when CI is green.
3. The merge pushes the tag `vX.Y.Z`, which triggers `release.yml`.

To cut a release **out of band** (hotfix, rollback, etc), tag manually:

```powershell
git tag -a v0.2.0 -m "v0.2.0"
git push origin v0.2.0
```

The workflow handles both entry points identically.

---

## One-time setup (do these once)

1. **Apply to SignPath Foundation** — start the queue early.
   <https://signpath.org/foundation>
2. **Create a Scoop bucket repo**: `Alpaq92/scoop-menyou` with a
   `bucket/` folder. Empty is fine — the workflow seeds it.
3. **Register at Chocolatey Community**: <https://community.chocolatey.org/account/Register>
   Copy your API key into the `CHOCO_API_KEY` secret. First-time
   moderation is manual (1–7 days); subsequent releases auto-approve.
4. **Set the secrets** listed above.
5. **Push the first tag**. The release will go out unsigned with a
   warning banner; signing flips on the moment SignPath approves.

## Things to keep stable across releases

These don't depend on having a cert yet and make Authenticode
reputation accrue cleanly:

- **Stable AssemblyName / Product / Company / Copyright** in
  `MenYou.csproj`. SmartScreen reads these.
- **`app.manifest`** declaring Win10/11 compatibility GUIDs, DPI
  awareness (`PerMonitorV2`), and `requestedExecutionLevel asInvoker`.
- **Deterministic build** flags: `<Deterministic>true</Deterministic>`,
  `<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>`.
- **Stable publisher identity** — pick `Alpaq` and use it
  consistently in csproj `<Authors>`, repo metadata, installer
  publisher field, and winget manifest. Authenticode reputation
  accrues against this string.
