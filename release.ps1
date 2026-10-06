<#
.SYNOPSIS
  Package CodeDiffer as a self-contained win-x64 zip (CLI + MCP server, no .NET needed) and, with -Publish,
  put it on GitHub Releases as v<version>.

.DESCRIPTION
  Same rules as the sister tools (CodeCompass make-release.ps1, CodeCarver release.ps1):
    1. the working tree must be clean and HEAD must already be on origin/main (a zip is always a pushed commit);
    2. the unit suite must pass in Release (skip with -SkipTests only if you just ran it);
    3. both exes are published self-contained single-file, the CLI's version must match the build, and the
       published MCP server must answer initialize + tools/list (a server that can't start never ships);
    4. the zip is written with '/' entry names (Compress-Archive on PowerShell 5.1 writes '\') and checked.
  The version is 1.0.<commit count> (Directory.Build.props), the same number `CodeDiffer.Cli.exe version` prints.

.EXAMPLE
  ./release.ps1              # test, build and zip into dist\
  ./release.ps1 -Publish     # ... and create the GitHub release v<version> from the zip
#>
param([switch]$Publish, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# git and gh write progress and "not found" to stderr; under Stop on PowerShell 5.1 that becomes a terminating
# error even on exit 0. Run natives under Continue and decide on the exit code.
function Native {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$a)
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & $a[0] @($a | Select-Object -Skip 1) 2>$null; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $prev }
    if ($code -ne 0) { throw "$($a -join ' ') failed (exit $code)" }
    return $o
}

# 1) A clean tree whose HEAD is on origin/main.
$dirty = git status --porcelain
if ($dirty) { throw "working tree has uncommitted changes - commit them first:`n$($dirty -join "`n")" }
$sha = (Native git rev-parse HEAD).Trim()
Native git fetch origin main --quiet | Out-Null
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
git merge-base --is-ancestor $sha origin/main 2>$null
$pushed = $LASTEXITCODE -eq 0
$ErrorActionPreference = $prev
if (-not $pushed) { throw "HEAD ($($sha.Substring(0, 9))) is not on origin/main - push it first (git push origin main)." }
Write-Host "OK: $($sha.Substring(0, 9)) is on origin/main." -ForegroundColor Green

# 2) Tests.
if ($SkipTests) { Write-Warning "SkipTests: not running the unit suite." }
else {
    Write-Host "== Tests (Release) ==" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'CodeDiffer.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed - not packaging" }
}

# 3) Publish both exes into a staging folder.
$stage = Join-Path $root 'dist\stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($proj in @('CodeDiffer.Cli', 'CodeDiffer.Mcp')) {
    Write-Host "== Publishing $proj ==" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src\$proj") -c Release -r win-x64 --self-contained --nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish of $proj failed" }
}
$extra = @(Get-ChildItem $stage -File | Where-Object { $_.Name -notin @('CodeDiffer.Cli.exe', 'CodeDiffer.Mcp.exe') })
if ($extra.Count) { $extra | Remove-Item -Force }   # stray config/pdb files; the exes are self-contained
if (git status --porcelain) { throw "publishing changed tracked files - not packaging:`n$(git status --porcelain)" }

# The version the shipped CLI reports, e.g. "codediffer 1.0.36+8d5e88b" -> 1.0.36. Its commit must be HEAD.
$cliExe = Join-Path $stage 'CodeDiffer.Cli.exe'
$verLine = (& $cliExe version | Out-String).Trim()
if ($verLine -notmatch '^codediffer (\d+\.\d+\.\d+)\+([0-9a-f]+)') { throw "unexpected version output: '$verLine'" }
$version = $Matches[1]
if (-not $sha.StartsWith($Matches[2])) { throw "the built CLI says commit $($Matches[2]) but HEAD is $sha" }
Write-Host "Version $version" -ForegroundColor Green

# Smoke the published MCP server: a real stdio handshake that must list the tools.
Write-Host "Smoke-testing the MCP server (initialize + tools/list) ..."
$psi = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $stage 'CodeDiffer.Mcp.exe'))
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
    foreach ($tool in @('start_compare', 'start_compare3', 'get_summary', 'apply_changeset')) {
        if ($out -notmatch "`"$tool`"") { throw "MCP smoke FAILED: tools/list has no $tool. stderr:`n$($errTask.Result)" }
    }
    Write-Host "  PASS  the server answers and lists its tools." -ForegroundColor Green
}
finally {
    try { if (-not $mcp.HasExited) { $mcp.Kill() } } catch {}
    try { $mcp.WaitForExit(5000) | Out-Null; $mcp.Dispose() } catch {}
}

# 4) What ships beside the exes.
foreach ($f in @('README.md', 'LICENSE')) { Copy-Item (Join-Path $root $f) $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\OUTPUT.md') (Join-Path $stage 'docs')
"CodeDiffer $version`ncommit $sha`nbuilt  $(Get-Date -Format o)" | Set-Content -Encoding UTF8 (Join-Path $stage 'RELEASE.txt')
@"
CodeDiffer $version - Windows x64, self-contained (no .NET needed)
=================================================================

CodeDiffer.Cli.exe   the command line:  CodeDiffer.Cli.exe help
CodeDiffer.Mcp.exe   the MCP server for agents

Put this folder somewhere stable OUTSIDE any build tree (a running MCP server locks its exe), e.g.
%LOCALAPPDATA%\CodeDiffer\bin, then register the server with Claude Code:

    claude mcp add --scope user codediffer -- "%LOCALAPPDATA%\CodeDiffer\bin\CodeDiffer.Mcp.exe"

and check it with  claude mcp list  (or /mcp in a session).

Updating while sessions are running the server: rename the old CodeDiffer.Mcp.exe aside (Windows allows
renaming a running exe), copy the new one in, and /mcp reconnect. Delete the renamed file later.

Compares are saved under %LOCALAPPDATA%\CodeDiffer\results (CODEDIFFER_RESULTS_DIR overrides).
Docs: README.md and docs\OUTPUT.md here, or https://github.com/RelentlessOldMan/CodeDiffer
"@ | Set-Content -Encoding UTF8 (Join-Path $stage 'INSTALL.txt')

# 5) Zip with '/' entry names, then check it.
$zip = Join-Path $root "dist\codediffer-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stageRoot = (Resolve-Path $stage).Path.TrimEnd('\')
$zw = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in [System.IO.Directory]::EnumerateFiles($stageRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
        $entry = $f.Substring($stageRoot.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zw, $f, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zw.Dispose() }
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $names = @($za.Entries | ForEach-Object { $_.FullName }) } finally { $za.Dispose() }
foreach ($must in @('CodeDiffer.Cli.exe', 'CodeDiffer.Mcp.exe', 'INSTALL.txt', 'README.md', 'LICENSE', 'docs/OUTPUT.md')) {
    if ($names -notcontains $must) { Remove-Item -Force $zip; throw "zip is missing '$must' - not packaging" }
}
Remove-Item -Recurse -Force $stage
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "PACKAGED: $zip ($mb MB) from commit $sha" -ForegroundColor Green

if (-not $Publish) { Write-Host "Not publishing (add -Publish for GitHub release v$version)."; exit 0 }

# 6) GitHub release, tagged on the exact commit built.
$tag = "v$version"
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$notes = @"
CodeDiffer $version - self-contained Windows x64 build of the CLI and the MCP server (no .NET needed).

Unzip somewhere outside any build tree (e.g. %LOCALAPPDATA%\CodeDiffer\bin) and register the server:

    claude mcp add --scope user codediffer -- "<folder>\CodeDiffer.Mcp.exe"

INSTALL.txt in the zip has the details; ``CodeDiffer.Cli.exe help`` lists the commands.

Built from commit $sha.
SHA256 (codediffer-$version-win-x64.zip): $hash
"@
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
gh release view $tag 2>$null 1>$null
$exists = $LASTEXITCODE -eq 0
if ($exists) { gh release upload $tag $zip --clobber }
else { gh release create $tag $zip --title "CodeDiffer $version" --notes $notes --target $sha }
$code = $LASTEXITCODE
$ErrorActionPreference = $prev
if ($code -ne 0) { throw "gh release failed" }
Write-Host "Published $tag." -ForegroundColor Green
