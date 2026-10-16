# Publishes the CodeDiffer MCP server + CLI as self-contained win-x64 binaries into plugin/bin, producing a
# ready-to-install Claude Code (and Codex) plugin that needs no .NET on the target machine. Same layout as
# CodeCompass's build-plugin.ps1: both exes share one runtime in plugin/bin, so both target net10.0.
#
# Usage:  ./build-plugin.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$binDir = Join-Path $root "plugin/bin"

# A server or CLI started from plugin/bin (claude --plugin-dir on the checkout) locks its binaries. Unlike
# CodeCompass's build-plugin this never stops it: it may be another session's server. Say which, and stop.
$binFull = [System.IO.Path]::GetFullPath($binDir)
function Get-Locking {
    @(Get-Process -Name "CodeDiffer.Mcp", "CodeDiffer.Cli" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($binFull, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}
$locking = Get-Locking
if ($locking) {
    throw "CodeDiffer is running from $binDir ($(($locking | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ', ')). End the session(s) using it, or /mcp disconnect, then re-run."
}

Write-Host "Cleaning $binDir ..."
if (Test-Path $binDir) {
    try { Remove-Item $binDir -Recurse -Force }
    catch { throw "Could not clean $binDir - a file in it is locked. $($_.Exception.Message)" }
}
New-Item -ItemType Directory -Path $binDir | Out-Null

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "--nologo", "-p:DebugType=none", "-o", $binDir)

Write-Host "Publishing MCP server ..."
dotnet publish (Join-Path $root "src/CodeDiffer.Mcp/CodeDiffer.Mcp.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed" }

Write-Host "Publishing CLI ..."
dotnet publish (Join-Path $root "src/CodeDiffer.Cli/CodeDiffer.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

$mcp = Join-Path $binDir "CodeDiffer.Mcp.exe"
$cli = Join-Path $binDir "CodeDiffer.Cli.exe"
if (-not (Test-Path $mcp)) { throw "expected $mcp" }
if (-not (Test-Path $cli)) { throw "expected $cli" }

$size = [math]::Round(((Get-ChildItem $binDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
$version = (& $cli version)   # e.g. "codediffer 1.0.70+a1b2c3d4"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
    throw "could not read the version from the freshly built CLI ($cli) - the plugin manifests can't be stamped."
}

# Stamp the numeric version (no +sha) into both manifests, so a plugin list and `CodeDiffer.Cli.exe version`
# agree. The committed value is the 0.0.0-dev sentinel; make-release.ps1 restores it after packaging. UTF-8
# WITHOUT a BOM: Claude Code's JSON parser rejects a BOM, and PowerShell 5.1's Set-Content -Encoding utf8 writes one.
$manifestVer = ($version -replace '^codediffer\s+', '') -replace '\+.*$', ''
if ($manifestVer -notmatch '^\d+\.\d+\.\d+$') { throw "unexpected version string from the CLI: '$version'" }
foreach ($m in @("plugin/.claude-plugin/plugin.json", "plugin/plugin.json")) {
    $path = Join-Path $root $m
    if (-not (Test-Path $path)) { throw "plugin manifest not found: $path" }
    $text = [System.IO.File]::ReadAllText($path)
    $text = [regex]::Replace($text, '("version"\s*:\s*")[^"]*(")', "`${1}$manifestVer`${2}")
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host "Stamped plugin.json version = $manifestVer"

Write-Host ""
Write-Host "Plugin ready: $(Join-Path $root 'plugin')  ($version, bin is $size MB)"
Write-Host "Install for one session:   claude --plugin-dir `"$(Join-Path $root 'plugin')`""
Write-Host "Install persistently:      /plugin marketplace add `"$(Join-Path $root 'plugin')`"  then  /plugin install codediffer@codediffer"
