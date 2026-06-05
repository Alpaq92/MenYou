#requires -Version 5.1
<#
.SYNOPSIS
    Unwrap hard-wrapped Markdown prose to one line per paragraph / list item.

.DESCRIPTION
    Joins consecutive wrapped lines of a paragraph, list item, or blockquote
    paragraph onto a single physical line, so nothing is hard-wrapped at
    ~76 columns. Rendering-neutral on GitHub/CommonMark (a single newline
    inside a block is already a soft break = space).

    Preserved verbatim (never joined):
      * fenced code blocks (``` / ~~~) and their contents
      * standalone indented code blocks (4-sp/tab AFTER a blank line)
      * headings, horizontal rules, tables (any line with '|')
      * the list MARKER boundary (each item starts its own line; nested
        items stay separate) — only an item's wrapped continuation joins in
      * HTML blocks (lines starting with <), reference-link definitions
      * GitHub alert markers ( > [!NOTE] etc. stay on their own line )
      * blank lines (count preserved) and blockquote '>' structure

    Per-file encoding (UTF-8, no BOM added), newline style and trailing
    newline are preserved.

.PARAMETER Paths
    Explicit files. If omitted, every *.md under the repo root is processed,
    excluding third-party / build dirs.
#>
param([string[]]$Paths)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $Paths) {
  $exclude = '\\(openshell_research|node_modules|bin|obj|\.git)\\'
  $Paths = Get-ChildItem $root -Recurse -Filter *.md -File |
           Where-Object { $_.FullName -notmatch $exclude } |
           Select-Object -ExpandProperty FullName
}

# "Hard" structural lines: flush the current block and emit as-is. Excludes
# list items (handled specially so their continuations can join) and indented
# lines (handled contextually).
function Test-HardStructural([string]$t) {
  if ($t -match '^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]') { return $true }  # GH alert
  if ($t -match '^#{1,6}(\s|$)')          { return $true }  # heading
  if ($t -match '\|')                      { return $true }  # table
  if ($t -match '^\s*([-*_])\1{2,}\s*$')  { return $true }  # hr
  if ($t -match '^\s*=+\s*$')              { return $true }  # setext '='
  if ($t -match '^\s*<')                   { return $true }  # html
  if ($t -match '^\s*\[[^\]]+\]:\s')       { return $true }  # ref-link def
  return $false
}
$listRe = '^\s*([-*+]|\d+[.)])(\s|$)'

foreach ($path in $Paths) {
  $raw   = [System.IO.File]::ReadAllText($path)
  $crlf  = $raw.Contains("`r`n")
  $lines = [regex]::Split($raw, "`r`n|`n")

  $out   = New-Object System.Collections.Generic.List[string]
  $fence = $false
  $buf   = $null      # current block (prose paragraph OR list item) being built
  $qbuf  = $null      # blockquote-prose accumulator

  foreach ($line in $lines) {
    if ($line -match '^\s*(\x60{3,}|~{3,})') {                 # fenced code
      if ($null -ne $buf)  { $out.Add($buf); $buf = $null }
      if ($null -ne $qbuf) { $out.Add('> ' + $qbuf); $qbuf = $null }
      $out.Add($line); $fence = -not $fence; continue
    }
    if ($fence) { $out.Add($line); continue }

    $isQuote = $line -match '^\s*>'
    if (-not $isQuote -and $null -ne $qbuf) { $out.Add('> ' + $qbuf); $qbuf = $null }
    if ($isQuote     -and $null -ne $buf)  { $out.Add($buf);          $buf  = $null }

    if ($isQuote) {                                            # blockquote (conservative)
      $c = $line -replace '^\s*>\s?', ''
      if ($c -match '^\s*$') {
        if ($null -ne $qbuf) { $out.Add('> ' + $qbuf); $qbuf = $null }
        $out.Add('>')
      } elseif ($c -match '^\s*>' -or $c -match $listRe -or (Test-HardStructural $c)) {
        if ($null -ne $qbuf) { $out.Add('> ' + $qbuf); $qbuf = $null }
        $out.Add($line)
      } else {
        if ($null -eq $qbuf) { $qbuf = $c.Trim() } else { $qbuf = "$qbuf $($c.Trim())" }
      }
      continue
    }

    if ($line -match '^\s*$') {                                # blank
      if ($null -ne $buf) { $out.Add($buf); $buf = $null }
      $out.Add($line); continue
    }
    if ($line -match $listRe) {                                # list item: starts a new block
      if ($null -ne $buf) { $out.Add($buf); $buf = $null }
      $buf = $line.TrimEnd(); continue
    }
    if (Test-HardStructural $line) {                           # heading/table/hr/html/...
      if ($null -ne $buf) { $out.Add($buf); $buf = $null }
      $out.Add($line); continue
    }
    if ($line -match '^( {4,}|\t)') {                          # indented
      if ($null -ne $buf) { $buf = "$($buf.TrimEnd()) $($line.Trim())" }  # item/para continuation
      else { $out.Add($line) }                                # standalone -> code block, keep
      continue
    }
    # plain prose: start a paragraph, or continue the current block (lazy continuation)
    if ($null -eq $buf) { $buf = $line.TrimEnd() } else { $buf = "$($buf.TrimEnd()) $($line.Trim())" }
  }
  if ($null -ne $buf)  { $out.Add($buf) }
  if ($null -ne $qbuf) { $out.Add('> ' + $qbuf) }

  $nl   = if ($crlf) { "`r`n" } else { "`n" }
  [System.IO.File]::WriteAllText($path, [string]::Join($nl, $out), (New-Object System.Text.UTF8Encoding($false)))
  "{0,-40} {1,4} -> {2,4} lines" -f $path.Replace("$root\", ''), $lines.Count, $out.Count
}
