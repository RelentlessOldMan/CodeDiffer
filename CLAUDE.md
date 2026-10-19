# CodeDiffer — project instructions

## The ground-truth contract (CodeSpawner coupling)

CodeDiffer's correctness oracle is CodeSpawner's **delta manifest** (`<corpus>-delta.json`), locked
2026-10-02. The stable coupling point is the versioned manifest + the `diffTruthSha` canonical digest,
NOT the corpus bytes. Always hard-assert `_meta.manifestVersion` before trusting a manifest, and
independently reproduce `diffTruthSha` from the four canonical sections before relying on it (reproduce
CodeSpawner's shipped `digest-selftest` golden vector first — same bar CodeCarver held them to). The
full contract is in [`docs/diff-delta-contract.md`](docs/diff-delta-contract.md).

## Stack / conventions

- .NET / C#, matching the family. Libraries target **net8.0** (the fleet floor, so the work box builds
  from source); the CLI and MCP server target **net10.0**: the MCP SDK needs the .NET 10 base libraries, and
  both exes share one self-contained runtime in the plugin's `bin\`, so they must match (the CodeCompass model).
- Distribution is a **plugin**, like CodeCompass: `plugin/` is a Claude Code + Codex plugin (manifests,
  `.mcp.json`, skill, installer); `build-plugin.ps1` publishes both exes into `plugin/bin` and stamps the
  version; `make-release.ps1 [-Publish]` tests, builds, smoke-tests the server and zips `plugin/` as
  `codediffer-plugin-<version>-win-x64.zip`. The committed manifest version is the `0.0.0-dev` sentinel.
- `src/CodeDiffer.Core` — the engine (walk · hash · classify · hunks · giant-file block index · digest).
- `src/CodeDiffer.Cli` — the `codediffer` command, built as **`CodeDiffer.Cli.exe`**.
- `src/CodeDiffer.Mcp` — the agent-facing stdio MCP server (paged, bounded queries), its own exe
  **`CodeDiffer.Mcp.exe`**. Same model as CodeCompass (`CodeCompass.Cli.exe` + `CodeCompass.Mcp.exe`):
  two exes over one Core; the CLI does not reference the MCP project.
- `tests/CodeDiffer.Tests` — xUnit, **net10.0** (it references the MCP and CLI projects to test them, like
  CodeCompass.Core.Tests); seed it from the prototype-review regression bugs (see DESIGN.txt).
- Public-repo conventions live in `C:\Playground\RelentlessOldMan\Project_Instructions\REPO-SETUP.md`.

## Resource needs
- Compares stream and hold only the verdicts: peak working set ~160–380 MB on death.
- Measured run costs are in [`docs/OUTPUT.md`](docs/OUTPUT.md) §9.
