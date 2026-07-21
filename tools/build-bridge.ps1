# Builds MenYou.Bridge.vcxproj using VS msbuild discovered via vswhere.
# Invoked by MenYou.csproj's BeforeBuild target so 'dotnet build' produces
# the native DLL alongside the managed binaries. Exits 0 even on failure so
# 'dotnet build' doesn't fail on machines without MSVC - MenYou will then
# fall back to its WinEvent monitor at runtime.
#
# -Platform x64 (default) or ARM64. The ARM64 flavor cross-compiles from an
# x64 host when the "MSVC v143 - ARM64 build tools" component is installed
# (GitHub's windows-latest images carry it); if the toolset is absent,
# msbuild fails and this script still exits 0 - same managed fallback.
# Output lands in src/MenYou.Bridge/bin/<Configuration>/<Platform>/.
[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)
$ErrorActionPreference = "Continue"

$vsw = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $vsw) {
    Write-Host "MenYou.Bridge: vswhere not present, skipping native build."
    exit 0
}

# -products * is required so vswhere includes "BuildTools" (the standalone
# C++ build tools install). Without it, only the full Visual Studio IDEs
# (Community/Professional/Enterprise) are searched.
$msbuild = & $vsw -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "MenYou.Bridge: MSBuild from VS Build Tools not located, skipping native build."
    exit 0
}

# This script lives in tools/, so the bridge project is one level up, under src/.
$proj = Join-Path $PSScriptRoot "..\src\MenYou.Bridge\MenYou.Bridge.vcxproj"
if (-not (Test-Path $proj)) {
    Write-Host "MenYou.Bridge: project file not found at $proj"
    exit 0
}

Write-Host "MenYou.Bridge: building with $msbuild"
& $msbuild $proj "/p:Configuration=$Configuration" "/p:Platform=$Platform" /nologo /v:m
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host "MenYou.Bridge: msbuild returned exit code $code. Bridge DLL won't be available."
}
exit 0
