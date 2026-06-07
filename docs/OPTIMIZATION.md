# MenYou — optimization

How MenYou is kept fast, and the reasoning behind each change — focused on **startup**, which is what users feel most for a Start-menu replacement that lives in the tray and must be ready the moment they sign in.

This is an engineering record, not a changelog: it covers *what* was done, *why*, the *measurements* that justified it, and the approaches that were **tried and rejected** (so they aren't re-attempted). It spans **0.5.0 → 0.7.0**.

---

## Results

Cold boot, time from the desktop appearing to MenYou being usable (the metric the user actually experiences):

| Build | Desktop → MenYou process | Desktop → tray usable | What changed |
|---|---|---|---|
| 0.5.0 (`HKCU\Run`) | ~15 s | ~16 s | **Deep *post-launch* pass** — discovery cache (instant data paint), immediate reveal, ReadyToRun (~½ framework startup), COM-free UWP fingerprint. Real wins, but all *after* the process starts (see note). |
| 0.6.0 (`HKCU\Run`) | ~15 s | ~16 s | No launch-path change; the Run-key throttle still dominates the cold start. |
| 0.7.0 — logon task @ PT3S | ~3 s | ~6 s | Autostart moved off the Run-key. |
| **0.7.0 — logon task @ PT1S** | **~1 s** | **~2–4 s** | Task delay trimmed. |

Same binary, same machine — **~14 seconds faster** purely by changing *how Windows is told to launch the app*. The remaining time is Windows reaching the desktop (not MenYou) plus a brief cold load.

> **Why 0.5.0 shows no improvement in this table.** 0.5.0 was itself a heavy startup-performance release — but it optimized the *post-launch* path: how fast the menu's data paints and its window is ready *once the process is running*. This cold-start metric is dominated by the **~15 s before the process even starts** — the Run-key throttle, which 0.5.0 never touched — so its gains are invisible end-to-end. That's precisely the measurement trap described in the next section: the right code, the wrong *segment*. The two fixes **compound**: 0.7.0 makes the process start promptly, and 0.5.0's cache + ReadyToRun are what make it *usable* only ~1–3 s after it does. Today's ~2–4 s desktop→usable is both, together.

---

## How startup is measured

Perceived "slow startup" is ambiguous, so every claim here is anchored to four timestamps from one cold boot:

1. **Boot** — `Win32_OperatingSystem.LastBootUpTime`.
2. **Desktop ready** — `explorer.exe` process start time (the shell is up).
3. **MenYou process start** — `MenYou.exe` process start time.
4. **Tray ready** — the `Startup: tray done` line in the opt-in trace log (`%TEMP%\menyou-hooks.log`, enabled via the Developer tab or `MENYOU_TRACE_HOOKS=1`), which is stamped `+<ms since process start>`.

The decisive insight came from **(3) − (2)**: the gap between the desktop being ready and MenYou's process starting. Measuring it isolated the bottleneck to *before our code runs but after the shell is up* — which ruled out Windows boot, Defender, disk, and MenYou's own init (all outside that window) and pointed straight at the autostart launch mechanism. Optimizing without this measurement is how an earlier round of work (below) tuned the wrong segment.

---

## 1. Cold-start: the autostart mechanism (the big win, 0.7.0)

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
- **Installer** — the opt-in Startup-folder shortcut was removed from the Inno script; autostart is owned entirely by the app now, so the two mechanisms can't double-launch.

---

## 2. Menu data: the discovery cache (0.5.0)

App discovery (enumerating `.lnk` shortcuts, UWP packages, Control Panel and Settings deep-links) is COM-heavy and slow on a cold shell. Two changes keep the menu's data instant:

- **Persisted snapshot** — discovery results are cached at `%AppData%\MenYou\discovery-cache.json`. On launch the menu is served from this plain file read (no shell COM), so it paints instantly on a cold start; a live scan still runs in the background and swaps in if anything changed, guarded by a filesystem fingerprint so a stale snapshot is never shown.
- **Parallelized `.lnk` walk** — per-shortcut `ShellLink` + shell-localization COM is fanned out across cores: cold discovery ~640 ms → ~400 ms.
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

## 5. Perceived first-run: the splash (0.7.0)

The one-time ~10 s first-install cold load (Defender + cold disk) can't be eliminated without signing or trimming (see Rejected), so it's **covered** instead:

- **`NativeSplash`** — a Win32/GDI splash drawn on its **own thread** with its own message pump. A splash on the UI thread can't paint during the cold load (the load blocks that very thread — it just shows the busy cursor and ghosts/blinks); a separate thread paints through it. Shown from `Program.Main` *before* Avalonia loads, so it appears as early as possible.
- **Theme-aware** (light/dark from the Windows apps theme), a **high-res icon** (256 px frame downscaled with GDI+ high-quality bicubic — `DrawIconEx` looked muddy), and a self-animated marquee. One-time only (gated by the absence of `settings.json`).
- **"Ready" tray balloon** — when the first load finishes, a one-time balloon confirms the install worked and teaches the hotkey ("MenYou is ready — Press Shift+Win"). Localized into the shipped languages.

---

## Rejected / not pursued

- **`PublishSingleFile`** — see §3 (~54 s cold autostart, unsigned). Kept multi-file.
- **`PublishTrimmed`** — a trim trial built cleanly but produced **430 trim warnings**, including the two that matter: `System.Text.Json` reflection (settings load/save) and `AvaloniaRuntimeXamlLoader` (custom themes). It would break both features. Doing it safely needs source-generated JSON + trimmer-root descriptors + a full feature-test pass — substantial work for a modest size win, when the startup bottleneck was never the payload size. Deferred.
- **NativeAOT** — incompatible with the runtime-XAML custom-theme feature (which needs the JIT). ReadyToRun gives most of the startup benefit without that cost.
- **Optimizing the in-process path *as the cold-start fix*** — an earlier round (warm-up priority tuning, pdb exclusion, single-file revert) targeted MenYou's own init, which was already fast. Necessary polish, but it was the wrong segment for the perceived ~15 s slowness; the measurement in the methodology section is what redirected the effort to the Run-key throttle.

---

## Per-version summary

| Version | Optimization work |
|---|---|
| **0.5.0** | Deep *post-launch* startup pass: discovery cache (instant cold paint) + COM-free UWP fingerprint, immediate menu reveal, ReadyToRun (~½ framework startup), parallelized `.lnk` walk, account-picture load off the UI thread. (End-to-end gain masked by the Run-key throttle until 0.7.0 — see the note under Results.) |
| **0.5.x** | pdb exclusion from the installer; multi-file kept (single-file trialed and reverted). |
| **0.7.0** | **Run-key → logon scheduled task** (the ~15 s → ~1 s cold-start win) + PT1S delay + self-healing migration; first-run native splash + ready balloon; Settings-window flash→fade; (trimming evaluated and rejected). |
