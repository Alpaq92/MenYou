# MenYou — startup & performance

An engineering record of what was done to MenYou's startup, why, and what was measured. Not a changelog: it exists so the same ground isn't re-covered, and so the things that **cost a release to learn** are written down where the next person will find them.

Startup is the metric that matters here — a Start-menu replacement lives in the tray and has to be ready the moment you sign in.

> **Section anchors are referenced from source code** (`[UnconditionalSuppressMessage]` justifications, `release.yml`, `README.md`). Link to headings by name, never by number — numbers shift, and the last scheme broke silently when sections were inserted.

**Where to start:**

| You want to… | Go to |
|---|---|
| Make startup faster | [Working on startup](#working-on-startup) |
| Enable trimming | [Trimming](#trimming) — **read the caution first** |
| Understand a measurement | [How to measure](#how-to-measure) |
| See what changed when | [By version](#by-version) |

---

## Results

**Launch** — desktop appearing → MenYou's process starting:

| Build | Cold | What moved it |
|---|---|---|
| 0.2.0 – 0.5.6 (`HKCU\Run`) | ~15 s | nothing — the autostart code was byte-identical across every Run-key release |
| 0.6.0 (logon task, `PT3S`) | ~3 s | autostart off the Run-key |
| **0.7.0 (logon task, `PT1S`)** | **~1 s** | trigger delay trimmed |

~15 s → ~1 s purely by changing *how Windows is told to start the app*. Desktop → tray-usable followed: ~16 s → ~2–4 s.

0.5.0 did add ReadyToRun and the discovery cache, which made the process faster *once running* — the Run-key throttle simply hid that in the end-to-end number. Right code, wrong segment; see [How to measure](#how-to-measure).

**Data paint** — what the launched process shows, and when. The launch stayed ~1 s throughout; the variable was whether any Start-Menu entry had changed since the last session:

| Build | Fingerprint fresh | Fingerprint stale |
|---|---|---|
| 0.8.5 | instant | **empty ~15–20 s** |
| **0.8.7+** | instant | **instant** |

**Icon fill** — visible Pinned/Recent batch **2086 ms → 446 ms** (0.9.2, parallel extraction), then **~7.2 s → ~0.1–0.2 s** for the full cold fill (0.9.6, on-disk cache). A real cold boot fills all 144 tiles in ~200 ms.

---

## How to measure

Perceived "slow startup" is ambiguous. Every claim here is anchored to four timestamps from one cold boot:

1. **Boot** — `Win32_OperatingSystem.LastBootUpTime`
2. **Desktop ready** — `explorer.exe` process start
3. **MenYou process start** — `MenYou.exe` process start
4. **Tray ready** — the `Startup: tray done` line in `%TEMP%\menyou-hooks.log` (Developer tab, or `MENYOU_TRACE_HOOKS=1`), stamped `+<ms since process start>`

**(3) − (2) is the decisive one.** It isolates "after the shell is up, before our code runs", which ruled out Windows boot, Defender, disk and MenYou's own init in one step and pointed straight at the autostart mechanism. An earlier round of work tuned the in-process path — which was already fast — because nobody had measured this gap.

### Two different numbers

**Time-to-tray** is when MenYou exists. **Time-to-first-open** is when the menu paints. Different bottlenecks; most of this document moves the first.

### The load path

```pwsh
# MUST be a 64-bit PowerShell session. A 32-bit (WOW64) host cannot read a
# 64-bit process's Path or Modules: they come back $null or as a partial list
# of WOW64 stubs, so the count and total look plausible and are wrong.
if (-not [Environment]::Is64BitProcess) {
    throw "Run this from 64-bit PowerShell - a 32-bit session cannot enumerate a 64-bit process's modules."
}

$p = Get-Process MenYou; $dir = Split-Path $p.Path
$mods = $p.Modules | Where-Object { $_.FileName -like "$dir*" }
if (-not $mods) { throw "No modules resolved - wrong architecture, or MenYou is not running." }

$mods.Count                                               # modules from the install dir
($mods | Measure-Object ModuleMemorySize -Sum).Sum / 1MB  # MiB of module IMAGE SIZE
$mods | Sort-Object ModuleMemorySize -Descending | Select -First 15
```

Diff that against everything shipped to find dead weight.

> **`ModuleMemorySize` is `SizeOfImage`** — the static image size of what's mapped, **not** pages faulted in. It's an upper bound on what a cold start must read and a fair proxy for "how much are we asking the OS to bring in", but it is not resident set and must not be quoted as one. For true page-in, measure working set or trace hard faults with ETW. Every figure below is image size, so the comparisons are like-for-like.

> **Beware Prefetch.** Windows retrains SuperFetch/Prefetch on new binaries, so the *first* cold boot after an update is slower than the steady state. An A/B across an update measures Prefetch, not your change. Boot each build twice and take the second.

---

## Where the time goes

Measured on an installed **0.9.27**, counting only modules from the install directory:

| | |
|---|---|
| Modules loaded | **73** |
| Module image mapped | **70.8 MB** |
| Files shipped | 236 (132.4 MB) |
| Shipped, **never loaded** | **160 files, 61.6 MB** |

Largest mapped, all irreducible: `System.Private.CoreLib` 15.6 MB, `libSkiaSharp` 11.4 MB, `Avalonia.Base` 6.8 MB, `coreclr` 4.6 MB.

Largest never loaded, all waste: `System.Private.Xml` 7.6 MB, `System.Linq.Expressions` 3.6 MB, `System.Data.Common` 2.7 MB, `System.Private.DataContractSerialization` 2.0 MB, `Microsoft.DiaSymReader.Native` 2.1 MB.

A traced cold boot reaches the first line of `Program.Main` at **+7.3 s**, then finishes every synchronous init step in **244 ms**. Optimising app code is close to pointless; the load path is the lever.

| Stage | Cost | Moved by |
|---|---|---|
| Task Scheduler fires the trigger | — | the task's `LogonDelay` (`PT1S`) — this is *when* the process launches, and nothing else changes it |
| Runtime + assembly load | **the bulk** | trimming, shipping less, composite R2R, **and the task's `Priority`** — priority governs the CPU and I/O the process gets *once running*, which is this stage, not the row above |
| MenYou synchronous init | ~244 ms | nothing worth having |
| Warm-up (window build, first paint) | deferred | already off the critical path |

---

## Working on startup

### Remaining levers, ranked

1. **Trimming — the only thing that removes the 61.6 MB.** Blocked; see [Trimming](#trimming). A retry is **not a flag flip**: the build is warning-clean either way, so budget the runtime verification, not the change.
2. **Defer `System.Drawing` off the startup path.** `LoadFallbackIconAsync()` runs during sync init and is the only startup caller of `IconExtractor`, the only user of `System.Drawing` — pulling `System.Private.Windows.Core` (1.8 MB), `System.Drawing.Common` (892 KB), `System.Private.Windows.GdiPlus` (408 KB) and `System.Drawing.Primitives` (120 KB) for a *fallback* icon needed only when an app's own extraction fails. Caveat: those assemblies load anyway at the first icon fill, so this **moves** ~3.2 MB out of the contended logon window rather than removing it.
3. **Ship less.** Audit the publish output against the loaded-module list whenever a dependency is dropped, and add an `[InstallDelete]` line so upgraders benefit too — Inno never deletes a file that has dropped out of `[Files]`, it only stops installing it.

### Rejected levers

| Lever | Why not |
|---|---|
| **NativeAOT** | Removes the JIT that runtime-XAML custom themes require. ReadyToRun gives most of the win without it. |
| **`PublishSingleFile`** | Tried, reverted. One ~127 MB unsigned blob made Defender pre-scan the whole monolith before `CreateProcess` returned — measured **~54 s** cold autostart. Multi-file keeps a tiny apphost that launches while its DLLs are scanned as they load. |
| **`InvariantGlobalization`** | No win — the payload ships no ICU. Would also break culture-aware sort/search across 13 locales. |
| **Cheaper AppId hash** (drops `System.Security.Cryptography`, 2.4 MB) | `SHA1.HashData` in `AppDiscoveryService` generates the AppIds **persisted in `settings.json`**. Changing it invalidates every user's Pinned and Recent. |
| **`TieredPGO`** | Steady-state throughput, not startup. |
| **Lowering `PT1S`** | The 1 s exists so the notification area is up before the tray icon registers. Trades a reliable tray icon for a fraction of a second. |
| **Optimising the in-process path *as the cold-start fix*** | Already fast. An earlier round spent effort here because the (3)−(2) gap hadn't been measured. |
| **Skeleton placeholder tiles** | Post-SWR a valid cache exists on essentially every boot after the first, so the empty frame is a first-run-only event — and the 0.7.0 splash already showed that a UI-thread veneer gets starved by the very cold load it covers. |
| **Softening the fingerprint** (dropping directory mtimes) | Marginal post-SWR: no COM scan saved, only a redundant cache rewrite. Park it as a ride-along for the next schema bump. |
| **`Directory.Build.props` to pin publish flags** | Doesn't close the drift — ReadyToRun needs the `-r win-x64` CI passes on the command line, and the ~100 MB shape difference is the installer's PDB exclusion. CI stays the source of truth. |

---

## Launch: escaping the Run-key throttle

**Symptom.** After a reboot the tray icon and hotkey took ~15 s to appear *after the desktop was already interactive*.

**Diagnosis.** `tray done` was ~0.5–2.3 s after process start — the in-process path was fine. The process didn't *start* until ~15 s after `explorer.exe`. That gap is Windows' deliberate **`HKCU\Run` startup throttle**: the shell defers and serializes Run-key and Startup-folder autostarts after sign-in.

**Fix.** A per-user **logon-triggered scheduled task** (`Win32AutostartService`), which is exempt from that throttle.

- **`LogonTrigger`, `Delay = PT1S`** — a 1 s nudge so the notification area exists before the tray icon registers.
- **`<Priority>4</Priority>`** — *not* Task Scheduler's default of 7. Escaping the Run-key throttle got MenYou **started** early; priority 7 then throttled it while it **ran**. Level 7 is below-normal CPU *and* reduced I/O priority, and this startup is almost entirely I/O — ~70 MB of module image off a cold disk, at the most I/O-contended moment on the machine, queued behind Explorer, OneDrive and Defender. 4 is the normal band; 0–3 are realtime/high and would be antisocial for a tray app. Fixed in 0.9.29; the value was inherited, never chosen.
- **`InteractiveToken` + `LeastPrivilege`** — medium integrity, *not* elevated. Essential: the low-level input hooks and Explorer foreground manipulation only work at the shell's integrity level, so an elevated autostart breaks the hooks via UIPI.
- **No admin needed** — a user may register tasks that run as themselves.

**Robustness, each earned the hard way:**

- **Fallback** — if task creation is blocked (group policy), fall back to the `HKCU\Run` value; throttled but working. Either way `StartupDelayInMSec` is zeroed so the fallback is as prompt as Windows allows.
- **Migration marks itself done only after verifying** `IsEnabled`. An earlier version flipped the flag unconditionally and left machines with neither task nor Run-key.
- **Self-heal for a missing task** (0.9.28) — the migration flag lives in `%AppData%`, the task does not. The uninstaller deletes it, and winget/Chocolatey upgrade by uninstall-then-install, so a routine update left `StartWithWindows=true`, the flag set, and nothing registered. Autostart is now re-registered whenever it's wanted but absent. Guarded on `unins000.exe` beside the exe, because `SetEnabled` registers `Environment.ProcessPath` and would otherwise point a user's autostart at a dev build.
- **Re-creating a stale task** (0.9.29) — an existing task keeps whatever settings it was created with, so changing the XML does nothing for existing installs, and the missing-task self-heal deliberately doesn't touch it. A one-shot `AutostartPriorityApplied` flag forces exactly one re-create.
- **Schema correctness** — the XML is `version="1.2"` and contains only 1.2-valid settings. A `<UseUnifiedSchedulingEngine>` node (1.3+) made Task Scheduler silently reject the whole document. Element order within `<Settings>` is load-bearing.
- **One owner** — the installer's Startup-folder shortcut was removed; autostart is owned entirely by the app so the two can't double-launch. The uninstaller deletes the task and any legacy Run value.

---

## Menu data: cache and stale-while-revalidate

App discovery (`.lnk` walk, UWP packages, Control Panel, Settings deep-links) is COM-heavy and slow on a cold shell.

- **Persisted snapshot** at `%AppData%\MenYou\discovery-cache.json`, served by a plain file read with no shell COM.
- **Parallelized `.lnk` walk** (0.2.0) — cold discovery ~640 ms → ~400 ms.
- **Eager preload** during sync init, so data is ready before the idle warm-up.

### The stale-cache regression (0.8.7)

**Symptom.** Some reboots painted instantly, others left the menu **empty for ~15–20 s** — with the process still launching in ~1 s.

**Diagnosis.** The original cache treated its fingerprint as a **validity gate**: any mismatch discarded the whole snapshot and blocked on a live COM scan, which on a cold boot was additionally held ~20 s behind the warm-up. The fingerprint hashes every Start-Menu entry's path, mtime and size — so *any* shortcut rewrite between sessions flipped the next boot from instant to 20 s empty. Whether a reboot was "slow" was literally whether some app had auto-updated itself.

**Fix — stale-while-revalidate.** A non-null snapshot is *structurally* valid; the fingerprint only says whether it's *current*.

- **Paint either way.** A stale paint is at most one app-shaped diff behind — invisible next to a 20 s empty menu.
- **Revalidate in the background**, after a settle delay, firing `Refreshed` only when the list actually changed.
- **Persist on change**, so the next boot is a hit. The watcher previously only dropped the in-memory copy, guaranteeing a miss next boot after every install.
- **Single-flight + coalesce** — one scan at a time; a persist-needing request arriving mid-scan schedules exactly one re-run instead of being dropped.
- **Fingerprint before scan**, so it can never claim state newer than the snapshot it describes.
- **"Updating apps…" caption** (0.8.8) during catch-up scans, with a 400 ms show-delay and 500 ms minimum-visible so it neither flickers nor blinks off mid-read.

### Keeping the paint truthful (0.8.14 → 0.9.0)

Stale-while-revalidate raises the stakes: a bad snapshot now replays instantly on **every** boot. A field bug ("Pinned and Last used are sometimes blank") produced four guards:

- **Degraded-scan quarantine** — `shell:AppsFolder` swallows failures into an empty result, and Win 11 can never genuinely have zero packaged apps. An empty UWP set is now marked degraded: never replaces data that still has packaged entries, never persisted. Previously one transient COM failure blanked every packaged pin and poisoned the cache.
- **Atomic id-map publish** — `FindById` reads lock-free from the UI thread; the map is built fully and swapped by reference rather than cleared and refilled, so a rebuild can't be joined against half-built.
- **Join-then-cap** — Recent resolves ids against discovery *first*, then caps, so a few dead ids at the top can't blank the section.
- **Ghost filtering** — the shell's resolver cache keeps listing uninstalled apps for a while. Dead-path AUMIDs and "Uninstall …" entries are dropped.

---

## Icons

With the data paint instant and truthful, what a cold start *shows* is the cog → real-icon fill.

**Parallel extraction (0.9.2).** The fill was strictly serial: ~150–300 shell-COM extractions, each with its own `Task.Run` hop plus an *awaited* per-icon UI invoke. Now one `Parallel.ForEachAsync` batch inside a single outer `Task.Run` (`ForEachAsync` may otherwise run a synchronous body inline on the caller), each icon landing via its own posted update.

Adversarial review reshaped the first cut, and the findings are the interesting part:

- **Exactly-once extraction** — the Pinned/Recent and Programs-tree batches overlap on every cold start and share most ids, so the naive version extracted them **twice**. An in-flight `Lazy` map collapses concurrent same-id requests. This alone was **2086 ms → 446 ms**.
- **DOP capped at half the cores (2–8)** — extraction fans into third-party shell extensions never before hit concurrently, and the login storm is core-starved already.
- **Per-item isolation** — `ForEachAsync` stops scheduling after an unhandled throw and callers discard the batch task, so one corrupt `.ico` would have silently left the rest of the menu on cogs.
- **Correctness riders** — tile snapshots moved onto the UI thread behind a `VerifyAccess`; a generation guard drops superseded posts; the extraction cache's last lock-free read was closed.

**On-disk cache (0.9.6).** `IconDiskCache` persists each icon as a PNG under `%AppData%\MenYou\icons\`, keyed by entry id, with source-mtime invalidation, negative-result caching (an icon-less app's null is cached, so it never re-runs the COM chain), atomic temp+move writes and a batched index flush. The ~150-icon cold fill dropped **~7.2 s → ~0.1–0.2 s**.

---

## Payload and packaging

MenYou ships **unsigned, self-contained**. Defender scans the payload as it loads, which dominates the first-ever launch.

- **Multi-file, not single-file** — see [Rejected levers](#rejected-levers).
- **No PDBs in the installer** — `libSkiaSharp.pdb` (~80 MB) and `libHarfBuzzSharp.pdb` (~20 MB) are ~45 % of the publish output and never loaded. `Excludes: "*.pdb"` removes 100 MB from Defender's first-run scan. MenYou's own symbols are embedded (`DebugType=embedded`).
- **Composite ReadyToRun** (0.9.29) — `PublishReadyToRun` AOT-compiles the app + Avalonia; *composite* emits one native image for the whole self-contained closure rather than one per assembly, so cross-assembly calls resolve directly instead of through indirection stubs, and one image is mapped instead of ~230. Slower build, larger output. The JIT stays as a fallback, so runtime-XAML custom themes are unaffected.
- **`av_libglesv2.dll` excluded** (0.9.29) — 5.3 MB of ANGLE for a GPU path `Program.cs` pinned off in 0.9.17 (`RenderingMode = Software` only). Confirmed absent from the running module list before removing. *If GPU rendering is re-enabled, drop the exclude with it.*
- **`[InstallDelete]` for retired files** (0.9.29) — `Avalonia.Fonts.Inter.dll` was still in a 0.9.27 install two releases after its reference was removed, which made 0.9.25's "1.8 MB saved" true for fresh installs and false for upgraders.

### The framework-dependent variant (retired in 0.9.20)

A second x64 installer shipped `--self-contained false`: ~17 MB download / ~50 MB installed against ~41 MB / ~122 MB, with the managed payload byte-identical.

**Measured: the size win was real, the cold-start win was conditional.** "Half the bytes ⇒ half the cold page-in" only holds if the shared runtime in `Program Files\dotnet` is already warm — i.e. some *other* .NET 10 app loads at logon. Where MenYou is the only one, the shared framework is exactly as cold as a bundled copy: the cost **moves**, it doesn't shrink. Traced: tray at **+10.3 s**, roughly the same as the trained self-contained build.

Retired for two reasons — and **one of them was a wrong inference worth recording**:

- **"It's the artifact Defender flags."** Through 0.9.19 every `Trojan:Win32/Wacatac.B!ml` hit had landed on `MenYou-fd-Setup`. That correlation broke immediately: `MenYou-Setup-0.9.20.exe` — self-contained — drew the identical verdict on the first release built *without* an FD variant. Retiring FD did not and could not fix the detections. What the investigation did establish stands: the verdict isn't reproducible offline (payload, locally built installer, and that installer with a GitHub Mark-of-the-Web all scan clean under `MpCmdRun`) because it's a cloud call at **download time** against a zero-prevalence **unsigned** binary. Every release mints fresh hashes, so each is an independent roll. See `CLAUDE.md` for the full investigation.
- **"Half the size" wasn't true from the user's side** — only on a machine that already had the ~55 MB runtime. Without it, the FD route cost *more* total bytes.

**Migration.** Both installers always shared one Inno `AppId`, so the SC installer upgrades an FD install in place. `GitHubUpdateService` still probes for `coreclr.dll`, but as a one-time migration trigger rather than a variant selector — an FD install is offered the SC asset **including on the same-version pass**, so it can't sit failing forever against an asset that stopped being published. Loop-safe: after the upgrade `coreclr.dll` exists. The name `MenYou-fd-Setup-<ver>.exe` stays **reserved** and must never be reused — FD installs in the field still look for exactly it.

---

## Trimming

> [!CAUTION]
> **Trimming shipped in 0.9.6 and was REVERTED in 0.9.11 — it crashed the app.**
>
> A trimmed build access-violates (`0xC0000005`, inside coreclr) in the shell-COM call `IShellItem::GetDisplayName` while enumerating `shell:AppsFolder`, which **kills the process** — an access violation is a corrupted-state exception, so the per-item `try/catch` cannot contain it. It fires on any cache-cold launch, so it also took out "open Settings".
>
> **Cause:** ILLink removes *uncalled* members from `[ComImport]` interfaces, but a COM method's native vtable slot **is** its declaration order — dropping one silently re-points every later method at the wrong slot. Metadata dumps of the shipped binaries showed **14 of 23** `[ComImport]` interfaces truncated, so this was never one bad call site: `IPersistFile::Load` would land on `GetClassID` and write a 16-byte CLSID over a string pointer.
>
> **The trap:** the build is *warning*-clean either way. Zero trim warnings does **not** prove hand-written COM interop survives trimming. Proven by strict A/B — same source, self-contained and ReadyToRun in both, only `PublishTrimmed` differing: **trimmed crashed 2/2, untrimmed survived 2/2**.
>
> **Before retrying:** `<TrimmerRootAssembly Include="MenYou" />` is already in the csproj as the precondition (it roots the whole assembly, so no interface is truncated). It is inert until `PublishTrimmed=true`. A retry **must** be verified by *running* a trimmed artifact — settings round-trip, custom-theme load and render, all localization bundles, Control Panel search and launch, jump lists, Start-pins policy, icon extraction, app enumeration. Budget the verification, not the change. The durable fix is migrating these interfaces to source-generated `[GeneratedComInterface]` (ComWrappers), whose vtable slots are fixed at compile time.

**What it bought:** 125.8 MB → 77.1 MB installed (−39 %), 231 → 112 files.

**What making it warning-clean took** — 412 trim warnings driven to zero, and all of this work survives in the tree for the next attempt:

1. **Source-generated JSON** — `SettingsJsonContext` + `MachineJsonContext` replace every reflection-based `System.Text.Json` path. Behaviour preserved deliberately: settings keep string enums and indentation; the discovery cache keeps *numeric* enums and PascalCase, so old caches still load and no cold rescan is forced. Start-pins anonymous types became named records.
2. **Compiled bindings** for the two layouts authored with `x:CompileBindings="False"` — reflection bindings run through `ReflectionBindingExtension`, which is `RequiresUnreferencedCode`.
3. **Explicit IDispatch COM** in `ControlPanelEnumerator`, replacing `dynamic` Shell.Application — `dynamic`-over-COM would run trimmed but drags the C# runtime binder and DLR into the payload.
4. **Per-site `[UnconditionalSuppressMessage]`** on hand-written shell-COM interop that is intrinsically flagged but safe.
5. **Trimmer roots** for the Avalonia XAML-loadable assemblies, so custom themes can still resolve any control or style type at runtime. Costs ~4 MB against a raw unsafe trim (73 → 77 MB).
6. **A trimmed-only `NoWarn`** covering residual *framework-emitted* plumbing with no MenYou call site — Avalonia's defensive per-view `[CompilerDynamicDependencies]` (~280 of them), XamlX, and `ComActivator`. **Trade-off:** new hand-written reflection won't be flagged in a release build, so keep the per-site suppression convention.

**NativeAOT stays ruled out** — it removes the JIT the runtime-XAML custom themes require.

---

## By version

| Version | Work |
|---|---|
| **0.2.0** | Parallel `.lnk` walk (~640 → ~400 ms), off-screen warm-up, single-flight `LoadAsync`, deferred shell-icon extraction, reveal-at-`Loaded` gated on real data. In-process only — still Run-key-bound. |
| **0.5.0** | Discovery cache, ReadyToRun (~½ framework startup), COM-free UWP fingerprint. Faster once running; end-to-end still Run-key-bound. |
| **0.5.x** | PDB exclusion from the installer; single-file trialled and reverted. |
| **0.6.0** | **Run-key → logon scheduled task** at PT3S — the ~15 s → ~3 s win, plus self-healing migration and zeroed `StartupDelayInMSec` fallback. |
| **0.7.0** | Trigger delay PT3S → PT1S (~3 s → ~1 s); first-run splash + ready balloon (splash removed in 0.8.0 — the cold load it covered starved its own first frame). |
| **0.8.7** | **Stale-while-revalidate** — fixed the intermittent ~20 s empty-menu cold start. |
| **0.8.8** | "Updating apps…" caption during catch-up scans. |
| **0.8.14** | Truthful-paint guards: degraded-scan quarantine, atomic id-map publish, Recent join-then-cap. |
| **0.9.0** | Ghost filtering — dead-path AUMIDs and uninstall-style entries dropped. |
| **0.9.2** | **Parallel icon fill** — visible batch 2086 → 446 ms, exactly-once extraction, capped DOP, per-item isolation. |
| **0.9.6** | **On-disk icon cache** (~7.2 s → ~0.1–0.2 s); FD installer variant; `PublishTrimmed` shipped — **runtime-broken**. |
| **0.9.11** | **Trimming reverted** — COM vtable slots shifted → `0xC0000005` on every cache-cold launch. `TrimmerRootAssembly` landed inert as the precondition for a retry. |
| **0.9.13** | FD cold start measured honestly: +10.3 s ≈ the trained SC build. FD's unconditional win is size, not cold start. |
| **0.9.17** | `WithInterFont()` dropped and `Win32RenderingMode.Software` pinned — ANGLE's `av_libGLESv2.dll` (~5.1 MB) leaves the load path. Modules 32 → 27. |
| **0.9.19** | **Input hooks installed before Avalonia** (`EarlyStartup`) — an early Start press no longer opens *Windows'* menu; presses that beat the UI are queued and replayed. Buys the Avalonia-init span, not the whole boot. |
| **0.9.20** | FD variant retired. |
| **0.9.28** | Self-heal for autostart deleted by an uninstall-then-install upgrade. |
| **0.9.29** | **Logon task priority 7 → 4** — below-normal CPU and I/O for an I/O-bound start at the most contended moment on the machine. Plus composite ReadyToRun, `av_libglesv2.dll` dropped, `[InstallDelete]` for retired files. |
