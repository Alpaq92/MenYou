# MenYou — Claude working notes

Project memory for Claude Code sessions on MenYou (Windows Start-menu
replacement, .NET 10 + Avalonia 12).

## Release process (release-please) — read before touching releases

Merging conventional-commit PRs to `main` makes **release-please** open/update a
`chore(main): release X.Y.Z` PR that bumps `.release-please-manifest.json` +
`src/MenYou/MenYou.csproj` and regenerates `CHANGELOG.md`. Merging that release
PR tags `vX.Y.Z`, which triggers `release.yml` to build the installer and publish
the GitHub Release.

### ⚠️ Release PRs auto-merge → code fixes keep shipping a release late
The `chore(main): release …` PRs (authored by `github-actions[bot]`) take the
**trusted-author fast path**: `trusted-author-auto-merge.yml` arms native
auto-merge for them, so they merge **the moment their CI passes** (they are
also exempt from `auto-merge.yml`'s 7-day soak; `auto-approve-chore.yml`
approval is not the gate — the ruleset requires 0 approvals). But **code /
installer PRs are NOT auto-approved** — only non-code changes (docs,
`Languages/*.json` i18n, chore) are — so they need manual review and sit `BEHIND`
(the branch ruleset requires up-to-date branches) while the release PR ships
ahead of them. Net effect: a code fix repeatedly **misses the release that cuts
just before it lands**, then trickles into the next one. (This shipped 0.8.3 with
only the i18n fix, then scattered the balloon fix into 0.8.4 and the uninstaller
fix into 0.8.5 — one micro-release per approved fix.)

**Do NOT try to batch by holding the release PR** (`gh pr merge <rp>
--disable-auto`). Tried for 0.8.4 and it failed: while code PRs sit awaiting the
owner's review there is often **no release PR to hold yet** (release-please only
opens/updates it after a fix merges), and once one exists,
`trusted-author-auto-merge.yml` re-arms auto-merge on every `synchronize`
event (each release-please force-push) — the release cuts within minutes of
each fix merging. With sequential approvals, each fix gets its own release.

**Recommendation — owner has NOT yet approved implementing this: make release
PRs skip the trusted fast path** so releases are cut **manually** and the owner
controls when a release ships and what's in it. The precise change is in
`trusted-author-auto-merge.yml`: its author allowlist (`Alpaq92`,
`dependabot[bot]`, `github-actions[bot]`) is what catches release-please's PRs
— add a release-please exclusion to the job's `if:` (head ref
`release-please--branches--main--components--MenYou`, or a `chore(main):
release` title check) rather than dropping `github-actions[bot]` entirely
(that would also slow the monthly dependency-refresh PR). Leave
`auto-approve-chore.yml` unchanged — approval is not what merges these PRs.
Until that change is made there is no reliable way to batch from the outside;
the closest is the owner approving all queued fix PRs back-to-back and
accepting whatever grouping the merge/release race produces.

**Second confirmed instance (0.9.0 pin, 2026-07-11):** `--disable-auto` is not
durable even for the narrower goal of landing a `Release-As` pin ahead of the
release PR. Sequence: disarmed auto-merge on the open release PR (`autoMergeRequest:
null` confirmed) → opened a docs PR carrying `Release-As: 0.9.0` → before that
docs PR merged, the release PR re-armed (a `synchronize` event — not
necessarily one this session caused) and merged on its own, cutting **0.8.16**
instead of 0.9.0. The `Release-As` pin then landed a few minutes later, so
release-please correctly opened a follow-up release PR for 0.9.0 on top of
0.8.16 and *that* shipped — but as two releases 22 minutes apart instead of
one. Net: treat any window between disarming a release PR and landing the
commit you're racing it for as unprotected; verify the release PR's state
again immediately before relying on it still being held, not just once after
the `--disable-auto` call.

### ⚠️ `Release-As:` is only parsed from top-level commit messages
A footer buried inside the **bulleted sub-messages of a multi-commit squash**
(GitHub's default squash body for a 2+-commit PR) is IGNORED — PR `#73` carried
`Release-As: 0.9.0` inside its first bullet and release-please still computed
0.8.16. Pin the version with a commit whose OWN top-level message ends with the
footer: a **single-commit** PR squash preserves the message verbatim (proven by
PR `#64`), so a docs/chore micro-PR is the reliable vehicle. Direct pushes to
`main` are blocked by the ruleset, so the empty-commit approach from
release-please's docs doesn't work here.

### ⚠️ `Release-As:` is sticky
A `Release-As: X.Y.Z` commit footer overrides the computed version on every run
until the commit carrying it is released. A stale footer pointing at an
already-shipped version makes release-please try to **re-cut** that version (it
caused a 0.8.1 re-release loop). Pin only to the version you actually intend to
cut next, and confirm the manifest matches.

## Windows Defender false positives — settled, don't re-litigate

Defender periodically flags a MenYou installer as `Trojan:Win32/Wacatac.B!ml`
(0.9.15 and 0.9.19 on `MenYou-fd-Setup`, 0.9.20 on `MenYou-Setup`). **Nothing in
the repo causes it and no code change fixes it.** Two attempts already burned a
release each, so before theorising again, the established facts:

- **Not reproducible offline.** The payload files, a locally built installer,
  and that installer carrying a GitHub Mark-of-the-Web all scan clean under
  `MpCmdRun -Scan`. The verdict is a **cloud call made at download time**; its
  dominant inputs are *unsigned* and *zero prevalence*, not the bytes.
- **Nondeterministic per hash.** Every release mints a fresh hash for every
  asset, so each release is an independent roll. A clean release is not
  evidence that anything was fixed — that mistake has now been made twice.
- **Wrong theory #1 (0.9.16):** the installer's WMI `Terminate()` call. Reverted
  to `taskkill`; 0.9.19 was flagged anyway.
- **Wrong theory #2 (0.9.20):** the framework-dependent variant was somehow
  special because all detections so far had landed on it. Retired it; the
  self-contained installer was flagged on the very next release. (FD stays
  retired — the *other* reason, that "half the size" ignored the ~55 MB runtime
  it required, is sound.)
- **Prevalence alone is insufficient.** `MenYou-Setup` ships via winget *and*
  Chocolatey and was still flagged on a direct download.

Only two things actually work, and both need the repo owner (identity
verification, payment, or a Microsoft account sign-in): a **WDSI submission**,
which clears one hash and nothing after it, and **code signing**, which is the
durable fix. `menyou.iss` is already wired for signing (`/DMySignTool`,
`SignedUninstaller=yes`) — what's missing is a `release.yml` step and a
certificate. Note that `docs/AUTOMATION.md` used to argue against signing purely
on SmartScreen grounds; that reasoning does not transfer to Defender's ML
classifier, for which a valid Authenticode signature is a strong negative signal
regardless of SmartScreen. See `docs/AUTOMATION.md` § Code signing and
`docs/OPTIMIZATION.md` §3.

## Changelog

`CHANGELOG.md` is release-please-generated — don't hand-edit released sections.
Per `release-please-config.json`, only `feat` (Features) and `fix` (Bug Fixes)
are shown; `docs`, `chore`, `refactor`, `test`, `build`, `ci` are hidden. So a
docs-only PR lands on `main` but produces no changelog line (by design).

## Localization

13 bundles in `src/MenYou/Languages/*.json`; `en.json` is the reference key set —
keep every file in sync (same keys). System labels ("Settings", "Pinned", …)
resolve live from Windows shell DLLs via `Platform/Windows/Strings.cs`; only
MenYou-specific strings live in the JSON bundles.
