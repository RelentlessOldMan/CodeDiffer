# CodeDiffer (Claude Code plugin)

2-/3-way diff for very large trees (~100 GB, single files over 1 GB, local or on a network share), exposed
to Claude Code (and Codex) as MCP tools, plus the `CodeDiffer.Cli.exe` command line in `bin\`.

## What it gives the agent

- `start_compare` / `start_compare3`: compare two trees, or a base and two variants, in the background
- `get_summary`, `list_files`, `get_stats`: bounded answers about what changed (paged, filtered)
- `get_file_diff`: one file's diff, or its 3-way merge with diff3 conflict markers
- `export_changeset`, `apply_changeset`: the whole patch to a file; port the changes onto a third tree
  (dry run unless `write=true`)
- `write_merge`, `write_report`: a 3-way merge as an overlay; the HTML report for a human
- `list_compares`, `cancel_compare`: saved compares (reopen by id); stop a running one

Compares are saved under `%LOCALAPPDATA%\CodeDiffer\results` (`CODEDIFFER_RESULTS_DIR` overrides), never
inside a compared tree.

## Install

> There is **no** `/plugin add "<path>"` command. Point at the folder that directly contains
> `.claude-plugin\` and `bin\`: the unzipped release, or `<repo>\plugin` in a source checkout.

### Prebuilt (no .NET needed)

1. Download `codediffer-plugin-<version>-win-x64.zip` from the Releases page and unzip it. **The unzipped
   folder IS the plugin.**
2. Persistent install (the folder is its own marketplace):

   ```
   /plugin marketplace add "C:\path\to\codediffer-plugin-<version>-win-x64"
   /plugin install codediffer@codediffer
   ```

   Or one session only: `claude --plugin-dir "C:\path\to\codediffer-plugin-<version>-win-x64"`.
3. Run `/mcp` to confirm the `codediffer` server is connected.

Claude Code copies an installed plugin into its own cache, one folder per version, so installing a new
release never touches the files a running session's server is using.

### From source (needs the .NET 10 SDK)

```powershell
./build-plugin.ps1
```

publishes `plugin\bin` (both exes, self-contained); then load `<repo>\plugin` as above.

### Codex (OpenAI)

From the unzipped folder: `./install-codediffer.ps1 -TargetHost Codex`, or by hand:

```
codex plugin marketplace add "C:\path\to\codediffer-plugin-<version>-win-x64"
codex plugin add codediffer@codediffer
```

**Upgrading:** Codex won't re-point a marketplace at a new folder; the installer removes the old
`codediffer` marketplace first (by hand: `codex plugin marketplace remove codediffer`, then the two
commands above).

## The command line

`bin\CodeDiffer.Cli.exe help` lists the commands: `compare`, `compare3`, `apply`, `apply-overlay`, `diff`,
`blockdiff`, `report`, `results`, `verify`. Docs: https://github.com/RelentlessOldMan/CodeDiffer
