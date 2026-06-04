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
;  The in-app updater (GitHubUpdateService) downloads this .exe from the
;  GitHub release and runs it with /SILENT to upgrade in place.
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
OutputBaseFilename=MenYou-Setup-{#MyAppVersion}
SetupIconFile=..\..\icon_v2.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; Shows the MIT license as an accept/decline page (wpLicense) right after
; Welcome. The plain-text LICENSE renders fine in Inno's license box.
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; .NET 10 self-contained x64 build; Win10+ only (matches the app's TFM).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Restart Manager: when the in-app updater runs this silently while
; MenYou is open, close the running instance for the file swap and
; relaunch it afterwards — no manual restart needed.
CloseApplications=yes
RestartApplications=yes

; Local signing only (CI signs the output via SignPath instead). Pass
; /DMySignTool="sign $f" plus a configured SignTool to enable it.
#ifdef MySignTool
SignTool={#MySignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish";  MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "{cm:AutoStartProgram,{#MyAppName}}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish folder.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
; "Start with Windows" — a per-user Startup-folder shortcut. MenYou also
; has its own in-app autostart toggle; this is the installer-time opt-in.
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

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
