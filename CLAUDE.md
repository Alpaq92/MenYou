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

## Windows Defender false positives — settled; read this before theorising

Defender flags MenYou installers as `Trojan:Win32/Wacatac.B!ml`. **Nothing in
this repo causes it and no code change can fix it.** Three releases have already
been spent on wrong theories. Start from the facts below.

### Detection history (ThreatID 2147735505 unless noted)

| Release | Asset flagged | Notes |
|---|---|---|
| 0.9.15 | `MenYou-fd-Setup` (×3) | first occurrence |
| 0.9.16 | — clean | *mistaken for a fix* (see theory #1) |
| 0.9.17, 0.9.18 | — clean | still just luck |
| 0.9.19 | `MenYou-fd-Setup` | with `taskkill` in place → theory #1 dead |
| 0.9.20 | **`MenYou-Setup`** (self-contained) | first release with no FD variant → theory #2 dead |
| 0.9.22 | **`MenYou-Setup`** | no functional change since 0.9.20 |

The same verdict also hits `wxNote-*-Setup.exe` in the owner's unrelated
`wx-notepad-plus-plus` repo — it is not MenYou-specific.

### Why MenYou specifically, and why it will keep happening

"Unsigned and low-prevalence" is necessary but not sufficient — plenty of
unsigned apps are never flagged. MenYou's own feature set is the rest of it:

| API / behaviour | Legitimate use here | How a classifier reads it |
|---|---|---|
| `WH_KEYBOARD_LL` (×3) | intercept the lone Win-key tap | **keylogger** |
| `WH_MOUSE_LL` (×4) | catch taskbar Start-button clicks | input interception |
| `WH_GETMESSAGE` mapping `MenYou.Bridge.dll` into `explorer.exe` | Win-key handling on 24H2 | **DLL injection into a system process** |
| `AdjustTokenPrivileges` (`SE_SHUTDOWN_NAME`) | the power menu | privilege manipulation |
| `...CurrentVersion\Run` **and** `schtasks` | start with Windows | **dual persistence** |

That is the canonical profile of a keylogger with persistence and injection.
`Wacatac` is Microsoft's generic bucket for precisely this shape. Every item is
required by a shipped feature and none can be dropped. (For the record the
injection is already done the *clean* way — `SetWindowsHookEx(WH_GETMESSAGE)`,
OS-mediated, as Open-Shell does it — not `CreateRemoteThread` +
`WriteProcessMemory`. There is no less-suspicious technique left to switch to.)

**Consequence: expect a flag on any release, forever, until the binaries are
signed.** A clean release is luck, not evidence of a fix — that inference has
now been drawn wrongly twice.

### What was measured

- **Not reproducible offline.** `MpCmdRun -Scan` returns clean on: the whole
  publish payload file-by-file, a locally built Inno installer, that same
  installer with a GitHub Mark-of-the-Web (`ZoneId=3`) attached, and the
  self-contained installer. The verdict only appears on download, so it is a
  **cloud call**, not a signature match on the bytes.
- **The reporting machine is stock.** `CloudBlockLevel: 0` (default),
  `DisableBlockAtFirstSeen: False`, tamper protection on. Not an
  over-aggressive local configuration — other users hit this too.
- **Per-hash and nondeterministic.** Every release mints fresh hashes for every
  asset, so each release is an independent roll.
- **Prevalence alone is insufficient.** `MenYou-Setup` ships via winget *and*
  Chocolatey and was still flagged on a direct download.

### Dead theories — do not revisit

1. **The installer's WMI `Terminate()` call** (0.9.15). Reverted to `taskkill`
   in 0.9.16; 0.9.19 was flagged anyway. Cost: one release.
2. **The framework-dependent variant was somehow special** (through 0.9.19 every
   detection had landed on it). Retired in 0.9.20; the self-contained installer
   was flagged on that very release. Cost: one release. *FD stays retired — its
   other justification, that "half the size" ignored the ~55 MB runtime it
   required, is sound and unaffected.*
3. **Inno packaging / compression entropy.** Locally built installers scan
   clean, so content is not the trigger.
4. **A new release will shake it off.** 0.9.22 had no functional change
   whatsoever and was flagged. Cutting a release to re-roll the hash is a coin
   flip, not a remedy — and it burns a version number.

### The only two things that work

Both need the repo owner — they involve a Microsoft account sign-in, identity
verification, or payment, so an agent cannot complete them.

- **WDSI submission** — <https://www.microsoft.com/en-us/wdsi/filesubmission>,
  choose **"Software developer"** ("software providers wanting to validate
  detection of their products"). Clears **one hash** and nothing after it, so it
  must be repeated per release — ideally *before* announcing a build.
- **Code signing** — the durable fix, and given the behavioural profile above,
  effectively the only one. Routes worth evaluating: **Azure Trusted Signing**
  (Microsoft-operated CA, subscription-priced, open to individuals subject to an
  identity-history check, has a first-party GitHub Action), **SignPath
  Foundation** (free for qualifying OSS, approval queue), **Certum Open Source**.
  Verify current pricing/eligibility directly — they change.

Note that `docs/AUTOMATION.md` originally argued against signing purely on
**SmartScreen** grounds (EV stopped bypassing it in 2024, no free route fits).
That is true and irrelevant: Defender's ML classifier is a separate mechanism
for which a trusted Authenticode signature is a strong negative signal. The two
decisions must be made separately, and conflating them is why signing was never
pursued.

### Wiring signing when a certificate exists

`installer/inno/menyou.iss` is **already prepared**: it honours
`/DMySignTool="<name>"` and sets `SignedUninstaller=yes`, so Inno signs both the
installer and the uninstaller. What is missing is a step in `release.yml`. Keep
it inert without secrets so an absent certificate can never break a release:

```pwsh
# In the "Compile installer (Inno Setup)" step, before the iscc calls:
$signArgs = @()
if ($env:SIGNTOOL_CMD) {
  # /S<name>=<command> defines the tool; $f is Inno's placeholder for the file.
  $signArgs += "/Ssigner=$env:SIGNTOOL_CMD"
  $signArgs += "/DMySignTool=signer"
}
& $iscc @signArgs "/DMyAppVersion=$v" "/DMyPublishDir=..." "installer\inno\menyou.iss"
```

with `env: SIGNTOOL_CMD: ${{ secrets.SIGNTOOL_CMD }}` on the step. Sign the
**payload** binaries (`MenYou.exe`, `MenYou.Bridge.dll`) before packing as well
— the classifier unpacks installers. Then drop the "Unsigned build" paragraph
from the release-notes template in `release.yml`.

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
