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
The `chore(main): release …` PRs are auto-approved (`auto-approve-chore.yml`) and
auto-merged (`auto-merge.yml`) **the moment their CI passes**. But **code /
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
opens/updates it after a fix merges), and once one exists the auto-merge
workflow re-arms it on every update — the release cuts within minutes of each
fix merging. With sequential approvals, each fix gets its own release.

**Recommendation — owner has NOT yet approved implementing this: exclude the
release-please branches from the auto-merge/auto-approve workflows** so releases
are cut **manually** and the owner controls when a release ships and what's in
it. Until that change is made there is no reliable way to batch from the
outside; the closest is the owner approving all queued fix PRs back-to-back and
accepting whatever grouping the merge/release race produces.

### ⚠️ `Release-As:` is sticky
A `Release-As: X.Y.Z` commit footer overrides the computed version on every run
until the commit carrying it is released. A stale footer pointing at an
already-shipped version makes release-please try to **re-cut** that version (it
caused a 0.8.1 re-release loop). Pin only to the version you actually intend to
cut next, and confirm the manifest matches.

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
