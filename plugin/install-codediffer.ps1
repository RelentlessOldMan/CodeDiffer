<#
.SYNOPSIS
  Register the CodeDiffer MCP server with Codex and/or print the Claude Code install steps.

.DESCRIPTION
  Run this from the unzipped plugin folder (it finds bin\CodeDiffer.Mcp.exe next to itself). The same
  installer as CodeCompass's install-codecompass.ps1.

  Codex: installs through the native Codex plugin marketplace, so Codex gets the MCP server AND the skill
  (skills\codediffer\SKILL.md) that points it at the tools:
      codex plugin marketplace add "<this folder>"     # this folder ships .agents\plugins\marketplace.json
      codex plugin add codediffer@codediffer           # registers the server (${CODEX_PLUGIN_ROOT}\bin\...)
    Older Codex without `codex plugin`: falls back to `codex mcp add codediffer -- "<abs>\bin\CodeDiffer.Mcp.exe"`
    (server only, no skill). Re-running upgrades cleanly either way.

  Claude Code: does NOT touch Claude config; it prints the marketplace commands to run.

.PARAMETER TargetHost   Which host(s) to set up: Codex, Claude, or Both (default Both).
.PARAMETER Name         Codex MCP server name (default 'codediffer').
.EXAMPLE
  ./install-codediffer.ps1                 # both hosts
  ./install-codediffer.ps1 -TargetHost Codex
  ./install-codediffer.ps1 -TargetHost Codex -WhatIf   # preview only
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Codex', 'Claude', 'Both')][string]$TargetHost = 'Both',
    [string]$Name = 'codediffer'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'bin/CodeDiffer.Mcp.exe'

function Install-Codex {
    $exeFull = (Resolve-Path -LiteralPath $exe -ErrorAction SilentlyContinue)
    if (-not $exeFull) { throw "CodeDiffer.Mcp.exe not found at '$exe'. Run this from the unzipped plugin folder." }
    $exeFull = $exeFull.Path

    if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
        Write-Warning "codex CLI not found on PATH. Install it (npm i -g @openai/codex), then re-run. Skipping Codex."
        return
    }

    # Prefer the plugin marketplace (server + skill); probe for it, fall back to raw `codex mcp add`.
    & codex plugin --help *> $null
    $hasPlugin = ($LASTEXITCODE -eq 0)

    if ($hasPlugin) {
        if ($PSCmdlet.ShouldProcess("Codex", "install plugin '$Name' from local marketplace '$root'")) {
            # Native stderr under Stop would abort on harmless chatter (PowerShell 5.1): decide on exit codes only.
            $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            try {
                $mkOut = & codex plugin marketplace add "$root" 2>&1
                $mkCode = $LASTEXITCODE
                if ($mkCode -ne 0 -and ($mkOut | Out-String) -match 'different source') {
                    # Upgrade: each release unzips to its own folder, and Codex won't re-point a marketplace name
                    # at a new folder. Without this, plugin add below "succeeds" against the OLD folder.
                    $oldRoot = (& codex plugin marketplace list 2>$null | Select-String '^\s*codediffer\s+(\S.*?)\s*$' |
                        Select-Object -First 1 | ForEach-Object { $_.Matches[0].Groups[1].Value })
                    Write-Host "Replacing the previous CodeDiffer marketplace ($oldRoot) with $root (upgrade)."
                    $rmOut = & codex plugin marketplace remove codediffer 2>&1
                    $mkOut = @($rmOut) + @(& codex plugin marketplace add "$root" 2>&1)
                    $mkCode = $LASTEXITCODE
                    if ($mkCode -ne 0 -and $oldRoot) {
                        # Don't leave the user with no CodeDiffer at all: put the working one back.
                        & codex plugin marketplace add "$oldRoot" *> $null
                        if ($LASTEXITCODE -eq 0) { $mkOut += "(the previous marketplace, $oldRoot, was put back)" }
                        else { $mkOut += "(putting the previous marketplace back failed too: codex plugin marketplace add `"$oldRoot`")" }
                    }
                }
                if ($mkCode -ne 0) {
                    throw "codex plugin marketplace add failed (exit $mkCode). Run it by hand to see why:`n  codex plugin marketplace add `"$root`"`n$(($mkOut | Out-String).Trim())"
                }
                $addOut = & codex plugin add "$Name@codediffer" 2>&1
                $addCode = $LASTEXITCODE
                if ($addCode -ne 0) {
                    # Older plugin CLIs spell it differently.
                    $addOut = & codex plugin add $Name --marketplace codediffer 2>&1
                    $addCode = $LASTEXITCODE
                }
            }
            finally { $ErrorActionPreference = $prevEap }
            if ($addCode -ne 0) {
                $detail = (@($mkOut) + @($addOut) | Out-String).Trim()
                throw "codex plugin install failed (marketplace add exit $mkCode, plugin add exit $addCode). Run it by hand to see why:`n  codex plugin marketplace add `"$root`"`n  codex plugin add $Name@codediffer`n$detail"
            }
            Write-Host "Installed Codex plugin '$Name' from $root (MCP server + skill)." -ForegroundColor Green
            Write-Host "Verify:  codex plugin list    |    codex mcp list"
        }
        return
    }

    if ($PSCmdlet.ShouldProcess("Codex", "register MCP server '$Name' -> $exeFull (fallback: no plugin subcommand)")) {
        try { & codex mcp remove $Name 2>$null | Out-Null } catch { }
        & codex mcp add $Name -- $exeFull
        if ($LASTEXITCODE -ne 0) { throw "codex mcp add failed (exit $LASTEXITCODE)." }
        Write-Host "Registered '$Name' with Codex -> $exeFull (raw MCP; upgrade Codex for the skill)." -ForegroundColor Green
        Write-Host "Verify:  codex mcp get $Name    |    codex mcp list"
    }
}

function Show-ClaudeInstall {
    Write-Host ""
    Write-Host "Claude Code (run these yourself; this script does not modify Claude config):" -ForegroundColor Cyan
    Write-Host "  /plugin marketplace add `"$root`""
    Write-Host "  /plugin install codediffer@codediffer"
}

if ($TargetHost -in 'Codex', 'Both') { Install-Codex }
if ($TargetHost -in 'Claude', 'Both') { Show-ClaudeInstall }
