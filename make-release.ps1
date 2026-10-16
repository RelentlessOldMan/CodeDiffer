<#
.SYNOPSIS
  Package the CodeDiffer plugin (Claude Code + Codex; CLI + MCP server, self-contained win-x64, no .NET needed)
  as one zip and, with -Publish, put it on GitHub Releases as v<version>.

.DESCRIPTION
  The CodeCompass model (make-release.ps1 + build-plugin.ps1): build-plugin.ps1 publishes plugin/bin and stamps
  the version into the plugin manifests; the zip is the plugin/ folder itself. Rules:
    1. the working tree must be clean and HEAD must already be on origin/main (a zip is always a pushed commit);
    2. the tests must pass in Release (skip with -SkipTests only if you just ran them);
    3. the CLI's version must match the build and the manifests, and the published MCP server must answer
       initialize + tools/list with every tool the source declares (a server that can't start never ships);
    4. the zip is written with '/' entry names (Compress-Archive on PowerShell 5.1 writes '\') and checked;
    5. the version stamp is put back to the committed 0.0.0-dev sentinel, so the tree stays clean.
  The version is 1.0.<commit count> (Directory.Build.props), the same number `CodeDiffer.Cli.exe version` prints.

.EXAMPLE
  ./make-release.ps1              # test, build and zip into dist\
  ./make-release.ps1 -Publish     # ... and create the GitHub release v<version> from the zip
#>
param([switch]$Publish, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# git and gh write progress and "not found" to stderr; under Stop on PowerShell 5.1 that becomes a terminating
# error even on exit 0. Run natives under Continue and decide on the exit code.
function Native {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$a)
    if (-not (Get-Command $a[0] -ErrorAction SilentlyContinue)) { throw "$($a[0]) is not on PATH" }
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $errFile = [System.IO.Path]::GetTempFileName()
    try { $global:LASTEXITCODE = 0; $o = & $a[0] @($a | Select-Object -Skip 1) 2>$errFile; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $prev }
    $err = (Get-Content -Raw $errFile -ErrorAction SilentlyContinue); Remove-Item $errFile -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "$($a -join ' ') failed (exit $code): $err" }
    return $o
}

# Text files that ship: UTF-8 without a BOM, CRLF, whichever PowerShell runs this.
function Write-Text([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))
}

# 1) A clean tree whose HEAD is origin/main, a full clone (the version is the commit count), and gh ready.
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-Publish needs the GitHub CLI (gh) on PATH" }
    Native gh auth status | Out-Null
}
$dirty = git status --porcelain
if ($dirty) { throw "working tree has uncommitted changes - commit them first:`n$($dirty -join "`n")" }
if ((Native git rev-parse --is-shallow-repository).Trim() -eq 'true') { throw "shallow clone: the version (commit count) would be wrong - git fetch --unshallow first" }
$sha = (Native git rev-parse HEAD).Trim()
Native git fetch origin main --quiet | Out-Null
$remote = (Native git rev-parse origin/main).Trim()
if ($remote -ne $sha) { throw "HEAD ($($sha.Substring(0, 9))) is not origin/main ($($remote.Substring(0, 9))) - push it, or pull first." }
Write-Host "OK: $($sha.Substring(0, 9)) is origin/main." -ForegroundColor Green

# 2) Tests.
if ($SkipTests) { Write-Warning "SkipTests: not running the unit suite." }
else {
    Write-Host "== Tests (Release) ==" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'CodeDiffer.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed - not packaging" }
}

# The manifests build-plugin stamps (tracked; committed as the 0.0.0-dev sentinel).
$stampedManifests = @('plugin/.claude-plugin/plugin.json', 'plugin/plugin.json')
function Restore-StampedManifests { git checkout -- @stampedManifests 2>$null | Out-Null }

# 3) Build the plugin: both exes self-contained into plugin/bin, the version stamped into the manifests.
$plugin = Join-Path $root 'plugin'
$bin = Join-Path $plugin 'bin'
# The stamped text goes into the zip (step 5); the tree goes straight back to the sentinel.
$stamped = @{}
try {
    Write-Host "== build-plugin.ps1 ==" -ForegroundColor Cyan
    & (Join-Path $root 'build-plugin.ps1')
    foreach ($m in $stampedManifests) { $stamped[$m.Substring('plugin/'.Length)] = [System.IO.File]::ReadAllText((Join-Path $root $m)) }
}
finally { Restore-StampedManifests }
$manifestVersions = @($stamped.Values | ForEach-Object { ($_ | ConvertFrom-Json).version })
if (git status --porcelain) { throw "publishing changed tracked files - not packaging:`n$(git status --porcelain)" }

# The version the shipped CLI reports, e.g. "codediffer 1.0.36+8d5e88b" -> 1.0.36. Its commit must be HEAD, and
# both manifests must carry the same number.
$cliExe = Join-Path $bin 'CodeDiffer.Cli.exe'
$verLine = (& $cliExe version | Out-String).Trim()
if ($verLine -notmatch '^codediffer (\d+\.\d+\.\d+)\+([0-9a-f]+)') { throw "unexpected version output: '$verLine'" }
$version = $Matches[1]
if (-not $sha.StartsWith($Matches[2])) { throw "the built CLI says commit $($Matches[2]) but HEAD is $sha" }
foreach ($v in $manifestVersions) { if ($v -ne $version) { throw "version mismatch: a manifest says '$v', the CLI '$version'" } }
Write-Host "Version $version" -ForegroundColor Green

# Smoke the published MCP server: a real stdio handshake that must list the tools.
Write-Host "Smoke-testing the MCP server (initialize + tools/list) ..."
$psi = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $bin 'CodeDiffer.Mcp.exe'))
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$mcp = [System.Diagnostics.Process]::Start($psi)
try {
    $errTask = $mcp.StandardError.ReadToEndAsync()   # drain, or a full stderr pipe stalls the server
    $mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1"}}}')
    $mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $mcp.StandardInput.Flush()
    # Read until the tools/list reply, THEN close stdin: closing first can end the session before it answers.
    $out = ''; $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $t = $mcp.StandardOutput.ReadLineAsync()
        if (-not $t.Wait([Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { break }
        if ($null -eq $t.Result) { break }
        $out += $t.Result + "`n"
        if ($t.Result -match '"id"\s*:\s*2\b') { break }
    }
    $mcp.StandardInput.Close()
    if (-not $mcp.WaitForExit(15000)) { $mcp.Kill() }
    # Every tool the source declares must be listed (read from the source, so the list never goes stale).
    $expected = @(Select-String -Path (Join-Path $root 'src\CodeDiffer.Mcp\*.cs') -Pattern 'McpServerTool\(Name = "([a-z0-9_]+)"' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $reply = $out -split "`n" | Where-Object { $_ -match '"id"\s*:\s*2\b' } | Select-Object -First 1
    if (-not $reply) { throw "MCP smoke FAILED: no tools/list reply. stderr:`n$($errTask.Result)" }
    $listed = @((ConvertFrom-Json $reply).result.tools | ForEach-Object { $_.name })
    $missing = @($expected | Where-Object { $listed -notcontains $_ })
    if ($expected.Count -eq 0 -or $missing.Count) { throw "MCP smoke FAILED: tools/list is missing $($missing -join ', '). stderr:`n$($errTask.Result)" }
    $unknown = @($listed | Where-Object { $expected -notcontains $_ })
    if ($unknown.Count) { Write-Warning "tools/list has tools the source scan didn't find: $($unknown -join ', ')" }
    Write-Host "  PASS  the server answers and lists all $($expected.Count) tools." -ForegroundColor Green
}
finally {
    try { if (-not $mcp.HasExited) { $mcp.Kill() } } catch {}
    try { $mcp.WaitForExit(5000) | Out-Null; $mcp.Dispose() } catch {}
}

# 4) What ships beside the plugin's own files: written straight into the zip, so the tree stays clean.
$extraText = [ordered]@{
    'RELEASE.txt' = "CodeDiffer $version`ncommit $sha`nbuilt  $(Get-Date -Format o)`n"
    'INSTALL.txt' = @"
CodeDiffer plugin $version - install (Windows x64, no .NET needed)
==================================================================

This folder IS the plugin. It directly contains .claude-plugin\ and bin\.
There is NO plugin\ subfolder to point at - use THIS folder. Put it somewhere stable first.

NOTE: there is no "/plugin add <path>" command. Use one of these:

PERSISTENT (stays installed across sessions) - this folder is a self-marketplace:

    /plugin marketplace add "<full path to this folder>"
    /plugin install codediffer@codediffer

e.g.  /plugin marketplace add "C:\Tools\codediffer-plugin-$version-win-x64"
      /plugin install codediffer@codediffer

ONE SESSION ONLY (no install) - launch Claude Code with:

    claude --plugin-dir "<full path to this folder>"

Then run  /mcp  to confirm the "codediffer" server is connected. Claude Code copies an installed plugin
into its own cache, one folder per version, so installing a newer release never touches the files a
running session's server is using.

--------------------------------------------------------------
CODEX (OpenAI) - this folder is also a Codex plugin (marketplace + MCP + skill):

    ./install-codediffer.ps1 -TargetHost Codex

or by hand:

    codex plugin marketplace add "<full path to this folder>"
    codex plugin add codediffer@codediffer

UPGRADING: unzip the new release anywhere and run the same command from it (by hand: codex plugin
marketplace remove codediffer first).

--------------------------------------------------------------
THE COMMAND LINE:  bin\CodeDiffer.Cli.exe help

Compares are saved under %LOCALAPPDATA%\CodeDiffer\results (CODEDIFFER_RESULTS_DIR overrides).
Docs: README.md and docs\OUTPUT.md here, or https://github.com/RelentlessOldMan/CodeDiffer
"@
}
$extraFiles = [ordered]@{ 'LICENSE' = 'LICENSE'; 'docs/OUTPUT.md' = 'docs\OUTPUT.md' }

# 5) Zip the plugin folder's contents (it extracts to a folder holding .claude-plugin\) with '/' entry names,
#    then check it.
$zipName = "codediffer-plugin-$version-win-x64.zip"
$zip = Join-Path $root "dist\$zipName"
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$level = [System.IO.Compression.CompressionLevel]::Optimal
function Add-TextEntry($zw, [string]$name, [string]$text) {
    $w = [System.IO.StreamWriter]::new($zw.CreateEntry($name, $level).Open(), [System.Text.UTF8Encoding]::new($false))
    try { $w.Write($text -replace "`r?`n", "`r`n") } finally { $w.Dispose() }
}
$pluginRoot = (Resolve-Path $plugin).Path.TrimEnd('\')
$zw = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in [System.IO.Directory]::EnumerateFiles($pluginRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
        $entry = $f.Substring($pluginRoot.Length + 1).Replace('\', '/')
        if ($extraText.Contains($entry) -or $extraFiles.Contains($entry)) { continue }   # a stray local copy
        if ($stamped.ContainsKey($entry)) {
            # The stamped manifest: no BOM, the bytes build-plugin wrote.
            $s = $zw.CreateEntry($entry, $level).Open()
            try { $b = [System.Text.UTF8Encoding]::new($false).GetBytes($stamped[$entry]); $s.Write($b, 0, $b.Length) } finally { $s.Dispose() }
            continue
        }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zw, $f, $entry, $level)
    }
    foreach ($k in $extraText.Keys) { Add-TextEntry $zw $k $extraText[$k] }
    foreach ($k in $extraFiles.Keys) { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zw, (Join-Path $root $extraFiles[$k]), $k, $level) }
}
finally { $zw.Dispose() }
# Claude Code AND Codex each need their own manifest, MCP config and marketplace, plus the skill and both exes.
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($za.Entries | ForEach-Object { $_.FullName })
    $zipped = $za.GetEntry('.claude-plugin/plugin.json')
    $r = [System.IO.StreamReader]::new($zipped.Open()); try { $zippedVer = ($r.ReadToEnd() | ConvertFrom-Json).version } finally { $r.Dispose() }
}
finally { $za.Dispose() }
foreach ($must in @('.claude-plugin/plugin.json', '.claude-plugin/marketplace.json', '.mcp.json',
                   'plugin.json', 'mcp.json', '.agents/plugins/marketplace.json', 'skills/codediffer/SKILL.md',
                   'bin/CodeDiffer.Cli.exe', 'bin/CodeDiffer.Mcp.exe', 'install-codediffer.ps1',
                   'INSTALL.txt', 'RELEASE.txt', 'README.md', 'LICENSE', 'docs/OUTPUT.md')) {
    if ($names -notcontains $must) { Remove-Item -Force $zip; throw "zip is missing '$must' - not packaging" }
}
if ($zippedVer -ne $version) { Remove-Item -Force $zip; throw "the zipped plugin.json says '$zippedVer', not $version - not packaging" }
if (git status --porcelain) { throw "packaging changed tracked files:`n$(git status --porcelain)" }
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "PACKAGED: $zip ($mb MB, $($names.Count) files) from commit $sha" -ForegroundColor Green

if (-not $Publish) { Write-Host "Not publishing (add -Publish for GitHub release v$version)."; exit 0 }

# 6) GitHub release, tagged on the exact commit built.
$tag = "v$version"
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$notes = @"
CodeDiffer $version - prebuilt, self-contained Windows x64 plugin: the MCP server and the CLI (no .NET needed).

Download $zipName below and unzip it somewhere stable (the unzipped folder IS the plugin), then in Claude Code
EITHER install persistently (the folder is a self-marketplace):

    /plugin marketplace add "<unzipped-folder>"
    /plugin install codediffer@codediffer

OR load it for one session only:

    claude --plugin-dir "<unzipped-folder>"

Then run /mcp to confirm the codediffer server is connected. Codex: ``./install-codediffer.ps1 -TargetHost Codex``.
The command line is ``bin\CodeDiffer.Cli.exe help``. INSTALL.txt in the zip has the details.

Built from commit $sha.
SHA256 ($zipName): $hash
"@
# Never replace a published zip (its SHA256 is in the notes), and never let a stray tag pick the commit.
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$global:LASTEXITCODE = 0
gh release view $tag 2>$null 1>$null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = $prev
if ($exists) { throw "release $tag already exists - commit something new for a new version (a published zip is never replaced)" }
$tagAt = (Native git ls-remote origin "refs/tags/$tag") -split '\s+' | Select-Object -First 1
if ($tagAt -and $tagAt -ne $sha) { throw "tag $tag already exists on origin at $tagAt, not $sha - delete it first if it is stale" }
$notesFile = Join-Path $root "dist\release-notes-$version.md"
Write-Text $notesFile $notes
try { Native gh release create $tag $zip --title "CodeDiffer $version" --notes-file $notesFile --target $sha | Out-Null }
finally { Remove-Item $notesFile -ErrorAction SilentlyContinue }
Write-Host "Published $tag." -ForegroundColor Green
