#requires -Version 5.1
<#
.SYNOPSIS
    One-off seed: upload MenYou's source strings and all existing
    translations to the Crowdin project (https://crowdin.com/project/menyou).

.DESCRIPTION
    Pushes src/MenYou/Languages/en.json as the source file, then uploads the
    existing per-locale files (de.json, es.json, ...) as translations so
    Crowdin starts pre-populated instead of empty. Uses the repo's
    crowdin.yml mapping and the official Crowdin CLI (installed via npm if
    missing). Credentials come from -ProjectId / -Token, the
    CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN environment variables, or an
    interactive prompt — the token is passed to the CLI via environment, not
    written to disk.

    This is a manual, run-once seed. Ongoing sync is handled monthly by
    .github/workflows/monthly-maintenance.yml.

.PARAMETER ProjectId
    Numeric Crowdin project ID (Project -> Settings -> API, or the URL).

.PARAMETER Token
    Crowdin personal access token with the "Projects (Source & translations)"
    scope. Create one at https://crowdin.com/settings#api-key

.PARAMETER AutoApprove
    Also mark the uploaded translations as approved (they were already
    shipping). Off by default so a moderator can review them in Crowdin.

.EXAMPLE
    ./tools/crowdin-upload.ps1 -ProjectId 123456 -Token abcdef...

.NOTES
    Add your 12 target languages (de, es, fr, it, ja, ko, nl, pl, pt, ru,
    uk, zh) to the Crowdin project FIRST — a language must exist there for
    its translation file to upload.
#>
[CmdletBinding()]
param(
    [string]$ProjectId = $env:CROWDIN_PROJECT_ID,
    [string]$Token     = $env:CROWDIN_PERSONAL_TOKEN,
    [switch]$AutoApprove
)

$ErrorActionPreference = 'Stop'

# This script lives in tools/, so the repo root is its parent directory.
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Config   = Join-Path $RepoRoot 'crowdin.yml'
$Source   = Join-Path $RepoRoot 'src/MenYou/Languages/en.json'
if (-not (Test-Path $Config)) { throw "crowdin.yml not found at the repo root ($RepoRoot)." }
if (-not (Test-Path $Source)) { throw "src/MenYou/Languages/en.json not found ($RepoRoot)." }

# --- credentials ----------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ProjectId)) { $ProjectId = Read-Host 'Crowdin numeric project ID' }
if ([string]::IsNullOrWhiteSpace($Token))     { $Token     = Read-Host 'Crowdin personal access token' }
if ([string]::IsNullOrWhiteSpace($ProjectId) -or [string]::IsNullOrWhiteSpace($Token)) {
    throw 'Both a project ID and a token are required.'
}

# --- locate / install the Crowdin CLI -------------------------------------
function Get-CrowdinPath {
    $c = Get-Command crowdin -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

$crowdin = Get-CrowdinPath
if (-not $crowdin) {
    Write-Host 'Crowdin CLI not found on PATH.' -ForegroundColor Yellow
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        Write-Host 'Installing @crowdin/cli globally via npm...' -ForegroundColor Yellow
        npm install -g '@crowdin/cli'
        # npm's global bin may not be on the current session PATH yet.
        $npmPrefix = (npm prefix -g) 2>$null
        if ($npmPrefix) { $env:PATH = "$npmPrefix;$env:PATH" }
        $crowdin = Get-CrowdinPath
    }
    if (-not $crowdin) {
        throw @'
Crowdin CLI is required. Install it one of these ways, then re-run:
  npm install -g @crowdin/cli      (needs Node.js)
  scoop install crowdin
  choco install crowdin-cli
Docs: https://crowdin.github.io/crowdin-cli/installation
'@
    }
}

# crowdin.yml reads these from the environment (project_id_env / api_token_env),
# so the token never lands in a file on disk.
$env:CROWDIN_PROJECT_ID     = $ProjectId
$env:CROWDIN_PERSONAL_TOKEN = $Token

Write-Host 'Uploading source strings (en.json)...' -ForegroundColor Cyan
& $crowdin upload sources --config $Config --base-path $RepoRoot
if ($LASTEXITCODE -ne 0) { throw "crowdin upload sources failed (exit $LASTEXITCODE)" }

Write-Host 'Uploading existing translations...' -ForegroundColor Cyan
$tArgs = @('upload', 'translations', '--config', $Config, '--base-path', $RepoRoot)
if ($AutoApprove) { $tArgs += '--auto-approve-imported' }
& $crowdin @tArgs
if ($LASTEXITCODE -ne 0) { throw "crowdin upload translations failed (exit $LASTEXITCODE)" }

Write-Host "`nDone. Review at https://crowdin.com/project/menyou" -ForegroundColor Green
