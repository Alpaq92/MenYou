# MenYou — Start-menu search

How MenYou's search works today, how the real Windows Start menu and Open-Shell
rank their results, and the concrete plan for closing the gap.

This is an engineering record, like [`OPTIMIZATION.md`](OPTIMIZATION.md): every
claim about MenYou and Open-Shell below was read from source (Open-Shell's tree
is vendored at `openshell_research/Open-Shell-Menu-master/`) and independently
re-verified; the Windows behavior is triangulated from the best public evidence
with confidence levels, because Microsoft deliberately keeps the ranker
undocumented. Code references are to MenYou 0.8.5.

The motivating bug: on a Polish system, typing **`ust`** put a TortoiseGit
shortcut named "Settings" *above* the Windows **Ustawienia** (Settings) app —
an ordering the real Start menu would never produce. §1.3 explains exactly why,
item for item.

---

## 1. How MenYou's search works today

### 1.1 Pipeline

A keystroke sets `SearchViewModel.Query`; after an 80 ms debounce the query
runs off-thread (`SearchViewModel.RunSearchAsync` →
`SearchService.SearchAsync`, `src/MenYou/Services/SearchService.cs`). The
service builds **one flat list** from four sources, concatenated in this order:

| # | Source | Matched fields | Notes |
|---|--------|----------------|-------|
| 1 | Discovered apps — Start-Menu `.lnk`/`.url`/`.exe` walk merged with the `shell:AppsFolder` UWP enumeration (`AppDiscoveryService`), served from `discovery-cache.json` when fresh | `DisplayName`, `AlternativeName`, every `SearchAliases` entry | Aliases = target exe basename + the `KnownAppAliases` multilingual group |
| 2 | Eight hard-coded English built-in commands (`SearchService.BuiltInCommands`) | `Title`, exe basename | Suppressed when a discovered app already matched the same name |
| 3 | Control Panel "All Tasks" (GodMode namespace, `ControlPanelEnumerator`) | localized `Name` | Statically cached after first enumeration |
| 4 | Settings deep-links from `%SystemRoot%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml` (`SettingsDeepLinkEnumerator`) | localized `Name`, localized `HighKeywords` blob | Gated by the hand-curated `PageIdToUri` table (105 page→`ms-settings:` arms) |

Each candidate gets an `int Score` from a single `Rank()` function; `score <= 0`
is dropped; the final order is just:

```csharp
results.OrderByDescending(r => r.Score).ThenBy(r => r.Title).Take(40)
```

The view binds the list verbatim — there is **no** grouping by kind, no
apps-before-settings priority, no "Best match" slot, and no view-side
reordering. The first row is auto-selected, so Enter launches it.

### 1.2 The scorer

`Rank(source, query)` (`SearchService.cs:149-176`) lowercases both strings with
`ToLowerInvariant` and returns the first tier that hits:

| Tier | Score | Notes |
|------|-------|-------|
| Exact equality | 1000 | |
| Prefix | `700 − (sourceLen − queryLen)` | longer titles score *lower* |
| Word-start | 500 flat | words split on `space - _ . ( ) [ ]` |
| Substring | `200 − index` | ordinal `IndexOf` |
| Char subsequence | 50 flat | each query char found in order |

The per-candidate score is the **max over all matched fields** — and only the
magnitude survives. Downstream, nothing knows *which field* matched: a hit on a
bolted-on alias scores identically to a hit on the visible title.

Locale notes (verified): the prefix/word-start checks use the parameterless
`string.StartsWith` (CurrentCulture), the substring check is ordinal, and
nothing folds diacritics — so `dzwiek` does **not** match *dźwięku*, and
matching quality varies subtly by code path.

### 1.3 Case study: why `ust` produced the observed order

1. TortoiseGit ships a shortcut literally named `Settings.lnk`.
   `AppDiscoveryService.BuildSearchAliases` looks up `KnownAppAliases` **by
   bare display name with no identity check**, so the shortcut receives the
   whole multilingual "Settings" group — including **"Ustawienia"**
   (`KnownAppAliases.cs:39-41`).
2. `ust` prefix-matches the injected alias: `700 − (10 − 3) = 693`.
3. The real Settings app (UWP, displayed "Ustawienia") scores the **same 693**
   on its title. Field-blind scoring → exact tie.
4. Tie-break is alphabetical: "**S**ettings" < "**U**stawienia", so the
   TortoiseGit row wins the top slot.
5. The deep-links order themselves by the length penalty: *Ustawienia HDR/USB*
   (689), *Lupy/mowy/ujęć* (688), *myszy* (687), *dotyku* (686),
   *dźwięku/grafiki* (685) — reproducing the observed list item for item.

### 1.4 The gaps, ranked

1. **Field-blind scoring** — alias hit ≡ title hit.
2. **Identity-free alias injection** — any `Settings.lnk` gets "Ustawienia".
3. **No usage/frecency input** — although `UserSettings.Recent` already
   persists `(AppId, LastUsedUtc, LaunchCount)` per app, search never reads it.
4. **No result-type ordering** — deep-links interleave freely with apps.
5. **Alphabetical tie-break decides contested top slots.**
6. **Fuzzy tiers Windows doesn't have** (substring, subsequence) admit rows the
   real Start menu rejects outright.
7. **Diacritic-blind matching** — `dzwiek` can't find *dźwięku*.
8. **Keyword blob ranked at full strength** — a keyword hit can outrank a
   weaker *name* hit on another row.

---

## 2. How the real Windows Start menu ranks

No public spec exists; Microsoft states the algorithm "is not documented;
indeed, the algorithm has changed many times over the years" (Raymond Chen,
2016). The picture below triangulates Microsoft statements, a systematic
black-box study (Ctrl.blog), DFIR literature, and two readable Start-like
rankers (Microsoft's own PowerToys Run, and Open-Shell — §3). Confidence is
flagged per claim; sources at the bottom.

**Match gate, not match score** *(high confidence)* — word-boundary
**prefix** matching only, against the **localized** display name. No
substring, no fuzzy, no typo tolerance: "locator" does not find *FileLocator*,
"alculator" does not find *Calculator*, and multi-word queries are
order-sensitive. Each query token must prefix-match a word. This is why the
TortoiseGit "Settings" row simply never appears for `ust` on real Windows —
it's rejected at the gate, not out-ranked.

**Usage dominates among survivors** *(high confidence that it exists and
decays; the formula is unpublished)* — "Each time you launch a program, it
earns a point, and the longer you don't launch a program, the more points it
loses" (Chen, 2007). The signal is per-app and query-independent: with two
Firefox editions installed, the more-used one wins the "firefox" query
regardless of which name matches better. PowerToys Run encodes the same idea
as `score + selectedCount × 5`.

**Result-type weighting** *(medium for Apps > Settings; documented for
local > web)* — Apps rank above Settings deep-links above files; since Insider
build 26300.8493 (May 2026) Microsoft documents that local files/apps rank
ahead of Bing web suggestions "when your content is a stronger match". Some
results are editorially pinned (e.g. "web browser" surfaces only Edge).

**Best match** — the single highest-scoring result across providers is
promoted into the labeled top slot (Enter launches it); the rest group by type:
Apps, then Settings, then files, then web.

**Usage storage** *(high confidence as the OS launch counter; whether the
modern `SearchHost.exe` ranker reads it directly is unproven)* — the
`UserAssist` registry counters (`HKCU\...\Explorer\UserAssist\{GUID}\Count`,
ROT13-encoded names, run count + last-execution time), gated by the privacy
toggle "Let Windows improve Start and search results by tracking app launches"
(`Start_TrackProgs`).

**Locale & diacritics** — matching runs against the resolved localized name
(the string on screen): on a Polish-only system, "settings" does *not* find
Ustawienia, while `ust` does. The Windows Search platform is
diacritic-insensitive by default. Settings pages additionally carry localized
keyword synonyms, which is why every "Ustawienia …" deep-link floods in.

**Moving target** — the Start box is `SearchHost.exe`, and Copilot+ machines
add on-device semantic indexing (Jan 2025). Exact parity is impossible; the
stable, imitable core is **prefix gate + usage decay + type weight**.

---

## 3. How Open-Shell ranks (read from vendored source)

Open-Shell is much simpler than its reputation suggests
(`Src/StartMenu/StartMenuDLL/SearchManager.cpp`, `MenuContainer.cpp`):

- **Sections are hard-coded**: Programs → Settings (metro) → Control Panel →
  indexed files (the last delegated to Windows Search SQL,
  `ORDER BY System.Search.Rank DESC, System.DateModified DESC, …`).
- **Within a section**: sort by `rank` descending, then raw `wcscmp` on the
  stored UPPERCASE name (code-point order, not collation). `rank` is
  `2 × launchCount`, plus — for settings only — a low bit meaning "the *name*
  matched, not just keywords". Match quality is otherwise a **binary filter**:
  one launch outweighs any difference in how well the text matched.
- **Launch counts** live in `HKCU\Software\OpenShell\StartMenu\ItemRanks`
  (REG_BINARY, 12-byte records keyed by an FNV-1a hash of the uppercase
  parsing name / AUMID). No decay — only LRU eviction past 255 entries.
- **Matching** is `FindNLSStringEx` with
  `LINGUISTIC_IGNORECASE | LINGUISTIC_IGNOREDIACRITIC` — culture-aware and
  diacritic-insensitive (*dzwiek* finds *dźwięku*). All query tokens must
  match; default mode is substring-anywhere (`SearchSubWord=1`), with a
  word-boundary prefix mode available.
- **Verified quirk**: launching a *metro* app from the search results never
  registers a rank — the write path hashes a category-seeded display name
  while the read path hashes the parsing name, so the keys never meet. Even
  the original's usage boost is partially broken.

With zero launch history Open-Shell degrades to alphabetical within sections —
the same failure mode MenYou exhibits, which is why usage tracking is
load-bearing in both designs.

---

## 4. Enhancement plan for MenYou

Goal: Windows-like ordering with MenYou's existing data and types. Single
`int` score with widely-spaced lexicographic bands (fits `SearchResult.Score`),
making the order a total, deterministic function of (match, type, usage).

### 4.1 Scoring spec

```
Score = tierBase + fieldBonus + typeBonus + usageBoost + proximity

tierBase   = tier × 1,000,000   Exact=5 · Prefix=4 · WordPrefix=3 · Substring=2 · KeywordOnly=1
fieldBonus = Title +30,000 · AlternativeName +20,000 · Alias +10,000 · Keywords +0
typeBonus  = App/PackagedApp +200,000 · built-in Command +100,000 · SettingsDeepLink +50,000 · ControlPanelTask +0
usageBoost = min(600,000, decayedCount × 60,000)
             decayedCount = LaunchCount × 0.5^(daysSinceLastUsed / 30)
proximity  = max(0, 1,000 − matchIndex×10 − max(0, sourceLen − queryLen))
```

Invariant chain (assert in tests): `proximity (≤1k) < fieldBonus step (10k) <
typeBonus step (50k) < usageBoost cap (600k) < tierBase step (1M)`. So: a title
hit always beats an alias hit of the same tier; apps beat settings within a
tier; ~10 recent launches saturate the usage boost and can reorder *within* a
tier but can never lift a substring hit over a prefix hit. The char-subsequence
tier is **deleted** (Windows has nothing like it); keyword matches always
classify as `KeywordOnly` regardless of how well the blob matched (Open-Shell
parity: keywords are a gate, not a quality signal).

Tie-breakers, in order: kind rank (apps → built-ins → deep-links → CP tasks),
`Title` (CurrentCultureIgnoreCase), `TargetPath` (ordinal), stable input order.

Worked re-check of `ust`, no history: real Settings app ≈ **4,230,993** >
TortoiseGit (if its alias even survives §4.2) ≈ 4,210,993 > "Ustawienia HDR"
deep-link ≈ 4,080,996 — the Windows visual order, deterministically. One launch
of the Settings app adds ≈ +60,000 and locks it in.

### 4.2 Matcher fixes

1. **Identity-gate the alias injection** (`BuildSearchAliases`): only consult
   `KnownAppAliases` when the shortcut's target is a system binary (under
   `%SystemRoot%`) or the entry is UWP/AUMID-keyed. Preferred refinement: give
   each alias group an `ExpectedTargets` set (exe basename / AUMID substring —
   Settings→`immersivecontrolpanel`, Edge→`msedge`, …). `TortoiseGitProc.exe`
   in Program Files then gets no multilingual group at all.
   **Gotcha:** aliases are baked into `discovery-cache.json` and the cache
   fingerprint only watches the filesystem — `DiscoveryCache.SchemaVersion`
   **must be bumped** or poisoned aliases survive the upgrade.
2. **Culture-aware, diacritic-insensitive matching**: replace the
   `ToLowerInvariant`/ordinal machinery with
   `CultureInfo.CurrentUICulture.CompareInfo` using
   `CompareOptions.IgnoreCase | IgnoreNonSpace` (the .NET twin of Open-Shell's
   NLS flags): *dzwiek* → *dźwięku*. Keep an **ASCII fast path** (ordinal when
   both strings are pure ASCII) because ICU comparisons are 10–50× slower.
   Don't add `IgnoreKanaType` (Windows doesn't fold kana); known shared limit:
   Polish *ł* doesn't decompose, so *lupa* won't match *łupa*-words.
3. **Multi-token queries**: split on whitespace, every token must reach ≥
   WordPrefix (any order), overall tier = the weakest token's tier — so
   `ustawienia dzw` finds *Ustawienia dźwięku*.

### 4.3 Frecency with existing data

`UserSettings.Recent` already stores `RecentEntry(AppId, LastUsedUtc,
LaunchCount)`, incremented on every search-originated app launch — app frecency
needs **zero schema change**; the ranker just joins on `AppId`. One addition
for non-app results: a `SearchUsage` list reusing `RecentEntry` with synthetic
keys (`ms-settings:…` URI, `cmdline:<path>`, `cpl:<name>`), LRU-capped at 256
(Open-Shell's precedent), kept separate from `Recent` so synthetic keys never
reach the Recent tiles. Decay is computed at read time only — counts are never
rewritten, so no extra `settings.json` churn.

### 4.4 Suggested PR slicing

| PR | Contents | Outcome |
|----|----------|---------|
| 1 | Alias identity gate + cache schema bump; field-aware tiered scoring; type bonus + `SearchResultKind.SettingsDeepLink`; delete subsequence tier; new tie-breakers; first test project (the `ust` scenario end-to-end + band invariants) | Fixes the reported bug outright; deep-links group below apps |
| 2 | `CompareInfo` matcher with ASCII fast path; multi-token semantics; perf benchmark (budget < 10 ms / query over a 2000-app corpus, behind the existing 80 ms debounce) | Polish/diacritic parity with Windows |
| 3 | `SearchUsage` + `RecordSearchLaunch`; usage boost with 30-day half-life | Frequently-used items rise; one launch wins contested ties |
| 4 (optional) | "Best match" slot + Apps/Settings section headers in the results view; toggles mirroring Open-Shell's `SearchTrack`/`SearchSubWord` | Windows-look presentation; ordering already correct |

### 4.5 Risks

- **Per-keystroke perf** of ICU comparisons — mitigated by the ASCII fast
  path, span-based word scan, and the existing debounce; benchmark before
  merge.
- **Stale poisoned aliases** in `discovery-cache.json` if the schema version
  isn't bumped (the fix silently wouldn't apply on real machines).
- **Selection semantics**: the first row auto-selects and Enter launches it;
  reordering changes what Enter does (intended), and big reorders stress the
  positional reconcile — verify no flicker with the Win 7/Classic detail panel
  open. The usage boost is query-independent, so order stays stable across
  keystrokes.
- **Recall regressions**: deleting the subsequence tier means typo-ish queries
  return nothing (Windows parity, but consider a toggle); pin ranking tests to
  fixed cultures (`pl-PL`, `ja-JP`), not the machine default.
- **No existing test harness** — the band invariants are easy to break
  silently later; they belong in asserts, not comments.

---

## Sources

- Ctrl.blog, *How does the Windows Start menu search function work?* —
  systematic black-box study: <https://www.ctrl.blog/entry/windows-start-search-results.html>
- Raymond Chen, *How does Windows decide which programs show up in the front
  page of the Start menu?* (2007): <https://devblogs.microsoft.com/oldnewthing/20070613-00/?p=26443>;
  follow-up confirming the algorithm is undocumented and changing (2016):
  <https://devblogs.microsoft.com/oldnewthing/20160329-00/?p=93214>
- Windows Insider release notes, build 26300.8493 — the only first-party
  ranking statement found (local above web "when your content is a stronger
  match"): <https://learn.microsoft.com/en-us/windows-insider/release-notes/experimental/preview-build-26300-8493>
- PowerToys Run scorer (Microsoft's own Start-like launcher):
  `Wox.Infrastructure/StringMatcher.cs`, `Wox.Plugin/Result.cs`,
  `Microsoft.Plugin.Program/Programs/Win32Program.cs` in
  <https://github.com/microsoft/PowerToys>
- Open-Shell `SearchManager.cpp` / `MenuContainer.cpp` (vendored at
  `openshell_research/Open-Shell-Menu-master/`)
- UserAssist forensics: Didier Stevens
  (<https://blog.didierstevens.com/programs/userassist/>), Cyber Triage
  *UserAssist Forensics* (2026), 4n6k (2013)
- Windows Search diacritic insensitivity:
  <https://learn.microsoft.com/en-us/windows/win32/search/-search-sql-accentinsensitivitysearches>
- Localized-name matching evidence: Microsoft Q&A 3902154 (ms-resource leak
  after language change), 3925985 (English names need the English LP)

> ⚠️ Several high-ranking web articles on this topic (e.g. windowsnews.ai
> mirrors describing a "local confidence score" with "telemetry-adjusted
> dynamic thresholds") are AI-generated embellishments of the one-line
> 26300.8493 note. They were checked and excluded as fabricated.
