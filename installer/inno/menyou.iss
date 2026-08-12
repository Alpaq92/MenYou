; ============================================================================
;  MenYou — Inno Setup installer script
; ----------------------------------------------------------------------------
;  Build (CI or local), from the repo root, after a self-contained publish:
;
;    dotnet publish src/MenYou/MenYou.csproj -c Release -r win-x64 ^
;        --self-contained -o publish
;    iscc /DMyAppVersion=0.2.0 installer\inno\menyou.iss
;
;  Override the publish folder with /DMyPublishDir=...; the output
;  MenYou-Setup-<ver>.exe lands in <repo>\dist. CI signs that .exe via
;  SignPath after compiling (so no SignTool directive is needed here);
;  for LOCAL signing pass /DMySignTool="<signtool cmd $f>" and Inno will
;  sign the installer + uninstaller itself.
;
;  Self-contained only: the payload bundles the .NET runtime, so the target
;  needs no prerequisite. A framework-dependent variant (MenYou-fd-Setup,
;  /DMyVariant=fd) was also built here until 0.9.19 — retired because it was
;  the only artifact Defender's cloud ML kept flagging as Wacatac.B!ml, and
;  the "half the size" pitch was hollow once the ~55 MB runtime it required
;  was counted. Installs still in the field migrate to this installer via
;  GitHubUpdateService (same AppId, so it upgrades in place).
;
;  The in-app updater (GitHubUpdateService) downloads this .exe from the
;  GitHub release and runs it with /SILENT to upgrade in place. It picks the
;  asset matching the installed variant (coreclr.dll presence), so an FD
;  install updates to FD and an SC install to SC.
; ============================================================================

; --- identity ---------------------------------------------------------------
; Raw GUID kept separate so [Code] can build the uninstall-registry path
; with single braces, while [Setup].AppId needs the {{ }} escape form.
; NEVER change MyAppGuid — Inno keys in-place upgrades off it.
#define MyAppGuid "A9F2C7E4-3B6D-4F8A-9C1E-5D7B2A4F6E83"
#define MyAppId   "{{" + MyAppGuid + "}"

#define MyAppName "MenYou"
#define MyAppPublisher "Alpaq"
#define MyAppURL "https://github.com/Alpaq92/MenYou"
#define MyAppExeName "MenYou.exe"
; .NET 10 runtime download, surfaced by the FD installer's runtime precheck
; (unused by the SC build). Keep in sync with the README / release-body links.

; Version + source dir are supplied by CI; defaults keep a bare `iscc`
; from failing during local experimentation.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\..\publish"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}

; Per-user install (no admin needed). The GUI never prompts for an
; install scope, so "Default" is truly frictionless; a silent caller
; (the Chocolatey package) can still force machine-wide with /ALLUSERS.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline

; Overridable in the wizard's Custom path: install directory + Start-Menu
; group + shortcut tasks. DisableProgramGroupPage=no so the group page is
; available in Custom mode; the Default path skips it (see [Code]).
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
AllowNoIcons=yes

OutputDir=..\..\dist
; Per-architecture output. Pass /DMyArch=arm64 for the Windows-on-ARM
; installer (default: x64). Every output name is a FROZEN compat contract —
; see the naming-contract note in
; src/MenYou/Services/GitHubUpdateService.cs. x64 stays x64compatible
; (= installs under emulation on ARM too: the pre-arm64 status quo and the
; updater's fallback path); arm64 is native-only.
#ifndef MyArch
  #define MyArch "x64"
#endif
; "MenYou-fd-Setup-<ver>.exe" is RETIRED but RESERVED: FD installs still in
; the field look for exactly that name, so it must never be reused for a
; different payload.
#if MyArch == "arm64"
  #define MySetupBaseName "MenYou-arm64-Setup-" + MyAppVersion
  #define MyArchAllowed "arm64"
#else
  #define MySetupBaseName "MenYou-Setup-" + MyAppVersion
  #define MyArchAllowed "x64compatible"
#endif
OutputBaseFilename={#MySetupBaseName}
SetupIconFile=..\..\icon_v2.ico
; Distinct uninstaller icon — the app disc with the palette inverted (blue
; disc, silver MDI close-thick cross; see icon_uninstall.svg at the repo
; root). Shown in Apps & features / Add-Remove Programs and on the
; Start-menu uninstall shortcut, so removal is never one mis-click away
; from launch.
UninstallDisplayIcon={app}\icon_uninstall.ico
; Shows the MIT license as an accept/decline page (wpLicense) right after
; Welcome. The plain-text LICENSE renders fine in Inno's license box.
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; .NET 10 build; Win10+ only (matches the app's TFM). The FD variant
; additionally requires the .NET 10 Desktop Runtime — enforced in [Code]
; (PrepareToInstall), since Inno has no "runtime present" architecture gate.
ArchitecturesAllowed={#MyArchAllowed}
ArchitecturesInstallIn64BitMode={#MyArchAllowed}
MinVersion=10.0

; Restart Manager: when the in-app updater runs this silently while
; MenYou is open, close the running instance for the file swap and
; relaunch it afterwards — no manual restart needed.
CloseApplications=yes
RestartApplications=yes
; ...but only ever bounce MenYou's OWN process — never a third party that
; merely holds one of our files open. MenYou injects MenYou.Bridge.dll into
; explorer.exe (a SetWindowsHookEx hook), so the DEFAULT filter
; (*.exe,*.dll,*.chm) sees Explorer holding that DLL and Restart Manager
; tries to CLOSE the shell mid-upgrade — which hangs Setup and tears down the
; taskbar. Restricting detection to *.exe means only MenYou.exe is closed +
; relaunched; Windows auto-removes the hook when MenYou exits (freeing the
; DLL), and the app shadow-copies the bridge OUT of {app} so the install copy
; is never the one Explorer locks anyway (see BridgeInjector.ResolveBridgePath).
CloseApplicationsFilter=*.exe

; Local signing only (CI signs the output via SignPath instead). Pass
; /DMySignTool="sign $f" plus a configured SignTool to enable it.
#ifdef MySignTool
SignTool={#MySignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish";  MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
; Shown by the FD installer's [Code] runtime precheck when the .NET 10 runtime
; is absent (unused by the SC build, which bundles the runtime). %1 is the
; download URL. Recommends the Desktop Runtime (the conventional superset for a
; Windows GUI app) even though MenYou only needs the base .NET 10 runtime
; (Microsoft.NETCore.App); either satisfies the check.
;
; ASCII-only ON PURPOSE: this .iss has no UTF-8 BOM, so Inno reads it via the
; system ANSI code page — non-ASCII here would compile to mojibake in the
; message. The Polish text is therefore diacritic-free (matching this file's
; existing transliterated-Polish convention, e.g. "Wystapil blad" below). To
; restore diacritics, save the whole script as UTF-8 WITH BOM.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; NOTE: no "startupicon" task. Autostart is owned entirely by the app now —
; StartWithWindows defaults on and Win32AutostartService registers a
; logon-triggered scheduled task (Run-key/Startup-folder autostarts are
; throttled ~10-16 s after sign-in by Windows; the task fires promptly). An
; installer-time Startup-folder shortcut would be a second, throttled autostart
; that double-launches MenYou alongside the task, so it's intentionally gone.

[Files]
; The whole self-contained publish folder, minus *.pdb. The native SkiaSharp
; + HarfBuzz symbol files (libSkiaSharp.pdb ~80 MB, libHarfBuzzSharp.pdb
; ~20 MB) are ~100 MB / ~44% of the payload yet are NEVER loaded at runtime —
; they only exist for native crash symbolication. Excluding them shrinks the
; install and, more importantly, removes 100 MB from Defender's first-run scan
; surface on a fresh (unsigned) install. MenYou's own symbols are embedded
; (DebugType=embedded), so no managed debugging is lost either.
; av_libglesv2.dll is ANGLE's GLES translator, ~5.3 MB. Program.cs pins
; Win32PlatformOptions.RenderingMode to Software ONLY, so the ANGLE path can
; never be selected and the DLL is never loaded (verified against a running
; process's module list). Excluded rather than shipped-and-ignored. If GPU
; rendering is ever re-enabled in Program.cs, DROP THIS EXCLUDE with it.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,av_libglesv2.dll"; Flags: recursesubdirs createallsubdirs ignoreversion
; Custom-theme sample, shipped as an on-disk reference users can copy and
; edit. It is NOT a built-in style and is never auto-loaded — Settings ->
; Custom loads an .axaml the user points it at. This is just a worked
; example of the theming format, lifted straight from the repo's samples
; folder (path resolves against this script's dir, installer\inno).
; Sample custom themes, loadable from Settings -> Custom -> Load...
; A glob would be tidier, but each name is listed so a renamed or deleted
; sample fails the build loudly instead of silently shipping fewer themes.
Source: "..\..\samples\custom-themes\Windows7Square.axaml"; DestDir: "{app}\samples\custom-themes"; Flags: ignoreversion
Source: "..\..\samples\custom-themes\ClassicXpBlue.axaml"; DestDir: "{app}\samples\custom-themes"; Flags: ignoreversion
Source: "..\..\samples\custom-themes\Classic9xBlue.axaml"; DestDir: "{app}\samples\custom-themes"; Flags: ignoreversion
; Uninstaller icon (blue disc + silver close cross) referenced by
; UninstallDisplayIcon and the Start-menu uninstall shortcut.
Source: "..\..\icon_uninstall.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; IconFilename: "{app}\icon_uninstall.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
; Autostart is registered by the app at runtime (logon scheduled task via
; Win32AutostartService), not by an installer shortcut — see the [Tasks] note.

[InstallDelete]
; Inno never removes a file that has DROPPED OUT of [Files] — it only stops
; installing it — so every payload removal leaves an orphan behind on upgrade
; and the install dir only ever grows. These were both measured still sitting
; in a 0.9.27 install long after the code stopped referencing them. Add a line
; here whenever a shipped file is retired.
;   Avalonia.Fonts.Inter.dll — package reference dropped in 0.9.25 (nothing
;   asks for the Inter font; every FontFamily in the app is Segoe-based).
;   av_libglesv2.dll — excluded above; software rendering since 0.9.17.
Type: files; Name: "{app}\Avalonia.Fonts.Inter.dll"
Type: files; Name: "{app}v_libglesv2.dll"

[UninstallDelete]
; The native bridge is shadow-copied here at runtime — outside {app}, so an
; in-place upgrade never finds Explorer locking a file under the install folder
; (see CloseApplicationsFilter above and BridgeInjector.ResolveBridgePath). It
; isn't part of [Files], so remove it explicitly on uninstall. Best-effort: a
; copy still mapped into Explorer can't be deleted right now and is reclaimed by
; Windows later.
Type: filesandordirs; Name: "{localappdata}\MenYou\bridge"

[Registry]
; One-shot "show the ready balloon" marker. MenYou shows its tray balloon on the
; next launch when this value is present, then clears it — so the "MenYou is
; ready" balloon appears after every install/update, not only on a brand-new
; profile (settings.json survives uninstall, which would otherwise suppress it
; after the first run). The whole key is MenYou-only, so uninsdeletekey cleans
; it up on uninstall.
Root: HKCU; Subkey: "Software\MenYou"; ValueType: dword; ValueName: "ShowReadyBalloon"; ValueData: 1; Flags: uninsdeletekey

[Run]
; Offered only on an interactive (first) install; silent updates skip it
; and rely on Restart Manager to relaunch the instance it closed.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  SetupTypePage: TInputOptionWizardPage;

{ Setup Type page, shown after the license page: Default vs Custom.
  Default installs with the recommended settings and skips the directory /
  Start-Menu / tasks pages; Custom shows them all so the user can change
  everything. }
procedure InitializeWizard();
begin
  SetupTypePage := CreateInputOptionPage(wpLicense,
    'Setup Type', 'How would you like to install ' + '{#MyAppName}' + '?',
    'Choose Default to install with the recommended settings, or Custom to pick the install folder, shortcuts and Start-Menu group. Then click Next.',
    True, False);
  SetupTypePage.Add('Default install (recommended)');
  SetupTypePage.Add('Custom install - choose folder, shortcuts and Start-Menu group');
  SetupTypePage.SelectedValueIndex := 0;
end;

function IsCustomInstall(): Boolean;
begin
  Result := Assigned(SetupTypePage) and (SetupTypePage.SelectedValueIndex = 1);
end;

{ In Default mode, skip every page that would let the user change a
  setting; Custom mode shows them all. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (not IsCustomInstall()) and
            ((PageID = wpSelectDir) or
             (PageID = wpSelectProgramGroup) or
             (PageID = wpSelectTasks));
end;

// Read the version a prior install recorded in its uninstall key (HKCU for
// a per-user install, HKLM for per-machine). Single-brace GUID built here
// because [Code] strings can't use the ISPP brace-escape form.
function PreviousVersion(): String;
var
  Key, V: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#MyAppGuid}' + '}_is1';
  if RegQueryStringValue(HKCU, Key, 'DisplayVersion', V) then
    Result := V
  else if RegQueryStringValue(HKLM, Key, 'DisplayVersion', V) then
    Result := V;
end;

function InitializeSetup(): Boolean;
var
  Prev: String;
  PrevPacked, CurrPacked: Int64;
begin
  Result := True;
  Prev := PreviousVersion();
  { Refuse to downgrade: if an installed build is newer than this one,
    bail out instead of silently rolling the user back. Best-effort —
    if either version can't be parsed, allow the install to proceed. }
  if (Prev <> '') and StrToVersion(Prev, PrevPacked)
     and StrToVersion('{#MyAppVersion}', CurrPacked) then
  begin
    if ComparePackedVersion(PrevPacked, CurrPacked) > 0 then
    begin
      MsgBox('A newer version of ' + '{#MyAppName}' + ' (' + Prev +
             ') is already installed. Setup will now exit.',
             mbInformation, MB_OK);
      Result := False;
    end;
  end;
end;


{ Close any MenYou.exe running from OUTSIDE the install directory.

  Why: the single-instance guard (Platform\Windows\SingleInstance.cs) keys off
  ONE fixed mutex name, so it is path-blind — every MenYou binary claims the
  same guard. Restart Manager (CloseApplications=yes) only closes processes
  holding files in the install directory, so a MenYou started from anywhere
  else (a portable extract, a dev build, an old folder left behind by a moved
  install) survives the upgrade, keeps the mutex, and the new exe then rings that
  stale process's doorbell and EXITS. The user sees the old build still
  serving the menu — reporting its own older version — with no sign the update
  did not take effect. (Observed while shipping 0.9.14.)

  So: leave the installed-directory process to Restart Manager, which closes it
  gracefully and relaunches it afterwards (RestartApplications=yes), and only
  force-close the strays RM cannot see. Terminating them is the intended
  outcome — they are the same app being upgraded, and MenYou persists settings
  on Apply rather than at exit, so there is nothing buffered to lose.

  Best-effort by design: a failed stray-check must never block an otherwise
  valid install. }
procedure CloseStrayMenYou();
var
  Loc, Svc, Procs, P: Variant;
  AppDir, ExePath: String;
  I, Pid, Rc: Integer;
begin
  AppDir := AddBackslash(ExpandConstant('{app}'));
  try
    { Inno's Pascal Script cannot chain a method onto CreateOleObject's
      result, so the locator needs its own variable. }
    Loc := CreateOleObject('WbemScripting.SWbemLocator');
    Svc := Loc.ConnectServer('', 'root\CIMV2', '', '');
    Procs := Svc.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process'
                           + ' WHERE Name = "MenYou.exe"');
    for I := 0 to Procs.Count - 1 do
    begin
      P := Procs.ItemIndex(I);
      ExePath := '';
      { Null when the query cannot read the path (another user's process). }
      if not VarIsNull(P.ExecutablePath) then
        ExePath := P.ExecutablePath;
      if (ExePath <> '')
         and (CompareText(Copy(ExePath, 1, Length(AppDir)), AppDir) <> 0) then
      begin
        Pid := P.ProcessId;
        Log('Closing stray MenYou.exe (pid ' + IntToStr(Pid) + ') at ' + ExePath);
        { Kill via taskkill.exe rather than the WMI instance's own Terminate(),
          even though we hold the instance and Terminate() is one call with no
          process spawn. 0.9.15 shipped Terminate() and Defender's cloud ML
          immediately flagged the FD installer as Trojan:Win32/Wacatac.B!ml —
          a false positive (the asset is CI-built by github-actions from the
          tagged public source), but a costly one, and WMI-enumerate-then-
          Terminate is textbook malware behaviour to a heuristic. taskkill has
          been in this script since before 0.9.0 across 15+ releases with no
          such detection, so the spawn is the price of not tripping the
          classifier. Do not "optimise" this back to Terminate(). }
        Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /pid ' + IntToStr(Pid), '',
             SW_HIDE, ewWaitUntilTerminated, Rc);
      end;
    end;
  except
    Log('Stray-instance check skipped: ' + GetExceptionMessage);
  end;
end;

{ Runs right before files are installed. Older MenYou builds (< 0.8.0) injected
  MenYou.Bridge.dll into explorer.exe with a hook OWNED BY EXPLORER, so the DLL
  stayed mapped — and its file locked — even after MenYou closed. Replacing it
  then failed with "Wystapil blad ... DeleteFile; kod 5. Odmowa dostepu."
  (DeleteFile failed, access denied). 0.8.0+ owns the hook from MenYou and drops
  it on exit, so the installed copy is never pinned; the block below therefore
  only triggers when upgrading FROM an older build, and never again afterwards.

  Probe by trying to delete the file: if that fails it is locked, so close
  MenYou (so it cannot re-inject), restart Explorer to unload the stale hook,
  then delete the now-free file — leaving a clean slate for [Files] with no
  error prompt. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dll: String;
  Rc: Integer;
begin
  Result := '';
  { Before touching any files — see the procedure header for why. }
  CloseStrayMenYou();
  Dll := ExpandConstant('{app}\MenYou.Bridge.dll');
  if FileExists(Dll) and (not DeleteFile(Dll)) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MenYou.exe', '',
         SW_HIDE, ewWaitUntilTerminated, Rc);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im explorer.exe', '',
         SW_HIDE, ewWaitUntilTerminated, Rc);
    Sleep(2000);
    { Windows relaunches the shell automatically (AutoRestartShell); only force
      it if the taskbar did not return, so we do not pop a stray Explorer
      window. }
    if FindWindowByClassName('Shell_TrayWnd') = 0 then
      Exec(ExpandConstant('{win}\explorer.exe'), '', '', SW_SHOWNORMAL, ewNoWait, Rc);
    { If it is somehow STILL locked after closing MenYou and restarting Explorer,
      fail fast with an actionable message rather than letting [Files] trip the
      cryptic "DeleteFile; kod 5" prompt later. }
    if (not DeleteFile(Dll)) and FileExists(Dll) then
      Result := 'Setup could not replace MenYou.Bridge.dll because it is still in use. ' +
                'Please reboot and run Setup again.';
  end;
end;

{ On uninstall (after the user confirms, before anything is removed):

  1. CLOSE a running MenYou first. Uninstalling while the app runs left a
     real machine in a zombie state: the uninstall log's registry entries
     (the <AppId>_is1 key, the autostart cleanup below) were processed, but
     the running process kept MenYou.exe + its loaded DLLs locked so the
     payload survived — and on its next launch the app self-healed its
     autostart task (EnsureAutostartDefault). Net result: still installed
     and autostarting, but invisible to Windows' Apps list and with the
     in-app updater dead (GitHubUpdateService.IsPackaged reads
     DisplayVersion from the _is1 key). Killing the process up front lets
     the file removal actually complete. Only MenYou.exe is touched — NOT
     explorer.exe: the 0.8.0+ input hook is MenYou-owned, so Windows drops
     it (and unmaps the bridge DLL from Explorer) when MenYou exits. The
     kill is verified — re-run until taskkill reports the process gone — and
     if MenYou is somehow still alive afterwards the uninstall is aborted
     before any payload is removed, rather than proceeding into the zombie
     state. 128 ("not running") is the benign success case. See the loop.

  2. Tear down the per-user autostart the APP created at runtime: the
     logon-triggered scheduled task (Win32AutostartService, task name
     "MenYou") and any legacy HKCU\...\Run value from pre-scheduled-task
     builds. [Files]/[Registry] don't know about these — they're written by
     the running app, not Setup — so without this they outlive the
     uninstall: the orphaned logon task keeps firing at each sign-in and
     silently fails because its target exe is gone.

  Per-user install (PrivilegesRequired=lowest), so the uninstaller runs in
  the user's context and the removals hit the right hive. User data under
  %APPDATA%\MenYou (settings, caches, custom themes) is intentionally left
  intact. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Rc, Attempt: Integer;
  StillRunning: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Kill, wait, then VERIFY the process is actually gone before touching
      files. taskkill's Rc is only a usable signal in /IM mode: there Rc=128
      means "no such process" (gone) while Rc=0 means it killed an instance
      this pass (teardown + handle release is async, so re-verify). Do NOT use
      /FI filter mode to probe: filter mode returns 0 whether it killed
      something or found nothing, so it cannot tell "gone" from "killed".
      Mirror PrepareToInstall's verify-by-side-effect: re-run /IM until it
      reports 128 (gone). 128 on a normal uninstall (app never running) is the
      benign success case and must NOT be treated as a failure. }
    StillRunning := True;
    for Attempt := 1 to 3 do
    begin
      if Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MenYou.exe', '',
              SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 128) then
      begin
        StillRunning := False;
        Break;
      end;
      { Either we just killed an instance (Rc=0) or the kill genuinely failed
        (other Rc / Exec failure). Give the OS a beat to finish teardown and
        release file handles, then loop to confirm it is really gone. }
      Sleep(1500);
    end;

    { Refuse to proceed if MenYou is verifiably still alive: continuing would
      strip the _is1 key + autostart while the live process keeps the payload
      locked, recreating the invisible, still-autostarting zombie this fix
      exists to prevent. RaiseException fails the uninstall with a real error
      (and a non-zero exit choco logs) instead of a MsgBox, which would hang
      the /VERYSILENT path since /SUPPRESSMSGBOXES does not suppress [Code]
      MsgBox. The uninstall aborts before any payload is removed, so the app
      stays fully installed and re-uninstallable. }
    if StillRunning then
      RaiseException('MenYou could not be closed automatically, so uninstall '
        + 'was stopped to avoid leaving a half-removed install. Please close '
        + 'MenYou (it may be in the system tray) and run the uninstaller '
        + 'again.');

    { Task name matches Win32AutostartService.TaskName. }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "MenYou" /f', '',
         SW_HIDE, ewWaitUntilTerminated, Rc);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MenYou');
  end;
end;
