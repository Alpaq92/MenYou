# MenYou — optimization

How MenYou is kept fast, and the reasoning behind each change — focused on **startup**, which is what users feel most for a Start-menu replacement that lives in the tray and must be ready the moment they sign in.

This is an engineering record, not a changelog: it covers *what* was done, *why*, the *measurements* that justified it, and the approaches that were **tried and rejected** (so they aren't re-attempted). It spans **0.5.0 → 0.9.x**, across two distinct eras: the **launch** era (§1–§5 — getting the process started promptly, fixed by the logon task) and the **data-paint** era (§6–§8 — getting a truthful, fully-drawn menu the moment the process is up).

---

## Results

Cold boot — time from the desktop appearing to MenYou's **process launching** (the term 0.7.0 fixed; tray-usable follows a beat later):

| Build | Desktop → process (cold) | What changed |
|---|---|---|
| 0.2.0 (`HKCU\Run`) | ~15 s | *Faster, flicker-free open* — parallel `.lnk` walk (~640 → 400 ms), off-screen warm-up, single-flight load, reveal-on-data. **No** ReadyToRun or discovery cache yet. |
| 0.5.0 (`HKCU\Run`) | ~15 s | Added the **discovery cache** (instant cold data) + **ReadyToRun** (~½ framework startup) + COM-free UWP fingerprint — faster *once running* than 0.2.0, but the Run-key launch was untouched, so this number didn't move. |
| 0.6.0 — logon task @ PT3S | ~3 s | Autostart moved **off** the Run-key — the launch fix. |
| **0.7.0 — logon task @ PT1S** | **~1 s** | Task trigger delay trimmed PT3S → PT1S. |

Same machine, same binary path — the **launch** dropped **~15 s → ~1 s** purely by changing *how Windows is told to start the app*. Desktop→tray-*usable* followed: ~16 s before, **~2–4 s** on 0.7.0.

A second, independent cold-start regression — in the **data paint**, not the launch — surfaced after 0.7.0 shipped: intermittently the menu opened *empty* for ~15–20 s even though the process launched in ~1 s. That one was fixed in **0.8.7** (stale-while-revalidate, §6), hardened through **0.8.14–0.9.0** (§7), with the remaining visible cost (the icon fill) parallelized in **0.9.2** (§8).

### Results — data-paint era

What the launched process shows, and when. The launch itself stayed ~1 s throughout; the variable is the fingerprint state — whether any Start-Menu entry changed since the last session (a third-party auto-updater rewriting its own `.lnk` is the everyday trigger, which is what made this look random):

| Build | Cold boot, fingerprint **fresh** | Cold boot, fingerprint **stale** |
|---|---|---|
| 0.8.5 (pre-SWR) | instant data paint | **empty menu ~15–20 s** — snapshot discarded, live COM scan held behind the ~20 s warm-up hold (§4) |
| **0.8.7+** | instant data paint | **instant data paint** — stale snapshot shown, background scan corrects + re-persists silently |

The 0.8.5 stale-boot number was measured live on the reporting machine (2026-07-06, the "sometimes MenYou takes longer to show after restart" diagnosis — launch verified fine at ~1.6 s after `explorer.exe`, data absent); the 0.8.7 row is structural — the discard path no longer exists, and the paint is bounded by the eager COM-free file read (~150 ms into startup, §2).

For the **icon fill** (§8), measured on the development machine (143 apps): the visible Pinned/Recent batch went from **~2.1 s to 446 ms** and the full tree fills in ~3 s alongside it. Per-boot numbers come from the `Icons: filled N/M tiles in X ms` trace line added in 0.9.2 (visible in `%TEMP%\menyou-hooks.log` with Developer → Logging on; it slots in as a fifth timestamp in the methodology below).

> **"Was 0.2.0 really no different from 0.5.0?"** For the *launch* number above — yes, provably. The `HKCU\Run` autostart code (`Win32AutostartService`) was **byte-identical across every Run-key release, 0.2.0 through 0.5.6** (sha `07a2825…`); the logon task and the `StartupDelayInMSec` tweak arrived with the task work (the 0.6.0 build at PT3S, trimmed to PT1S for 0.7.0), so nothing could move the ~15 s launch across the Run-key era.
>
> But the releases *were* different where it counts **after** launch: **ReadyToRun landed in 0.5.0** (commit `8d39ce3`, ~½ framework startup) alongside the discovery cache — so 0.5.0 reached *usable* faster than 0.2.0 once its process was running. The Run-key throttle simply hid that gain in the end-to-end cold start until the task removed the launch delay. (Only the 0.6.0/0.7.0 task builds were directly timed this session; the flat 0.2.0–0.5.6 *launch* is **structural** — identical code — not extrapolated.) Right code, wrong *segment*: the measurement trap in the next section.

---

## How startup is measured

Perceived "slow startup" is ambiguous, so every claim here is anchored to four timestamps from one cold boot:

1. **Boot** — `Win32_OperatingSystem.LastBootUpTime`.
2. **Desktop ready** — `explorer.exe` process start time (the shell is up).
3. **MenYou process start** — `MenYou.exe` process start time.
4. **Tray ready** — the `Startup: tray done` line in the opt-in trace log (`%TEMP%\menyou-hooks.log`, enabled via the Developer tab or `MENYOU_TRACE_HOOKS=1`), which is stamped `+<ms since process start>`.

The decisive insight came from **(3) − (2)**: the gap between the desktop being ready and MenYou's process starting. Measuring it isolated the bottleneck to *before our code runs but after the shell is up* — which ruled out Windows boot, Defender, disk, and MenYou's own init (all outside that window) and pointed straight at the autostart launch mechanism. Optimizing without this measurement is how an earlier round of work (below) tuned the wrong segment.

---

## 1. Cold-start: the autostart mechanism (the big win, 0.6.0–0.7.0)

**Symptom.** After a reboot, MenYou's tray icon and hotkey took ~15 s to appear *after the desktop was already interactive*.

**Diagnosis.** The in-process path was already fast — the trace showed `tray done` ~0.5–2.3 s after the process started. But the process didn't *start* until ~15 s after `explorer.exe`. That gap is **Windows' deliberate `HKCU\Run` startup throttle**: the shell defers and serializes Run-key (and Startup-folder) autostarts after sign-in to keep login responsive (`Explorer\Serialize\StartupDelayInMSec` baseline plus serialized processing behind other startup entries).

**Fix.** Autostart was moved from the `HKCU\Run` value to a **per-user logon-triggered Scheduled Task** (`Win32AutostartService`), which is *exempt* from that throttle — it fires as part of the logon sequence. Key properties of the task:

- **`LogonTrigger`** for the current user, **`Delay = PT1S`** (a 1 s nudge so the shell's notification area exists before the tray icon registers; measured to still land right after Explorer while shaving ~2 s off the earlier PT3S).
- **`InteractiveToken` + `LeastPrivilege`** — runs in the user's session at medium integrity, *not* elevated. This is essential: MenYou installs low-level input hooks and manipulates Explorer's foreground, which only works at the same integrity level as the shell (an elevated autostart would break the hooks via UIPI).
- **No admin needed** to create it — a user may freely register tasks that run as themselves.

**Robustness:**
- **Fallback** — if task creation is blocked (locked-down box, group policy), it falls back to the legacy `HKCU\Run` value so autostart still works, just throttled. Either way it also zeroes `StartupDelayInMSec` so that fallback path is as prompt as Windows allows.
- **Self-healing migration** — existing installs migrate Run-key → task once on launch. The migration only marks itself done **after verifying autostart is actually in place** (`IsEnabled`), so a transient failure can't strand a user with no autostart (an earlier version flipped the "done" flag unconditionally and left a machine with neither task nor Run-key).
- **Schema correctness** — the task XML is declared `version="1.2"` and contains only 1.2-valid settings. A `<UseUnifiedSchedulingEngine>` node (1.3+) silently made Task Scheduler reject the whole XML; it was removed. Element order within `<Settings>` is also load-bearing.
- **Installer / uninstaller** — the opt-in Startup-folder shortcut was removed from the Inno script; autostart is owned entirely by the app now, so the two mechanisms can't double-launch. The uninstaller deletes the task (and any legacy `HKCU\Run` value) in `CurUninstallStepChanged`, so an orphaned task can't outlive MenYou and fire at each sign-in with a missing target.

---

## 2. Menu data: the discovery cache (0.5.0)

App discovery (enumerating `.lnk` shortcuts, UWP packages, Control Panel and Settings deep-links) is COM-heavy and slow on a cold shell. A few changes keep the menu's data instant:

- **Persisted snapshot** *(0.5.0, reworked 0.8.7)* — discovery results are cached at `%AppData%\MenYou\discovery-cache.json`. On launch the menu is served from this plain file read (no shell COM), so it paints instantly on a cold start — fresh **or stale**: since 0.8.7 the filesystem fingerprint decides only whether the background scan must re-persist a corrected snapshot, not whether the paint happens at all. (0.5.0's original design *discarded* the snapshot on any fingerprint miss — the cold-start regression dissected in §6.)
- **Parallelized `.lnk` walk** *(0.2.0)* — per-shortcut `ShellLink` + shell-localization COM is fanned out across cores: cold discovery ~640 ms → ~400 ms.
- **Eager cache preload** — the cache is warmed from disk during sync init (file I/O only, no COM), so the data is ready well before the idle-time warm-up, even for an early first open.

---

## 3. Payload & Defender (installer / packaging)

MenYou ships **unsigned, self-contained** (.NET runtime bundled). On a fresh install Windows Defender scans the payload as it loads, which dominates the *first-ever* launch. Choices that minimize that surface:

- **Multi-file, not single-file.** `PublishSingleFile=true` was tried and **reverted**: bundling everything into one ~127 MB unsigned blob made Defender pre-scan the whole monolith before `CreateProcess` could return — a measured **~54 s** cold autostart after a reboot. Multi-file keeps a tiny apphost that launches immediately while its DLLs are scanned as they load.
- **No PDBs in the installer.** The native SkiaSharp + HarfBuzz symbol files (`libSkiaSharp.pdb` ~80 MB, `libHarfBuzzSharp.pdb` ~20 MB) are ~100 MB / ~44 % of the publish output yet never loaded at runtime. Excluding them (`Excludes: "*.pdb"` in the Inno script) drops the installed footprint to ~52 MB and removes 100 MB from Defender's first-run scan. MenYou's own symbols are embedded (`DebugType=embedded`), so no managed debugging is lost.
- The cold first-run cost that remains (~10 s, one-time, as Defender scans the fresh DLLs the first time) is a perception problem, handled in §5.

---

## 4. In-process startup & first open

The first `Shift+Win` after launch otherwise pays the full window-realization + data-load cost. Mitigations:

- **ReadyToRun** (`-p:PublishReadyToRun=true` in `release.yml`) — crossgen2 AOT-compiles the app + Avalonia to native images so startup skips JITing those paths (~halved framework startup, ~16 MB larger). The JIT stays as a fallback, so the runtime-XAML custom-theme feature is unaffected — unlike NativeAOT, which would break it.
- **Off-screen warm-up** — after sync init the Start window is built and its data loaded, then pre-rendered once off-screen (transparent, never activated), so the first real open is instant. On a **cold boot** the warm-up is *held* ~20 s so the heavy window build doesn't pile onto the post-login storm; on a warm launch it runs immediately at Input priority.
- **Single-flight `LoadAsync`** — the warm-up and a fast first click share one load instead of racing two rebuilds.
- **Deferred shell-icon extraction** — the Places/right-panel icons (slow AUMID lookups) are streamed in off the constructor rather than blocking the tray/hotkey coming up.
- **Reveal on real data** — the menu reveals at `DispatcherPriority.Loaded` (≈ 1 frame) gated on actual content, so a cold open never flashes empty.

---

## 5. Perceived first-run: the splash (0.7.0, removed in 0.8.0)

> **Removed in 0.8.0.** The splash didn't reliably paint on a fresh install — the very cold-load it was meant to cover also starved its own first frame, so users often saw nothing anyway. It was dropped to simplify startup; the one-time "ready" balloon (below) stays. The original design is kept here for the record.

The one-time ~10 s first-install cold load (Defender + cold disk) can't be eliminated without signing or trimming (see Rejected), so it's **covered** instead:

- **`NativeSplash`** — a Win32/GDI splash drawn on its **own thread** with its own message pump. A splash on the UI thread can't paint during the cold load (the load blocks that very thread — it just shows the busy cursor and ghosts/blinks); a separate thread paints through it. Shown from `Program.Main` *before* Avalonia loads, so it appears as early as possible.
- **Theme-aware** (light/dark from the Windows apps theme), a **high-res icon** (256 px frame downscaled with GDI+ high-quality bicubic — `DrawIconEx` looked muddy), and a self-animated marquee. One-time only (gated by the absence of `settings.json`).
- **"Ready" tray balloon** — when the first load finishes, a one-time balloon confirms the install worked and teaches the hotkey ("MenYou is ready — Press Shift+Win"). Localized into the shipped languages.

---

## 6. Cold-start regression #2: the stale-cache discard → stale-while-revalidate (0.8.7)

**Symptom.** Intermittent: some reboots painted the menu instantly, others left it **empty for ~15–20 s** — with the process itself launching in ~1 s (§1's win intact). Classic "sometimes slow after restart" report.

**Diagnosis.** The 0.5.0 cache treated its fingerprint as a **validity gate**: on any mismatch the whole snapshot was discarded and the menu blocked on a live COM scan — which on a cold boot is additionally *held ~20 s* behind the warm-up delay (§4). But the fingerprint hashes every Start-Menu entry's path + mtime + size, so **any** shortcut rewrite between sessions — a third-party auto-updater touching its own `.lnk` is the everyday case — flipped the next boot from instant to ~20 s empty. Whether a given reboot was "slow" was literally whether any installed app had updated itself since the last one.

**Fix — stale-while-revalidate.** A non-null snapshot is *structurally* valid (`TryLoad` already rejects wrong-schema files); the fingerprint only says whether it's *current*. So:

- **Paint it either way.** Fresh or stale, the cached list paints instantly. A stale paint is at most one app-shaped diff behind — invisible next to a 20 s empty menu.
- **Revalidate in the background.** The live scan (after a settle delay so its COM work doesn't fight the post-login storm) swaps in corrections and fires `Refreshed` only when the app list actually changed — a benign confirming pass never rebuilds the menu.
- **Persist on change.** A stale paint or a Start-Menu watcher event force-persists the corrected snapshot + fresh fingerprint, so the **next** boot is a hit. The watcher previously only dropped the in-memory copy — guaranteeing a miss on the next boot after every install/update; since most app updates land mid-session, that alone kept the misses coming.
- **Single-flight + coalesce.** The eager preload, the first-open fallback and the watcher can all request the scan; one runs at a time, and a persist-needing request that arrives mid-scan schedules exactly one coalesced re-run instead of being dropped (an app installed during the scan window would otherwise stay missing until the next reboot).
- **Fingerprint before scan.** The persisted fingerprint is computed *before* the data it describes is scanned, so it can never claim state newer than the snapshot: a `.lnk` landing mid-scan yields a benign next-boot stale-paint, never a false hit presenting stale data as fresh.

**Perception (0.8.8).** During a catch-up scan (stale paint or watcher change — not the routine confirming backstop) a dimmed **"Updating apps…"** caption shows beside the All-Programs header, gated by a 400 ms show-delay + 500 ms minimum-visible so it neither flickers on fast scans nor blinks off mid-read.

---

## 7. Keeping the instant paint truthful (0.8.14 → 0.9.0)

Stale-while-revalidate raises the stakes on cache content: a bad snapshot now replays **instantly on every boot**. A field bug ("Pinned and Last used are sometimes blank" — data intact on disk, sections empty on screen) traced to exactly that, and produced four guards:

- **Degraded-scan quarantine** *(0.8.14)* — the `shell:AppsFolder` enumeration swallows every failure into an empty result, and Win 11 can never genuinely have zero packaged apps — so an empty UWP set now marks the scan **degraded**: it never replaces data that still has packaged entries, and is **never persisted**. Previously one transient COM failure (login-storm timing) blanked every packaged-app pin/recent *and* poisoned the cache until the fingerprint next happened to change.
- **Atomic id-map publish** *(0.8.14)* — `FindById` (the pin/recent → app join) reads lock-free from the UI thread; the id map is now built fully and swapped by reference instead of Clear()+refill in place, so a rebuild can never join against a half-built map and render both sections empty.
- **Join-then-cap** *(0.8.14)* — Recent resolves ids against discovery *first*, then applies the display cap; a few dead ids at the top can no longer blank the whole section while resolvable history sits just below the cap.
- **Ghost filtering** *(0.9.0)* — the shell's app-resolver cache keeps listing uninstalled Win32 apps for a while, reporting a raw exe path as the "AUMID"; the mid-uninstall watcher rescan used to ingest those ghosts and persist them (launching one sent `explorer.exe` to an invalid `shell:AppsFolder` item — which opens Documents). Scans now drop dead-path AUMIDs and "Uninstall …"-named AppsFolder entries, mirroring the filter the `.lnk` scanner always had.

---

## 8. The icon fill: parallel extraction (0.9.2)

With the data paint instant (§6) and truthful (§7), what a cold start *shows* is the *cog → real-icon* fill. That fill was **strictly serial**: ~150–300 shell-COM extractions, each paying its own `Task.Run` hop plus an **awaited** per-icon UI invoke — the last tile filled N × extract-time after the menu opened.

0.9.2 batches the whole fill through one `Parallel.ForEachAsync` (a single outer `Task.Run` keeps every worker off the UI thread — `ForEachAsync` may otherwise run a fully-synchronous body inline on the caller), each icon landing via its own fire-and-forget posted UI update as it completes. An adversarial review of the first cut reshaped the design:

- **Exactly-once extraction** — the Pinned/Recent batch and the Programs-tree batch overlap on every cold start and share most pinned/recent ids, so the naive version extracted those icons **twice** (confirmed). An in-flight `Lazy` map now collapses concurrent same-id requests: losers block on the winner instead of re-running COM. Measured effect on the visible batch: **2086 ms → 446 ms** (143-app machine) — the visible tiles stopped paying for the tree's duplicate work.
- **DOP capped to half the cores (2–8)** — extraction fans into third-party shell extensions (icon handlers, AV overlays) that had never been hit concurrently before, and the login storm is core-starved already; half-DOP keeps nearly all of the wall-clock win.
- **Per-item isolation** — `ForEachAsync` stops scheduling after an unhandled throw and every call site discards the batch task, so one corrupt `.ico` would have silently left the rest of the menu on cogs. Each item now catches; a failed extraction is cached as null (cog stays, no per-open retry).
- **Correctness riders** — tile-list snapshots moved onto the UI thread behind a loud `VerifyAccess` (the old loop enumerated live `ObservableCollection`s inside `Task.Run`, racing rebuilds); a generation guard drops a superseded tree batch's posts; the extraction cache's one lock-free read was closed (parallel writers made the read-during-write real).

**Next lever (planned):** a small on-disk icon cache keyed by entry id, so warm boots skip extraction entirely — gated on per-entry mtime invalidation and negative-result handling (the in-memory cache stores nulls; a disk layer that doesn't would re-extract every icon-less app each boot), plus a measurement pass on the added post-login decode cost.

---

## Rejected / not pursued

- **`PublishSingleFile`** — see §3 (~54 s cold autostart, unsigned). Kept multi-file.
- **`PublishTrimmed`** — a trim trial built cleanly but produced **430 trim warnings**, including the two that matter: `System.Text.Json` reflection (settings load/save) and `AvaloniaRuntimeXamlLoader` (custom themes). It would break both features. Doing it safely needs source-generated JSON + trimmer-root descriptors + a full feature-test pass — substantial work for a modest size win, when the startup bottleneck was never the payload size. Deferred.
- **NativeAOT** — incompatible with the runtime-XAML custom-theme feature (which needs the JIT). ReadyToRun gives most of the startup benefit without that cost.
- **Optimizing the in-process path *as the cold-start fix*** — an earlier round (warm-up priority tuning, pdb exclusion, single-file revert) targeted MenYou's own init, which was already fast. Necessary polish, but it was the wrong segment for the perceived ~15 s slowness; the measurement in the methodology section is what redirected the effort to the Run-key throttle.
- **Skeleton placeholder tiles for the empty first frame** — rejected in review: post-SWR (§6) a valid cache exists on essentially every boot after the very first, so the empty frame is a first-ever-run-only event; and the 0.7.0 splash (§5) already demonstrated that a UI-thread veneer gets starved by the very cold load it's meant to cover. The "ready" balloon carries first-run perception instead.
- **Softening the fingerprint (dropping directory mtimes)** — real but marginal post-SWR: no COM scan is saved (the background backstop runs regardless); it would only trim a redundant cache rewrite + caption on benign-touch boots. Parked as a ride-along for the next cache-schema bump, not its own change.
- **`Directory.Build.props` to pin publish flags locally** — doesn't close the drift it targets: ReadyToRun needs the `-r win-x64` that CI passes on the command line (not the csproj), and the ~100 MB shape difference is the installer's PDB exclusion, not publish flags. CI stays the single source of truth.

---

## Per-version summary

| Version | Optimization work |
|---|---|
| **0.2.0** | "Faster, flicker-free Start-menu open": parallelized `.lnk` discovery walk (~640 ms → ~400 ms), off-screen warm-up/pre-render, single-flight `LoadAsync`, deferred shell-icon extraction, account-picture off the UI thread, reveal-at-`Loaded` gated on real data, diff-aware tile rebuild. (In-process/open path — cold start still Run-key-bound.) |
| **0.5.0** | Persisted discovery cache (instant cold data paint), immediate-reveal option, ReadyToRun (~½ framework startup), COM-free UWP fingerprint, Windows 11 default look. (Cold-paint + framework — still Run-key-bound end-to-end until the 0.6.0 task build.) |
| **0.5.x** | pdb exclusion from the installer; multi-file kept (single-file trialed and reverted). |
| **0.6.0** | **Run-key → logon scheduled task** at PT3S — autostart off the throttled Run-key: the ~15 s → ~3 s cold-start win, plus self-healing migration and a zeroed-`StartupDelayInMSec` fallback. |
| **0.7.0** | Trigger delay trimmed PT3S → PT1S (~3 s → ~1 s); first-run native splash + ready balloon; power-button `SeShutdownPrivilege` fix; Settings-window flash→fade; (trimming evaluated and rejected). |
| **0.8.7** | **Stale-while-revalidate discovery cache** — paint fresh *or* stale, revalidate + persist-on-change in the background (single-flight, coalesced, fingerprint-before-scan): fixed the intermittent ~20 s empty-menu cold start caused by discarding the snapshot on any fingerprint miss. |
| **0.8.8** | "Updating apps…" caption during catch-up scans (400 ms show-delay / 500 ms min-visible so it never flickers). |
| **0.8.14** | Truthful-paint guards: degraded-UWP-scan quarantine (empty ≠ success; never persisted), atomic id-map publish for the lock-free pin/recent join, Recent join-then-cap, and the mirror's empty-export wipe guard. |
| **0.9.0** | Ghost filtering — uninstall-style entries and dead-path AUMIDs dropped from the AppsFolder merge, so uninstalled apps can't persist in the snapshot (and the resolver-cache lag window can't reintroduce them). |
| **0.9.2** | **Parallel icon fill** — the serial per-icon `Task.Run` + awaited-UI-invoke loop became one capped-DOP `Parallel.ForEachAsync` batch with exactly-once per-id extraction, per-item fault isolation, and per-icon posted updates (visible batch measured 2086 → 446 ms); tile-list snapshots moved on-thread; the icon cache's last lock-free read closed. |
